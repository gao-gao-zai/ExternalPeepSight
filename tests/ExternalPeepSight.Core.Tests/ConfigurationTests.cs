using System.IO.Compression;
using System.Text;

namespace ExternalPeepSight.Core.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void DefaultsAreValidAndRoundTripThroughJson()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();

        Assert.True(ConfigurationValidator.Validate(document).IsValid);

        string json = ConfigurationJson.Serialize(document);
        ConfigurationDocument restored = ConfigurationJson.Deserialize(json);

        Assert.Equal(
            ConfigurationJson.Serialize(document),
            ConfigurationJson.Serialize(restored));
        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"angleDeg\":0", json);
    }

    [Fact]
    public void ColorParserSupportsRgbAndRgba()
    {
        Assert.Equal(new RgbaColor(1, 2, 3, 255), RgbaColor.Parse("#010203"));
        Assert.Equal("#01020304", RgbaColor.Parse("01020304").ToString());
        Assert.Throws<FormatException>(() => RgbaColor.Parse("#123"));
    }

    [Fact]
    public void MissingSchemaVersionIsMigrated()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        string json = ConfigurationJson.Serialize(document).Replace("\"schemaVersion\":1,", string.Empty);

        ConfigurationDocument restored = ConfigurationJson.Deserialize(json);

        Assert.Equal(ConfigurationJson.CurrentSchemaVersion, restored.SchemaVersion);
    }

    [Fact]
    public void NewerSchemaVersionIsRejected()
    {
        string json = ConfigurationJson.Serialize(ConfigurationDefaults.Create())
            .Replace("\"schemaVersion\":1", "\"schemaVersion\":99");

        Assert.Throws<ConfigurationFormatException>(() => ConfigurationJson.Deserialize(json));
    }

    [Fact]
    public void UnknownJsonPropertyIsRejected()
    {
        string json = ConfigurationJson.Serialize(ConfigurationDefaults.Create())
            .Replace("}", ",\"unexpected\":true}", StringComparison.Ordinal);

        Assert.Throws<ConfigurationFormatException>(() => ConfigurationJson.Deserialize(json));
    }

    [Fact]
    public void ValidatorReportsCrossObjectAndRangeFailures()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create() with
        {
            Profiles =
            [
                ConfigurationDefaults.Create().Profiles[0] with
                {
                    Id = Guid.Empty,
                    Name = string.Empty,
                    ActiveMode = OverlayMode.Image,
                    Crosshair = ConfigurationDefaults.Create().Profiles[0].Crosshair with
                    {
                        Arms =
                        [
                            new(double.NaN, -1, 10001, 0, RgbaColor.White, true),
                        ],
                    },
                    Image = new ImageOverlay(Guid.NewGuid(), AnchorMode.ScreenCenter, new PixelPoint(200000, 0), 0, true),
                },
            ],
            ProfileSets =
            [
                new(Guid.NewGuid(), "Set", [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid()),
            ],
            Assets =
            [
                new(Guid.NewGuid(), "../bad.png", "application/octet-stream", 0, "invalid"),
            ],
            MonitorSelection = new(MonitorSelectionMode.Explicit, [], FocusMonitorSource.Mouse),
            Switches = new(
                (VisibilityRule)99,
                false,
                false,
                new HotkeyBinding(HotkeyActivationMode.Toggle, null, null, null, null),
                new HotkeyBinding(HotkeyActivationMode.Unbound, null, null, null, null)),
            Toasts = new(true, (ToastPosition)99, 1, string.Empty, 1, RgbaColor.White, RgbaColor.Transparent),
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.True(result.Issues.Count >= 10);
        Assert.Contains(result.Issues, issue => issue.Path.Contains("assetId", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Path.Contains("durationMs", StringComparison.Ordinal));
    }

    [Fact]
    public void AtomicStoreKeepsBackupWhenPrimaryIsCorrupted()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "config.json");
            var store = new AtomicConfigurationStore(path);
            ConfigurationDocument first = ConfigurationDefaults.Create();
            ConfigurationDocument second = first with { Toasts = first.Toasts with { DurationMs = 2000 } };

            store.Save(first);
            store.Save(second);
            File.WriteAllText(path, "{ invalid", Encoding.UTF8);

            ConfigurationDocument recovered = store.Load();

            Assert.Equal(
                ConfigurationJson.Serialize(first),
                ConfigurationJson.Serialize(recovered));
            Assert.True(store.TryRestoreBackup());
            Assert.Equal(
                ConfigurationJson.Serialize(first),
                ConfigurationJson.Serialize(store.Load()));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task DebouncedSaveCanBeCancelled()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new AtomicConfigurationStore(Path.Combine(directory, "config.json"));
            using var cancellation = new CancellationTokenSource();
            Task pending = store.SaveDebouncedAsync(
                ConfigurationDefaults.Create(),
                TimeSpan.FromSeconds(1),
                cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(File.Exists(store.FilePath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void EpsxRoundTripPreservesConfigurationAndAsset()
    {
        (ConfigurationDocument document, EpsxAsset asset) = CreateImageDocument();
        using var stream = new MemoryStream();

        EpsxArchive.Export(stream, document, [asset]);
        stream.Position = 0;
        ConfigurationMergeResult result = EpsxArchive.Import(stream, ConfigurationDefaults.Create());

        Assert.Equal(2, result.Document.Profiles.Length);
        Assert.Contains(result.Document.Profiles, profile => profile.Name == "Image");
        Assert.Single(result.Assets);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void EpsxImportResolvesConflictingProfileAndAssetIds()
    {
        (ConfigurationDocument imported, EpsxAsset asset) = CreateImageDocument();
        ConfigurationDocument existing = imported with
        {
            Profiles = [imported.Profiles[0] with { Name = "Local" }],
            Assets =
            [
                imported.Assets[0] with { Sha256 = new string('A', 64) },
            ],
        };

        using var stream = new MemoryStream();
        EpsxArchive.Export(stream, imported, [asset]);
        stream.Position = 0;
        ConfigurationMergeResult result = EpsxArchive.Import(stream, existing);

        Assert.Equal(2, result.Document.Profiles.Length);
        Assert.Equal(2, result.Document.Assets.Length);
        Assert.Contains(result.Conflicts, conflict => conflict.Kind == "profile");
        Assert.Contains(result.Conflicts, conflict => conflict.Kind == "asset");
    }

    [Fact]
    public void EpsxRejectsUnsafeZipPath()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("../evil");
            using Stream output = entry.Open();
            output.WriteByte(1);
        }

        stream.Position = 0;
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(stream, ConfigurationDefaults.Create()));
    }

    [Fact]
    public void EpsxRejectsInconsistentArchiveOptions()
    {
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EpsxArchive.Export(
                stream,
                ConfigurationDefaults.Create(),
                [],
                new EpsxArchiveOptions { MaxEntryBytes = 20, MaxTotalBytes = 10 }));
    }

    private static (ConfigurationDocument Document, EpsxAsset Asset) CreateImageDocument()
    {
        Guid profileId = Guid.NewGuid();
        byte[] content = [137, 80, 78, 71, 13, 10, 26, 10];
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        var reference = new AssetReference(profileId, "crosshair.png", "image/png", content.Length, hash);
        var profile = ConfigurationDefaults.Create().Profiles[0] with
        {
            Id = profileId,
            Name = "Image",
            ActiveMode = OverlayMode.Image,
            Image = new ImageOverlay(profileId, AnchorMode.ScreenCenter, new PixelPoint(0, 0), 1, true),
        };
        var document = ConfigurationDefaults.Create() with
        {
            Profiles = [profile],
            ProfileSets = [new(Guid.NewGuid(), "Images", [profileId], profileId)],
            Assets = [reference],
        };
        return (document, new EpsxAsset(reference, content));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

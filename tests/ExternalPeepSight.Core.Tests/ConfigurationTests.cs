using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

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
        Assert.Contains("\"schemaVersion\":6", json);
        Assert.Contains("\"orbitAngleOffsetDeg\":0", json);
        Assert.DoesNotContain("\"switches\":", json[..json.IndexOf("\"profiles\"", StringComparison.Ordinal)]);
    }

    [Fact]
    public void ColorParserSupportsRgbAndRgba()
    {
        Assert.Equal(new RgbaColor(1, 2, 3, 255), RgbaColor.Parse("#010203"));
        Assert.Equal("#01020304", RgbaColor.Parse("01020304").ToString());
        Assert.Throws<FormatException>(() => RgbaColor.Parse("#123"));
    }

    [Fact]
    public void VersionOneGlobalSwitchesAreMigratedIntoEveryProfile()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        Profile first = document.Profiles[0];
        Profile second = first with { Id = Guid.NewGuid(), Name = "Second" };
        SwitchConfiguration switches = first.Switches with { InitialStateA = true };
        ConfigurationDocument versionTwo = document with
        {
            Profiles =
            [
                first with { Switches = switches },
                second with { Switches = switches },
            ],
            ProfileSets = [],
        };
        System.Text.Json.Nodes.JsonObject versionOne =
            System.Text.Json.Nodes.JsonNode.Parse(ConfigurationJson.Serialize(versionTwo))!.AsObject();
        versionOne["schemaVersion"] = 1;
        System.Text.Json.Nodes.JsonArray profiles = versionOne["profiles"]!.AsArray();
        System.Text.Json.Nodes.JsonNode migratedSwitches = profiles[0]!["switches"]!.DeepClone();
        foreach (System.Text.Json.Nodes.JsonNode? profile in profiles)
        {
            if (profile is System.Text.Json.Nodes.JsonObject profileObject)
            {
                profileObject.Remove("switches");
            }
        }
        versionOne["switches"] = migratedSwitches;

        ConfigurationDocument restored = ConfigurationJson.Deserialize(versionOne.ToJsonString());

        Assert.Equal(ConfigurationJson.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.All(restored.Profiles, profile => Assert.Equal(switches, profile.Switches));
        Assert.DoesNotContain(
            "\"switches\":",
            ConfigurationJson.Serialize(restored)[..ConfigurationJson.Serialize(restored).IndexOf("\"profiles\"", StringComparison.Ordinal)]);
    }

    [Fact]
    public void VersionTwoKeyboardBindingsAreMigratedToDeviceCodes()
    {
        ConfigurationDocument current = ConfigurationDefaults.Create();
        var keyboard = new KeyIdentity(InputDeviceKind.Keyboard, 0x31, false, KeyModifiers.Ctrl);
        current = current with
        {
            Profiles =
            [
                current.Profiles[0] with
                {
                    Switches = current.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(
                            HotkeyActivationMode.Toggle,
                            keyboard,
                            null,
                            null,
                            null),
                    },
                },
            ],
        };
        JsonObject versionTwo = JsonNode.Parse(ConfigurationJson.Serialize(current))!.AsObject();
        versionTwo["schemaVersion"] = 2;
        JsonObject key = versionTwo["profiles"]![0]!["switches"]!["switchA"]!["toggleKey"]!.AsObject();
        key["scanCode"] = key["code"]!.DeepClone();
        key.Remove("device");
        key.Remove("code");

        ConfigurationDocument restored = ConfigurationJson.Deserialize(versionTwo.ToJsonString());

        Assert.Equal(keyboard, restored.Profiles[0].Switches.SwitchA.ToggleKey);
        string serialized = ConfigurationJson.Serialize(restored);
        Assert.Contains("\"device\":\"keyboard\"", serialized);
        Assert.Contains("\"code\":49", serialized);
        Assert.DoesNotContain("\"scanCode\"", serialized);
    }

    [Fact]
    public void VersionThreeAbsoluteArmValuesAreMigratedToCurrentSchema()
    {
        JsonObject versionThree = JsonNode.Parse(ConfigurationJson.Serialize(ConfigurationDefaults.Create()))!.AsObject();
        versionThree["schemaVersion"] = 3;
        JsonArray arms = versionThree["profiles"]![0]!["crosshair"]!["arms"]!.AsArray();
        for (int index = 0; index < arms.Count; index++)
        {
            JsonObject arm = arms[index]!.AsObject();
            ArmDefaults defaults = CrosshairArmDefaults.Get(index);
            arm["angleDeg"] = defaults.OrbitAngleDeg + 15;
            arm["gapPx"] = defaults.GapPx + 2;
            arm["lengthPx"] = defaults.LengthPx - 1;
            arm["widthPx"] = defaults.WidthPx + 1;
            arm.Remove("orbitAngleOffsetDeg");
            arm.Remove("rotationAngleOffsetDeg");
            arm.Remove("gapOffsetPx");
            arm.Remove("lengthOffsetPx");
            arm.Remove("widthOffsetPx");
        }

        ConfigurationDocument restored = ConfigurationJson.Deserialize(versionThree.ToJsonString());
        Assert.Equal(6, restored.SchemaVersion);
        Assert.All(
            restored.Profiles[0].Crosshair.Arms,
            arm =>
            {
                Assert.Equal(15, arm.OrbitAngleOffsetDeg);
                Assert.Equal(0, arm.RotationAngleOffsetDeg);
                Assert.Equal(8, arm.GapPx);
                Assert.Equal(11, arm.LengthPx);
                Assert.Equal(3, arm.WidthPx);
            });
    }

    [Fact]
    public void VersionFourDimensionOffsetsAreMigratedToAbsoluteValues()
    {
        JsonObject versionFour = JsonNode.Parse(ConfigurationJson.Serialize(ConfigurationDefaults.Create()))!.AsObject();
        versionFour["schemaVersion"] = 4;
        JsonArray arms = versionFour["profiles"]![0]!["crosshair"]!["arms"]!.AsArray();
        foreach (JsonNode? node in arms)
        {
            JsonObject arm = node!.AsObject();
            arm["gapOffsetPx"] = -8;
            arm["lengthOffsetPx"] = 3;
            arm["widthOffsetPx"] = 2;
            arm.Remove("gapPx");
            arm.Remove("lengthPx");
            arm.Remove("widthPx");
        }

        ConfigurationDocument restored = ConfigurationJson.Deserialize(versionFour.ToJsonString());

        Assert.Equal(6, restored.SchemaVersion);
        Assert.All(
            restored.Profiles[0].Crosshair.Arms,
            arm =>
            {
                Assert.Equal(-2, arm.GapPx);
                Assert.Equal(15, arm.LengthPx);
                Assert.Equal(4, arm.WidthPx);
            });
    }

    [Fact]
    public void VersionFiveDocumentsDefaultToRawInputBackend()
    {
        JsonObject versionFive = JsonNode.Parse(ConfigurationJson.Serialize(ConfigurationDefaults.Create()))!.AsObject();
        versionFive["schemaVersion"] = 5;
        versionFive.Remove("inputBackend");

        ConfigurationDocument restored = ConfigurationJson.Deserialize(versionFive.ToJsonString());

        Assert.Equal(ConfigurationJson.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(InputCaptureBackend.RawInput, restored.InputBackend);
    }

    [Fact]
    public void InputBackendRoundTripsThroughJson()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create() with
        {
            InputBackend = InputCaptureBackend.LowLevelHook,
        };

        ConfigurationDocument restored = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(document));

        Assert.Equal(InputCaptureBackend.LowLevelHook, restored.InputBackend);
    }

    [Fact]
    public void MouseBindingRoundTripsThroughJson()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        var mouse = new KeyIdentity(
            InputDeviceKind.Mouse,
            (ushort)InputMouseButton.X1,
            false,
            KeyModifiers.Ctrl);
        document = document with
        {
            Profiles =
            [
                document.Profiles[0] with
                {
                    Switches = document.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(
                            HotkeyActivationMode.Hold,
                            null,
                            null,
                            null,
                            mouse),
                    },
                },
            ],
        };

        ConfigurationDocument restored = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(document));

        Assert.Equal(mouse, restored.Profiles[0].Switches.SwitchA.HoldKey);
    }

    [Fact]
    public void NewerSchemaVersionIsRejected()
    {
        string json = ConfigurationJson.Serialize(ConfigurationDefaults.Create())
            .Replace("\"schemaVersion\":6", "\"schemaVersion\":99");

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
                            new(double.NaN, double.NaN, -7, 9989, -3, RgbaColor.White, true),
                        ],
                    },
                    Image = new ImageOverlay(Guid.NewGuid(), AnchorMode.ScreenCenter, new PixelPoint(200000, 0), 0, true),
                    Switches = new(
                        (VisibilityRule)99,
                        false,
                        false,
                        new HotkeyBinding(HotkeyActivationMode.Toggle, null, null, null, null),
                        new HotkeyBinding(HotkeyActivationMode.Unbound, null, null, null, null)),
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
    public void EpsxContainsOnlyPortableConfigurationData()
    {
        (ConfigurationDocument document, EpsxAsset asset) = CreateImageDocument();
        using var stream = new MemoryStream();

        EpsxArchive.Export(stream, document, [asset]);
        string profilesJson = ReadZipText(stream, "profiles.json");
        JsonObject root = JsonNode.Parse(profilesJson)!.AsObject();

        Assert.Equal(ConfigurationJson.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.NotNull(root["profiles"]);
        Assert.NotNull(root["profileSets"]);
        Assert.NotNull(root["assets"]);
        Assert.NotNull(root["profiles"]![0]!["switches"]);
        Assert.Null(root["monitorSelection"]);
        Assert.Null(root["toasts"]);
        Assert.Null(root["switches"]);
    }

    [Fact]
    public void EpsxImportKeepsExistingApplicationSettings()
    {
        (ConfigurationDocument imported, EpsxAsset asset) = CreateImageDocument();
        ConfigurationDocument existing = ConfigurationDefaults.Create() with
        {
            MonitorSelection = new(
                MonitorSelectionMode.Explicit,
                ["DISPLAY-LOCAL"],
                FocusMonitorSource.Mouse),
            Toasts = new(
                false,
                ToastPosition.BottomLeft,
                4321,
                "Consolas",
                16,
                new RgbaColor(1, 2, 3),
                new RgbaColor(4, 5, 6, 7)),
        };
        using var stream = new MemoryStream();
        EpsxArchive.Export(stream, imported, [asset]);
        stream.Position = 0;

        ConfigurationMergeResult result = EpsxArchive.Import(stream, existing);

        Assert.Equal(existing.MonitorSelection, result.Document.MonitorSelection);
        Assert.Equal(existing.Toasts, result.Document.Toasts);
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

    private static string ReadZipText(MemoryStream stream, string entryName)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry entry = archive.GetEntry(entryName)!;
        using Stream input = entry.Open();
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
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

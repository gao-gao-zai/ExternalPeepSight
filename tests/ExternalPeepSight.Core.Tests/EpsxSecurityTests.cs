using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExternalPeepSight.Core.Tests;

public sealed class EpsxSecurityTests
{
    [Fact]
    public void CorruptZipIsRejected()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(stream, ConfigurationDefaults.Create()));
    }

    [Fact]
    public void MissingManifestOrProfilesIsRejected()
    {
        using MemoryStream missingManifest = CreateZip(
            ("profiles.json", CreatePortableDocument(ConfigurationDefaults.Create())));
        using MemoryStream missingProfiles = CreateZip(("manifest.json", "{\"schemaVersion\":3,\"assets\":[]}"));

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(missingManifest, ConfigurationDefaults.Create()));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(missingProfiles, ConfigurationDefaults.Create()));
    }

    [Fact]
    public void InvalidManifestAndSchemaAreRejected()
    {
        using MemoryStream invalidJson = CreateZip(
            ("manifest.json", "{"),
            ("profiles.json", CreatePortableDocument(ConfigurationDefaults.Create())));
        using MemoryStream invalidSchema = CreateZip(
            ("manifest.json", "{\"schemaVersion\":99,\"assets\":[]}"),
            ("profiles.json", CreatePortableDocument(ConfigurationDefaults.Create())));

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(invalidJson, ConfigurationDefaults.Create()));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(invalidSchema, ConfigurationDefaults.Create()));
    }

    [Fact]
    public void EntryLimitsAndDuplicateNamesAreEnforced()
    {
        using MemoryStream duplicate = CreateZip(
            ("manifest.json", "{}"),
            ("manifest.json", "{}"));
        using MemoryStream tooMany = CreateZip(
            ("manifest.json", "{}"),
            ("profiles.json", "{}"));
        using MemoryStream tooLarge = CreateZip(("manifest.json", new string('x', 100)));

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(duplicate, ConfigurationDefaults.Create()));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(
                tooMany,
                ConfigurationDefaults.Create(),
                new EpsxArchiveOptions { MaxEntries = 1 }));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(
                tooLarge,
                ConfigurationDefaults.Create(),
                new EpsxArchiveOptions { MaxEntryBytes = 10, MaxTotalBytes = 10 }));
    }

    [Fact]
    public void CompressionRatioAndTotalSizeAreEnforced()
    {
        byte[] repeated = Encoding.UTF8.GetBytes(new string('x', 100000));
        using MemoryStream ratio = CreateZipBytes(("manifest.json", repeated));
        using MemoryStream total = CreateZipBytes(
            ("manifest.json", Encoding.UTF8.GetBytes(new string('a', 20))),
            ("profiles.json", Encoding.UTF8.GetBytes(new string('b', 20))));

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(
                ratio,
                ConfigurationDefaults.Create(),
                new EpsxArchiveOptions { MaxCompressionRatio = 1 }));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(
                total,
                ConfigurationDefaults.Create(),
                new EpsxArchiveOptions { MaxEntryBytes = 30, MaxTotalBytes = 30 }));
    }

    [Fact]
    public void ManifestAssetMustExistAndMatchHash()
    {
        (ConfigurationDocument document, EpsxAsset asset) = CreateImageDocument();
        string manifest = CreateManifest(asset.Reference, asset.Reference.Sha256);
        using MemoryStream missing = CreateZip(
            ("manifest.json", manifest),
            ("profiles.json", CreatePortableDocument(document)));
        using MemoryStream wrongHash = CreateZipBytes(
            ("manifest.json", Encoding.UTF8.GetBytes(CreateManifest(asset.Reference, new string('0', 64)))),
            ("profiles.json", Encoding.UTF8.GetBytes(CreatePortableDocument(document))),
            ($"assets/{asset.Reference.Id:N}.png", asset.Content));

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(missing, ConfigurationDefaults.Create()));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(wrongHash, ConfigurationDefaults.Create()));
    }

    [Fact]
    public void ExportValidatesAssetContentAndReferences()
    {
        (ConfigurationDocument document, EpsxAsset asset) = CreateImageDocument();
        EpsxAsset wrongContent = asset with { Content = [1, 2, 3] };
        EpsxAsset unsupported = asset with
        {
            Reference = asset.Reference with { MediaType = "image/gif" },
        };

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Export(new MemoryStream(), document, [wrongContent]));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Export(new MemoryStream(), document, [unsupported]));
        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Export(new MemoryStream(), document, []));
    }

    [Fact]
    public void ImportingSamePackageIsIdempotent()
    {
        (ConfigurationDocument document, EpsxAsset asset) = CreateImageDocument();
        using var package = new MemoryStream();
        EpsxArchive.Export(package, document, [asset]);
        package.Position = 0;

        ConfigurationMergeResult result = EpsxArchive.Import(package, document);

        Assert.Empty(result.Conflicts);
        Assert.Empty(result.Assets);
        Assert.Equal(
            ConfigurationJson.Serialize(document),
            ConfigurationJson.Serialize(result.Document));
    }

    [Fact]
    public void FailedImportDoesNotChangeExistingDocument()
    {
        ConfigurationDocument existing = ConfigurationDefaults.Create();
        string before = ConfigurationJson.Serialize(existing);
        using var package = CreateZip(("manifest.json", "{}"));

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(package, existing));
        Assert.Equal(before, ConfigurationJson.Serialize(existing));
    }

    [Fact]
    public void ProfilesPayloadRejectsApplicationLevelSettings()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        JsonObject portable = JsonNode.Parse(CreatePortableDocument(document))!.AsObject();
        portable["toasts"] = JsonSerializer.SerializeToNode(document.Toasts);
        using MemoryStream package = CreateZip(
            ("manifest.json", "{\"schemaVersion\":3,\"assets\":[]}"),
            ("profiles.json", portable.ToJsonString()));

        Assert.Throws<ConfigurationFormatException>(() =>
            EpsxArchive.Import(package, ConfigurationDefaults.Create()));
    }

    private static string CreateManifest(AssetReference reference, string sha256) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = ConfigurationJson.CurrentSchemaVersion,
            assets = new[]
            {
                new
                {
                    id = reference.Id,
                    fileName = reference.FileName,
                    mediaType = reference.MediaType,
                    sizeBytes = reference.SizeBytes,
                    sha256,
                    path = $"assets/{reference.Id:N}.png",
                },
            },
        });

    private static string CreatePortableDocument(ConfigurationDocument document)
    {
        JsonObject root = JsonNode.Parse(ConfigurationJson.Serialize(document))!.AsObject();
        root.Remove("monitorSelection");
        root.Remove("toasts");
        return root.ToJsonString();
    }

    private static MemoryStream CreateZip(params (string Name, string Content)[] entries) =>
        CreateZipBytes(entries.Select(entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Content))).ToArray());

    private static MemoryStream CreateZipBytes(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using Stream output = entry.Open();
                output.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static (ConfigurationDocument Document, EpsxAsset Asset) CreateImageDocument()
    {
        Guid profileId = Guid.NewGuid();
        byte[] content = [137, 80, 78, 71, 13, 10, 26, 10];
        string hash = Convert.ToHexString(SHA256.HashData(content));
        var reference = new AssetReference(profileId, "image.png", "image/png", content.Length, hash);
        Profile profile = ConfigurationDefaults.Create().Profiles[0] with
        {
            Id = profileId,
            Name = "Image",
            ActiveMode = OverlayMode.Image,
            Image = new ImageOverlay(profileId, AnchorMode.ScreenCenter, new PixelPoint(0, 0), 1, true),
        };
        ConfigurationDocument document = ConfigurationDefaults.Create() with
        {
            Profiles = [profile],
            ProfileSets = [new(Guid.NewGuid(), "Images", [profileId], profileId)],
            Assets = [reference],
        };
        return (document, new EpsxAsset(reference, content));
    }
}

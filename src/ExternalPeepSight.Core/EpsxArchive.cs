using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExternalPeepSight.Core;

/// <summary>
/// Defines limits applied while reading an EPSX archive.
/// </summary>
public sealed record EpsxArchiveOptions
{
    /// <summary>
    /// Gets the maximum number of ZIP entries.
    /// </summary>
    public int MaxEntries { get; init; } = 256;

    /// <summary>
    /// Gets the maximum uncompressed size of one ZIP entry.
    /// </summary>
    public long MaxEntryBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum uncompressed size of the complete archive.
    /// </summary>
    public long MaxTotalBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum allowed compression ratio.
    /// </summary>
    public long MaxCompressionRatio { get; init; } = 100;
}

/// <summary>
/// Carries one resource's metadata and bytes for EPSX operations.
/// </summary>
public sealed record EpsxAsset(AssetReference Reference, byte[] Content);

/// <summary>
/// Describes one identifier conflict resolved during a merge.
/// </summary>
public sealed record MergeConflict(
    string Kind,
    Guid OriginalId,
    Guid ResolvedId,
    string Reason);

/// <summary>
/// Contains a merged configuration and the conflicts encountered during import.
/// </summary>
public sealed record ConfigurationMergeResult(
    ConfigurationDocument Document,
    EpsxAsset[] Assets,
    MergeConflict[] Conflicts);

/// <summary>
/// Reads, writes, and merges secure ExternalPeepSight exchange packages.
/// </summary>
public static class EpsxArchive
{
    private const string ManifestEntryName = "manifest.json";
    private const string ProfilesEntryName = "profiles.json";

    /// <summary>
    /// Exports portable profiles, profile sets, and their resources to an EPSX stream.
    /// </summary>
    /// <param name="destination">The writable destination stream.</param>
    /// <param name="document">The configuration to export.</param>
    /// <param name="assets">The resources referenced by the document.</param>
    /// <param name="options">Optional archive limits.</param>
    public static void Export(
        Stream destination,
        ConfigurationDocument document,
        IReadOnlyCollection<EpsxAsset> assets,
        EpsxArchiveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        options ??= new EpsxArchiveOptions();
        ConfigurationValidator.Validate(document).ThrowIfInvalid();
        ValidateArchiveOptions(options);

        Dictionary<Guid, EpsxAsset> assetMap = BuildAssetMap(assets, options);
        ValidateDocumentAssets(document, assetMap);
        var manifest = new EpsxManifest
        {
            SchemaVersion = ConfigurationJson.CurrentSchemaVersion,
            Assets = assetMap.Values.Select(asset => new EpsxManifestAsset
            {
                Id = asset.Reference.Id,
                FileName = asset.Reference.FileName,
                MediaType = asset.Reference.MediaType,
                SizeBytes = asset.Reference.SizeBytes,
                Sha256 = asset.Reference.Sha256,
                Path = GetAssetPath(asset.Reference),
            }).ToArray(),
        };

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteTextEntry(archive, ManifestEntryName, JsonSerializer.Serialize(manifest, CreateJsonOptions(true)));
        var portable = new PortableConfigurationDocument
        {
            SchemaVersion = ConfigurationJson.CurrentSchemaVersion,
            Profiles = document.Profiles,
            ProfileSets = document.ProfileSets,
            Assets = document.Assets,
        };
        WriteTextEntry(
            archive,
            ProfilesEntryName,
            ConfigurationJson.SerializeCanonical(portable, indented: true));
        foreach (EpsxAsset asset in assetMap.Values)
        {
            ZipArchiveEntry entry = archive.CreateEntry(GetAssetPath(asset.Reference), CompressionLevel.Optimal);
            using Stream output = entry.Open();
            output.Write(asset.Content);
        }
    }

    /// <summary>
    /// Imports and merges an EPSX package without mutating the existing document.
    /// </summary>
    /// <param name="source">The readable EPSX stream.</param>
    /// <param name="existing">The current configuration.</param>
    /// <param name="options">Optional archive limits.</param>
    /// <returns>The merged document, imported assets, and conflict report.</returns>
    /// <exception cref="ConfigurationFormatException">The archive structure is invalid.</exception>
    /// <exception cref="ConfigurationValidationException">The imported document is invalid.</exception>
    public static ConfigurationMergeResult Import(
        Stream source,
        ConfigurationDocument existing,
        EpsxArchiveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(existing);
        options ??= new EpsxArchiveOptions();
        ValidateArchiveOptions(options);
        ConfigurationValidator.Validate(existing).ThrowIfInvalid();

        var entries = ReadEntries(source, options);
        EpsxManifest manifest = DeserializeManifest(entries);
        if (!entries.TryGetValue(ProfilesEntryName, out byte[]? documentBytes))
        {
            throw new ConfigurationFormatException("EPSX package is missing profiles.json.");
        }

        PortableConfigurationDocument portable = DeserializePortableDocument(documentBytes);
        ProfileSet[] importedProfileSets = portable.ProfileSets
            ?? throw new ConfigurationFormatException("EPSX profiles.json requires profileSets.");
        ConfigurationDocument imported = existing with
        {
            SchemaVersion = portable.SchemaVersion,
            Profiles = portable.Profiles
                ?? throw new ConfigurationFormatException("EPSX profiles.json requires profiles."),
            ProfileSets = importedProfileSets,
            ActiveProfileSetId = importedProfileSets.FirstOrDefault()?.Id ?? Guid.Empty,
            Assets = portable.Assets
                ?? throw new ConfigurationFormatException("EPSX profiles.json requires assets."),
        };
        ConfigurationValidator.Validate(imported).ThrowIfInvalid();
        var assets = ReadAssets(manifest, imported, entries, options);
        ConfigurationMergeResult result = ConfigurationMerger.Merge(existing, imported, assets);
        ConfigurationValidator.Validate(result.Document).ThrowIfInvalid();
        return result;
    }

    private static Dictionary<string, byte[]> ReadEntries(Stream source, EpsxArchiveOptions options)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > options.MaxEntries)
            {
                throw new ConfigurationFormatException("EPSX entry count exceeds the configured limit.");
            }

            long totalBytes = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ValidateEntryPath(entry.FullName);
                if (!entries.TryAdd(entry.FullName, []))
                {
                    throw new ConfigurationFormatException("EPSX contains duplicate entry names.");
                }

                if (entry.Length > options.MaxEntryBytes)
                {
                    throw new ConfigurationFormatException("EPSX entry exceeds the configured size limit.");
                }

                if (entry.CompressedLength > 0 &&
                    entry.Length / (double)entry.CompressedLength > options.MaxCompressionRatio)
                {
                    throw new ConfigurationFormatException("EPSX entry compression ratio exceeds the configured limit.");
                }

                using Stream input = entry.Open();
                using var buffer = new MemoryStream();
                CopyWithLimit(input, buffer, options.MaxEntryBytes, ref totalBytes, options.MaxTotalBytes);
                entries[entry.FullName] = buffer.ToArray();
            }

            return entries;
        }
        catch (InvalidDataException exception)
        {
            throw new ConfigurationFormatException("EPSX ZIP data is invalid.", exception);
        }
    }

    private static EpsxManifest DeserializeManifest(Dictionary<string, byte[]> entries)
    {
        if (!entries.TryGetValue(ManifestEntryName, out byte[]? manifestBytes))
        {
            throw new ConfigurationFormatException("EPSX package is missing manifest.json.");
        }

        try
        {
            EpsxManifest manifest = JsonSerializer.Deserialize<EpsxManifest>(
                    manifestBytes,
                    CreateJsonOptions(false))
                ?? throw new ConfigurationFormatException("EPSX manifest is empty.");
            if (manifest.SchemaVersion != ConfigurationJson.CurrentSchemaVersion ||
                manifest.Assets is null)
            {
                throw new ConfigurationFormatException("EPSX manifest schema is unsupported.");
            }

            return manifest;
        }
        catch (JsonException exception)
        {
            throw new ConfigurationFormatException("EPSX manifest is invalid JSON.", exception);
        }
    }

    private static PortableConfigurationDocument DeserializePortableDocument(byte[] documentBytes)
    {
        try
        {
            PortableConfigurationDocument document =
                ConfigurationJson.DeserializeCanonical<PortableConfigurationDocument>(documentBytes)
                ?? throw new ConfigurationFormatException("EPSX profiles.json is empty.");
            if (document.SchemaVersion != ConfigurationJson.CurrentSchemaVersion)
            {
                throw new ConfigurationFormatException("EPSX profiles.json schema is unsupported.");
            }

            if (document.Profiles is null || document.ProfileSets is null || document.Assets is null)
            {
                throw new ConfigurationFormatException("EPSX profiles.json requires profiles, profileSets, and assets.");
            }

            return document;
        }
        catch (ConfigurationFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ConfigurationFormatException("EPSX profiles.json is invalid JSON.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ConfigurationFormatException("EPSX profiles.json contains an unsupported value.", exception);
        }
    }

    private static EpsxAsset[] ReadAssets(
        EpsxManifest manifest,
        ConfigurationDocument imported,
        Dictionary<string, byte[]> entries,
        EpsxArchiveOptions options)
    {
        if (manifest.Assets.Length > options.MaxEntries)
        {
            throw new ConfigurationFormatException("EPSX asset count exceeds the configured limit.");
        }

        var documentAssetIds = imported.Assets.Select(asset => asset.Id).ToHashSet();
        var assets = new List<EpsxAsset>(manifest.Assets.Length);
        var ids = new HashSet<Guid>();
        foreach (EpsxManifestAsset item in manifest.Assets)
        {
            if (item.Id == Guid.Empty || !ids.Add(item.Id) || !documentAssetIds.Contains(item.Id))
            {
                throw new ConfigurationFormatException("EPSX manifest contains an invalid or unreferenced asset.");
            }

            if (!entries.TryGetValue(item.Path, out byte[]? content))
            {
                throw new ConfigurationFormatException("EPSX manifest references a missing asset entry.");
            }

            if (content.LongLength != item.SizeBytes ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(content)), item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationFormatException("EPSX asset hash or size does not match its manifest.");
            }

            assets.Add(new EpsxAsset(
                new AssetReference(item.Id, item.FileName, item.MediaType, item.SizeBytes, item.Sha256),
                content));
        }

        if (assets.Count != imported.Assets.Length)
        {
            throw new ConfigurationFormatException("EPSX asset metadata does not match profiles.json.");
        }

        return assets.ToArray();
    }

    private static Dictionary<Guid, EpsxAsset> BuildAssetMap(
        IReadOnlyCollection<EpsxAsset> assets,
        EpsxArchiveOptions options)
    {
        var result = new Dictionary<Guid, EpsxAsset>();
        if (assets.Count > options.MaxEntries)
        {
            throw new ConfigurationFormatException("Asset count exceeds the configured limit.");
        }

        foreach (EpsxAsset asset in assets)
        {
            if (asset.Content is null ||
                asset.Reference.SizeBytes != asset.Content.LongLength ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(asset.Content)),
                    asset.Reference.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationFormatException("Asset metadata does not match asset content.");
            }

            if (!result.TryAdd(asset.Reference.Id, asset))
            {
                throw new ConfigurationFormatException("Asset identifiers must be unique.");
            }
        }

        return result;
    }

    private static void ValidateDocumentAssets(
        ConfigurationDocument document,
        Dictionary<Guid, EpsxAsset> assets)
    {
        foreach (AssetReference reference in document.Assets)
        {
            if (!assets.ContainsKey(reference.Id))
            {
                throw new ConfigurationFormatException("Every document asset must be present in the EPSX export.");
            }
        }
    }

    private static string GetAssetPath(AssetReference reference) =>
        $"assets/{reference.Id:N}{GetExtension(reference.MediaType)}";

    private static string GetExtension(string mediaType) =>
        mediaType switch
        {
            "image/png" => ".png",
            "image/svg+xml" => ".svg",
            _ => throw new ConfigurationFormatException("Unsupported EPSX asset media type."),
        };

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void ValidateEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\\') ||
            path.StartsWith('/') ||
            path.Contains(':') ||
            path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ConfigurationFormatException("EPSX contains an unsafe ZIP path.");
        }
    }

    private static void CopyWithLimit(
        Stream input,
        Stream output,
        long entryLimit,
        ref long totalBytes,
        long totalLimit)
    {
        byte[] buffer = new byte[81920];
        int read;
        long entryBytes = 0;
        while ((read = input.Read(buffer)) > 0)
        {
            entryBytes += read;
            totalBytes += read;
            if (entryBytes > entryLimit || totalBytes > totalLimit)
            {
                throw new ConfigurationFormatException("EPSX uncompressed size exceeds the configured limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static void ValidateArchiveOptions(EpsxArchiveOptions options)
    {
        if (options.MaxEntries < 1 ||
            options.MaxEntryBytes < 1 ||
            options.MaxTotalBytes < options.MaxEntryBytes ||
            options.MaxCompressionRatio < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "EPSX limits are inconsistent.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented,
    };

    private sealed class EpsxManifest
    {
        public int SchemaVersion { get; init; }
        public EpsxManifestAsset[] Assets { get; init; } = [];
    }

    private sealed class EpsxManifestAsset
    {
        public Guid Id { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string MediaType { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
    }

    private sealed record PortableConfigurationDocument
    {
        public int SchemaVersion { get; init; }
        public Profile[]? Profiles { get; init; }
        public ProfileSet[]? ProfileSets { get; init; }
        public AssetReference[]? Assets { get; init; }
    }
}

/// <summary>
/// Merges imported configuration data using stable identifier and content rules.
/// </summary>
public static class ConfigurationMerger
{
    /// <summary>
    /// Merges an imported document into an existing document.
    /// </summary>
    /// <param name="existing">The current document.</param>
    /// <param name="imported">The document to merge.</param>
    /// <param name="assets">The imported resources.</param>
    /// <returns>A new document and conflict report.</returns>
    public static ConfigurationMergeResult Merge(
        ConfigurationDocument existing,
        ConfigurationDocument imported,
        IReadOnlyCollection<EpsxAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(imported);
        ArgumentNullException.ThrowIfNull(assets);
        ConfigurationValidator.Validate(existing).ThrowIfInvalid();
        ConfigurationValidator.Validate(imported).ThrowIfInvalid();

        var conflicts = new List<MergeConflict>();
        var existingAssets = existing.Assets.ToDictionary(asset => asset.Id);
        var assetIdMap = new Dictionary<Guid, Guid>();
        var mergedAssets = existing.Assets.ToList();
        foreach (EpsxAsset importedAsset in assets)
        {
            if (!existingAssets.TryGetValue(importedAsset.Reference.Id, out AssetReference? current))
            {
                assetIdMap[importedAsset.Reference.Id] = importedAsset.Reference.Id;
                mergedAssets.Add(importedAsset.Reference);
                continue;
            }

            if (string.Equals(current.Sha256, importedAsset.Reference.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                assetIdMap[importedAsset.Reference.Id] = current.Id;
                continue;
            }

            Guid newId = Guid.NewGuid();
            assetIdMap[importedAsset.Reference.Id] = newId;
            mergedAssets.Add(importedAsset.Reference with { Id = newId });
            conflicts.Add(new MergeConflict("asset", importedAsset.Reference.Id, newId, "Asset identifier matched different content."));
        }

        var existingProfiles = existing.Profiles.ToDictionary(profile => profile.Id);
        var profileIdMap = new Dictionary<Guid, Guid>();
        var mergedProfiles = existing.Profiles.ToList();
        foreach (Profile importedProfile in imported.Profiles)
        {
            Guid resolvedId = importedProfile.Id;
            if (existingProfiles.TryGetValue(importedProfile.Id, out Profile? currentProfile))
            {
                Profile normalized = RemapProfile(importedProfile, importedProfile.Id, assetIdMap);
                if (ConfigurationJson.SerializeCanonical(currentProfile) == ConfigurationJson.SerializeCanonical(normalized))
                {
                    profileIdMap[importedProfile.Id] = importedProfile.Id;
                    continue;
                }

                resolvedId = Guid.NewGuid();
                conflicts.Add(new MergeConflict("profile", importedProfile.Id, resolvedId, "Profile identifier matched different content."));
            }

            profileIdMap[importedProfile.Id] = resolvedId;
            mergedProfiles.Add(RemapProfile(importedProfile, resolvedId, assetIdMap));
        }

        var existingSets = existing.ProfileSets.ToDictionary(profileSet => profileSet.Id);
        var mergedSets = existing.ProfileSets.ToList();
        foreach (ProfileSet importedSet in imported.ProfileSets)
        {
            Guid resolvedId = importedSet.Id;
            ProfileSet normalized = RemapProfileSet(importedSet, importedSet.Id, profileIdMap);
            if (existingSets.TryGetValue(importedSet.Id, out ProfileSet? currentSet))
            {
                if (ConfigurationJson.SerializeCanonical(currentSet) == ConfigurationJson.SerializeCanonical(normalized))
                {
                    continue;
                }

                resolvedId = Guid.NewGuid();
                normalized = normalized with { Id = resolvedId };
                conflicts.Add(new MergeConflict("profileSet", importedSet.Id, resolvedId, "Profile set identifier matched different content."));
            }

            mergedSets.Add(normalized);
        }

        ConfigurationDocument merged = existing with
        {
            Profiles = mergedProfiles.ToArray(),
            ProfileSets = mergedSets.ToArray(),
            Assets = mergedAssets.ToArray(),
        };

        var mergedAssetContent = new List<EpsxAsset>();
        foreach (EpsxAsset asset in assets)
        {
            Guid resolvedId = assetIdMap[asset.Reference.Id];
            if (resolvedId != asset.Reference.Id)
            {
                mergedAssetContent.Add(asset with { Reference = asset.Reference with { Id = resolvedId } });
            }
            else if (!existingAssets.ContainsKey(resolvedId))
            {
                mergedAssetContent.Add(asset);
            }
        }

        return new ConfigurationMergeResult(merged, mergedAssetContent.ToArray(), conflicts.ToArray());
    }

    private static Profile RemapProfile(
        Profile profile,
        Guid id,
        Dictionary<Guid, Guid> assetIdMap) =>
        profile with
        {
            Id = id,
            Image = profile.Image with
            {
                AssetId = profile.Image.AssetId.HasValue &&
                    assetIdMap.TryGetValue(profile.Image.AssetId.Value, out Guid mappedId)
                    ? mappedId
                    : profile.Image.AssetId,
            },
        };

    private static ProfileSet RemapProfileSet(
        ProfileSet profileSet,
        Guid id,
        Dictionary<Guid, Guid> profileIdMap) =>
        profileSet with
        {
            Id = id,
            ProfileIds = profileSet.ProfileIds.Select(profileId =>
                profileIdMap.TryGetValue(profileId, out Guid mappedId) ? mappedId : profileId).ToArray(),
            SelectedProfileId = profileSet.SelectedProfileId.HasValue &&
                profileIdMap.TryGetValue(profileSet.SelectedProfileId.Value, out Guid selectedId)
                ? selectedId
                : profileSet.SelectedProfileId,
        };
}

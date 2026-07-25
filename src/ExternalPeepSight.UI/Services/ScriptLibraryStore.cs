using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExternalPeepSight.Core;

namespace ExternalPeepSight.UI.Services;

internal sealed record ScriptLibraryEntry(
    Guid Id,
    string Name,
    ScriptScope Scope,
    string Source);

internal interface IScriptLibraryStore
{
    public IReadOnlyList<ScriptLibraryEntry> Load();

    public void Save(IReadOnlyCollection<ScriptLibraryEntry> scripts);
}

internal sealed class ScriptLibraryStore : IScriptLibraryStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumScripts = 256;
    private const int MaximumNameLength = 128;
    private const int MaximumSourceLength = 256 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string filePath;

    public ScriptLibraryStore(string filePath)
    {
        this.filePath = Path.GetFullPath(filePath);
    }

    public IReadOnlyList<ScriptLibraryEntry> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            ScriptLibraryDocument document =
                JsonSerializer.Deserialize<ScriptLibraryDocument>(
                    File.ReadAllText(filePath, Encoding.UTF8),
                    SerializerOptions)
                ?? throw new JsonException("Script library JSON produced no document.");
            Validate(document);
            return document.Scripts;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ApplicationLog.Write("ui.script_library_load_failed", exception);
            return [];
        }
    }

    public void Save(IReadOnlyCollection<ScriptLibraryEntry> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var document = new ScriptLibraryDocument(CurrentSchemaVersion, scripts.ToArray());
        Validate(document);

        string? directory = Path.GetDirectoryName(filePath);
        if (directory is null)
        {
            throw new InvalidOperationException("Script library path has no directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, SerializerOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void Validate(ScriptLibraryDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Script library schema version is not supported.");
        }
        if (document.Scripts.Length > MaximumScripts)
        {
            throw new InvalidDataException("Script library contains too many scripts.");
        }

        var identifiers = new HashSet<Guid>();
        foreach (ScriptLibraryEntry script in document.Scripts)
        {
            if (script.Id == Guid.Empty || !identifiers.Add(script.Id))
            {
                throw new InvalidDataException("Script library identifiers must be unique and non-empty.");
            }
            if (string.IsNullOrWhiteSpace(script.Name) || script.Name.Length > MaximumNameLength)
            {
                throw new InvalidDataException("Script library names are invalid.");
            }
            if (string.IsNullOrWhiteSpace(script.Source) || script.Source.Length > MaximumSourceLength)
            {
                throw new InvalidDataException("Script library source is invalid.");
            }
            if (!Enum.IsDefined(script.Scope))
            {
                throw new InvalidDataException("Script library scope is invalid.");
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record ScriptLibraryDocument(
        int SchemaVersion,
        ScriptLibraryEntry[] Scripts);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ExternalPeepSight.Core;

/// <summary>
/// Reports invalid JSON, unsupported schemas, or failed migrations.
/// </summary>
public sealed class ConfigurationFormatException : Exception
{
    /// <summary>
    /// Initializes a configuration format exception.
    /// </summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying parser failure, when available.</param>
    public ConfigurationFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Serializes, migrates, and validates versioned configuration JSON.
/// </summary>
public static class ConfigurationJson
{
    /// <summary>
    /// Gets the newest schema version supported by this build.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(false);
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(true);

    /// <summary>
    /// Serializes a validated configuration document.
    /// </summary>
    /// <param name="document">The document to serialize.</param>
    /// <param name="indented">Whether to emit indented JSON.</param>
    /// <returns>The versioned JSON document.</returns>
    /// <exception cref="ConfigurationValidationException">The document is invalid.</exception>
    public static string Serialize(ConfigurationDocument document, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ConfigurationValidator.Validate(document).ThrowIfInvalid();
        return JsonSerializer.Serialize(document, indented ? IndentedOptions : CompactOptions);
    }

    /// <summary>
    /// Deserializes, migrates, and validates a configuration document.
    /// </summary>
    /// <param name="json">The serialized configuration.</param>
    /// <returns>The current-schema configuration document.</returns>
    /// <exception cref="ConfigurationFormatException">The JSON or schema version is invalid.</exception>
    /// <exception cref="ConfigurationValidationException">The migrated document is invalid.</exception>
    public static ConfigurationDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            JsonObject root = JsonNode.Parse(
                    json,
                    new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 64,
                    }) as JsonObject
                ?? throw new ConfigurationFormatException("Configuration root must be a JSON object.");

            int version = GetSchemaVersion(root);
            JsonObject migrated = version switch
            {
                0 => MigrateVersionZero(root),
                CurrentSchemaVersion => root,
                > CurrentSchemaVersion => throw new ConfigurationFormatException(
                    $"Configuration schema version {version} is newer than supported version {CurrentSchemaVersion}."),
                _ => throw new ConfigurationFormatException($"Configuration schema version {version} is invalid."),
            };

            ConfigurationDocument document =
                migrated.Deserialize<ConfigurationDocument>(CompactOptions)
                ?? throw new ConfigurationFormatException("Configuration JSON produced no document.");
            ConfigurationValidator.Validate(document).ThrowIfInvalid();
            return document;
        }
        catch (ConfigurationFormatException)
        {
            throw;
        }
        catch (ConfigurationValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ConfigurationFormatException("Configuration JSON is invalid.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ConfigurationFormatException("Configuration JSON contains an unsupported value.", exception);
        }
    }

    internal static string SerializeCanonical<T>(T value) =>
        JsonSerializer.Serialize(value, CompactOptions);

    private static int GetSchemaVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue("schemaVersion", out JsonNode? versionNode) || versionNode is null)
        {
            return 0;
        }

        if (versionNode is JsonValue value && value.TryGetValue(out int version))
        {
            return version;
        }

        throw new ConfigurationFormatException("Configuration schema version must be an integer.");
    }

    private static JsonObject MigrateVersionZero(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        migrated["schemaVersion"] = CurrentSchemaVersion;
        migrated["profiles"] ??= new JsonArray();
        migrated["profileSets"] ??= new JsonArray();
        migrated["assets"] ??= new JsonArray();
        return migrated;
    }

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = indented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            MaxDepth = 64,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new RgbaColorJsonConverter());
        return options;
    }

    private sealed class RgbaColorJsonConverter : JsonConverter<RgbaColor>
    {
        public override RgbaColor Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Color must be a hexadecimal string.");
            }

            try
            {
                return RgbaColor.Parse(reader.GetString()!);
            }
            catch (FormatException exception)
            {
                throw new JsonException("Color must use #RRGGBB or #RRGGBBAA format.", exception);
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            RgbaColor value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

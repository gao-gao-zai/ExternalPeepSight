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
    public const int CurrentSchemaVersion = 9;

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
                0 => MigrateVersionEight(MigrateVersionSeven(MigrateVersionSix(
                    MigrateVersionFive(MigrateVersionFour(
                        MigrateVersionThree(MigrateVersionTwo(MigrateVersionOne(MigrateVersionZero(root))))))))),
                1 => MigrateVersionEight(MigrateVersionSeven(MigrateVersionSix(
                    MigrateVersionFive(MigrateVersionFour(
                        MigrateVersionThree(MigrateVersionTwo(MigrateVersionOne(root)))))))),
                2 => MigrateVersionEight(MigrateVersionSeven(MigrateVersionSix(
                    MigrateVersionFive(MigrateVersionFour(MigrateVersionThree(MigrateVersionTwo(root))))))),
                3 => MigrateVersionEight(MigrateVersionSeven(MigrateVersionSix(
                    MigrateVersionFive(MigrateVersionFour(MigrateVersionThree(root)))))),
                4 => MigrateVersionEight(MigrateVersionSeven(
                    MigrateVersionSix(MigrateVersionFive(MigrateVersionFour(root))))),
                5 => MigrateVersionEight(MigrateVersionSeven(MigrateVersionSix(MigrateVersionFive(root)))),
                6 => MigrateVersionEight(MigrateVersionSeven(MigrateVersionSix(root))),
                7 => MigrateVersionEight(MigrateVersionSeven(root)),
                8 => MigrateVersionEight(root),
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

    internal static string SerializeCanonical<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? IndentedOptions : CompactOptions);

    internal static T? DeserializeCanonical<T>(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<T>(json, CompactOptions);

    internal static T? DeserializeCanonical<T>(JsonNode json) =>
        json.Deserialize<T>(CompactOptions);

    internal static int GetSchemaVersion(JsonObject root)
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
        migrated["schemaVersion"] = 1;
        migrated["profiles"] ??= new JsonArray();
        migrated["profileSets"] ??= new JsonArray();
        migrated["assets"] ??= new JsonArray();
        migrated["switches"] ??= JsonSerializer.SerializeToNode(
            ConfigurationDefaults.CreateSwitches(),
            CompactOptions);
        return migrated;
    }

    private static JsonObject MigrateVersionOne(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        JsonNode switches = migrated["switches"]?.DeepClone()
            ?? JsonSerializer.SerializeToNode(ConfigurationDefaults.CreateSwitches(), CompactOptions)
            ?? throw new ConfigurationFormatException("Default switch configuration could not be serialized.");
        if (migrated["profiles"] is JsonArray profiles)
        {
            foreach (JsonNode? item in profiles)
            {
                if (item is JsonObject profile)
                {
                    profile["switches"] ??= switches.DeepClone();
                }
            }
        }

        migrated.Remove("switches");
        migrated["schemaVersion"] = 2;
        return migrated;
    }

    private static JsonObject MigrateVersionTwo(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        if (migrated["profiles"] is JsonArray profiles)
        {
            foreach (JsonNode? profileNode in profiles)
            {
                if (profileNode is not JsonObject profile ||
                    profile["switches"] is not JsonObject switches)
                {
                    continue;
                }

                MigrateBindingKeys(switches["switchA"] as JsonObject);
                MigrateBindingKeys(switches["switchB"] as JsonObject);
            }
        }

        migrated["schemaVersion"] = 3;
        return migrated;
    }

    private static JsonObject MigrateVersionFour(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        if (migrated["profiles"] is JsonArray profiles)
        {
            foreach (JsonNode? profileNode in profiles)
            {
                if (profileNode is not JsonObject profile ||
                    profile["crosshair"] is not JsonObject crosshair ||
                    crosshair["arms"] is not JsonArray arms)
                {
                    continue;
                }

                for (int index = 0; index < arms.Count; index++)
                {
                    if (arms[index] is not JsonObject arm ||
                        arm["gapOffsetPx"] is null)
                    {
                        continue;
                    }

                    ArmDefaults defaults = CrosshairArmDefaults.Get(index);
                    arm["gapPx"] = defaults.GapPx + arm["gapOffsetPx"]!.GetValue<int>();
                    arm["lengthPx"] = defaults.LengthPx + arm["lengthOffsetPx"]!.GetValue<int>();
                    arm["widthPx"] = defaults.WidthPx + arm["widthOffsetPx"]!.GetValue<int>();
                    arm.Remove("gapOffsetPx");
                    arm.Remove("lengthOffsetPx");
                    arm.Remove("widthOffsetPx");
                }
            }
        }

        migrated["schemaVersion"] = 5;
        return migrated;
    }

    private static JsonObject MigrateVersionFive(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        migrated["inputBackend"] ??= "rawInput";
        migrated["schemaVersion"] = 6;
        return migrated;
    }

    private static JsonObject MigrateVersionThree(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        if (migrated["profiles"] is JsonArray profiles)
        {
            foreach (JsonNode? profileNode in profiles)
            {
                if (profileNode is not JsonObject profile ||
                    profile["crosshair"] is not JsonObject crosshair ||
                    crosshair["arms"] is not JsonArray arms)
                {
                    continue;
                }

                for (int index = 0; index < arms.Count; index++)
                {
                    if (arms[index] is not JsonObject arm)
                    {
                        continue;
                    }

                    if (arm["orbitAngleOffsetDeg"] is not null)
                    {
                        continue;
                    }

                    ArmDefaults defaults = CrosshairArmDefaults.Get(index);
                    double angleDeg = arm["angleDeg"]?.GetValue<double>()
                        ?? throw new ConfigurationFormatException($"Profile arm {index} is missing angleDeg.");
                    int gapPx = arm["gapPx"]?.GetValue<int>()
                        ?? throw new ConfigurationFormatException($"Profile arm {index} is missing gapPx.");
                    int lengthPx = arm["lengthPx"]?.GetValue<int>()
                        ?? throw new ConfigurationFormatException($"Profile arm {index} is missing lengthPx.");
                    int widthPx = arm["widthPx"]?.GetValue<int>()
                        ?? throw new ConfigurationFormatException($"Profile arm {index} is missing widthPx.");

                    arm["orbitAngleOffsetDeg"] = angleDeg - defaults.OrbitAngleDeg;
                    arm["rotationAngleOffsetDeg"] = 0;
                    arm["gapOffsetPx"] = gapPx - defaults.GapPx;
                    arm["lengthOffsetPx"] = lengthPx - defaults.LengthPx;
                    arm["widthOffsetPx"] = widthPx - defaults.WidthPx;
                    arm.Remove("angleDeg");
                    arm.Remove("gapPx");
                    arm.Remove("lengthPx");
                    arm.Remove("widthPx");
                }
            }
        }

        migrated["schemaVersion"] = 4;
        return migrated;
    }

    private static JsonObject MigrateVersionSix(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        migrated["activeProfileSetId"] ??= FindFirstProfileSetId(migrated);

        if (migrated["profiles"] is JsonArray profiles)
        {
            foreach (JsonNode? profileNode in profiles)
            {
                if (profileNode is not JsonObject profile)
                {
                    continue;
                }

                profile["controlMode"] ??= "basic";
                profile["script"] ??= null;
            }
        }

        if (migrated["profileSets"] is JsonArray profileSets)
        {
            foreach (JsonNode? profileSetNode in profileSets)
            {
                if (profileSetNode is JsonObject profileSet)
                {
                    profileSet["script"] ??= null;
                }
            }
        }

        migrated["globalScript"] ??= null;
        migrated["schemaVersion"] = 7;
        return migrated;
    }

    private static JsonObject MigrateVersionSeven(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        migrated["activeProfileSetId"] ??= FindFirstProfileSetId(migrated);
        migrated["globalScript"] ??= null;

        if (migrated["profiles"] is JsonArray profiles)
        {
            foreach (JsonNode? profileNode in profiles)
            {
                if (profileNode is JsonObject profile)
                {
                    AddScriptUi(profile["script"]);
                }
            }
        }

        if (migrated["profileSets"] is JsonArray profileSets)
        {
            foreach (JsonNode? profileSetNode in profileSets)
            {
                if (profileSetNode is JsonObject profileSet)
                {
                    AddScriptUi(profileSet["script"]);
                }
            }
        }

        AddScriptUi(migrated["globalScript"]);
        migrated["schemaVersion"] = 8;
        return migrated;
    }

    private static JsonObject MigrateVersionEight(JsonObject root)
    {
        var migrated = (JsonObject)root.DeepClone();
        migrated["globalScripts"] = MigrateScriptStack(migrated["globalScript"], "Global");
        migrated.Remove("globalScript");

        if (migrated["profiles"] is JsonArray profiles)
        {
            foreach (JsonNode? profileNode in profiles)
            {
                if (profileNode is not JsonObject profile)
                {
                    continue;
                }

                string fallbackName = profile["name"]?.GetValue<string>() ?? "Script";
                profile["scripts"] = MigrateScriptStack(profile["script"], fallbackName);
                profile.Remove("script");
            }
        }

        if (migrated["profileSets"] is JsonArray profileSets)
        {
            foreach (JsonNode? profileSetNode in profileSets)
            {
                if (profileSetNode is not JsonObject profileSet)
                {
                    continue;
                }

                string fallbackName = profileSet["name"]?.GetValue<string>() ?? "Script";
                profileSet["scripts"] = MigrateScriptStack(profileSet["script"], fallbackName);
                profileSet.Remove("script");
            }
        }

        migrated["schemaVersion"] = CurrentSchemaVersion;
        return migrated;
    }

    internal static JsonObject MigratePortableVersionEight(JsonObject root)
    {
        JsonObject migrated = MigrateVersionEight(root);
        migrated.Remove("globalScripts");
        return migrated;
    }

    private static JsonArray MigrateScriptStack(JsonNode? scriptNode, string fallbackName)
    {
        var result = new JsonArray();
        if (scriptNode is not JsonObject script)
        {
            return result;
        }

        var migrated = (JsonObject)script.DeepClone();
        migrated["id"] = CreateMigratedScriptId(migrated, fallbackName);
        migrated["name"] = fallbackName;
        result.Add(migrated);
        return result;
    }

    private static Guid CreateMigratedScriptId(JsonObject script, string fallbackName)
    {
        string identity =
            $"{fallbackName}\n{script["sourceHash"]?.GetValue<string>()}\n{script["source"]?.GetValue<string>()}";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        byte[] guidBytes = hash[..16];
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static void AddScriptUi(JsonNode? scriptNode)
    {
        if (scriptNode is JsonObject script)
        {
            script["ui"] ??= null;
        }
    }

    private static JsonNode FindFirstProfileSetId(JsonObject root)
    {
        if (root["profileSets"] is JsonArray profileSets)
        {
            foreach (JsonNode? item in profileSets)
            {
                if (item is JsonObject profileSet &&
                    profileSet["id"] is JsonNode id &&
                    id.GetValueKind() == JsonValueKind.String)
                {
                    return id.DeepClone();
                }
            }
        }

        return JsonValue.Create(Guid.Empty);
    }

    private static void MigrateBindingKeys(JsonObject? binding)
    {
        if (binding is null)
        {
            return;
        }

        foreach (string propertyName in new[] { "toggleKey", "enableKey", "disableKey", "holdKey" })
        {
            if (binding[propertyName] is not JsonObject key ||
                !key.TryGetPropertyValue("scanCode", out JsonNode? scanCode))
            {
                continue;
            }

            key["device"] = "keyboard";
            key["code"] = scanCode?.DeepClone();
            key.Remove("scanCode");
        }
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

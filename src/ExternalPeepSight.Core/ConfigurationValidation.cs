using System.Collections.ObjectModel;
using System.Globalization;

namespace ExternalPeepSight.Core;

/// <summary>
/// Sets resource and collection limits used during configuration validation.
/// </summary>
public sealed record ConfigurationValidationOptions
{
    /// <summary>
    /// Gets the maximum number of profiles.
    /// </summary>
    public int MaxProfiles { get; init; } = 1000;

    /// <summary>
    /// Gets the maximum number of profile sets.
    /// </summary>
    public int MaxProfileSets { get; init; } = 1000;

    /// <summary>
    /// Gets the maximum number of assets.
    /// </summary>
    public int MaxAssets { get; init; } = 1000;

    /// <summary>
    /// Gets the maximum number of monitor identifiers in a selection.
    /// </summary>
    public int MaxMonitorIds { get; init; } = 64;

    /// <summary>
    /// Gets the maximum size of one image resource in bytes.
    /// </summary>
    public long MaxAssetBytes { get; init; } = 10 * 1024 * 1024;
}

/// <summary>
/// Describes one configuration validation failure.
/// </summary>
public sealed record ValidationIssue(string Path, string Message);

/// <summary>
/// Contains all validation failures found in a configuration.
/// </summary>
public sealed class ConfigurationValidationResult
{
    /// <summary>
    /// Initializes a validation result.
    /// </summary>
    /// <param name="issues">The validation failures.</param>
    public ConfigurationValidationResult(IEnumerable<ValidationIssue> issues)
    {
        Issues = new ReadOnlyCollection<ValidationIssue>(issues.ToArray());
    }

    /// <summary>
    /// Gets the validation failures.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>
    /// Gets a value indicating whether validation succeeded.
    /// </summary>
    public bool IsValid => Issues.Count == 0;

    /// <summary>
    /// Throws a format exception when validation failed.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">The document is invalid.</exception>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new ConfigurationValidationException(Issues);
        }
    }
}

/// <summary>
/// Reports one or more configuration validation failures.
/// </summary>
public sealed class ConfigurationValidationException : Exception
{
    /// <summary>
    /// Initializes a validation exception.
    /// </summary>
    /// <param name="issues">The validation failures.</param>
    public ConfigurationValidationException(IReadOnlyList<ValidationIssue> issues)
        : base(string.Join("; ", issues.Select(issue => $"{issue.Path}: {issue.Message}")))
    {
        Issues = issues;
    }

    /// <summary>
    /// Gets the validation failures.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }
}

/// <summary>
/// Validates configuration values and cross-object references.
/// </summary>
public static class ConfigurationValidator
{
    private const int MaxNameLength = 100;
    private const int MaxFontFamilyLength = 100;
    private const int MaxFileNameLength = 255;
    private const int MaxMonitorIdLength = 512;
    private const int MaxScriptSourceLength = 256 * 1024;
    private const int MaxScriptItems = 64;
    private const int MaxScriptUiSections = 16;
    private const int MaxScriptIdentifierLength = 64;
    private const int MaxScriptDisplayNameLength = 100;
    private const int MaxScriptDescriptionLength = 300;
    private const int MaxScriptUnitLength = 32;
    private const int MaxScriptStringValueLength = 4096;

    /// <summary>
    /// Validates a complete configuration document.
    /// </summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="options">Optional resource limits.</param>
    /// <returns>All validation failures, or an empty result when valid.</returns>
    public static ConfigurationValidationResult Validate(
        ConfigurationDocument? document,
        ConfigurationValidationOptions? options = null)
    {
        var issues = new List<ValidationIssue>();
        options ??= new ConfigurationValidationOptions();
        if (document is null)
        {
            issues.Add(new ValidationIssue("$", "Configuration document is required."));
            return new ConfigurationValidationResult(issues);
        }

        if (document.SchemaVersion != ConfigurationJson.CurrentSchemaVersion)
        {
            issues.Add(new ValidationIssue("$.schemaVersion", "Unsupported schema version."));
        }

        ValidateCollectionLimits(document, options, issues);
        ValidateProfiles(document, issues);
        ValidateProfileSets(document, issues);
        ValidateActiveProfileSet(document, issues);
        ValidateAssets(document, options, issues);
        ValidateMonitorSelection(document.MonitorSelection, options, issues);
        if (!Enum.IsDefined(document.InputBackend))
        {
            issues.Add(new ValidationIssue("$.inputBackend", "Input capture backend is invalid."));
        }
        ValidateToasts(document.Toasts, issues);
        ValidateScript(document.GlobalScript, "$.globalScript", issues);
        ValidateActiveInputConflicts(document, issues);
        return new ConfigurationValidationResult(issues);
    }

    private static void ValidateCollectionLimits(
        ConfigurationDocument document,
        ConfigurationValidationOptions options,
        List<ValidationIssue> issues)
    {
        if (document.Profiles is null || document.Profiles.Length > options.MaxProfiles)
        {
            issues.Add(new ValidationIssue("$.profiles", "Profile count exceeds the configured limit."));
        }

        if (document.ProfileSets is null || document.ProfileSets.Length > options.MaxProfileSets)
        {
            issues.Add(new ValidationIssue("$.profileSets", "Profile set count exceeds the configured limit."));
        }

        if (document.Assets is null || document.Assets.Length > options.MaxAssets)
        {
            issues.Add(new ValidationIssue("$.assets", "Asset count exceeds the configured limit."));
        }
    }

    private static void ValidateProfiles(
        ConfigurationDocument document,
        List<ValidationIssue> issues)
    {
        if (document.Profiles is null)
        {
            return;
        }

        var ids = new HashSet<Guid>();
        for (int index = 0; index < document.Profiles.Length; index++)
        {
            Profile? profile = document.Profiles[index];
            string path = $"$.profiles[{index}]";
            if (profile is null)
            {
                issues.Add(new ValidationIssue(path, "Profile is required."));
                continue;
            }

            if (!ids.Add(profile.Id))
            {
                issues.Add(new ValidationIssue($"{path}.id", "Profile identifier must be unique."));
            }

            ValidateName(profile.Name, $"{path}.name", issues);
            ValidateCrosshair(profile.Crosshair, $"{path}.crosshair", issues);
            ValidateImage(profile, $"{path}.image", issues);
            ValidateSwitches(profile.Switches, $"{path}.switches", issues);
            if (!Enum.IsDefined(profile.ControlMode))
            {
                issues.Add(new ValidationIssue($"{path}.controlMode", "Display control mode is invalid."));
            }
            else if (profile.ControlMode == DisplayControlMode.Lua &&
                (profile.Script is null || !profile.Script.Enabled))
            {
                issues.Add(new ValidationIssue(
                    $"{path}.script",
                    "Lua control mode requires an enabled profile script."));
            }

            ValidateScript(profile.Script, $"{path}.script", issues);
        }

        HashSet<Guid> assetIds = document.Assets is null
            ? []
            : document.Assets.Where(asset => asset is not null).Select(asset => asset!.Id).ToHashSet();
        foreach (Profile profile in document.Profiles.Where(profile => profile is not null)!)
        {
            if (profile.ActiveMode == OverlayMode.Image &&
                (!profile.Image.AssetId.HasValue || !assetIds.Contains(profile.Image.AssetId.Value)))
            {
                issues.Add(new ValidationIssue(
                    $"$.profiles[{Array.IndexOf(document.Profiles, profile)}].image.assetId",
                    "Active image mode requires an existing asset."));
            }
        }
    }

    private static void ValidateProfileSets(
        ConfigurationDocument document,
        List<ValidationIssue> issues)
    {
        if (document.ProfileSets is null)
        {
            return;
        }

        var profileIds = document.Profiles?.Where(profile => profile is not null).Select(profile => profile!.Id).ToHashSet() ?? [];
        var setIds = new HashSet<Guid>();
        for (int index = 0; index < document.ProfileSets.Length; index++)
        {
            ProfileSet? set = document.ProfileSets[index];
            string path = $"$.profileSets[{index}]";
            if (set is null)
            {
                issues.Add(new ValidationIssue(path, "Profile set is required."));
                continue;
            }

            if (!setIds.Add(set.Id))
            {
                issues.Add(new ValidationIssue($"{path}.id", "Profile set identifier must be unique."));
            }

            ValidateName(set.Name, $"{path}.name", issues);
            if (set.ProfileIds is null || set.ProfileIds.Length == 0)
            {
                issues.Add(new ValidationIssue($"{path}.profileIds", "A profile set must contain at least one profile."));
            }
            else
            {
                var references = new HashSet<Guid>();
                foreach (Guid profileId in set.ProfileIds)
                {
                    if (!references.Add(profileId))
                    {
                        issues.Add(new ValidationIssue($"{path}.profileIds", "Profile identifiers must be unique within a set."));
                    }
                    else if (!profileIds.Contains(profileId))
                    {
                        issues.Add(new ValidationIssue($"{path}.profileIds", "Profile set references a missing profile."));
                    }
                }
            }

            if (set.SelectedProfileId.HasValue &&
                (set.ProfileIds is null || !set.ProfileIds.Contains(set.SelectedProfileId.Value)))
            {
                issues.Add(new ValidationIssue(
                    $"{path}.selectedProfileId",
                    "Selected profile must belong to the profile set."));
            }

            ValidateScript(set.Script, $"{path}.script", issues);
        }
    }

    private static void ValidateActiveProfileSet(
        ConfigurationDocument document,
        List<ValidationIssue> issues)
    {
        if (document.ProfileSets is null || document.ProfileSets.Length == 0)
        {
            if (document.ActiveProfileSetId != Guid.Empty)
            {
                issues.Add(new ValidationIssue(
                    "$.activeProfileSetId",
                    "Active profile set identifier must be empty when no profile sets exist."));
            }
            return;
        }

        if (document.ActiveProfileSetId == Guid.Empty)
        {
            issues.Add(new ValidationIssue("$.activeProfileSetId", "Active profile set identifier is required."));
            return;
        }

        if (document.ProfileSets.All(set => set is null || set.Id != document.ActiveProfileSetId))
        {
            issues.Add(new ValidationIssue(
                "$.activeProfileSetId",
                "Active profile set identifier must reference an existing profile set."));
        }
    }

    private static void ValidateScript(
        ScriptConfiguration? script,
        string path,
        List<ValidationIssue> issues)
    {
        if (script is null)
        {
            return;
        }

        if (script.ApiVersion is not ("1" or "2"))
        {
            issues.Add(new ValidationIssue($"{path}.apiVersion", "Script API version is not supported."));
        }

        if (string.IsNullOrWhiteSpace(script.Source) || script.Source.Length > MaxScriptSourceLength)
        {
            issues.Add(new ValidationIssue(
                $"{path}.source",
                "Script source must contain between 1 and 262144 characters."));
        }

        if (!IsSha256(script.SourceHash))
        {
            issues.Add(new ValidationIssue(
                $"{path}.sourceHash",
                "Script source SHA-256 must contain 64 hexadecimal characters."));
        }

        if (script.Bindings is null || script.Bindings.Length > MaxScriptItems)
        {
            issues.Add(new ValidationIssue($"{path}.bindings", "Script binding count exceeds the configured limit."));
        }
        else
        {
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var assignedKeys = new Dictionary<KeyIdentity, string>();
            for (int index = 0; index < script.Bindings.Length; index++)
            {
                ScriptBindingSlot? binding = script.Bindings[index];
                string bindingPath = $"{path}.bindings[{index}]";
                if (binding is null)
                {
                    issues.Add(new ValidationIssue(bindingPath, "Script binding is required."));
                    continue;
                }

                ValidateScriptIdentifier(binding.Id, $"{bindingPath}.id", identifiers, issues);
                ValidateScriptDisplayName(binding.DisplayName, $"{bindingPath}.displayName", issues);
                if (!binding.Pressed && !binding.Released)
                {
                    issues.Add(new ValidationIssue(
                        bindingPath,
                        "Script binding must accept pressed, released, or both phases."));
                }

                ValidateKey(binding.Key, $"{bindingPath}.key", issues);
                if (binding.Enabled && binding.Key is { } key)
                {
                    if (assignedKeys.TryGetValue(key, out string? existingPath))
                    {
                        issues.Add(new ValidationIssue(
                            $"{bindingPath}.key",
                            $"Script binding duplicates the key at {existingPath}."));
                    }
                    else
                    {
                        assignedKeys.Add(key, $"{bindingPath}.key");
                    }
                }
            }
        }

        if (script.Settings is null || script.Settings.Length > MaxScriptItems)
        {
            issues.Add(new ValidationIssue($"{path}.settings", "Script setting count exceeds the configured limit."));
            return;
        }

        var settingIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < script.Settings.Length; index++)
        {
            ScriptSetting? setting = script.Settings[index];
            string settingPath = $"{path}.settings[{index}]";
            if (setting is null)
            {
                issues.Add(new ValidationIssue(settingPath, "Script setting is required."));
                continue;
            }

            ValidateScriptIdentifier(setting.Id, $"{settingPath}.id", settingIdentifiers, issues);
            ValidateScriptDisplayName(setting.DisplayName, $"{settingPath}.displayName", issues);
            ValidateScriptSetting(setting, settingPath, issues);
        }

        ValidateScriptUi(script, path, issues);
    }

    private static void ValidateScriptUi(
        ScriptConfiguration script,
        string path,
        List<ValidationIssue> issues)
    {
        if (script.Ui is null)
        {
            return;
        }

        if (script.ApiVersion != "2")
        {
            issues.Add(new ValidationIssue(
                $"{path}.ui",
                "Custom script UI requires script API version 2."));
        }

        if (script.Ui.Sections is null || script.Ui.Sections.Length > MaxScriptUiSections)
        {
            issues.Add(new ValidationIssue(
                $"{path}.ui.sections",
                "Script UI section count exceeds the configured limit."));
            return;
        }

        var sectionIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var referencedSettings = new HashSet<string>(StringComparer.Ordinal);
        var settings = new Dictionary<string, ScriptSetting>(StringComparer.Ordinal);
        foreach (ScriptSetting? setting in script.Settings)
        {
            if (setting is not null && !string.IsNullOrEmpty(setting.Id))
            {
                settings.TryAdd(setting.Id, setting);
            }
        }
        int itemCount = 0;

        for (int sectionIndex = 0; sectionIndex < script.Ui.Sections.Length; sectionIndex++)
        {
            ScriptUiSection? section = script.Ui.Sections[sectionIndex];
            string sectionPath = $"{path}.ui.sections[{sectionIndex}]";
            if (section is null)
            {
                issues.Add(new ValidationIssue(sectionPath, "Script UI section is required."));
                continue;
            }

            ValidateScriptIdentifier(section.Id, $"{sectionPath}.id", sectionIdentifiers, issues);
            ValidateScriptDisplayName(section.DisplayName, $"{sectionPath}.displayName", issues);
            if (section.Description is null || section.Description.Length > MaxScriptDescriptionLength)
            {
                issues.Add(new ValidationIssue(
                    $"{sectionPath}.description",
                    "Script UI section description is too long."));
            }
            if (section.Columns is < 1 or > 2)
            {
                issues.Add(new ValidationIssue(
                    $"{sectionPath}.columns",
                    "Script UI section columns must be one or two."));
            }
            if (section.Items is null || section.Items.Length == 0)
            {
                issues.Add(new ValidationIssue(
                    $"{sectionPath}.items",
                    "Script UI section must contain at least one item."));
                continue;
            }

            itemCount += section.Items.Length;
            if (itemCount > MaxScriptItems)
            {
                issues.Add(new ValidationIssue(
                    $"{sectionPath}.items",
                    "Script UI item count exceeds the configured limit."));
            }

            for (int itemIndex = 0; itemIndex < section.Items.Length; itemIndex++)
            {
                ScriptUiItem? item = section.Items[itemIndex];
                string itemPath = $"{sectionPath}.items[{itemIndex}]";
                if (item is null)
                {
                    issues.Add(new ValidationIssue(itemPath, "Script UI item is required."));
                    continue;
                }

                if (!settings.TryGetValue(item.SettingId, out ScriptSetting? setting))
                {
                    issues.Add(new ValidationIssue(
                        $"{itemPath}.settingId",
                        "Script UI item references an unknown setting."));
                    continue;
                }
                if (!referencedSettings.Add(item.SettingId))
                {
                    issues.Add(new ValidationIssue(
                        $"{itemPath}.settingId",
                        "Script UI setting must be referenced exactly once."));
                }

                ValidateScriptUiItem(item, setting, settings, itemPath, issues);
            }
        }

        if (settings.Keys.Any(identifier => !referencedSettings.Contains(identifier)))
        {
            issues.Add(new ValidationIssue(
                $"{path}.ui.sections",
                "Script UI must reference every declared setting exactly once."));
        }
    }

    private static void ValidateScriptUiItem(
        ScriptUiItem item,
        ScriptSetting setting,
        Dictionary<string, ScriptSetting> settings,
        string path,
        List<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(item.Control))
        {
            issues.Add(new ValidationIssue($"{path}.control", "Script UI control type is invalid."));
            return;
        }
        if (item.Description is null || item.Description.Length > MaxScriptDescriptionLength)
        {
            issues.Add(new ValidationIssue(
                $"{path}.description",
                "Script UI item description is too long."));
        }
        if (item.Unit is null || item.Unit.Length > MaxScriptUnitLength)
        {
            issues.Add(new ValidationIssue($"{path}.unit", "Script UI item unit is too long."));
        }

        bool numeric = setting.Type is ScriptSettingType.Integer or ScriptSettingType.Double;
        bool compatible = item.Control switch
        {
            ScriptUiControlType.Auto => true,
            ScriptUiControlType.Switch or ScriptUiControlType.Checkbox =>
                setting.Type == ScriptSettingType.Boolean,
            ScriptUiControlType.Slider =>
                numeric && setting.Minimum.HasValue && setting.Maximum.HasValue,
            ScriptUiControlType.Number => numeric,
            ScriptUiControlType.Textbox => setting.Type == ScriptSettingType.String,
            ScriptUiControlType.Select => setting.Type == ScriptSettingType.Enum,
            ScriptUiControlType.Segmented =>
                setting.Type == ScriptSettingType.Enum && setting.Options is { Length: <= 8 },
            _ => false,
        };
        if (!compatible)
        {
            issues.Add(new ValidationIssue(
                $"{path}.control",
                "Script UI control is incompatible with the referenced setting."));
        }

        if (item.Step.HasValue &&
            (!double.IsFinite(item.Step.Value) ||
             item.Step.Value <= 0 ||
             !numeric ||
             item.Control is not (
                 ScriptUiControlType.Auto or
                 ScriptUiControlType.Slider or
                 ScriptUiControlType.Number)))
        {
            issues.Add(new ValidationIssue(
                $"{path}.step",
                "Script UI step must be a positive finite value used by a numeric control."));
        }

        if (item.VisibleWhen is not { } condition)
        {
            return;
        }
        if (condition.SettingId == item.SettingId)
        {
            issues.Add(new ValidationIssue(
                $"{path}.visibleWhen.settingId",
                "Script UI item cannot control its own visibility."));
            return;
        }
        if (!settings.TryGetValue(condition.SettingId, out ScriptSetting? conditionSetting))
        {
            issues.Add(new ValidationIssue(
                $"{path}.visibleWhen.settingId",
                "Script UI visibility condition references an unknown setting."));
            return;
        }
        if (!IsValidScriptConditionValue(conditionSetting, condition.EqualsValue))
        {
            issues.Add(new ValidationIssue(
                $"{path}.visibleWhen.equalsValue",
                "Script UI visibility condition value is invalid."));
        }
    }

    private static bool IsValidScriptConditionValue(ScriptSetting setting, string? value)
    {
        if (value is null || value.Length > MaxScriptStringValueLength)
        {
            return false;
        }

        return setting.Type switch
        {
            ScriptSettingType.Boolean => bool.TryParse(value, out _),
            ScriptSettingType.Integer =>
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer) &&
                IsWithinScriptRange(integer, setting),
            ScriptSettingType.Double =>
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
                double.IsFinite(number) &&
                IsWithinScriptRange(number, setting),
            ScriptSettingType.String => true,
            ScriptSettingType.Enum =>
                setting.Options?.Contains(value, StringComparer.Ordinal) == true,
            _ => false,
        };
    }

    private static bool IsWithinScriptRange(double value, ScriptSetting setting) =>
        (!setting.Minimum.HasValue || value >= setting.Minimum.Value) &&
        (!setting.Maximum.HasValue || value <= setting.Maximum.Value);

    private static void ValidateActiveInputConflicts(
        ConfigurationDocument document,
        List<ValidationIssue> issues)
    {
        ProfileSet? activeSet = document.ProfileSets?.FirstOrDefault(set => set?.Id == document.ActiveProfileSetId);
        Profile? activeProfile = activeSet is null
            ? null
            : document.Profiles?.FirstOrDefault(profile => profile?.Id == activeSet.SelectedProfileId);
        var assigned = new Dictionary<KeyIdentity, string>();

        static void Add(
            KeyIdentity? key,
            string path,
            Dictionary<KeyIdentity, string> assigned,
            List<ValidationIssue> issues)
        {
            if (!key.HasValue)
            {
                return;
            }

            if (assigned.TryGetValue(key.Value, out string? existing))
            {
                issues.Add(new ValidationIssue(path, $"Input binding duplicates {existing}."));
            }
            else
            {
                assigned.Add(key.Value, path);
            }
        }

        if (activeProfile is not null)
        {
            AddBasic(activeProfile.Switches.SwitchA, "$.profiles[active].switches.switchA", Add, assigned, issues);
            AddBasic(activeProfile.Switches.SwitchB, "$.profiles[active].switches.switchB", Add, assigned, issues);
        }

        AddScript(document.GlobalScript, "$.globalScript", Add, assigned, issues);
        AddScript(activeSet?.Script, "$.profileSets[active].script", Add, assigned, issues);
        if (activeProfile?.ControlMode == DisplayControlMode.Lua)
        {
            AddScript(activeProfile.Script, "$.profiles[active].script", Add, assigned, issues);
        }
    }

    private static void AddBasic(
        HotkeyBinding binding,
        string path,
        Action<KeyIdentity?, string, Dictionary<KeyIdentity, string>, List<ValidationIssue>> add,
        Dictionary<KeyIdentity, string> assigned,
        List<ValidationIssue> issues)
    {
        switch (binding.Mode)
        {
            case HotkeyActivationMode.Toggle:
                add(binding.ToggleKey, $"{path}.toggleKey", assigned, issues);
                break;
            case HotkeyActivationMode.Independent:
                add(binding.EnableKey, $"{path}.enableKey", assigned, issues);
                add(binding.DisableKey, $"{path}.disableKey", assigned, issues);
                break;
            case HotkeyActivationMode.Hold:
                add(binding.HoldKey, $"{path}.holdKey", assigned, issues);
                break;
        }
    }

    private static void AddScript(
        ScriptConfiguration? script,
        string path,
        Action<KeyIdentity?, string, Dictionary<KeyIdentity, string>, List<ValidationIssue>> add,
        Dictionary<KeyIdentity, string> assigned,
        List<ValidationIssue> issues)
    {
        if (script is null || !script.Enabled || script.Bindings is null)
        {
            return;
        }

        for (int index = 0; index < script.Bindings.Length; index++)
        {
            ScriptBindingSlot? binding = script.Bindings[index];
            if (binding is not null && binding.Enabled)
            {
                add(binding.Key, $"{path}.bindings[{index}].key", assigned, issues);
            }
        }
    }

    private static void ValidateScriptIdentifier(
        string? value,
        string path,
        HashSet<string> identifiers,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxScriptIdentifierLength ||
            !IsStableIdentifier(value))
        {
            issues.Add(new ValidationIssue(
                path,
                "Script identifier must start with a letter and contain only ASCII letters, digits, underscore, or hyphen."));
            return;
        }

        if (!identifiers.Add(value))
        {
            issues.Add(new ValidationIssue(path, "Script identifier must be unique."));
        }
    }

    private static void ValidateScriptDisplayName(
        string? value,
        string path,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxScriptDisplayNameLength)
        {
            issues.Add(new ValidationIssue(path, "Script display name is invalid."));
        }
    }

    private static void ValidateScriptSetting(
        ScriptSetting setting,
        string path,
        List<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(setting.Type))
        {
            issues.Add(new ValidationIssue($"{path}.type", "Script setting type is invalid."));
            return;
        }

        if (setting.Value is null || setting.Value.Length > MaxScriptStringValueLength)
        {
            issues.Add(new ValidationIssue($"{path}.value", "Script setting value is too long."));
            return;
        }

        if (setting.Minimum.HasValue && setting.Maximum.HasValue && setting.Minimum.Value > setting.Maximum.Value)
        {
            issues.Add(new ValidationIssue(path, "Script setting minimum cannot exceed its maximum."));
        }

        switch (setting.Type)
        {
            case ScriptSettingType.Boolean:
                if (!bool.TryParse(setting.Value, out _))
                {
                    issues.Add(new ValidationIssue($"{path}.value", "Boolean setting value is invalid."));
                }
                break;
            case ScriptSettingType.Integer:
                if (!long.TryParse(
                        setting.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long integer))
                {
                    issues.Add(new ValidationIssue($"{path}.value", "Integer setting value is invalid."));
                }
                else
                {
                    ValidateScriptNumericRange(integer, setting, path, issues);
                }
                break;
            case ScriptSettingType.Double:
                if (!double.TryParse(
                        setting.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double number) ||
                    !double.IsFinite(number))
                {
                    issues.Add(new ValidationIssue($"{path}.value", "Double setting value is invalid."));
                }
                else
                {
                    ValidateScriptNumericRange(number, setting, path, issues);
                }
                break;
            case ScriptSettingType.String:
                if (setting.Options is null || setting.Options.Length != 0)
                {
                    issues.Add(new ValidationIssue($"{path}.options", "String settings cannot define enum options."));
                }
                break;
            case ScriptSettingType.Enum:
                if (setting.Options is null ||
                    setting.Options.Length == 0 ||
                    setting.Options.Length > MaxScriptItems ||
                    setting.Options.Any(string.IsNullOrWhiteSpace) ||
                    setting.Options.Distinct(StringComparer.Ordinal).Count() != setting.Options.Length)
                {
                    issues.Add(new ValidationIssue($"{path}.options", "Enum setting options are invalid."));
                }
                else if (!setting.Options.Contains(setting.Value, StringComparer.Ordinal))
                {
                    issues.Add(new ValidationIssue($"{path}.value", "Enum setting value is not one of its options."));
                }
                break;
        }
    }

    private static void ValidateScriptNumericRange(
        double value,
        ScriptSetting setting,
        string path,
        List<ValidationIssue> issues)
    {
        if ((setting.Minimum.HasValue && value < setting.Minimum.Value) ||
            (setting.Maximum.HasValue && value > setting.Maximum.Value))
        {
            issues.Add(new ValidationIssue($"{path}.value", "Script setting value is outside its declared range."));
        }
    }

    private static bool IsStableIdentifier(string value)
    {
        if (!IsAsciiLetter(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiLetter(character) ||
            char.IsAsciiDigit(character) ||
            character is '_' or '-');
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static void ValidateAssets(
        ConfigurationDocument document,
        ConfigurationValidationOptions options,
        List<ValidationIssue> issues)
    {
        if (document.Assets is null)
        {
            return;
        }

        var ids = new HashSet<Guid>();
        for (int index = 0; index < document.Assets.Length; index++)
        {
            AssetReference? asset = document.Assets[index];
            string path = $"$.assets[{index}]";
            if (asset is null)
            {
                issues.Add(new ValidationIssue(path, "Asset metadata is required."));
                continue;
            }

            if (asset.Id == Guid.Empty)
            {
                issues.Add(new ValidationIssue($"{path}.id", "Asset identifier must not be empty."));
            }
            else if (!ids.Add(asset.Id))
            {
                issues.Add(new ValidationIssue($"{path}.id", "Asset identifier must be unique."));
            }

            if (string.IsNullOrWhiteSpace(asset.FileName) ||
                asset.FileName.Length > MaxFileNameLength ||
                Path.GetFileName(asset.FileName) != asset.FileName)
            {
                issues.Add(new ValidationIssue($"{path}.fileName", "Asset file name must be a simple file name."));
            }

            if (asset.MediaType is not ("image/png" or "image/svg+xml"))
            {
                issues.Add(new ValidationIssue($"{path}.mediaType", "Only PNG and static SVG assets are supported."));
            }

            if (asset.SizeBytes < 1 || asset.SizeBytes > options.MaxAssetBytes)
            {
                issues.Add(new ValidationIssue($"{path}.sizeBytes", "Asset size exceeds the configured limit."));
            }

            if (!IsSha256(asset.Sha256))
            {
                issues.Add(new ValidationIssue($"{path}.sha256", "Asset SHA-256 must contain 64 hexadecimal characters."));
            }
        }
    }

    private static void ValidateMonitorSelection(
        MonitorSelection? selection,
        ConfigurationValidationOptions options,
        List<ValidationIssue> issues)
    {
        if (selection is null)
        {
            issues.Add(new ValidationIssue("$.monitorSelection", "Monitor selection is required."));
            return;
        }

        if (selection.Mode == MonitorSelectionMode.Explicit &&
            (selection.MonitorIds is null || selection.MonitorIds.Length == 0))
        {
            issues.Add(new ValidationIssue("$.monitorSelection.monitorIds", "Explicit selection requires at least one monitor."));
        }

        if (selection.MonitorIds is null || selection.MonitorIds.Length > options.MaxMonitorIds)
        {
            issues.Add(new ValidationIssue("$.monitorSelection.monitorIds", "Monitor count exceeds the configured limit."));
        }

        if (selection.MonitorIds is not null)
        {
            foreach (string? monitorId in selection.MonitorIds)
            {
                if (string.IsNullOrWhiteSpace(monitorId) || monitorId.Length > MaxMonitorIdLength)
                {
                    issues.Add(new ValidationIssue("$.monitorSelection.monitorIds", "Monitor identifier is invalid."));
                }
            }
        }
    }

    private static void ValidateSwitches(
        SwitchConfiguration? switches,
        string path,
        List<ValidationIssue> issues)
    {
        if (switches is null)
        {
            issues.Add(new ValidationIssue(path, "Switch configuration is required."));
            return;
        }

        if (!Enum.IsDefined(switches.VisibilityRule))
        {
            issues.Add(new ValidationIssue($"{path}.visibilityRule", "Visibility rule is invalid."));
        }

        ValidateHotkey(switches.SwitchA, $"{path}.switchA", issues);
        ValidateHotkey(switches.SwitchB, $"{path}.switchB", issues);
        ValidateUniqueHotkeys(switches, path, issues);
    }

    private static void ValidateHotkey(
        HotkeyBinding? binding,
        string path,
        List<ValidationIssue> issues)
    {
        if (binding is null)
        {
            issues.Add(new ValidationIssue(path, "Hotkey binding is required."));
            return;
        }

        if (!Enum.IsDefined(binding.Mode))
        {
            issues.Add(new ValidationIssue($"{path}.mode", "Hotkey activation mode is invalid."));
            return;
        }

        switch (binding.Mode)
        {
            case HotkeyActivationMode.Unbound:
                if (binding.ToggleKey is not null ||
                    binding.EnableKey is not null ||
                    binding.DisableKey is not null ||
                    binding.HoldKey is not null)
                {
                    issues.Add(new ValidationIssue(path, "Unbound mode cannot contain keys."));
                }

                break;
            case HotkeyActivationMode.Toggle:
                if (binding.ToggleKey is null)
                {
                    issues.Add(new ValidationIssue($"{path}.toggleKey", "Toggle mode requires a toggle key."));
                }

                if (binding.EnableKey is not null || binding.DisableKey is not null || binding.HoldKey is not null)
                {
                    issues.Add(new ValidationIssue(path, "Toggle mode can contain only a toggle key."));
                }

                break;
            case HotkeyActivationMode.Independent:
                if (binding.EnableKey is null || binding.DisableKey is null)
                {
                    issues.Add(new ValidationIssue(path, "Independent mode requires enable and disable keys."));
                }

                if (binding.ToggleKey is not null || binding.HoldKey is not null)
                {
                    issues.Add(new ValidationIssue(path, "Independent mode can contain only enable and disable keys."));
                }

                break;
            case HotkeyActivationMode.Hold:
                if (binding.HoldKey is null)
                {
                    issues.Add(new ValidationIssue($"{path}.holdKey", "Hold mode requires a hold key."));
                }

                if (binding.ToggleKey is not null || binding.EnableKey is not null || binding.DisableKey is not null)
                {
                    issues.Add(new ValidationIssue(path, "Hold mode can contain only a hold key."));
                }

                break;
        }

        ValidateKey(binding.ToggleKey, $"{path}.toggleKey", issues);
        ValidateKey(binding.EnableKey, $"{path}.enableKey", issues);
        ValidateKey(binding.DisableKey, $"{path}.disableKey", issues);
        ValidateKey(binding.HoldKey, $"{path}.holdKey", issues);
    }

    private static void ValidateKey(
        KeyIdentity? key,
        string path,
        List<ValidationIssue> issues)
    {
        if (key is null)
        {
            return;
        }

        if (!Enum.IsDefined(key.Value.Device))
        {
            issues.Add(new ValidationIssue($"{path}.device", "Input device is invalid."));
        }
        else if (key.Value.Device == InputDeviceKind.Keyboard && key.Value.Code == 0)
        {
            issues.Add(new ValidationIssue($"{path}.code", "Keyboard scan code must be non-zero."));
        }
        else if (key.Value.Device == InputDeviceKind.Mouse &&
                 !Enum.IsDefined((InputMouseButton)key.Value.Code))
        {
            issues.Add(new ValidationIssue($"{path}.code", "Mouse button code is invalid."));
        }
        else if (key.Value.Device == InputDeviceKind.Mouse && key.Value.Extended)
        {
            issues.Add(new ValidationIssue($"{path}.extended", "Mouse buttons cannot use the keyboard extended flag."));
        }

        if ((key.Value.Modifiers & ~(
                KeyModifiers.Ctrl |
                KeyModifiers.Alt |
                KeyModifiers.Shift |
                KeyModifiers.Win)) != 0)
        {
            issues.Add(new ValidationIssue(path, "Key modifiers contain unknown flags."));
        }

        if (IsSystemReserved(key.Value))
        {
            issues.Add(new ValidationIssue(path, "System-reserved shortcuts cannot be assigned."));
        }
    }

    private static void ValidateUniqueHotkeys(
        SwitchConfiguration switches,
        string path,
        List<ValidationIssue> issues)
    {
        var assignedKeys = new Dictionary<KeyIdentity, string>();
        foreach ((KeyIdentity Key, string Path) assignment in ActiveHotkeys(switches.SwitchA, $"{path}.switchA")
                     .Concat(ActiveHotkeys(switches.SwitchB, $"{path}.switchB")))
        {
            if (assignedKeys.TryGetValue(assignment.Key, out string? existingPath))
            {
                issues.Add(new ValidationIssue(
                    assignment.Path,
                    $"Hotkey duplicates the binding at {existingPath}."));
            }
            else
            {
                assignedKeys.Add(assignment.Key, assignment.Path);
            }
        }
    }

    private static IEnumerable<(KeyIdentity Key, string Path)> ActiveHotkeys(
        HotkeyBinding? binding,
        string path)
    {
        if (binding is null || !Enum.IsDefined(binding.Mode))
        {
            yield break;
        }

        switch (binding.Mode)
        {
            case HotkeyActivationMode.Toggle when binding.ToggleKey is { } toggle:
                yield return (toggle, $"{path}.toggleKey");
                break;
            case HotkeyActivationMode.Independent:
                if (binding.EnableKey is { } enable)
                {
                    yield return (enable, $"{path}.enableKey");
                }

                if (binding.DisableKey is { } disable)
                {
                    yield return (disable, $"{path}.disableKey");
                }

                break;
            case HotkeyActivationMode.Hold when binding.HoldKey is { } hold:
                yield return (hold, $"{path}.holdKey");
                break;
        }
    }

    private static bool IsSystemReserved(KeyIdentity key)
    {
        const ushort EscapeScanCode = 0x01;
        const ushort TabScanCode = 0x0F;
        const ushort DeleteScanCode = 0x53;
        const ushort F12ScanCode = 0x58;
        const ushort LeftWindowsScanCode = 0x5B;
        const ushort RightWindowsScanCode = 0x5C;

        if (key.Device != InputDeviceKind.Keyboard)
        {
            return false;
        }

        bool alt = key.Modifiers.HasFlag(KeyModifiers.Alt);
        bool ctrl = key.Modifiers.HasFlag(KeyModifiers.Ctrl);
        bool windows = key.Modifiers.HasFlag(KeyModifiers.Win);
        bool primaryWindowsKey =
            key.Extended && key.Code is LeftWindowsScanCode or RightWindowsScanCode;

        return windows ||
               primaryWindowsKey ||
               key.Code == F12ScanCode ||
               (alt && key.Code is TabScanCode or EscapeScanCode) ||
               (ctrl && !alt && key.Code == EscapeScanCode) ||
               (ctrl && alt && key.Extended && key.Code == DeleteScanCode);
    }

    private static void ValidateToasts(
        ToastConfiguration? toasts,
        List<ValidationIssue> issues)
    {
        if (toasts is null)
        {
            issues.Add(new ValidationIssue("$.toasts", "Toast configuration is required."));
            return;
        }

        if (!Enum.IsDefined(toasts.Position))
        {
            issues.Add(new ValidationIssue("$.toasts.position", "Toast position is invalid."));
        }

        if (toasts.DurationMs is < 100 or > 60000)
        {
            issues.Add(new ValidationIssue("$.toasts.durationMs", "Toast duration must be between 100 and 60000 ms."));
        }

        if (string.IsNullOrWhiteSpace(toasts.FontFamily) || toasts.FontFamily.Length > MaxFontFamilyLength)
        {
            issues.Add(new ValidationIssue("$.toasts.fontFamily", "Toast font family is invalid."));
        }

        if (toasts.FontSizePx is < 6 or > 200)
        {
            issues.Add(new ValidationIssue("$.toasts.fontSizePx", "Toast font size must be between 6 and 200 px."));
        }
    }

    private static void ValidateCrosshair(
        Crosshair? crosshair,
        string path,
        List<ValidationIssue> issues)
    {
        if (crosshair is null)
        {
            issues.Add(new ValidationIssue(path, "Crosshair configuration is required."));
            return;
        }

        ValidateOffset(crosshair.OffsetPx, $"{path}.offsetPx", issues);
        if (crosshair.Center is null)
        {
            issues.Add(new ValidationIssue($"{path}.center", "Center point is required."));
        }
        else if (crosshair.Center.RadiusPx is < 0 or > 1000)
        {
            issues.Add(new ValidationIssue($"{path}.center.radiusPx", "Center radius must be between 0 and 1000 px."));
        }

        if (crosshair.Arms is null || crosshair.Arms.Length != 4)
        {
            issues.Add(new ValidationIssue($"{path}.arms", "Exactly four arms are required."));
        }
        else
        {
            for (int index = 0; index < crosshair.Arms.Length; index++)
            {
                Arm? arm = crosshair.Arms[index];
                if (arm is null)
                {
                    issues.Add(new ValidationIssue($"{path}.arms[{index}]", "Arm is required."));
                    continue;
                }

                if (double.IsNaN(arm.OrbitAngleOffsetDeg) ||
                    double.IsInfinity(arm.OrbitAngleOffsetDeg) ||
                    arm.OrbitAngleOffsetDeg is < -720 or > 720)
                {
                    issues.Add(new ValidationIssue(
                        $"{path}.arms[{index}].orbitAngleOffsetDeg",
                        "Arm orbit angle offset must be between -720 and 720 degrees."));
                }

                if (double.IsNaN(arm.RotationAngleOffsetDeg) ||
                    double.IsInfinity(arm.RotationAngleOffsetDeg) ||
                    arm.RotationAngleOffsetDeg is < -720 or > 720)
                {
                    issues.Add(new ValidationIssue(
                        $"{path}.arms[{index}].rotationAngleOffsetDeg",
                        "Arm rotation angle offset must be between -720 and 720 degrees."));
                }

                if (arm.GapPx is < -10000 or > 10000)
                {
                    issues.Add(new ValidationIssue(
                        $"{path}.arms[{index}].gapPx",
                        "Arm gap must be between -10000 and 10000 px."));
                }

                if (arm.LengthPx is < 0 or > 10000)
                {
                    issues.Add(new ValidationIssue(
                        $"{path}.arms[{index}].lengthPx",
                        "Arm length must be between 0 and 10000 px."));
                }

                if (arm.WidthPx is < 1 or > 1000)
                {
                    issues.Add(new ValidationIssue(
                        $"{path}.arms[{index}].widthPx",
                        "Arm width must be between 1 and 1000 px."));
                }
            }
        }
    }

    private static void ValidateImage(
        Profile profile,
        string path,
        List<ValidationIssue> issues)
    {
        if (profile.Image is null)
        {
            issues.Add(new ValidationIssue(path, "Image configuration is required."));
            return;
        }

        ValidateOffset(profile.Image.OffsetPx, $"{path}.offsetPx", issues);
        if (double.IsNaN(profile.Image.Scale) ||
            double.IsInfinity(profile.Image.Scale) ||
            profile.Image.Scale is < 0.01 or > 100)
        {
            issues.Add(new ValidationIssue($"{path}.scale", "Image scale must be between 0.01 and 100."));
        }
    }

    private static void ValidateOffset(
        PixelPoint offset,
        string path,
        List<ValidationIssue> issues)
    {
        if (offset.X is < -100000 or > 100000 || offset.Y is < -100000 or > 100000)
        {
            issues.Add(new ValidationIssue(path, "Physical-pixel offset must be between -100000 and 100000."));
        }
    }

    private static void ValidateName(
        string? name,
        string path,
        List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            issues.Add(new ValidationIssue(path, "Name must be between 1 and 100 characters."));
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

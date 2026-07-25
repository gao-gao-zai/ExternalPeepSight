using System.Collections.ObjectModel;
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
        ValidateAssets(document, options, issues);
        ValidateMonitorSelection(document.MonitorSelection, options, issues);
        if (!Enum.IsDefined(document.InputBackend))
        {
            issues.Add(new ValidationIssue("$.inputBackend", "Input capture backend is invalid."));
        }
        ValidateToasts(document.Toasts, issues);
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
        }
    }

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

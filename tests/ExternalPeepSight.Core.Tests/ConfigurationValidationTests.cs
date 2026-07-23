namespace ExternalPeepSight.Core.Tests;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void NullDocumentAndNullSectionsAreRejected()
    {
        Assert.False(ConfigurationValidator.Validate(null).IsValid);

        ConfigurationDocument document = new()
        {
            SchemaVersion = 0,
            Profiles = null!,
            ProfileSets = null!,
            Assets = null!,
            MonitorSelection = null!,
            Switches = null!,
            Toasts = null!,
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Path == "$.schemaVersion");
        Assert.Contains(result.Issues, issue => issue.Path == "$.monitorSelection");
        Assert.Contains(result.Issues, issue => issue.Path == "$.switches");
        Assert.Contains(result.Issues, issue => issue.Path == "$.toasts");
    }

    [Fact]
    public void DuplicateAndNullObjectsAreRejected()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        Profile profile = seed.Profiles[0];
        ProfileSet profileSet = seed.ProfileSets[0];
        Guid assetId = Guid.NewGuid();
        var asset = new AssetReference(assetId, "asset.png", "image/png", 1, new string('A', 64));
        ConfigurationDocument document = seed with
        {
            Profiles = [null!, profile, profile],
            ProfileSets =
            [
                null!,
                profileSet with { ProfileIds = [profile.Id, profile.Id] },
                profileSet,
            ],
            Assets = [null!, asset, asset],
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("unique", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("required", StringComparison.Ordinal));
    }

    [Fact]
    public void DetailedFieldLimitsAreRejected()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        Profile profile = seed.Profiles[0] with
        {
            Name = new string('N', 101),
            Crosshair = seed.Profiles[0].Crosshair with
            {
                Center = null!,
                Arms =
                [
                    null!,
                    new(double.PositiveInfinity, -1, -1, 0, RgbaColor.White, true),
                    new(361, 10001, 10001, 1001, RgbaColor.White, true),
                    new(-361, 0, 0, 1, RgbaColor.White, true),
                ],
            },
            Image = new ImageOverlay(null, AnchorMode.TopLeft, new PixelPoint(-100001, 100001), double.PositiveInfinity, false),
        };
        Guid emptyAssetId = Guid.Empty;
        ConfigurationDocument document = seed with
        {
            Profiles = [profile],
            ProfileSets =
            [
                new(Guid.NewGuid(), string.Empty, [], Guid.NewGuid()),
            ],
            Assets =
            [
                new(emptyAssetId, new string('F', 256), "text/plain", 11, new string('Z', 64)),
            ],
            MonitorSelection = new(
                MonitorSelectionMode.Explicit,
                [string.Empty, new string('M', 513)],
                FocusMonitorSource.Mouse),
            Switches = new(
                VisibilityRule.Both,
                false,
                false,
                new HotkeyBinding((HotkeyActivationMode)99, null, null, null, null),
                new HotkeyBinding(
                    HotkeyActivationMode.Independent,
                    null,
                    new KeyIdentity(0, false, (KeyModifiers)32),
                    null,
                    null)),
            Toasts = new(true, ToastPosition.BottomCenter, 60001, new string('F', 101), 201, RgbaColor.White, RgbaColor.Transparent),
        };
        var options = new ConfigurationValidationOptions
        {
            MaxProfiles = 0,
            MaxProfileSets = 0,
            MaxAssets = 0,
            MaxMonitorIds = 1,
            MaxAssetBytes = 10,
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document, options);

        Assert.False(result.IsValid);
        Assert.True(result.Issues.Count >= 20);
        Assert.Contains(result.Issues, issue => issue.Path.Contains("scanCode", StringComparison.OrdinalIgnoreCase) ||
                                                issue.Message.Contains("Scan code", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message == "Arm is required.");
        Assert.Contains(result.Issues, issue => issue.Message.Contains("Monitor identifier", StringComparison.Ordinal));
    }

    [Fact]
    public void HotkeyModesRequireTheirSpecificKeys()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity validKey = new(30, false, KeyModifiers.Ctrl | KeyModifiers.Shift);
        ConfigurationDocument missingKeys = seed with
        {
            Switches = seed.Switches with
            {
                SwitchA = new HotkeyBinding(HotkeyActivationMode.Hold, null, null, null, null),
                SwitchB = new HotkeyBinding(HotkeyActivationMode.Toggle, null, null, null, null),
            },
        };
        ConfigurationDocument validKeys = seed with
        {
            Switches = seed.Switches with
            {
                SwitchA = new HotkeyBinding(HotkeyActivationMode.Hold, null, null, null, validKey),
                SwitchB = new HotkeyBinding(HotkeyActivationMode.Toggle, validKey, null, null, null),
            },
        };

        Assert.False(ConfigurationValidator.Validate(missingKeys).IsValid);
        Assert.True(ConfigurationValidator.Validate(validKeys).IsValid);
    }

    [Fact]
    public void InvalidResultThrowsAllIssues()
    {
        ConfigurationValidationResult result = ConfigurationValidator.Validate(null);

        ConfigurationValidationException exception =
            Assert.Throws<ConfigurationValidationException>(result.ThrowIfInvalid);

        Assert.Single(exception.Issues);
        Assert.Contains("$:", exception.Message);
    }
}

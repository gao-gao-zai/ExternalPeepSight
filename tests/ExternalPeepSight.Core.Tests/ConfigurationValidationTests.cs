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
        KeyIdentity secondValidKey = new(48, false, KeyModifiers.Ctrl | KeyModifiers.Shift);
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
                SwitchB = new HotkeyBinding(HotkeyActivationMode.Toggle, secondValidKey, null, null, null),
            },
        };

        Assert.False(ConfigurationValidator.Validate(missingKeys).IsValid);
        Assert.True(ConfigurationValidator.Validate(validKeys).IsValid);
    }

    [Fact]
    public void HotkeyModesRejectIrrelevantKeys()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity keyA = new(30, false, KeyModifiers.None);
        KeyIdentity keyB = new(48, false, KeyModifiers.None);
        ConfigurationDocument document = seed with
        {
            Switches = seed.Switches with
            {
                SwitchA = new HotkeyBinding(HotkeyActivationMode.Unbound, keyA, null, null, null),
                SwitchB = new HotkeyBinding(HotkeyActivationMode.Toggle, keyA, keyB, null, null),
            },
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.Contains(result.Issues, issue => issue.Message == "Unbound mode cannot contain keys.");
        Assert.Contains(result.Issues, issue => issue.Message == "Toggle mode can contain only a toggle key.");
    }

    [Fact]
    public void DuplicateActiveHotkeysAcrossSwitchesAreRejected()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity duplicate = new(30, false, KeyModifiers.Ctrl);
        ConfigurationDocument document = seed with
        {
            Switches = seed.Switches with
            {
                SwitchA = new HotkeyBinding(HotkeyActivationMode.Toggle, duplicate, null, null, null),
                SwitchB = new HotkeyBinding(HotkeyActivationMode.Hold, null, null, null, duplicate),
            },
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.Contains(result.Issues, issue =>
            issue.Path == "$.switches.switchB.holdKey" &&
            issue.Message.Contains("$.switches.switchA.toggleKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(30, false, KeyModifiers.Win)]
    [InlineData(91, true, KeyModifiers.None)]
    [InlineData(15, false, KeyModifiers.Alt)]
    [InlineData(1, false, KeyModifiers.Ctrl)]
    [InlineData(83, true, KeyModifiers.Ctrl | KeyModifiers.Alt)]
    [InlineData(88, false, KeyModifiers.None)]
    public void SystemReservedHotkeysAreRejected(
        ushort scanCode,
        bool extended,
        KeyModifiers modifiers)
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity reserved = new(scanCode, extended, modifiers);
        ConfigurationDocument document = seed with
        {
            Switches = seed.Switches with
            {
                SwitchA = new HotkeyBinding(HotkeyActivationMode.Toggle, reserved, null, null, null),
            },
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.Contains(result.Issues, issue =>
            issue.Path == "$.switches.switchA.toggleKey" &&
            issue.Message.Contains("System-reserved", StringComparison.Ordinal));
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

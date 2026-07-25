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
            Toasts = null!,
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Path == "$.schemaVersion");
        Assert.Contains(result.Issues, issue => issue.Path == "$.monitorSelection");
        Assert.Contains(result.Issues, issue => issue.Path == "$.toasts");
    }

    [Fact]
    public void InvalidInputBackendIsRejected()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create() with
        {
            InputBackend = (InputCaptureBackend)99,
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.Contains(result.Issues, issue => issue.Path == "$.inputBackend");
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
                    new(double.PositiveInfinity, double.PositiveInfinity, -7, -13, -3, RgbaColor.White, true),
                    new(721, 0, 10000, 10000, 1000, RgbaColor.White, true),
                    new(-721, 0, 0, 0, -1, RgbaColor.White, true),
                ],
            },
            Image = new ImageOverlay(null, AnchorMode.TopLeft, new PixelPoint(-100001, 100001), double.PositiveInfinity, false),
            Switches = new(
                VisibilityRule.Both,
                false,
                false,
                new HotkeyBinding((HotkeyActivationMode)99, null, null, null, null),
                new HotkeyBinding(
                    HotkeyActivationMode.Independent,
                    null,
                    new KeyIdentity(InputDeviceKind.Keyboard, 0, false, (KeyModifiers)32),
                    null,
                    null)),
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
        Assert.Contains(result.Issues, issue => issue.Path.Contains("code", StringComparison.OrdinalIgnoreCase) ||
                                                issue.Message.Contains("scan code", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Issues, issue => issue.Message == "Arm is required.");
        Assert.Contains(result.Issues, issue => issue.Message.Contains("Monitor identifier", StringComparison.Ordinal));
    }

    [Fact]
    public void HotkeyModesRequireTheirSpecificKeys()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity validKey = new(InputDeviceKind.Keyboard, 30, false, KeyModifiers.Ctrl | KeyModifiers.Shift);
        KeyIdentity secondValidKey = new(InputDeviceKind.Keyboard, 48, false, KeyModifiers.Ctrl | KeyModifiers.Shift);
        ConfigurationDocument missingKeys = seed with
        {
            Profiles =
            [
                seed.Profiles[0] with
                {
                    Switches = seed.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(HotkeyActivationMode.Hold, null, null, null, null),
                        SwitchB = new HotkeyBinding(HotkeyActivationMode.Toggle, null, null, null, null),
                    },
                },
            ],
        };
        ConfigurationDocument validKeys = seed with
        {
            Profiles =
            [
                seed.Profiles[0] with
                {
                    Switches = seed.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(HotkeyActivationMode.Hold, null, null, null, validKey),
                        SwitchB = new HotkeyBinding(HotkeyActivationMode.Toggle, secondValidKey, null, null, null),
                    },
                },
            ],
        };

        Assert.False(ConfigurationValidator.Validate(missingKeys).IsValid);
        Assert.True(ConfigurationValidator.Validate(validKeys).IsValid);
    }

    [Fact]
    public void MouseButtonsAreValidatedByDevice()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        var validMouse = new KeyIdentity(
            InputDeviceKind.Mouse,
            (ushort)InputMouseButton.Middle,
            false,
            KeyModifiers.Alt);
        var invalidCode = new KeyIdentity(InputDeviceKind.Mouse, 99, false, KeyModifiers.None);
        var invalidExtended = validMouse with { Extended = true };
        ConfigurationDocument valid = seed with
        {
            Profiles =
            [
                seed.Profiles[0] with
                {
                    Switches = seed.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(
                            HotkeyActivationMode.Toggle,
                            validMouse,
                            null,
                            null,
                            null),
                    },
                },
            ],
        };
        ConfigurationDocument invalidCodeDocument = valid with
        {
            Profiles =
            [
                valid.Profiles[0] with
                {
                    Switches = valid.Profiles[0].Switches with
                    {
                        SwitchA = valid.Profiles[0].Switches.SwitchA with { ToggleKey = invalidCode },
                    },
                },
            ],
        };
        ConfigurationDocument invalidExtendedDocument = valid with
        {
            Profiles =
            [
                valid.Profiles[0] with
                {
                    Switches = valid.Profiles[0].Switches with
                    {
                        SwitchA = valid.Profiles[0].Switches.SwitchA with { ToggleKey = invalidExtended },
                    },
                },
            ],
        };

        Assert.True(ConfigurationValidator.Validate(valid).IsValid);
        Assert.Contains(
            ConfigurationValidator.Validate(invalidCodeDocument).Issues,
            issue => issue.Path.EndsWith(".code", StringComparison.Ordinal));
        Assert.Contains(
            ConfigurationValidator.Validate(invalidExtendedDocument).Issues,
            issue => issue.Path.EndsWith(".extended", StringComparison.Ordinal));
    }

    [Fact]
    public void HotkeyModesRejectIrrelevantKeys()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity keyA = new(InputDeviceKind.Keyboard, 30, false, KeyModifiers.None);
        KeyIdentity keyB = new(InputDeviceKind.Keyboard, 48, false, KeyModifiers.None);
        ConfigurationDocument document = seed with
        {
            Profiles =
            [
                seed.Profiles[0] with
                {
                    Switches = seed.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(HotkeyActivationMode.Unbound, keyA, null, null, null),
                        SwitchB = new HotkeyBinding(HotkeyActivationMode.Toggle, keyA, keyB, null, null),
                    },
                },
            ],
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.Contains(result.Issues, issue => issue.Message == "Unbound mode cannot contain keys.");
        Assert.Contains(result.Issues, issue => issue.Message == "Toggle mode can contain only a toggle key.");
    }

    [Fact]
    public void DuplicateActiveHotkeysAcrossSwitchesAreRejected()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity duplicate = new(InputDeviceKind.Keyboard, 30, false, KeyModifiers.Ctrl);
        ConfigurationDocument document = seed with
        {
            Profiles =
            [
                seed.Profiles[0] with
                {
                    Switches = seed.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(HotkeyActivationMode.Toggle, duplicate, null, null, null),
                        SwitchB = new HotkeyBinding(HotkeyActivationMode.Hold, null, null, null, duplicate),
                    },
                },
            ],
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.Contains(result.Issues, issue =>
            issue.Path == "$.profiles[0].switches.switchB.holdKey" &&
            issue.Message.Contains("$.profiles[0].switches.switchA.toggleKey", StringComparison.Ordinal));
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
        KeyIdentity reserved = new(InputDeviceKind.Keyboard, scanCode, extended, modifiers);
        ConfigurationDocument document = seed with
        {
            Profiles =
            [
                seed.Profiles[0] with
                {
                    Switches = seed.Profiles[0].Switches with
                    {
                        SwitchA = new HotkeyBinding(HotkeyActivationMode.Toggle, reserved, null, null, null),
                    },
                },
            ],
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(document);

        Assert.Contains(result.Issues, issue =>
            issue.Path == "$.profiles[0].switches.switchA.toggleKey" &&
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

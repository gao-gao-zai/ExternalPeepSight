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

    [Fact]
    public void LuaControlModeRequiresAnEnabledProfileScript()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        Profile profile = seed.Profiles[0] with
        {
            ControlMode = DisplayControlMode.Lua,
            Script = new ScriptConfiguration(
                false,
                "1",
                "return eps.script {}",
                new string('A', 64),
                [],
                []),
        };

        ConfigurationValidationResult result =
            ConfigurationValidator.Validate(seed with { Profiles = [profile] });

        Assert.Contains(result.Issues, issue =>
            issue.Path == "$.profiles[0].scripts" &&
            issue.Message.Contains("enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptStacksRequireBoundedUniqueNamedEntries()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        ScriptConfiguration valid = CreateEnabledScript(
            new KeyIdentity(InputDeviceKind.Keyboard, 30, false, KeyModifiers.None)) with
        {
            Id = Guid.NewGuid(),
            Name = "First",
        };
        ConfigurationValidationResult nullCollection = ConfigurationValidator.Validate(
            seed with { GlobalScripts = null! });
        ConfigurationValidationResult tooMany = ConfigurationValidator.Validate(
            seed with { GlobalScripts = Enumerable.Repeat(valid, 17).ToArray() });
        ConfigurationValidationResult malformed = ConfigurationValidator.Validate(
            seed with
            {
                GlobalScripts =
                [
                    null!,
                    valid,
                    valid with { Name = " " },
                    valid with { Id = Guid.Empty, Name = "Empty identifier" },
                ],
            });

        Assert.Contains(nullCollection.Issues, issue =>
            issue.Path == "$.globalScripts" &&
            issue.Message.Contains("required", StringComparison.Ordinal));
        Assert.Contains(tooMany.Issues, issue =>
            issue.Path == "$.globalScripts" &&
            issue.Message.Contains("count", StringComparison.Ordinal));
        Assert.Contains(malformed.Issues, issue => issue.Path == "$.globalScripts[0]");
        Assert.Contains(malformed.Issues, issue => issue.Path == "$.globalScripts[2].id");
        Assert.Contains(malformed.Issues, issue => issue.Path == "$.globalScripts[2].name");
        Assert.Contains(malformed.Issues, issue => issue.Path == "$.globalScripts[3].id");
    }

    [Fact]
    public void ValidLuaDeclarationsAcceptAllSupportedSettingTypes()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity key = new(InputDeviceKind.Keyboard, 30, false, KeyModifiers.Ctrl);
        ScriptConfiguration script = new(
            true,
            "1",
            "return eps.script {}",
            new string('A', 64),
            [
                new("pressed", "Pressed", true, false, true, key),
                new("released", "Released", false, true, false, null),
            ],
            [
                new("enabled", "Enabled", ScriptSettingType.Boolean, "true", [], null, null),
                new("count", "Count", ScriptSettingType.Integer, "3", [], 0, 10),
                new("scale", "Scale", ScriptSettingType.Double, "0.5", [], 0, 1),
                new("label", "Label", ScriptSettingType.String, "value", [], null, null),
                new("mode", "Mode", ScriptSettingType.Enum, "one", ["one", "two"], null, null),
            ]);
        Profile profile = seed.Profiles[0] with
        {
            ControlMode = DisplayControlMode.Lua,
            Script = script,
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(seed with { Profiles = [profile] });

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void ValidScriptUiAcceptsTrustedControlsSectionsAndConditions()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        ScriptConfiguration script = new(
            true,
            "2",
            "return eps.script { api_version = \"2\" }",
            new string('A', 64),
            [],
            [
                new("enabled", "Enabled", ScriptSettingType.Boolean, "true", [], null, null),
                new("opacity", "Opacity", ScriptSettingType.Double, "0.5", [], 0, 1),
                new("mode", "Mode", ScriptSettingType.Enum, "one", ["one", "two"], null, null),
            ],
            new ScriptUiLayout(
            [
                new ScriptUiSection(
                    "general",
                    "General",
                    string.Empty,
                    true,
                    true,
                    2,
                    [
                        new ScriptUiItem(
                            "enabled",
                            ScriptUiControlType.Switch,
                            string.Empty,
                            string.Empty,
                            null,
                            null),
                        new ScriptUiItem(
                            "opacity",
                            ScriptUiControlType.Slider,
                            "Opacity",
                            "%",
                            0.05,
                            new ScriptUiVisibilityCondition("enabled", "true")),
                        new ScriptUiItem(
                            "mode",
                            ScriptUiControlType.Segmented,
                            string.Empty,
                            string.Empty,
                            null,
                            null),
                    ]),
            ]));

        ConfigurationValidationResult result = ConfigurationValidator.Validate(
            seed with { GlobalScript = script });

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void InvalidScriptUiRejectsUnsafeOrInconsistentLayout()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        ScriptConfiguration script = new(
            true,
            "1",
            "return eps.script {}",
            new string('A', 64),
            [],
            [
                new("enabled", "Enabled", ScriptSettingType.Boolean, "true", [], null, null),
                new("opacity", "Opacity", ScriptSettingType.Double, "0.5", [], 0, 1),
            ],
            new ScriptUiLayout(
            [
                new ScriptUiSection(
                    "general",
                    "General",
                    string.Empty,
                    false,
                    true,
                    3,
                    [
                        new ScriptUiItem(
                            "enabled",
                            ScriptUiControlType.Slider,
                            string.Empty,
                            string.Empty,
                            -1,
                            new ScriptUiVisibilityCondition("enabled", "true")),
                    ]),
            ]));

        ConfigurationValidationResult result = ConfigurationValidator.Validate(
            seed with { GlobalScript = script });

        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].ui");
        Assert.Contains(result.Issues, issue => issue.Path.EndsWith(".columns", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Path.EndsWith(".control", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Path.EndsWith(".step", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue =>
            issue.Path.EndsWith(".visibleWhen.settingId", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue =>
            issue.Path == "$.globalScripts[0].ui.sections" &&
            issue.Message.Contains("every declared setting", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidLuaBindingDeclarationsAreRejected()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        ScriptConfiguration script = new(
            true,
            "3",
            " ",
            "invalid",
            [
                null!,
                new(
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    true,
                    new KeyIdentity((InputDeviceKind)99, 0, false, (KeyModifiers)32)),
                new(
                    "duplicate",
                    new string('D', 101),
                    true,
                    false,
                    true,
                    new KeyIdentity(InputDeviceKind.Keyboard, 30, false, KeyModifiers.None)),
                new(
                    "duplicate",
                    "Second",
                    true,
                    false,
                    true,
                    new KeyIdentity(InputDeviceKind.Keyboard, 30, false, KeyModifiers.None)),
            ],
            []);

        ConfigurationValidationResult result = ConfigurationValidator.Validate(
            seed with
            {
                GlobalScript = script,
            });

        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].apiVersion");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].source");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].sourceHash");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].bindings[0]");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].bindings[1].id");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].bindings[1].key.device");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].bindings[2].displayName");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].bindings[3].id");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].bindings[3].key");
    }

    [Fact]
    public void InvalidLuaSettingDeclarationsAreRejected()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        ScriptConfiguration script = new(
            true,
            "1",
            "return eps.script {}",
            new string('A', 64),
            [],
            [
                null!,
                new("1invalid", new string('D', 101), (ScriptSettingType)99, "value", [], null, null),
                new("boolean", "Boolean", ScriptSettingType.Boolean, "yes", [], null, null),
                new("integer", "Integer", ScriptSettingType.Integer, "1.5", [], null, null),
                new("range", "Range", ScriptSettingType.Integer, "10", [], 20, 10),
                new("double", "Double", ScriptSettingType.Double, "NaN", [], null, null),
                new("doubleRange", "Double range", ScriptSettingType.Double, "2", [], 0, 1),
                new("string", "String", ScriptSettingType.String, "value", ["option"], null, null),
                new("emptyEnum", "Empty enum", ScriptSettingType.Enum, "value", [], null, null),
                new("duplicateEnum", "Duplicate enum", ScriptSettingType.Enum, "one", ["one", "one"], null, null),
                new("missingEnum", "Missing enum", ScriptSettingType.Enum, "three", ["one", "two"], null, null),
                new("nullValue", "Null value", ScriptSettingType.String, null!, [], null, null),
                new("longValue", "Long value", ScriptSettingType.String, new string('V', 4097), [], null, null),
            ]);

        ConfigurationValidationResult result = ConfigurationValidator.Validate(
            seed with
            {
                GlobalScript = script,
            });

        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[0]");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[1].type");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[2].value");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[3].value");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[4]");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[5].value");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[6].value");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[7].options");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[8].options");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[9].options");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[10].value");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[11].value");
        Assert.Contains(result.Issues, issue => issue.Path == "$.globalScripts[0].settings[12].value");
    }

    [Fact]
    public void LuaDeclarationCollectionsEnforceLimits()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        ScriptBindingSlot binding = new("binding", "Binding", true, false, true, null);
        ScriptSetting setting = new("setting", "Setting", ScriptSettingType.String, "value", [], null, null);
        ScriptConfiguration tooMany = new(
            true,
            "1",
            "return eps.script {}",
            new string('A', 64),
            Enumerable.Repeat(binding, 65).ToArray(),
            Enumerable.Repeat(setting, 65).ToArray());
        ScriptConfiguration nullCollections = tooMany with
        {
            Bindings = null!,
            Settings = null!,
        };

        ConfigurationValidationResult tooManyResult = ConfigurationValidator.Validate(
            seed with
            {
                GlobalScript = tooMany,
            });
        ConfigurationValidationResult nullResult = ConfigurationValidator.Validate(
            seed with
            {
                GlobalScript = nullCollections,
            });

        Assert.Contains(tooManyResult.Issues, issue => issue.Path == "$.globalScripts[0].bindings");
        Assert.Contains(tooManyResult.Issues, issue => issue.Path == "$.globalScripts[0].settings");
        Assert.Contains(nullResult.Issues, issue => issue.Path == "$.globalScripts[0].bindings");
        Assert.Contains(nullResult.Issues, issue => issue.Path == "$.globalScripts[0].settings");
    }

    [Fact]
    public void ActiveScopeBindingsCannotReuseBasicOrOtherScriptKeys()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity first = new(InputDeviceKind.Keyboard, 30, false, KeyModifiers.Ctrl);
        KeyIdentity second = new(InputDeviceKind.Mouse, (ushort)InputMouseButton.Middle, false, KeyModifiers.None);
        ScriptConfiguration profileScript = CreateEnabledScript(first);
        ScriptConfiguration profileSetScript = CreateEnabledScript(second);
        ScriptConfiguration globalScript = CreateEnabledScript(first);
        Profile profile = seed.Profiles[0] with
        {
            ControlMode = DisplayControlMode.Lua,
            Script = profileScript,
            Switches = seed.Profiles[0].Switches with
            {
                SwitchA = new HotkeyBinding(HotkeyActivationMode.Toggle, first, null, null, null),
                SwitchB = new HotkeyBinding(HotkeyActivationMode.Hold, null, null, null, second),
            },
        };
        ProfileSet profileSet = seed.ProfileSets[0] with
        {
            Script = profileSetScript,
        };

        ConfigurationValidationResult result = ConfigurationValidator.Validate(
            seed with
            {
                Profiles = [profile],
                ProfileSets = [profileSet],
                GlobalScript = globalScript,
            });

        Assert.True(result.Issues.Count(issue => issue.Message.Contains("Input binding duplicates", StringComparison.Ordinal)) >= 3);
    }

    [Fact]
    public void ScriptsInTheSameScopeCannotReuseInputKeys()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        KeyIdentity key = new(InputDeviceKind.Keyboard, 30, false, KeyModifiers.Ctrl);
        ScriptConfiguration first = CreateEnabledScript(key) with
        {
            Id = Guid.NewGuid(),
            Name = "First",
        };
        ScriptConfiguration second = CreateEnabledScript(key) with
        {
            Id = Guid.NewGuid(),
            Name = "Second",
        };
        Profile profile = seed.Profiles[0] with
        {
            ControlMode = DisplayControlMode.Lua,
            Scripts = [first, second],
        };

        ConfigurationValidationResult result =
            ConfigurationValidator.Validate(seed with { Profiles = [profile] });

        Assert.Contains(
            result.Issues,
            issue => issue.Path == "$.profiles[active].scripts[1].bindings[0].key");
    }

    [Fact]
    public void ActiveProfileSetReferenceMustBePresentAndValid()
    {
        ConfigurationDocument seed = ConfigurationDefaults.Create();
        ConfigurationValidationResult missing = ConfigurationValidator.Validate(
            seed with
            {
                ActiveProfileSetId = Guid.Empty,
            });
        ConfigurationValidationResult unknown = ConfigurationValidator.Validate(
            seed with
            {
                ActiveProfileSetId = Guid.NewGuid(),
            });
        ConfigurationValidationResult emptyCollections = ConfigurationValidator.Validate(
            seed with
            {
                ProfileSets = [],
                ActiveProfileSetId = Guid.NewGuid(),
            });

        Assert.Contains(missing.Issues, issue => issue.Path == "$.activeProfileSetId");
        Assert.Contains(unknown.Issues, issue => issue.Path == "$.activeProfileSetId");
        Assert.Contains(emptyCollections.Issues, issue => issue.Path == "$.activeProfileSetId");
    }

    private static ScriptConfiguration CreateEnabledScript(KeyIdentity key) =>
        new(
            true,
            "1",
            "return eps.script {}",
            new string('A', 64),
            [new("binding", "Binding", true, false, true, key)],
            []);

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

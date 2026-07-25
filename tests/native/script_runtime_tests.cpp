#include "script_runtime.h"

#include <gtest/gtest.h>

#include <algorithm>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
using external_peepsight::LuaScript;
using external_peepsight::ScriptBindingDeviceKind;
using external_peepsight::ScriptCommand;
using external_peepsight::ScriptCommandType;
using external_peepsight::ScriptContext;
using external_peepsight::ScriptInputPhase;
using external_peepsight::ScriptKeyModifiers;
using external_peepsight::ScriptScope;
using external_peepsight::ScriptSettingType;
using external_peepsight::ScriptSettingValue;

constexpr std::string_view kScript = R"(
return eps.script {
    bindings = {
        toggle = {
            title = "Toggle visibility",
            events = { "pressed", "released" },
            enabled = true,
            default_key = {
                device = "keyboard",
                key = "N",
                modifiers = { "ctrl", "shift" }
            }
        }
    },
    settings = {
        enabled = {
            title = "Enabled",
            type = "boolean",
            default = true
        },
        amount = {
            title = "Amount",
            type = "integer",
            default = 2,
            minimum = 1,
            maximum = 5
        },
        mode = {
            title = "Mode",
            type = "enum",
            default = "one",
            options = { "one", "two" }
        }
    },
    on_start = function(ctx)
        ctx.visibility:set(ctx.settings.enabled)
    end,
    on_input = function(event, ctx)
        if event.id == "toggle" and event.phase == "pressed" then
            ctx.visibility:toggle()
            ctx.profile:next()
        end
    end
}
)";

TEST(LuaScript, ExtractsBindingsAndTypedSettings)
{
    LuaScript script(std::string(kScript), ScriptScope::profile);

    ASSERT_EQ(1U, script.declarations().bindings.size());
    EXPECT_EQ("toggle", script.declarations().bindings[0].id);
    EXPECT_TRUE(script.declarations().bindings[0].pressed);
    EXPECT_TRUE(script.declarations().bindings[0].released);
    ASSERT_TRUE(script.declarations().bindings[0].default_key.has_value());
    EXPECT_EQ(ScriptBindingDeviceKind::keyboard, script.declarations().bindings[0].default_key->device);
    EXPECT_EQ(0x31U, script.declarations().bindings[0].default_key->code);
    EXPECT_FALSE(script.declarations().bindings[0].default_key->extended);
    EXPECT_EQ(static_cast<std::uint8_t>(ScriptKeyModifiers::ctrl) |
                  static_cast<std::uint8_t>(ScriptKeyModifiers::shift),
              static_cast<std::uint8_t>(script.declarations().bindings[0].default_key->modifiers));
    ASSERT_EQ(3U, script.declarations().settings.size());
    const auto enabled =
        std::ranges::find_if(script.declarations().settings, [](const auto &item) { return item.id == "enabled"; });
    const auto amount =
        std::ranges::find_if(script.declarations().settings, [](const auto &item) { return item.id == "amount"; });
    const auto mode =
        std::ranges::find_if(script.declarations().settings, [](const auto &item) { return item.id == "mode"; });
    ASSERT_NE(script.declarations().settings.end(), enabled);
    ASSERT_NE(script.declarations().settings.end(), amount);
    ASSERT_NE(script.declarations().settings.end(), mode);
    EXPECT_EQ(ScriptSettingType::boolean, enabled->type);
    EXPECT_EQ("2", amount->default_value);
    EXPECT_EQ(std::vector<std::string>({"one", "two"}), mode->options);
}

TEST(LuaScript, ExtractsTrustedApiVersionTwoUiLayout)
{
    LuaScript script(R"(
return eps.script {
    api_version = "2",
    settings = {
        enabled = {
            title = "Enabled",
            type = "boolean",
            default = true
        },
        opacity = {
            title = "Opacity",
            type = "double",
            default = 0.8,
            minimum = 0.0,
            maximum = 1.0
        },
        mode = {
            title = "Mode",
            type = "enum",
            default = "normal",
            options = { "normal", "hold" }
        }
    },
    ui = {
        sections = {
            {
                id = "general",
                title = "General",
                description = "Primary controls",
                collapsible = true,
                default_expanded = false,
                columns = 2,
                items = {
                    {
                        setting = "enabled",
                        control = "switch"
                    },
                    {
                        setting = "opacity",
                        control = "slider",
                        step = 0.05,
                        unit = "%"
                    },
                    {
                        setting = "mode",
                        control = "segmented",
                        visible_when = {
                            setting = "enabled",
                            equals = true
                        }
                    }
                }
            }
        }
    }
}
)",
                     ScriptScope::profile);

    ASSERT_EQ("2", script.declarations().api_version);
    ASSERT_TRUE(script.declarations().ui.has_value());
    ASSERT_EQ(1U, script.declarations().ui->sections.size());
    const auto &section = script.declarations().ui->sections[0];
    EXPECT_EQ("general", section.id);
    EXPECT_TRUE(section.collapsible);
    EXPECT_FALSE(section.default_expanded);
    EXPECT_EQ(2U, section.columns);
    ASSERT_EQ(3U, section.items.size());
    EXPECT_EQ(external_peepsight::ScriptUiControlType::slider, section.items[1].control);
    EXPECT_EQ(0.05, section.items[1].step);
    ASSERT_TRUE(section.items[2].visible_when.has_value());
    EXPECT_EQ("enabled", section.items[2].visible_when->setting_id);
    EXPECT_EQ("true", section.items[2].visible_when->equals_value);
}

TEST(LuaScript, RejectsInvalidTrustedUiDeclarations)
{
    EXPECT_THROW(static_cast<void>(LuaScript(R"(
return eps.script {
    settings = {
        enabled = { title = "Enabled", type = "boolean", default = true }
    },
    ui = {
        sections = {
            { id = "general", title = "General", items = {
                { setting = "enabled", control = "switch" }
            } }
        }
    }
}
)",
                                             ScriptScope::profile)),
                 std::invalid_argument);

    EXPECT_THROW(static_cast<void>(LuaScript(R"(
return eps.script {
    api_version = "2",
    settings = {
        enabled = { title = "Enabled", type = "boolean", default = true }
    },
    ui = {
        sections = {
            { id = "general", title = "General", items = {
                { setting = "enabled", control = "slider" }
            } }
        }
    }
}
)",
                                             ScriptScope::profile)),
                 std::invalid_argument);

    EXPECT_THROW(static_cast<void>(LuaScript(R"(
return eps.script {
    api_version = "2",
    settings = {
        enabled = { title = "Enabled", type = "boolean", default = true },
        mode = { title = "Mode", type = "enum", default = "one", options = { "one", "two" } }
    },
    ui = {
        sections = {
            { id = "general", title = "General", items = {
                { setting = "enabled", control = "switch" }
            } }
        }
    }
}
)",
                                             ScriptScope::profile)),
                 std::invalid_argument);
}

TEST(LuaScript, AppliesVisibilityAndStagesProfileCommands)
{
    LuaScript script(std::string(kScript), ScriptScope::profile);
    const ScriptContext context{"set-a", "profile-a"};

    const auto started = script.start(context);
    const auto input = script.input("toggle", ScriptInputPhase::pressed, context);

    EXPECT_TRUE(started.succeeded);
    EXPECT_TRUE(started.allows_visible);
    EXPECT_TRUE(input.succeeded);
    EXPECT_FALSE(input.allows_visible);
    EXPECT_EQ(std::vector<ScriptCommand>({{ScriptCommandType::next_profile, {}}}), input.commands);
}

TEST(LuaScript, UsesPersistedSettingValues)
{
    LuaScript script(std::string(kScript), ScriptScope::profile,
                     {ScriptSettingValue{"enabled", ScriptSettingType::boolean, "false"}});

    const auto result = script.start({"set-a", "profile-a"});

    EXPECT_TRUE(result.succeeded);
    EXPECT_FALSE(result.allows_visible);
}

TEST(LuaScript, DoesNotExposeOperatingSystemLibraries)
{
    LuaScript script(R"(
return eps.script {
    on_start = function(ctx)
        if io ~= nil or os ~= nil or package ~= nil or debug ~= nil or load ~= nil then
            error("unsafe library is available")
        end
        ctx.visibility:set(false)
    end
}
)",
                     ScriptScope::global);

    const auto result = script.start({"set-a", "profile-a"});

    EXPECT_TRUE(result.succeeded);
    EXPECT_FALSE(result.allows_visible);
}

TEST(LuaScript, RejectsBinaryChunksAndInvalidDeclarations)
{
    EXPECT_THROW(static_cast<void>(LuaScript(std::string("\x1bLua", 4), ScriptScope::profile)), std::invalid_argument);
    EXPECT_THROW(static_cast<void>(LuaScript(R"(
return eps.script {
    bindings = { bad = { title = "Bad", events = { "unknown" } } }
}
)",
                                             ScriptScope::profile)),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(LuaScript(R"(
return eps.script {
    bindings = {
        bad = {
            title = "Bad",
            events = { "pressed" },
            default_key = { device = "keyboard", key = "Unknown" }
        }
    }
}
)",
                                             ScriptScope::profile)),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(LuaScript(R"(
return eps.script {
    bindings = {
        bad = {
            title = "Bad",
            events = { "pressed" },
            default_key = { device = "mouse", key = "x1", modifiers = { "ctrl", "ctrl" } }
        }
    }
}
)",
                                             ScriptScope::profile)),
                 std::invalid_argument);
}

TEST(LuaScript, StopsInfiniteCallbacksAndPreservesPreviousVisibility)
{
    LuaScript script(R"(
return eps.script {
    on_start = function(ctx)
        ctx.visibility:set(false)
    end,
    on_input = function(event, ctx)
        while true do end
    end
}
)",
                     ScriptScope::profile);
    ASSERT_TRUE(script.start({"set-a", "profile-a"}).succeeded);

    const auto result = script.input("anything", ScriptInputPhase::pressed, {"set-a", "profile-a"});

    EXPECT_FALSE(result.succeeded);
    EXPECT_FALSE(result.allows_visible);
    EXPECT_TRUE(result.commands.empty());
}

TEST(LuaScript, RejectsProfileSetSwitchOutsideGlobalScope)
{
    LuaScript script(R"(
return eps.script {
    on_start = function(ctx)
        ctx.profile_set:next()
    end
}
)",
                     ScriptScope::profile_set);

    const auto result = script.start({"set-a", "profile-a"});

    EXPECT_FALSE(result.succeeded);
    EXPECT_TRUE(result.commands.empty());
}

TEST(LuaScript, RejectsNonBooleanVisibilityValuesWithoutChangingState)
{
    LuaScript script(R"(
return eps.script {
    on_start = function(ctx)
        ctx.visibility:set("yes")
    end
}
)",
                     ScriptScope::profile);

    const auto result = script.start({"set-a", "profile-a"});

    EXPECT_FALSE(result.succeeded);
    EXPECT_TRUE(result.allows_visible);
}

TEST(LuaScript, RejectsPersistedSettingTypeChangesAndOutOfRangeValues)
{
    EXPECT_THROW(static_cast<void>(LuaScript(std::string(kScript), ScriptScope::profile,
                                             {ScriptSettingValue{"amount", ScriptSettingType::number, "2"}})),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(LuaScript(std::string(kScript), ScriptScope::profile,
                                             {ScriptSettingValue{"amount", ScriptSettingType::integer, "6"}})),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(LuaScript(
                     std::string(kScript), ScriptScope::profile,
                     {ScriptSettingValue{"amount", ScriptSettingType::integer, "999999999999999999999999999999"}})),
                 std::invalid_argument);
}

TEST(LuaScript, RejectsScriptsThatExceedTheMemoryLimit)
{
    EXPECT_THROW(static_cast<void>(LuaScript(R"(
local value = string.rep("x", 5 * 1024 * 1024)
return eps.script {}
)",
                                             ScriptScope::global)),
                 std::invalid_argument);
}
} // namespace

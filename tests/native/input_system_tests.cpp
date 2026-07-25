#include "input_system.h"

#include <winrt/base.h>

#include <gtest/gtest.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>

namespace
{
using external_peepsight::HotkeyStateMachine;
using external_peepsight::InputActivationMode;
using external_peepsight::InputCaptureBackend;
using external_peepsight::InputConfiguration;
using external_peepsight::InputDeviceKind;
using external_peepsight::InputHotkeyBinding;
using external_peepsight::InputKeyIdentity;
using external_peepsight::InputLogicalSwitch;
using external_peepsight::InputModifiers;
using external_peepsight::InputMouseButton;
using external_peepsight::InputSwitchOperation;
using external_peepsight::InputVisibilityRule;
using external_peepsight::RawInputBinding;
using external_peepsight::ScriptInputBinding;
using external_peepsight::ScriptInputEvent;
using external_peepsight::ScriptInputPhase;
using external_peepsight::ScriptInputStateMachine;
using external_peepsight::ScriptScope;

constexpr UINT kHookKeyboardMessage = WM_APP + 2U;
constexpr InputKeyIdentity kKeyA{InputDeviceKind::keyboard, 0x1EU, false, InputModifiers::none};
constexpr InputKeyIdentity kKeyB{InputDeviceKind::keyboard, 0x30U, false, InputModifiers::none};
constexpr InputKeyIdentity kCtrlKeyA{InputDeviceKind::keyboard, 0x1EU, false, InputModifiers::ctrl};
constexpr InputKeyIdentity kMouseX1{InputDeviceKind::mouse, static_cast<std::uint16_t>(InputMouseButton::x1), false,
                                    InputModifiers::none};

struct InputWindowSearch
{
    DWORD process_id;
    HWND window = nullptr;
};

BOOL CALLBACK find_input_window(_In_ const HWND window, _In_ const LPARAM parameter) noexcept
{
    auto *search = reinterpret_cast<InputWindowSearch *>(parameter);
    DWORD process_id = 0U;
    GetWindowThreadProcessId(window, &process_id);
    if (process_id != search->process_id)
    {
        return TRUE;
    }

    wchar_t class_name[64]{};
    if (GetClassNameW(window, class_name, static_cast<int>(std::size(class_name))) != 0 &&
        std::wstring_view(class_name) == L"ExternalPeepSight.Input.Window")
    {
        search->window = window;
        return FALSE;
    }
    return TRUE;
}

[[nodiscard]] HWND find_current_process_input_window()
{
    InputWindowSearch search{GetCurrentProcessId()};
    EnumWindows(find_input_window, reinterpret_cast<LPARAM>(&search));
    return search.window;
}

[[nodiscard]] InputConfiguration unbound_configuration()
{
    return {};
}

[[nodiscard]] InputHotkeyBinding toggle_binding(const InputKeyIdentity key)
{
    return {InputActivationMode::toggle, key, std::nullopt, std::nullopt, std::nullopt};
}

[[nodiscard]] InputHotkeyBinding independent_binding(const InputKeyIdentity enable, const InputKeyIdentity disable)
{
    return {InputActivationMode::independent, std::nullopt, enable, disable, std::nullopt};
}

[[nodiscard]] InputHotkeyBinding hold_binding(const InputKeyIdentity key)
{
    return {InputActivationMode::hold, std::nullopt, std::nullopt, std::nullopt, key};
}

[[nodiscard]] std::string valid_snapshot(const std::string_view modifiers = "none",
                                         const std::string_view selected_profile_id = "profile-a")
{
    return "{\"schemaVersion\":8,\"inputBackend\":\"rawInput\",\"activeProfileSetId\":\"set-a\","
           "\"profiles\":[{\"id\":\"profile-a\",\"switches\":{"
           "\"visibilityRule\":\"either\","
           "\"initialStateA\":false,\"initialStateB\":true,"
           "\"switchA\":{\"mode\":\"toggle\",\"toggleKey\":{\"device\":\"keyboard\",\"code\":30,"
           "\"extended\":false,\"modifiers\":\"" +
           std::string(modifiers) +
           "\"},\"enableKey\":null,\"disableKey\":null,\"holdKey\":null},"
           "\"switchB\":{\"mode\":\"unbound\",\"toggleKey\":null,\"enableKey\":null,\"disableKey\":null,"
           "\"holdKey\":null}}},{\"id\":\"profile-b\",\"switches\":{\"visibilityRule\":\"both\","
           "\"initialStateA\":true,\"initialStateB\":true,"
           "\"switchA\":{\"mode\":\"unbound\",\"toggleKey\":null,\"enableKey\":null,\"disableKey\":null,"
           "\"holdKey\":null},\"switchB\":{\"mode\":\"unbound\",\"toggleKey\":null,\"enableKey\":null,"
           "\"disableKey\":null,\"holdKey\":null}}}],\"profileSets\":[{\"selectedProfileId\":\"" +
           std::string(selected_profile_id) + "\",\"id\":\"set-a\"}]}";
}

TEST(HotkeyStateMachine, ToggleChangesOnceAndSuppressesRepeatedKeyDown)
{
    HotkeyStateMachine state_machine;
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(kKeyA);
    static_cast<void>(
        state_machine.configure(configuration, {{kKeyA, {InputLogicalSwitch::a, InputSwitchOperation::toggle}}}));

    const auto first = state_machine.handle_key(kKeyA, true);
    const auto repeated = state_machine.handle_key(kKeyA, true);

    ASSERT_TRUE(first.has_value());
    EXPECT_TRUE(first->switch_a);
    EXPECT_FALSE(repeated.has_value());
}

TEST(HotkeyStateMachine, MouseToggleUsesTheSameRepeatAndReleaseRules)
{
    HotkeyStateMachine state_machine;
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(kMouseX1);
    static_cast<void>(
        state_machine.configure(configuration, {{kMouseX1, {InputLogicalSwitch::a, InputSwitchOperation::toggle}}}));

    const auto pressed = state_machine.handle_key(kMouseX1, true);
    const auto repeated = state_machine.handle_key(kMouseX1, true);
    static_cast<void>(state_machine.handle_key(kMouseX1, false));
    const auto pressed_again = state_machine.handle_key(kMouseX1, true);

    ASSERT_TRUE(pressed.has_value());
    EXPECT_TRUE(pressed->switch_a);
    EXPECT_FALSE(repeated.has_value());
    ASSERT_TRUE(pressed_again.has_value());
    EXPECT_FALSE(pressed_again->switch_a);
}

TEST(HotkeyStateMachine, IndependentBindingsEnableAndDisableTheirSwitch)
{
    HotkeyStateMachine state_machine;
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = independent_binding(kKeyA, kKeyB);
    static_cast<void>(
        state_machine.configure(configuration, {{kKeyA, {InputLogicalSwitch::a, InputSwitchOperation::enable}},
                                                {kKeyB, {InputLogicalSwitch::a, InputSwitchOperation::disable}}}));

    ASSERT_TRUE(state_machine.handle_key(kKeyA, true).has_value());
    static_cast<void>(state_machine.handle_key(kKeyA, false));
    const auto disabled = state_machine.handle_key(kKeyB, true);

    ASSERT_TRUE(disabled.has_value());
    EXPECT_FALSE(disabled->switch_a);
}

TEST(HotkeyStateMachine, HoldClearsOnKeyUpEvenAfterModifierStateChanges)
{
    HotkeyStateMachine state_machine;
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = hold_binding(kCtrlKeyA);
    static_cast<void>(
        state_machine.configure(configuration, {{kCtrlKeyA, {InputLogicalSwitch::a, InputSwitchOperation::hold}}}));

    const auto pressed = state_machine.handle_key(kCtrlKeyA, true);
    const auto released =
        state_machine.handle_key({kCtrlKeyA.device, kCtrlKeyA.code, kCtrlKeyA.extended, InputModifiers::none}, false);

    ASSERT_TRUE(pressed.has_value());
    EXPECT_TRUE(pressed->switch_a);
    ASSERT_TRUE(released.has_value());
    EXPECT_FALSE(released->switch_a);
}

TEST(HotkeyStateMachine, ResetClearsAnActiveHold)
{
    HotkeyStateMachine state_machine;
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_b = hold_binding(kKeyB);
    static_cast<void>(
        state_machine.configure(configuration, {{kKeyB, {InputLogicalSwitch::b, InputSwitchOperation::hold}}}));
    static_cast<void>(state_machine.handle_key(kKeyB, true));

    const auto reset = state_machine.reset_pressed_keys();

    ASSERT_TRUE(reset.has_value());
    EXPECT_FALSE(reset->switch_b);
}

TEST(HotkeyStateMachine, ReconfigurationRestoresInitialStateAndVisibilityRule)
{
    HotkeyStateMachine state_machine;
    InputConfiguration configuration = unbound_configuration();
    configuration.visibility_rule = InputVisibilityRule::both;
    configuration.initial_state_a = true;
    configuration.initial_state_b = false;

    const auto snapshot = state_machine.configure(configuration, {});

    EXPECT_TRUE(snapshot.switch_a);
    EXPECT_FALSE(snapshot.switch_b);
    EXPECT_FALSE(snapshot.visible);
}

TEST(HotkeyStateMachine, RegisteredActionsApplyAndRejectHold)
{
    HotkeyStateMachine state_machine;
    static_cast<void>(state_machine.configure(unbound_configuration(), {}));

    const auto enabled = state_machine.trigger_registered({InputLogicalSwitch::a, InputSwitchOperation::enable});
    const auto toggled = state_machine.trigger_registered({InputLogicalSwitch::a, InputSwitchOperation::toggle});

    ASSERT_TRUE(enabled.has_value());
    EXPECT_TRUE(enabled->switch_a);
    ASSERT_TRUE(toggled.has_value());
    EXPECT_FALSE(toggled->switch_a);
    EXPECT_THROW(
        static_cast<void>(state_machine.trigger_registered({InputLogicalSwitch::a, InputSwitchOperation::hold})),
        std::invalid_argument);
}

TEST(InputVisibility, EvaluatesEveryVisibilityRule)
{
    EXPECT_TRUE(external_peepsight::evaluate_input_visibility(InputVisibilityRule::switch_a, true, false));
    EXPECT_TRUE(external_peepsight::evaluate_input_visibility(InputVisibilityRule::switch_b, false, true));
    EXPECT_TRUE(external_peepsight::evaluate_input_visibility(InputVisibilityRule::both, true, true));
    EXPECT_FALSE(external_peepsight::evaluate_input_visibility(InputVisibilityRule::both, true, false));
    EXPECT_TRUE(external_peepsight::evaluate_input_visibility(InputVisibilityRule::either, false, true));
    EXPECT_FALSE(external_peepsight::evaluate_input_visibility(InputVisibilityRule::either, false, false));
}

TEST(InputBindingPlan, AssignsModifiedActionsToRegisterHotKeyAndBareOrHoldActionsToRawInput)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(kCtrlKeyA);
    configuration.switch_b = hold_binding(kKeyB);

    const auto plan = external_peepsight::build_input_binding_plan(configuration);

    ASSERT_EQ(1U, plan.registered_hotkeys.size());
    EXPECT_EQ(kCtrlKeyA, plan.registered_hotkeys.front().key);
    ASSERT_EQ(1U, plan.raw_bindings.size());
    EXPECT_EQ(kKeyB, plan.raw_bindings.front().key);
}

TEST(InputBindingPlan, AssignsMouseBindingsToRawInputEvenWithModifiers)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(
        {InputDeviceKind::mouse, static_cast<std::uint16_t>(InputMouseButton::right), false, InputModifiers::ctrl});

    const auto plan = external_peepsight::build_input_binding_plan(configuration);

    ASSERT_EQ(1U, plan.raw_bindings.size());
    EXPECT_TRUE(plan.registered_hotkeys.empty());
}

TEST(InputBindingPlan, LowLevelHookRoutesModifiedKeyboardBindingsToHookTransitions)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.input_backend = InputCaptureBackend::low_level_hook;
    configuration.switch_a = toggle_binding(kCtrlKeyA);

    const auto plan = external_peepsight::build_input_binding_plan(configuration);

    ASSERT_EQ(1U, plan.raw_bindings.size());
    EXPECT_EQ(kCtrlKeyA, plan.raw_bindings.front().key);
    EXPECT_TRUE(plan.registered_hotkeys.empty());
}

TEST(InputBindingPlan, LowLevelHookAddsMainKeyAndPhysicalModifierPollingKeys)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.input_backend = InputCaptureBackend::low_level_hook;
    configuration.switch_a = toggle_binding(kCtrlKeyA);

    const auto plan = external_peepsight::build_input_binding_plan(configuration);

    ASSERT_EQ(3U, plan.polling_keys.size());
    EXPECT_NE(std::ranges::find(plan.polling_keys,
                                external_peepsight::InputPollingKey{{InputDeviceKind::keyboard, 0x1EU, false}, 0x41U}),
              plan.polling_keys.end());
    EXPECT_NE(
        std::ranges::find(plan.polling_keys,
                          external_peepsight::InputPollingKey{{InputDeviceKind::keyboard, 0x1DU, false}, VK_LCONTROL}),
        plan.polling_keys.end());
    EXPECT_NE(
        std::ranges::find(plan.polling_keys,
                          external_peepsight::InputPollingKey{{InputDeviceKind::keyboard, 0x1DU, true}, VK_RCONTROL}),
        plan.polling_keys.end());
}

TEST(InputBindingPlan, LowLevelHookDeduplicatesPollingKeysAcrossModifiedBindings)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.input_backend = InputCaptureBackend::low_level_hook;
    configuration.switch_a = toggle_binding(kCtrlKeyA);
    configuration.switch_b = toggle_binding({InputDeviceKind::keyboard, 0x1EU, false, InputModifiers::shift});

    const auto plan = external_peepsight::build_input_binding_plan(configuration);

    EXPECT_EQ(5U, plan.polling_keys.size());
    EXPECT_EQ(1U, std::ranges::count(plan.polling_keys, external_peepsight::InputPollingKey{
                                                            {InputDeviceKind::keyboard, 0x1EU, false}, 0x41U}));
}

TEST(InputBindingPlan, RawInputDoesNotCreateKeyboardPollingPlan)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(kCtrlKeyA);

    const auto plan = external_peepsight::build_input_binding_plan(configuration);

    EXPECT_TRUE(plan.polling_keys.empty());
}

TEST(InputBindingPlan, RejectsInvalidMouseButtonCodesAndKeyboardExtendedFlag)
{
    InputConfiguration zero_code = unbound_configuration();
    zero_code.switch_a = toggle_binding({InputDeviceKind::mouse, 0U, false, InputModifiers::none});
    InputConfiguration out_of_range_code = unbound_configuration();
    out_of_range_code.switch_a = toggle_binding({InputDeviceKind::mouse, 6U, false, InputModifiers::none});
    InputConfiguration extended_mouse = unbound_configuration();
    extended_mouse.switch_a = toggle_binding(
        {InputDeviceKind::mouse, static_cast<std::uint16_t>(InputMouseButton::left), true, InputModifiers::none});

    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(zero_code)), std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(out_of_range_code)),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(extended_mouse)),
                 std::invalid_argument);
}

TEST(InputBindingPlan, RejectsDuplicateBindingsAcrossSwitches)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(kKeyA);
    configuration.switch_b = hold_binding(kKeyA);

    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(configuration)), std::invalid_argument);
}

TEST(InputBindingPlan, RejectsDuplicateMouseBindingsAcrossSwitches)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(kMouseX1);
    configuration.switch_b = hold_binding(kMouseX1);

    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(configuration)), std::invalid_argument);
}

TEST(InputBindingPlan, IncludesScriptBindingsAndRejectsConflictsWithBasicBindings)
{
    InputConfiguration configuration = unbound_configuration();
    configuration.script_bindings.push_back({kMouseX1, ScriptScope::global, "global", "toggle", true, true});

    const auto plan = external_peepsight::build_input_binding_plan(configuration);

    ASSERT_EQ(1U, plan.script_bindings.size());
    EXPECT_EQ(kMouseX1, plan.script_bindings.front().key);

    configuration.switch_a = toggle_binding(kMouseX1);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(configuration)), std::invalid_argument);
}

TEST(InputBindingPlan, RejectsScriptBindingsWithoutIdentifiersOrEvents)
{
    InputConfiguration missing_identifier = unbound_configuration();
    missing_identifier.script_bindings.push_back({kKeyA, ScriptScope::profile, "", "toggle", true, false});
    InputConfiguration missing_phase = unbound_configuration();
    missing_phase.script_bindings.push_back({kKeyA, ScriptScope::profile, "profile-a", "toggle", false, false});

    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(missing_identifier)),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(missing_phase)), std::invalid_argument);
}

TEST(ScriptInputStateMachine, PublishesPressedAndReleasedAndSuppressesRepeats)
{
    ScriptInputStateMachine state_machine;
    state_machine.configure({{kCtrlKeyA, ScriptScope::profile, "profile-a", "toggle", true, true}});

    const auto pressed = state_machine.handle_key(kCtrlKeyA, true);
    const auto repeated = state_machine.handle_key(kCtrlKeyA, true);
    const auto released =
        state_machine.handle_key({kKeyA.device, kKeyA.code, kKeyA.extended, InputModifiers::none}, false);

    ASSERT_TRUE(pressed.has_value());
    EXPECT_EQ(ScriptInputPhase::pressed, pressed->phase);
    EXPECT_FALSE(repeated.has_value());
    ASSERT_TRUE(released.has_value());
    EXPECT_EQ(ScriptInputPhase::released, released->phase);
}

TEST(ScriptInputStateMachine, ResetPublishesDeclaredReleaseEvents)
{
    ScriptInputStateMachine state_machine;
    state_machine.configure({{kMouseX1, ScriptScope::profile_set, "set-a", "hold", false, true}});
    EXPECT_FALSE(state_machine.handle_key(kMouseX1, true).has_value());

    const std::vector<ScriptInputEvent> released = state_machine.reset_pressed_keys();

    ASSERT_EQ(1U, released.size());
    EXPECT_EQ(ScriptScope::profile_set, released.front().scope);
    EXPECT_EQ("hold", released.front().binding_id);
    EXPECT_EQ(ScriptInputPhase::released, released.front().phase);
}

TEST(InputBindingPlan, RejectsModeSpecificMissingAndExtraKeys)
{
    InputConfiguration unbound_with_key = unbound_configuration();
    unbound_with_key.switch_a.toggle_key = kKeyA;
    InputConfiguration toggle_with_extra = unbound_configuration();
    toggle_with_extra.switch_a = toggle_binding(kKeyA);
    toggle_with_extra.switch_a.hold_key = kKeyB;
    InputConfiguration independent_missing_key = unbound_configuration();
    independent_missing_key.switch_a = independent_binding(kKeyA, kKeyB);
    independent_missing_key.switch_a.disable_key.reset();

    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(unbound_with_key)),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(toggle_with_extra)),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(independent_missing_key)),
                 std::invalid_argument);
}

TEST(InputBindingPlan, RejectsSystemReservedShortcuts)
{
    InputConfiguration win_binding = unbound_configuration();
    win_binding.switch_a = toggle_binding({InputDeviceKind::keyboard, 0x1EU, false, InputModifiers::win});
    InputConfiguration bare_windows_key = unbound_configuration();
    bare_windows_key.switch_a = toggle_binding({InputDeviceKind::keyboard, 0x5BU, true, InputModifiers::none});
    InputConfiguration alt_tab = unbound_configuration();
    alt_tab.switch_a = toggle_binding({InputDeviceKind::keyboard, 0x0FU, false, InputModifiers::alt});
    InputConfiguration f12 = unbound_configuration();
    f12.switch_a = toggle_binding({InputDeviceKind::keyboard, 0x58U, false, InputModifiers::none});

    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(win_binding)), std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(bare_windows_key)),
                 std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(alt_tab)), std::invalid_argument);
    EXPECT_THROW(static_cast<void>(external_peepsight::build_input_binding_plan(f12)), std::invalid_argument);
}

TEST(InputConfigurationParser, AcceptsFlagEnumFormattingAndRejectsUnknownContent)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);

    const InputConfiguration parsed = external_peepsight::parse_input_configuration(valid_snapshot("ctrl, shift"));
    EXPECT_EQ(InputCaptureBackend::raw_input, parsed.input_backend);
    EXPECT_EQ(InputModifiers::ctrl | InputModifiers::shift, parsed.switch_a.toggle_key->modifiers);

    std::string unknown_modifier = valid_snapshot("meta");
    EXPECT_THROW(static_cast<void>(external_peepsight::parse_input_configuration(unknown_modifier)),
                 std::invalid_argument);

    std::string unknown_property = valid_snapshot();
    unknown_property.replace(unknown_property.find("\"initialStateA\""), std::string("\"initialStateA\"").size(),
                             "\"unexpected\"");
    EXPECT_THROW(static_cast<void>(external_peepsight::parse_input_configuration(unknown_property)),
                 std::invalid_argument);
}

TEST(InputConfigurationParser, UsesSwitchesFromSelectedProfile)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);

    const InputConfiguration parsed =
        external_peepsight::parse_input_configuration(valid_snapshot("none", "profile-b"));

    EXPECT_EQ(InputVisibilityRule::both, parsed.visibility_rule);
    EXPECT_TRUE(parsed.initial_state_a);
    EXPECT_TRUE(parsed.initial_state_b);
    EXPECT_EQ(InputActivationMode::unbound, parsed.switch_a.mode);
}

TEST(InputConfigurationParser, ParsesMouseButtonIdentity)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    std::string snapshot = valid_snapshot();
    const std::string keyboard = "\"device\":\"keyboard\",\"code\":30";
    snapshot.replace(snapshot.find(keyboard), keyboard.size(), "\"device\":\"mouse\",\"code\":4");

    const InputConfiguration parsed = external_peepsight::parse_input_configuration(snapshot);

    ASSERT_TRUE(parsed.switch_a.toggle_key.has_value());
    EXPECT_EQ(InputDeviceKind::mouse, parsed.switch_a.toggle_key->device);
    EXPECT_EQ(static_cast<std::uint16_t>(InputMouseButton::x1), parsed.switch_a.toggle_key->code);
}

TEST(InputConfigurationParser, RejectsUnknownInputBackend)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    std::string snapshot = valid_snapshot();
    snapshot.replace(snapshot.find("\"rawInput\""), std::string("\"rawInput\"").size(), "\"unsupported\"");

    EXPECT_THROW(static_cast<void>(external_peepsight::parse_input_configuration(snapshot)), std::invalid_argument);
}

TEST(RawKeyboardInput, AcceptsKeyboardPayloadSmallerThanRawInputUnion)
{
    RAWINPUT input{};
    input.header.dwType = RIM_TYPEKEYBOARD;
    input.data.keyboard.MakeCode = 0x31U;
    input.data.keyboard.Flags = 0U;
    constexpr std::size_t keyboard_payload_size = offsetof(RAWINPUT, data) + sizeof(RAWKEYBOARD);
    ASSERT_LT(keyboard_payload_size, sizeof(RAWINPUT));

    const auto *bytes = reinterpret_cast<const std::byte *>(&input);
    const std::vector<external_peepsight::RawInputTransition> transitions =
        external_peepsight::parse_raw_input({bytes, keyboard_payload_size});

    ASSERT_EQ(1U, transitions.size());
    EXPECT_EQ((external_peepsight::InputPhysicalKey{InputDeviceKind::keyboard, 0x31U, false}), transitions[0].key);
    EXPECT_TRUE(transitions[0].pressed);
}

TEST(RawKeyboardInput, NormalizesArrowUpWhenExtendedFlagIsMissing)
{
    RAWINPUT input{};
    input.header.dwType = RIM_TYPEKEYBOARD;
    input.data.keyboard.VKey = VK_UP;
    input.data.keyboard.MakeCode = 0x48U;
    input.data.keyboard.Flags = 0U;
    constexpr std::size_t keyboard_payload_size = offsetof(RAWINPUT, data) + sizeof(RAWKEYBOARD);

    const auto *bytes = reinterpret_cast<const std::byte *>(&input);
    const std::vector<external_peepsight::RawInputTransition> transitions =
        external_peepsight::parse_raw_input({bytes, keyboard_payload_size});

    ASSERT_EQ(1U, transitions.size());
    EXPECT_EQ((external_peepsight::InputPhysicalKey{InputDeviceKind::keyboard, 0x48U, true}), transitions[0].key);
    EXPECT_TRUE(transitions[0].pressed);
}

TEST(RawMouseInput, DecodesMultipleButtonTransitions)
{
    RAWINPUT input{};
    input.header.dwType = RIM_TYPEMOUSE;
    input.data.mouse.usButtonFlags = RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_BUTTON_4_UP;
    constexpr std::size_t mouse_payload_size = offsetof(RAWINPUT, data) + sizeof(RAWMOUSE);

    const auto *bytes = reinterpret_cast<const std::byte *>(&input);
    const std::vector<external_peepsight::RawInputTransition> transitions =
        external_peepsight::parse_raw_input({bytes, mouse_payload_size});

    ASSERT_EQ(2U, transitions.size());
    EXPECT_EQ((external_peepsight::InputPhysicalKey{InputDeviceKind::mouse,
                                                    static_cast<std::uint16_t>(InputMouseButton::left), false}),
              transitions[0].key);
    EXPECT_TRUE(transitions[0].pressed);
    EXPECT_EQ(kMouseX1.device, transitions[1].key.device);
    EXPECT_EQ(kMouseX1.code, transitions[1].key.code);
    EXPECT_FALSE(transitions[1].pressed);
}

TEST(LowLevelKeyboardInput, AcceptsInjectedKeyboardTransitions)
{
    KBDLLHOOKSTRUCT event{};
    event.vkCode = 0x4EU;
    event.scanCode = 0x31U;
    event.flags = LLKHF_INJECTED;

    const std::optional<external_peepsight::RawInputTransition> transition =
        external_peepsight::decode_low_level_keyboard_input(WM_KEYDOWN, event);

    ASSERT_TRUE(transition.has_value());
    EXPECT_EQ((external_peepsight::InputPhysicalKey{InputDeviceKind::keyboard, 0x31U, false}), transition->key);
    EXPECT_TRUE(transition->pressed);
}

TEST(LowLevelKeyboardInput, MapsInjectedExtendedKeyWhenScanCodeIsMissing)
{
    KBDLLHOOKSTRUCT event{};
    event.vkCode = VK_UP;
    event.flags = LLKHF_INJECTED;

    const std::optional<external_peepsight::RawInputTransition> transition =
        external_peepsight::decode_low_level_keyboard_input(WM_KEYUP, event);

    ASSERT_TRUE(transition.has_value());
    EXPECT_EQ((external_peepsight::InputPhysicalKey{InputDeviceKind::keyboard, 0x48U, true}), transition->key);
    EXPECT_FALSE(transition->pressed);
}

TEST(LowLevelMouseInput, AcceptsInjectedMouseTransitions)
{
    MSLLHOOKSTRUCT event{};
    event.mouseData = static_cast<DWORD>(XBUTTON1) << 16U;
    event.flags = LLMHF_INJECTED;

    const std::optional<external_peepsight::RawInputTransition> transition =
        external_peepsight::decode_low_level_mouse_input(WM_XBUTTONDOWN, event);

    ASSERT_TRUE(transition.has_value());
    EXPECT_EQ(kMouseX1.device, transition->key.device);
    EXPECT_EQ(kMouseX1.code, transition->key.code);
    EXPECT_TRUE(transition->pressed);
}

TEST(GlobalInputService, StartsAppliesConfigurationAndAllowsRepeatedStop)
{
    std::atomic<unsigned int> callback_count = 0U;
    std::atomic<bool> last_visible = false;
    external_peepsight::GlobalInputService service(
        [&callback_count, &last_visible](const external_peepsight::InputStateSnapshot state)
        {
            last_visible.store(state.visible, std::memory_order_release);
            callback_count.fetch_add(1U, std::memory_order_relaxed);
        });
    InputConfiguration configuration = unbound_configuration();
    configuration.initial_state_a = true;

    service.start();
    const external_peepsight::InputApplyResult result = service.apply_configuration(configuration);
    service.stop();
    service.stop();

    EXPECT_TRUE(result.applied);
    EXPECT_EQ(ERROR_SUCCESS, result.win32_error);
    EXPECT_GE(callback_count.load(std::memory_order_relaxed), 1U);
    EXPECT_TRUE(last_visible.load(std::memory_order_acquire));
}

TEST(GlobalInputService, RawInputBackendAcceptsLowLevelKeyboardRedundancy)
{
    std::atomic<bool> last_visible = false;
    external_peepsight::GlobalInputService service([&last_visible](const external_peepsight::InputStateSnapshot state)
                                                   { last_visible.store(state.visible, std::memory_order_release); });
    InputConfiguration configuration = unbound_configuration();
    configuration.switch_a = toggle_binding(kKeyA);

    service.start();
    const external_peepsight::InputApplyResult result = service.apply_configuration(configuration);
    const HWND input_window = find_current_process_input_window();
    const LPARAM packed_key_down = static_cast<LPARAM>(kKeyA.code | (1U << 17U));
    SendMessageW(input_window, kHookKeyboardMessage, 0U, packed_key_down);
    service.stop();

    ASSERT_TRUE(result.applied);
    ASSERT_NE(nullptr, input_window);
    EXPECT_TRUE(last_visible.load(std::memory_order_acquire));
}

TEST(GlobalInputService, InvalidRawInputEventDoesNotStopConfigurationUpdates)
{
    external_peepsight::GlobalInputService service([](const external_peepsight::InputStateSnapshot) {});
    service.start();

    const HWND input_window = find_current_process_input_window();
    ASSERT_NE(nullptr, input_window);
    SendMessageW(input_window, WM_INPUT, RIM_INPUT, reinterpret_cast<LPARAM>(INVALID_HANDLE_VALUE));

    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    const external_peepsight::InputApplyResult result = service.apply_configuration(unbound_configuration());
    service.stop();

    EXPECT_TRUE(result.applied);
    EXPECT_EQ(ERROR_SUCCESS, result.win32_error);
}
} // namespace

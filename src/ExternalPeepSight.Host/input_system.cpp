#include "input_system.h"

#include "diagnostics.h"

#include <shellapi.h>
#include <wtsapi32.h>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <cstring>
#include <exception>
#include <format>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <utility>

namespace external_peepsight
{
namespace
{
using winrt::Windows::Data::Json::IJsonValue;
using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

constexpr wchar_t kInputWindowClassName[] = L"ExternalPeepSight.Input.Window";
constexpr UINT kApplyConfigurationMessage = WM_APP + 1U;
constexpr UINT kHookKeyboardMessage = WM_APP + 2U;
constexpr UINT kHookMouseMessage = WM_APP + 3U;
constexpr UINT_PTR kKeyboardPollingTimerId = 1U;
constexpr int kFirstHotkeyIdentifier = 100;
constexpr DWORD kApplyConfigurationTimeoutMs = 5'000U;
constexpr UINT kKeyboardPollingIntervalMs = 12U;
constexpr InputModifiers kKnownModifiers =
    InputModifiers::ctrl | InputModifiers::alt | InputModifiers::shift | InputModifiers::win;

struct ApplyConfigurationRequest
{
    const InputConfiguration *configuration;
    InputApplyResult result;
};

[[nodiscard]] bool has_modifier(const InputModifiers value, const InputModifiers modifier) noexcept
{
    return (value & modifier) != InputModifiers::none;
}

[[nodiscard]] InputModifiers without_modifier(const InputModifiers value, const InputModifiers modifier) noexcept
{
    return static_cast<InputModifiers>(static_cast<std::uint8_t>(value) & ~static_cast<std::uint8_t>(modifier));
}

void require_allowed_properties(const JsonObject &object, const std::initializer_list<std::wstring_view> allowed)
{
    for (const auto &entry : object)
    {
        const std::wstring name(entry.Key());
        const bool accepted = std::ranges::any_of(allowed, [&name](const std::wstring_view allowed_name)
                                                  { return name == allowed_name; });
        if (!accepted)
        {
            throw std::invalid_argument("Input configuration contains an unknown property.");
        }
    }
}

[[nodiscard]] std::uint16_t get_input_code(const JsonObject &object)
{
    const double value = object.GetNamedNumber(L"code");
    if (value < 1.0 || value > static_cast<double>((std::numeric_limits<std::uint16_t>::max)()) ||
        std::floor(value) != value)
    {
        throw std::invalid_argument("Input code is outside the supported range.");
    }
    return static_cast<std::uint16_t>(value);
}

[[nodiscard]] InputDeviceKind parse_device(const std::wstring_view value)
{
    if (value == L"keyboard")
    {
        return InputDeviceKind::keyboard;
    }
    if (value == L"mouse")
    {
        return InputDeviceKind::mouse;
    }
    throw std::invalid_argument("Input device is invalid.");
}

[[nodiscard]] InputModifiers parse_modifiers(const std::wstring_view encoded)
{
    if (encoded.empty() || encoded == L"none")
    {
        return InputModifiers::none;
    }

    InputModifiers modifiers = InputModifiers::none;
    std::size_t offset = 0U;
    while (offset < encoded.size())
    {
        const std::size_t separator = encoded.find(L',', offset);
        const std::size_t end = separator == std::wstring_view::npos ? encoded.size() : separator;
        std::wstring_view token = encoded.substr(offset, end - offset);
        while (!token.empty() && token.front() == L' ')
        {
            token.remove_prefix(1U);
        }
        while (!token.empty() && token.back() == L' ')
        {
            token.remove_suffix(1U);
        }

        InputModifiers parsed = InputModifiers::none;
        if (token == L"ctrl")
        {
            parsed = InputModifiers::ctrl;
        }
        else if (token == L"alt")
        {
            parsed = InputModifiers::alt;
        }
        else if (token == L"shift")
        {
            parsed = InputModifiers::shift;
        }
        else if (token == L"win")
        {
            parsed = InputModifiers::win;
        }
        else
        {
            throw std::invalid_argument("Input modifiers contain an unknown value.");
        }

        if (has_modifier(modifiers, parsed))
        {
            throw std::invalid_argument("Input modifiers contain a duplicate value.");
        }
        modifiers = modifiers | parsed;
        offset = separator == std::wstring_view::npos ? encoded.size() : separator + 1U;
    }
    return modifiers;
}

[[nodiscard]] std::optional<InputKeyIdentity> parse_optional_key(const JsonObject &object,
                                                                 const std::wstring_view property_name)
{
    const IJsonValue value = object.GetNamedValue(property_name);
    if (value.ValueType() == JsonValueType::Null)
    {
        return std::nullopt;
    }
    if (value.ValueType() != JsonValueType::Object)
    {
        throw std::invalid_argument("Input key must be an object or null.");
    }

    const JsonObject key = value.GetObject();
    require_allowed_properties(key, {L"device", L"code", L"extended", L"modifiers"});
    return InputKeyIdentity{parse_device(key.GetNamedString(L"device")), get_input_code(key),
                            key.GetNamedBoolean(L"extended"), parse_modifiers(key.GetNamedString(L"modifiers"))};
}

[[nodiscard]] InputActivationMode parse_activation_mode(const std::wstring_view value)
{
    if (value == L"unbound")
    {
        return InputActivationMode::unbound;
    }
    if (value == L"independent")
    {
        return InputActivationMode::independent;
    }
    if (value == L"toggle")
    {
        return InputActivationMode::toggle;
    }
    if (value == L"hold")
    {
        return InputActivationMode::hold;
    }
    throw std::invalid_argument("Input activation mode is invalid.");
}

[[nodiscard]] InputHotkeyBinding parse_binding(const JsonObject &object)
{
    require_allowed_properties(object, {L"mode", L"toggleKey", L"enableKey", L"disableKey", L"holdKey"});
    return {parse_activation_mode(object.GetNamedString(L"mode")), parse_optional_key(object, L"toggleKey"),
            parse_optional_key(object, L"enableKey"), parse_optional_key(object, L"disableKey"),
            parse_optional_key(object, L"holdKey")};
}

[[nodiscard]] InputVisibilityRule parse_visibility_rule(const std::wstring_view value)
{
    if (value == L"switchA")
    {
        return InputVisibilityRule::switch_a;
    }
    if (value == L"switchB")
    {
        return InputVisibilityRule::switch_b;
    }
    if (value == L"both")
    {
        return InputVisibilityRule::both;
    }
    if (value == L"either")
    {
        return InputVisibilityRule::either;
    }
    throw std::invalid_argument("Input visibility rule is invalid.");
}

[[nodiscard]] InputCaptureBackend parse_input_backend(const std::wstring_view value)
{
    if (value == L"rawInput")
    {
        return InputCaptureBackend::raw_input;
    }
    if (value == L"lowLevelHook")
    {
        return InputCaptureBackend::low_level_hook;
    }
    throw std::invalid_argument("Input capture backend is invalid.");
}

[[nodiscard]] bool is_modifier_key(const InputKeyIdentity &key) noexcept
{
    if (key.device != InputDeviceKind::keyboard)
    {
        return false;
    }

    return (!key.extended && (key.code == 0x1DU || key.code == 0x2AU || key.code == 0x36U || key.code == 0x38U)) ||
           (key.extended && (key.code == 0x1DU || key.code == 0x38U || key.code == 0x5BU || key.code == 0x5CU));
}

[[nodiscard]] bool is_system_reserved(const InputKeyIdentity &key) noexcept
{
    if (key.device != InputDeviceKind::keyboard)
    {
        return false;
    }

    constexpr std::uint16_t f12_scan_code = 0x58U;
    const bool primary_windows_key = key.extended && (key.code == 0x5BU || key.code == 0x5CU);
    if (has_modifier(key.modifiers, InputModifiers::win) || primary_windows_key || key.code == f12_scan_code)
    {
        return true;
    }

    const bool alt = has_modifier(key.modifiers, InputModifiers::alt);
    const bool ctrl = has_modifier(key.modifiers, InputModifiers::ctrl);
    if ((alt && (key.code == 0x0FU || key.code == 0x01U)) || (ctrl && !alt && key.code == 0x01U))
    {
        return true;
    }
    return ctrl && alt && key.extended && key.code == 0x53U;
}

void validate_key(const InputKeyIdentity &key)
{
    const bool valid_mouse_button = key.code >= static_cast<std::uint16_t>(InputMouseButton::left) &&
                                    key.code <= static_cast<std::uint16_t>(InputMouseButton::x2);
    if (key.code == 0U || (key.modifiers & kKnownModifiers) != key.modifiers ||
        (key.device == InputDeviceKind::mouse && (!valid_mouse_button || key.extended)))
    {
        throw std::invalid_argument("Input key identity is invalid.");
    }
    if (is_system_reserved(key))
    {
        throw std::invalid_argument("Input key uses a system-reserved shortcut.");
    }
}

[[nodiscard]] UINT native_modifiers(const InputModifiers modifiers) noexcept
{
    UINT result = MOD_NOREPEAT;
    if (has_modifier(modifiers, InputModifiers::ctrl))
    {
        result |= MOD_CONTROL;
    }
    if (has_modifier(modifiers, InputModifiers::alt))
    {
        result |= MOD_ALT;
    }
    if (has_modifier(modifiers, InputModifiers::shift))
    {
        result |= MOD_SHIFT;
    }
    if (has_modifier(modifiers, InputModifiers::win))
    {
        result |= MOD_WIN;
    }
    return result;
}

[[nodiscard]] UINT virtual_key(const InputKeyIdentity &key)
{
    if (key.device != InputDeviceKind::keyboard)
    {
        throw std::invalid_argument("Only keyboard inputs can be registered with RegisterHotKey.");
    }

    const UINT encoded_scan_code = key.extended ? static_cast<UINT>(key.code) | 0xE000U : key.code;
    const UINT result = MapVirtualKeyW(encoded_scan_code, MAPVK_VSC_TO_VK_EX);
    if (result == 0U)
    {
        throw std::invalid_argument("Input scan code cannot be mapped to a virtual key.");
    }
    return result;
}

void add_action(InputBindingPlan &plan, std::vector<InputKeyIdentity> &used_keys, const InputKeyIdentity &key,
                const InputAction action, const InputCaptureBackend backend, int &next_hotkey_identifier)
{
    validate_key(key);
    if (std::ranges::find(used_keys, key) != used_keys.end())
    {
        throw std::invalid_argument("Input configuration assigns the same key to multiple actions.");
    }
    used_keys.push_back(key);

    const bool requires_raw_input =
        backend == InputCaptureBackend::low_level_hook || key.device == InputDeviceKind::mouse ||
        action.operation == InputSwitchOperation::hold || key.modifiers == InputModifiers::none;
    if (requires_raw_input)
    {
        plan.raw_bindings.push_back({key, action});
        return;
    }
    if (is_modifier_key(key))
    {
        throw std::invalid_argument("A modified RegisterHotKey binding cannot use a modifier as its primary key.");
    }

    plan.registered_hotkeys.push_back(
        {next_hotkey_identifier++, key, virtual_key(key), native_modifiers(key.modifiers), action});
}

void add_switch_binding(InputBindingPlan &plan, std::vector<InputKeyIdentity> &used_keys,
                        const InputHotkeyBinding &binding, const InputLogicalSwitch target,
                        const InputCaptureBackend backend, int &next_hotkey_identifier)
{
    switch (binding.mode)
    {
    case InputActivationMode::unbound:
        if (binding.toggle_key || binding.enable_key || binding.disable_key || binding.hold_key)
        {
            throw std::invalid_argument("An unbound input binding cannot contain keys.");
        }
        return;
    case InputActivationMode::toggle:
        if (!binding.toggle_key || binding.enable_key || binding.disable_key || binding.hold_key)
        {
            throw std::invalid_argument("A toggle input binding must contain only toggleKey.");
        }
        add_action(plan, used_keys, *binding.toggle_key, {target, InputSwitchOperation::toggle}, backend,
                   next_hotkey_identifier);
        return;
    case InputActivationMode::independent:
        if (!binding.enable_key || !binding.disable_key || binding.toggle_key || binding.hold_key)
        {
            throw std::invalid_argument("An independent input binding must contain only enableKey and disableKey.");
        }
        add_action(plan, used_keys, *binding.enable_key, {target, InputSwitchOperation::enable}, backend,
                   next_hotkey_identifier);
        add_action(plan, used_keys, *binding.disable_key, {target, InputSwitchOperation::disable}, backend,
                   next_hotkey_identifier);
        return;
    case InputActivationMode::hold:
        if (!binding.hold_key || binding.toggle_key || binding.enable_key || binding.disable_key)
        {
            throw std::invalid_argument("A hold input binding must contain only holdKey.");
        }
        add_action(plan, used_keys, *binding.hold_key, {target, InputSwitchOperation::hold}, backend,
                   next_hotkey_identifier);
        return;
    }
    throw std::invalid_argument("Input activation mode is invalid.");
}

[[nodiscard]] InputPhysicalKey physical_key(const InputKeyIdentity &key) noexcept
{
    return {key.device, key.code, key.extended};
}

void add_polling_key(InputBindingPlan &plan, const InputPhysicalKey key, const UINT virtual_key_code)
{
    if (std::ranges::find(plan.polling_keys, InputPollingKey{key, virtual_key_code}) == plan.polling_keys.end())
    {
        plan.polling_keys.push_back({key, virtual_key_code});
    }
}

void add_modifier_polling_keys(InputBindingPlan &plan, const InputModifiers modifiers)
{
    if (has_modifier(modifiers, InputModifiers::ctrl))
    {
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x1DU, false}, VK_LCONTROL);
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x1DU, true}, VK_RCONTROL);
    }
    if (has_modifier(modifiers, InputModifiers::alt))
    {
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x38U, false}, VK_LMENU);
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x38U, true}, VK_RMENU);
    }
    if (has_modifier(modifiers, InputModifiers::shift))
    {
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x2AU, false}, VK_LSHIFT);
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x36U, false}, VK_RSHIFT);
    }
    if (has_modifier(modifiers, InputModifiers::win))
    {
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x5BU, true}, VK_LWIN);
        add_polling_key(plan, {InputDeviceKind::keyboard, 0x5CU, true}, VK_RWIN);
    }
}

void add_low_level_polling_keys(InputBindingPlan &plan)
{
    for (const RawInputBinding &binding : plan.raw_bindings)
    {
        if (binding.key.device != InputDeviceKind::keyboard)
        {
            continue;
        }

        add_polling_key(plan, physical_key(binding.key), virtual_key(binding.key));
        add_modifier_polling_keys(plan, binding.key.modifiers);
    }
    for (const ScriptInputBinding &binding : plan.script_bindings)
    {
        if (binding.key.device != InputDeviceKind::keyboard)
        {
            continue;
        }

        add_polling_key(plan, physical_key(binding.key), virtual_key(binding.key));
        add_modifier_polling_keys(plan, binding.key.modifiers);
    }
}

[[nodiscard]] std::string describe_key(const InputKeyIdentity &key)
{
    return std::format("device={}, code=0x{:04X}, extended={}, modifiers=0x{:02X}",
                       key.device == InputDeviceKind::keyboard ? "keyboard" : "mouse", key.code, key.extended,
                       static_cast<std::uint8_t>(key.modifiers));
}

[[nodiscard]] InputModifiers modifier_for_key(const InputPhysicalKey key) noexcept
{
    if (key.device != InputDeviceKind::keyboard)
    {
        return InputModifiers::none;
    }
    if (key.code == 0x1DU)
    {
        return InputModifiers::ctrl;
    }
    if (!key.extended && (key.code == 0x2AU || key.code == 0x36U))
    {
        return InputModifiers::shift;
    }
    if (key.code == 0x38U)
    {
        return InputModifiers::alt;
    }
    if (key.extended && (key.code == 0x5BU || key.code == 0x5CU))
    {
        return InputModifiers::win;
    }
    return InputModifiers::none;
}

[[nodiscard]] bool is_extended_virtual_key(const UINT virtual_key_code) noexcept
{
    // Some keyboard/HID paths report the virtual-key identity correctly but omit RI_KEY_E0.
    switch (virtual_key_code)
    {
    case VK_RCONTROL:
    case VK_RMENU:
    case VK_INSERT:
    case VK_DELETE:
    case VK_HOME:
    case VK_END:
    case VK_PRIOR:
    case VK_NEXT:
    case VK_LEFT:
    case VK_UP:
    case VK_RIGHT:
    case VK_DOWN:
    case VK_SNAPSHOT:
    case VK_LWIN:
    case VK_RWIN:
    case VK_APPS:
        return true;
    default:
        return false;
    }
}

[[nodiscard]] JsonObject select_active_profile(const JsonObject &root)
{
    const JsonArray profiles = root.GetNamedArray(L"profiles");
    if (profiles.Size() == 0U)
    {
        throw std::invalid_argument("Input configuration requires at least one profile.");
    }

    std::wstring selected_id;
    const std::wstring active_set_id = root.GetNamedString(L"activeProfileSetId").c_str();
    const JsonArray sets = root.GetNamedArray(L"profileSets");
    for (const auto &item : sets)
    {
        const JsonObject profile_set = item.GetObject();
        if (profile_set.GetNamedString(L"id") != active_set_id)
        {
            continue;
        }
        const IJsonValue selected = profile_set.GetNamedValue(L"selectedProfileId");
        if (selected.ValueType() == JsonValueType::String)
        {
            selected_id = selected.GetString().c_str();
        }
        break;
    }

    if (selected_id.empty())
    {
        return profiles.GetObjectAt(0U);
    }

    for (const auto &item : profiles)
    {
        const JsonObject profile = item.GetObject();
        if (profile.GetNamedString(L"id") == selected_id)
        {
            return profile;
        }
    }

    throw std::invalid_argument("Selected input profile does not exist.");
}
} // namespace

InputConfiguration parse_input_configuration(const std::string_view snapshot_json)
{
    if (snapshot_json.empty())
    {
        throw std::invalid_argument("Input configuration snapshot cannot be empty.");
    }

    const JsonObject root = JsonObject::Parse(winrt::to_hstring(snapshot_json));
    const double schema_version = root.GetNamedNumber(L"schemaVersion");
    if (!std::isfinite(schema_version) || schema_version != 8.0)
    {
        throw std::invalid_argument("Input configuration schema version is not supported.");
    }

    const InputCaptureBackend input_backend = parse_input_backend(root.GetNamedString(L"inputBackend"));
    const JsonObject profile = select_active_profile(root);
    const IJsonValue switches_value = profile.GetNamedValue(L"switches");
    if (switches_value.ValueType() != JsonValueType::Object)
    {
        throw std::invalid_argument("Active profile requires a switches object.");
    }

    const JsonObject switches = switches_value.GetObject();
    require_allowed_properties(switches,
                               {L"visibilityRule", L"initialStateA", L"initialStateB", L"switchA", L"switchB"});
    return {
        input_backend,
        parse_visibility_rule(switches.GetNamedString(L"visibilityRule")),
        switches.GetNamedBoolean(L"initialStateA"),
        switches.GetNamedBoolean(L"initialStateB"),
        parse_binding(switches.GetNamedObject(L"switchA")),
        parse_binding(switches.GetNamedObject(L"switchB")),
    };
}

InputBindingPlan build_input_binding_plan(const InputConfiguration &configuration)
{
    InputBindingPlan plan;
    std::vector<InputKeyIdentity> used_keys;
    int next_hotkey_identifier = kFirstHotkeyIdentifier;
    add_switch_binding(plan, used_keys, configuration.switch_a, InputLogicalSwitch::a, configuration.input_backend,
                       next_hotkey_identifier);
    add_switch_binding(plan, used_keys, configuration.switch_b, InputLogicalSwitch::b, configuration.input_backend,
                       next_hotkey_identifier);
    for (const ScriptInputBinding &binding : configuration.script_bindings)
    {
        validate_key(binding.key);
        if (binding.script_id.empty() || binding.binding_id.empty())
        {
            throw std::invalid_argument("Script input binding identifiers cannot be empty.");
        }
        if (!binding.pressed && !binding.released)
        {
            throw std::invalid_argument("Script input binding must deliver at least one event phase.");
        }
        if (std::ranges::find(used_keys, binding.key) != used_keys.end())
        {
            throw std::invalid_argument("Input configuration assigns the same key to multiple actions.");
        }
        used_keys.push_back(binding.key);
        plan.script_bindings.push_back(binding);
    }
    if (configuration.input_backend == InputCaptureBackend::low_level_hook)
    {
        add_low_level_polling_keys(plan);
    }
    return plan;
}

bool evaluate_input_visibility(const InputVisibilityRule rule, const bool switch_a, const bool switch_b) noexcept
{
    switch (rule)
    {
    case InputVisibilityRule::switch_a:
        return switch_a;
    case InputVisibilityRule::switch_b:
        return switch_b;
    case InputVisibilityRule::both:
        return switch_a && switch_b;
    case InputVisibilityRule::either:
        return switch_a || switch_b;
    }
    return false;
}

std::vector<RawInputTransition> parse_raw_input(const std::span<const std::byte> payload)
{
    if (payload.size() < sizeof(RAWINPUTHEADER))
    {
        throw std::invalid_argument("Raw Input payload is shorter than its header.");
    }

    RAWINPUTHEADER header{};
    std::memcpy(&header, payload.data(), sizeof(header));
    constexpr std::size_t data_offset = offsetof(RAWINPUT, data);
    if (header.dwType == RIM_TYPEKEYBOARD)
    {
        if (payload.size() < data_offset + sizeof(RAWKEYBOARD))
        {
            throw std::invalid_argument("Raw Input keyboard payload is incomplete.");
        }

        RAWKEYBOARD keyboard{};
        std::memcpy(&keyboard, payload.data() + data_offset, sizeof(keyboard));
        std::uint16_t scan_code = keyboard.MakeCode;
        bool extended = (keyboard.Flags & (RI_KEY_E0 | RI_KEY_E1)) != 0U;
        extended = extended || is_extended_virtual_key(keyboard.VKey);
        if (scan_code == 0U)
        {
            const UINT mapped = MapVirtualKeyW(keyboard.VKey, MAPVK_VK_TO_VSC_EX);
            scan_code = static_cast<std::uint16_t>(mapped & 0xFFU);
            extended = extended || (mapped & 0xFF00U) != 0U;
        }
        if (scan_code == 0U)
        {
            return {};
        }

        return {{{InputDeviceKind::keyboard, scan_code, extended}, (keyboard.Flags & RI_KEY_BREAK) == 0U}};
    }

    if (header.dwType != RIM_TYPEMOUSE)
    {
        return {};
    }
    if (payload.size() < data_offset + sizeof(RAWMOUSE))
    {
        throw std::invalid_argument("Raw Input mouse payload is incomplete.");
    }

    RAWMOUSE mouse{};
    std::memcpy(&mouse, payload.data() + data_offset, sizeof(mouse));
    const USHORT flags = mouse.usButtonFlags;
    std::vector<RawInputTransition> transitions;
    const auto append =
        [&transitions, flags](const USHORT down_flag, const USHORT up_flag, const InputMouseButton button)
    {
        if ((flags & down_flag) != 0U)
        {
            transitions.push_back({{InputDeviceKind::mouse, static_cast<std::uint16_t>(button), false}, true});
        }
        if ((flags & up_flag) != 0U)
        {
            transitions.push_back({{InputDeviceKind::mouse, static_cast<std::uint16_t>(button), false}, false});
        }
    };
    append(RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_LEFT_BUTTON_UP, InputMouseButton::left);
    append(RI_MOUSE_RIGHT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_UP, InputMouseButton::right);
    append(RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP, InputMouseButton::middle);
    append(RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP, InputMouseButton::x1);
    append(RI_MOUSE_BUTTON_5_DOWN, RI_MOUSE_BUTTON_5_UP, InputMouseButton::x2);
    return transitions;
}

std::optional<RawInputTransition> decode_low_level_keyboard_input(const WPARAM message,
                                                                  const KBDLLHOOKSTRUCT &event) noexcept
{
    bool pressed = false;
    switch (message)
    {
    case WM_KEYDOWN:
    case WM_SYSKEYDOWN:
        pressed = true;
        break;
    case WM_KEYUP:
    case WM_SYSKEYUP:
        break;
    default:
        return std::nullopt;
    }

    std::uint16_t scan_code = static_cast<std::uint16_t>(event.scanCode & 0xFFFFU);
    bool extended = (event.flags & LLKHF_EXTENDED) != 0U || is_extended_virtual_key(event.vkCode);
    if (scan_code == 0U)
    {
        const UINT mapped = MapVirtualKeyW(event.vkCode, MAPVK_VK_TO_VSC_EX);
        scan_code = static_cast<std::uint16_t>(mapped & 0xFFU);
        extended = extended || (mapped & 0xFF00U) != 0U;
    }
    if (scan_code == 0U)
    {
        return std::nullopt;
    }

    return RawInputTransition{{InputDeviceKind::keyboard, scan_code, extended}, pressed};
}

std::optional<RawInputTransition> decode_low_level_mouse_input(const WPARAM message,
                                                               const MSLLHOOKSTRUCT &event) noexcept
{
    InputMouseButton button{};
    bool pressed = false;
    switch (message)
    {
    case WM_LBUTTONDOWN:
        button = InputMouseButton::left;
        pressed = true;
        break;
    case WM_LBUTTONUP:
        button = InputMouseButton::left;
        break;
    case WM_RBUTTONDOWN:
        button = InputMouseButton::right;
        pressed = true;
        break;
    case WM_RBUTTONUP:
        button = InputMouseButton::right;
        break;
    case WM_MBUTTONDOWN:
        button = InputMouseButton::middle;
        pressed = true;
        break;
    case WM_MBUTTONUP:
        button = InputMouseButton::middle;
        break;
    case WM_XBUTTONDOWN:
    case WM_XBUTTONUP:
        button = HIWORD(event.mouseData) == XBUTTON1 ? InputMouseButton::x1 : InputMouseButton::x2;
        pressed = message == WM_XBUTTONDOWN;
        break;
    default:
        return std::nullopt;
    }

    return RawInputTransition{
        {InputDeviceKind::mouse, static_cast<std::uint16_t>(button), false},
        pressed,
    };
}

InputStateSnapshot HotkeyStateMachine::configure(const InputConfiguration &configuration,
                                                 std::vector<RawInputBinding> raw_bindings)
{
    visibility_rule_ = configuration.visibility_rule;
    switch_a_ = configuration.initial_state_a;
    switch_b_ = configuration.initial_state_b;
    raw_bindings_ = std::move(raw_bindings);
    pressed_keys_.clear();
    active_holds_.clear();
    return snapshot();
}

std::optional<InputStateSnapshot> HotkeyStateMachine::handle_key(const InputKeyIdentity key, const bool pressed)
{
    const InputPhysicalKey physical = physical_key(key);
    const auto pressed_position = std::ranges::find(pressed_keys_, physical);
    if (pressed)
    {
        if (pressed_position != pressed_keys_.end())
        {
            return std::nullopt;
        }
        pressed_keys_.push_back(physical);

        const auto binding = std::ranges::find_if(raw_bindings_, [&key](const RawInputBinding &candidate)
                                                  { return candidate.key == key; });
        if (binding == raw_bindings_.end())
        {
            return std::nullopt;
        }
        if (binding->action.operation == InputSwitchOperation::hold)
        {
            active_holds_.push_back({physical, binding->action.target});
        }
        return apply(binding->action);
    }

    if (pressed_position != pressed_keys_.end())
    {
        pressed_keys_.erase(pressed_position);
    }

    const auto hold = std::ranges::find_if(active_holds_, [&physical](const ActiveHold &candidate)
                                           { return candidate.key == physical; });
    if (hold == active_holds_.end())
    {
        return std::nullopt;
    }
    const InputLogicalSwitch target = hold->target;
    active_holds_.erase(hold);
    return apply({target, InputSwitchOperation::disable});
}

std::optional<InputStateSnapshot> HotkeyStateMachine::trigger_registered(const InputAction action)
{
    if (action.operation == InputSwitchOperation::hold)
    {
        throw std::invalid_argument("RegisterHotKey cannot deliver a Hold action.");
    }
    return apply(action);
}

std::optional<InputStateSnapshot> HotkeyStateMachine::reset_pressed_keys()
{
    pressed_keys_.clear();
    bool changed = false;
    for (const ActiveHold &hold : active_holds_)
    {
        bool &target = state(hold.target);
        changed = changed || target;
        target = false;
    }
    active_holds_.clear();
    return changed ? std::optional(snapshot()) : std::nullopt;
}

InputStateSnapshot HotkeyStateMachine::snapshot(const bool configured) const noexcept
{
    return {switch_a_, switch_b_, evaluate_input_visibility(visibility_rule_, switch_a_, switch_b_), configured};
}

std::optional<InputStateSnapshot> HotkeyStateMachine::apply(const InputAction action)
{
    bool &target = state(action.target);
    const bool before = target;
    switch (action.operation)
    {
    case InputSwitchOperation::enable:
    case InputSwitchOperation::hold:
        target = true;
        break;
    case InputSwitchOperation::disable:
        target = false;
        break;
    case InputSwitchOperation::toggle:
        target = !target;
        break;
    }
    return before == target ? std::nullopt : std::optional(snapshot());
}

bool &HotkeyStateMachine::state(const InputLogicalSwitch target) noexcept
{
    return target == InputLogicalSwitch::a ? switch_a_ : switch_b_;
}

void ScriptInputStateMachine::configure(std::vector<ScriptInputBinding> bindings)
{
    bindings_ = std::move(bindings);
    active_inputs_.clear();
}

std::optional<ScriptInputEvent> ScriptInputStateMachine::handle_key(const InputKeyIdentity key, const bool pressed)
{
    const InputPhysicalKey physical = physical_key(key);
    const auto active = std::ranges::find_if(active_inputs_, [&physical](const ActiveInput &candidate)
                                             { return candidate.key == physical; });
    if (pressed)
    {
        if (active != active_inputs_.end())
        {
            return std::nullopt;
        }

        const auto binding = std::ranges::find_if(bindings_, [&key](const ScriptInputBinding &candidate)
                                                  { return candidate.key == key; });
        if (binding == bindings_.end())
        {
            return std::nullopt;
        }
        active_inputs_.push_back({physical, *binding});
        if (!binding->pressed)
        {
            return std::nullopt;
        }
        return ScriptInputEvent{
            binding->scope,
            binding->script_id,
            binding->binding_id,
            ScriptInputPhase::pressed,
        };
    }

    if (active == active_inputs_.end())
    {
        return std::nullopt;
    }
    ScriptInputBinding binding = std::move(active->binding);
    active_inputs_.erase(active);
    if (!binding.released)
    {
        return std::nullopt;
    }
    return ScriptInputEvent{
        binding.scope,
        std::move(binding.script_id),
        std::move(binding.binding_id),
        ScriptInputPhase::released,
    };
}

std::vector<ScriptInputEvent> ScriptInputStateMachine::reset_pressed_keys()
{
    std::vector<ScriptInputEvent> events;
    events.reserve(active_inputs_.size());
    for (ActiveInput &active : active_inputs_)
    {
        if (active.binding.released)
        {
            events.push_back({
                active.binding.scope,
                std::move(active.binding.script_id),
                std::move(active.binding.binding_id),
                ScriptInputPhase::released,
            });
        }
    }
    active_inputs_.clear();
    return events;
}

class GlobalInputService::Impl
{
  public:
    Impl(StateChanged state_changed, ScriptInputReceived script_input_received)
        : state_changed_(std::move(state_changed)), script_input_received_(std::move(script_input_received)),
          worker_(HostThreadRole::input, [this](const std::stop_token stop_token) { run(stop_token); })
    {
        if (!state_changed_)
        {
            throw std::invalid_argument("Input state callback cannot be empty.");
        }
    }

    ~Impl()
    {
        stop();
    }

    void start()
    {
        worker_.start();
        std::unique_lock lock(startup_mutex_);
        startup_changed_.wait(lock, [this] { return ready_; });
        const std::exception_ptr failure = startup_failure_;
        lock.unlock();
        if (failure)
        {
            worker_.join();
            std::rethrow_exception(failure);
        }
    }

    [[nodiscard]] InputApplyResult apply_configuration(const InputConfiguration &configuration)
    {
        const HWND window = window_.load(std::memory_order_acquire);
        if (window == nullptr)
        {
            return {false, ERROR_INVALID_STATE, std::nullopt, "Input service is not running."};
        }

        ApplyConfigurationRequest request{&configuration, {}};
        DWORD_PTR ignored = 0U;
        if (!SendMessageTimeoutW(window, kApplyConfigurationMessage, 0U, reinterpret_cast<LPARAM>(&request),
                                 SMTO_ABORTIFHUNG | SMTO_BLOCK, kApplyConfigurationTimeoutMs, &ignored))
        {
            const DWORD error = GetLastError();
            return {false, error == ERROR_SUCCESS ? ERROR_TIMEOUT : error, std::nullopt,
                    "Input thread did not apply configuration before the timeout."};
        }
        return request.result;
    }

    void stop() noexcept
    {
        worker_.request_stop();
        const HWND window = window_.load(std::memory_order_acquire);
        if (window != nullptr)
        {
            PostMessageW(window, WM_CLOSE, 0U, 0);
        }
        worker_.join();
    }

  private:
    static LRESULT CALLBACK window_proc(_In_ const HWND window, const UINT message, const WPARAM word_parameter,
                                        const LPARAM long_parameter) noexcept
    {
        Impl *service = reinterpret_cast<Impl *>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            const auto *create = reinterpret_cast<const CREATESTRUCTW *>(long_parameter);
            service = static_cast<Impl *>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(service));
        }

        if (service == nullptr)
        {
            return DefWindowProcW(window, message, word_parameter, long_parameter);
        }

        try
        {
            return service->handle_message(window, message, word_parameter, long_parameter);
        }
        catch (const std::exception &error)
        {
            log_diagnostic(DiagnosticLevel::error, "input.window_message_failed", error.what());
            service->runtime_failure_ = std::current_exception();
            PostQuitMessage(1);
            return 0;
        }
    }

    static LRESULT CALLBACK low_level_keyboard_proc(const int code, const WPARAM word_parameter,
                                                    const LPARAM long_parameter) noexcept
    {
        Impl *service = hook_owner_.load(std::memory_order_acquire);
        if (code == HC_ACTION && service != nullptr &&
            (word_parameter == WM_KEYDOWN || word_parameter == WM_SYSKEYDOWN || word_parameter == WM_KEYUP ||
             word_parameter == WM_SYSKEYUP))
        {
            const auto *event = reinterpret_cast<const KBDLLHOOKSTRUCT *>(long_parameter);
            const std::optional<RawInputTransition> transition =
                decode_low_level_keyboard_input(word_parameter, *event);
            if (transition)
            {
                const LPARAM packed =
                    static_cast<LPARAM>(transition->key.code | (transition->key.extended ? 1U << 16U : 0U) |
                                        (transition->pressed ? 1U << 17U : 0U));
                const HWND window = service->window_.load(std::memory_order_acquire);
                if (window != nullptr)
                {
                    PostMessageW(window, kHookKeyboardMessage, 0U, packed);
                }
            }
        }
        return CallNextHookEx(nullptr, code, word_parameter, long_parameter);
    }

    static LRESULT CALLBACK low_level_mouse_proc(const int code, const WPARAM word_parameter,
                                                 const LPARAM long_parameter) noexcept
    {
        Impl *service = hook_owner_.load(std::memory_order_acquire);
        if (code == HC_ACTION && service != nullptr)
        {
            const auto *event = reinterpret_cast<const MSLLHOOKSTRUCT *>(long_parameter);
            const std::optional<RawInputTransition> transition = decode_low_level_mouse_input(word_parameter, *event);
            const HWND window = service->window_.load(std::memory_order_acquire);
            if (transition && window != nullptr)
            {
                PostMessageW(window, kHookMouseMessage, static_cast<WPARAM>(transition->key.code),
                             transition->pressed ? 1 : 0);
            }
        }
        return CallNextHookEx(nullptr, code, word_parameter, long_parameter);
    }

    void signal_startup(std::exception_ptr failure = nullptr)
    {
        {
            std::scoped_lock lock(startup_mutex_);
            ready_ = true;
            startup_failure_ = failure;
        }
        startup_changed_.notify_all();
    }

    void run(const std::stop_token stop_token)
    {
        bool startup_signaled = false;
        try
        {
            const HINSTANCE instance = GetModuleHandleW(nullptr);
            WNDCLASSEXW window_class{};
            window_class.cbSize = sizeof(window_class);
            window_class.lpfnWndProc = window_proc;
            window_class.hInstance = instance;
            window_class.lpszClassName = kInputWindowClassName;
            if (RegisterClassExW(&window_class) == 0U)
            {
                throw_last_error("Register Input window class");
            }
            class_registered_ = true;

            const HWND window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kInputWindowClassName, L"",
                                                WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, instance, this);
            if (window == nullptr)
            {
                throw_last_error("Create Input window");
            }
            window_.store(window, std::memory_order_release);

            initialize_input_backend(window);
            if (!WTSRegisterSessionNotification(window, NOTIFY_FOR_THIS_SESSION))
            {
                throw_last_error("WTSRegisterSessionNotification");
            }
            session_notifications_registered_ = true;

            std::stop_callback close_window(stop_token,
                                            [this]
                                            {
                                                const HWND target = window_.load(std::memory_order_acquire);
                                                if (target != nullptr)
                                                {
                                                    PostMessageW(target, WM_CLOSE, 0U, 0);
                                                }
                                            });
            signal_startup();
            startup_signaled = true;

            MSG message{};
            while (true)
            {
                const BOOL result = GetMessageW(&message, nullptr, 0U, 0U);
                if (result == 0)
                {
                    break;
                }
                if (result == -1)
                {
                    throw_last_error("GetMessageW Input");
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            cleanup();
            if (runtime_failure_)
            {
                std::rethrow_exception(runtime_failure_);
            }
        }
        catch (...)
        {
            const std::exception_ptr failure = std::current_exception();
            cleanup();
            if (!startup_signaled)
            {
                signal_startup(failure);
            }
            throw;
        }
    }

    void initialize_input_backend(_In_ const HWND window)
    {
        std::array<RAWINPUTDEVICE, 2> devices{};
        devices[0].usUsagePage = 0x01U;
        devices[0].usUsage = 0x06U;
        devices[0].dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
        devices[0].hwndTarget = window;
        devices[1].usUsagePage = 0x01U;
        devices[1].usUsage = 0x02U;
        devices[1].dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
        devices[1].hwndTarget = window;
        if (RegisterRawInputDevices(devices.data(), static_cast<UINT>(devices.size()), sizeof(RAWINPUTDEVICE)))
        {
            raw_input_registered_ = true;
            hook_owner_.store(this, std::memory_order_release);
            low_level_hook_ = SetWindowsHookExW(WH_KEYBOARD_LL, low_level_keyboard_proc, GetModuleHandleW(nullptr), 0U);
            low_level_mouse_hook_ = SetWindowsHookExW(WH_MOUSE_LL, low_level_mouse_proc, GetModuleHandleW(nullptr), 0U);
            if (low_level_hook_ == nullptr || low_level_mouse_hook_ == nullptr)
            {
                const DWORD hook_error = GetLastError();
                hook_owner_.store(nullptr, std::memory_order_release);
                log_diagnostic(DiagnosticLevel::warning, "input.keyboard_hook_unavailable",
                               "Raw Input is active, but the low-level input backend could not be installed.",
                               NativeErrorStatus{NativeErrorDomain::win32, hook_error});
            }
            log_diagnostic(DiagnosticLevel::information, "input.raw_input_ready",
                           "Raw Input is active with low-level keyboard redundancy.");
            return;
        }

        hook_owner_.store(this, std::memory_order_release);
        low_level_hook_ = SetWindowsHookExW(WH_KEYBOARD_LL, low_level_keyboard_proc, GetModuleHandleW(nullptr), 0U);
        const DWORD keyboard_hook_error = low_level_hook_ == nullptr ? GetLastError() : ERROR_SUCCESS;
        low_level_mouse_hook_ = SetWindowsHookExW(WH_MOUSE_LL, low_level_mouse_proc, GetModuleHandleW(nullptr), 0U);
        const DWORD mouse_hook_error = low_level_mouse_hook_ == nullptr ? GetLastError() : ERROR_SUCCESS;
        if (low_level_hook_ == nullptr || low_level_mouse_hook_ == nullptr)
        {
            if (low_level_hook_ != nullptr)
            {
                UnhookWindowsHookEx(low_level_hook_);
                low_level_hook_ = nullptr;
            }
            if (low_level_mouse_hook_ != nullptr)
            {
                UnhookWindowsHookEx(low_level_mouse_hook_);
                low_level_mouse_hook_ = nullptr;
            }
            hook_owner_.store(nullptr, std::memory_order_release);
            SetLastError(keyboard_hook_error != ERROR_SUCCESS ? keyboard_hook_error : mouse_hook_error);
            throw_last_error("Register Raw Input and low-level input fallback");
        }
        log_diagnostic(DiagnosticLevel::warning, "input.raw_input_fallback",
                       "Raw Input registration failed; low-level keyboard and mouse hook fallback is active.");
    }

    void cleanup() noexcept
    {
        const HWND timer_window = window_.load(std::memory_order_acquire);
        if (timer_window != nullptr)
        {
            KillTimer(timer_window, kKeyboardPollingTimerId);
        }
        unregister_hotkeys(active_plan_);
        if (const auto reset = state_machine_.reset_pressed_keys())
        {
            publish(*reset);
        }
        publish_script_events(script_state_machine_.reset_pressed_keys());

        const HWND window = window_.exchange(nullptr, std::memory_order_acq_rel);
        if (session_notifications_registered_ && window != nullptr)
        {
            WTSUnRegisterSessionNotification(window);
            session_notifications_registered_ = false;
        }
        if (raw_input_registered_)
        {
            std::array<RAWINPUTDEVICE, 2> devices{};
            devices[0].usUsagePage = 0x01U;
            devices[0].usUsage = 0x06U;
            devices[0].dwFlags = RIDEV_REMOVE;
            devices[1].usUsagePage = 0x01U;
            devices[1].usUsage = 0x02U;
            devices[1].dwFlags = RIDEV_REMOVE;
            static_cast<void>(
                RegisterRawInputDevices(devices.data(), static_cast<UINT>(devices.size()), sizeof(RAWINPUTDEVICE)));
            raw_input_registered_ = false;
        }
        if (low_level_hook_ != nullptr)
        {
            hook_owner_.store(nullptr, std::memory_order_release);
            UnhookWindowsHookEx(low_level_hook_);
            low_level_hook_ = nullptr;
        }
        if (low_level_mouse_hook_ != nullptr)
        {
            UnhookWindowsHookEx(low_level_mouse_hook_);
            low_level_mouse_hook_ = nullptr;
        }
        hook_owner_.store(nullptr, std::memory_order_release);
        if (window != nullptr && IsWindow(window))
        {
            DestroyWindow(window);
        }
        if (class_registered_)
        {
            UnregisterClassW(kInputWindowClassName, GetModuleHandleW(nullptr));
            class_registered_ = false;
        }
    }

    [[nodiscard]] LRESULT handle_message(_In_ const HWND window, const UINT message, const WPARAM word_parameter,
                                         const LPARAM long_parameter)
    {
        switch (message)
        {
        case kApplyConfigurationMessage:
        {
            auto *request = reinterpret_cast<ApplyConfigurationRequest *>(long_parameter);
            request->result = apply_configuration_on_thread(*request->configuration);
            return 1;
        }
        case kHookKeyboardMessage:
        {
            // Cloud clients may omit keyboard Raw Input while still forwarding low-level injected events.
            // The shared state machine suppresses duplicate transitions when both sources are available.
            const std::uint16_t scan_code = static_cast<std::uint16_t>(long_parameter & 0xFFFF);
            const bool extended = (long_parameter & (1 << 16)) != 0;
            const bool pressed = (long_parameter & (1 << 17)) != 0;
            handle_input_transition({InputDeviceKind::keyboard, scan_code, extended}, pressed);
            return 0;
        }
        case kHookMouseMessage:
        {
            if (active_backend_ != InputCaptureBackend::low_level_hook)
            {
                return 0;
            }
            handle_input_transition({InputDeviceKind::mouse, static_cast<std::uint16_t>(word_parameter), false},
                                    long_parameter != 0);
            return 0;
        }
        case WM_TIMER:
            if (word_parameter == kKeyboardPollingTimerId && active_backend_ == InputCaptureBackend::low_level_hook)
            {
                poll_keyboard_state();
            }
            return 0;
        case WM_HOTKEY:
        {
            if (active_backend_ != InputCaptureBackend::raw_input)
            {
                return 0;
            }
            const int identifier = static_cast<int>(word_parameter);
            const auto binding = std::ranges::find_if(active_plan_.registered_hotkeys,
                                                      [identifier](const RegisteredHotkeyBinding &candidate)
                                                      { return candidate.identifier == identifier; });
            if (binding != active_plan_.registered_hotkeys.end())
            {
                if (const auto update = state_machine_.trigger_registered(binding->action))
                {
                    publish(*update);
                }
            }
            return 0;
        }
        case WM_INPUT:
            if (active_backend_ != InputCaptureBackend::raw_input)
            {
                return 0;
            }
            try
            {
                handle_raw_input(reinterpret_cast<HRAWINPUT>(long_parameter));
            }
            catch (const NativeError &error)
            {
                log_diagnostic(DiagnosticLevel::warning, "input.raw_input_event_failed", error.what(), error.status());
                reset_pressed_keys();
            }
            catch (const std::exception &error)
            {
                log_diagnostic(DiagnosticLevel::warning, "input.raw_input_event_failed", error.what());
                reset_pressed_keys();
            }
            return DefWindowProcW(window, message, word_parameter, long_parameter);
        case WM_INPUT_DEVICE_CHANGE:
            if (word_parameter == GIDC_REMOVAL)
            {
                reset_pressed_keys();
            }
            return 0;
        case WM_WTSSESSION_CHANGE:
            reset_pressed_keys();
            return 0;
        case WM_CLOSE:
            PostQuitMessage(0);
            return 0;
        case WM_DESTROY:
            window_.store(nullptr, std::memory_order_release);
            PostQuitMessage(0);
            return 0;
        default:
            return DefWindowProcW(window, message, word_parameter, long_parameter);
        }
    }

    [[nodiscard]] InputApplyResult apply_configuration_on_thread(const InputConfiguration &configuration)
    {
        if (configuration.input_backend == InputCaptureBackend::low_level_hook && !low_level_backend_available())
        {
            return {false, ERROR_HOOK_NOT_INSTALLED, std::nullopt,
                    "Low-level keyboard and mouse hooks are not available."};
        }

        InputConfiguration effective_configuration = configuration;
        if (configuration.input_backend == InputCaptureBackend::raw_input && !raw_input_registered_)
        {
            effective_configuration.input_backend = InputCaptureBackend::low_level_hook;
        }

        InputBindingPlan candidate;
        try
        {
            candidate = build_input_binding_plan(effective_configuration);
        }
        catch (const std::exception &error)
        {
            return {false, ERROR_INVALID_DATA, std::nullopt, error.what()};
        }

        const HWND window = window_.load(std::memory_order_acquire);
        const bool needs_keyboard_polling =
            effective_configuration.input_backend == InputCaptureBackend::low_level_hook &&
            !candidate.polling_keys.empty();
        if (needs_keyboard_polling &&
            SetTimer(window, kKeyboardPollingTimerId, kKeyboardPollingIntervalMs, nullptr) == 0U)
        {
            const DWORD timer_error = GetLastError();
            return {false, timer_error == ERROR_SUCCESS ? ERROR_FUNCTION_FAILED : timer_error, std::nullopt,
                    "Keyboard polling timer could not be started."};
        }

        unregister_hotkeys(active_plan_);
        std::optional<RegisteredHotkeyBinding> failure;
        DWORD registration_error = ERROR_SUCCESS;
        for (const RegisteredHotkeyBinding &binding : candidate.registered_hotkeys)
        {
            if (!RegisterHotKey(window_.load(std::memory_order_acquire), binding.identifier, binding.native_modifiers,
                                binding.virtual_key))
            {
                registration_error = GetLastError();
                failure = binding;
                break;
            }
        }

        if (failure)
        {
            unregister_hotkeys(candidate);
            std::optional<RegisteredHotkeyBinding> restore_failure;
            DWORD restore_error = ERROR_SUCCESS;
            for (const RegisteredHotkeyBinding &binding : active_plan_.registered_hotkeys)
            {
                if (!RegisterHotKey(window_.load(std::memory_order_acquire), binding.identifier,
                                    binding.native_modifiers, binding.virtual_key))
                {
                    restore_error = GetLastError();
                    restore_failure = binding;
                    break;
                }
            }
            if (restore_failure)
            {
                unregister_hotkeys(active_plan_);
                active_plan_ = {};
                modifier_keys_.clear();
                publish_script_events(script_state_machine_.reset_pressed_keys());
                script_state_machine_.configure({});
                configured_ = false;
                if (const auto reset = state_machine_.reset_pressed_keys())
                {
                    publish(*reset);
                }
                return {false, restore_error, restore_failure->key,
                        std::format("RegisterHotKey rollback failed for {} with Win32 error {}.",
                                    describe_key(restore_failure->key), restore_error)};
            }
            return {false, registration_error, failure->key,
                    std::format("RegisterHotKey failed for {} with Win32 error {}.", describe_key(failure->key),
                                registration_error)};
        }

        active_plan_ = std::move(candidate);
        active_backend_ = effective_configuration.input_backend;
        if (!needs_keyboard_polling)
        {
            KillTimer(window, kKeyboardPollingTimerId);
        }
        modifier_keys_.clear();
        configured_ = true;
        publish_script_events(script_state_machine_.reset_pressed_keys());
        script_state_machine_.configure(active_plan_.script_bindings);
        publish(state_machine_.configure(effective_configuration, active_plan_.raw_bindings));
        return {true, ERROR_SUCCESS, std::nullopt, {}};
    }

    void unregister_hotkeys(const InputBindingPlan &plan) const noexcept
    {
        const HWND window = window_.load(std::memory_order_acquire);
        if (window == nullptr)
        {
            return;
        }
        for (const RegisteredHotkeyBinding &binding : plan.registered_hotkeys)
        {
            UnregisterHotKey(window, binding.identifier);
        }
    }

    void handle_raw_input(_In_ const HRAWINPUT raw_input)
    {
        UINT size = 0U;
        if (GetRawInputData(raw_input, RID_INPUT, nullptr, &size, sizeof(RAWINPUTHEADER)) == static_cast<UINT>(-1))
        {
            throw_last_error("GetRawInputData size");
        }
        if (size < sizeof(RAWINPUTHEADER))
        {
            throw NativeError(ERROR_INVALID_DATA, "GetRawInputData header size");
        }
        std::vector<std::byte> buffer(size);
        const UINT received = GetRawInputData(raw_input, RID_INPUT, buffer.data(), &size, sizeof(RAWINPUTHEADER));
        if (received == static_cast<UINT>(-1))
        {
            throw_last_error("GetRawInputData payload");
        }
        if (received != size)
        {
            throw NativeError(ERROR_INVALID_DATA, "GetRawInputData payload length");
        }

        for (const RawInputTransition &transition : parse_raw_input(buffer))
        {
            handle_input_transition(transition.key, transition.pressed);
        }
    }

    void handle_input_transition(const InputPhysicalKey physical, const bool pressed)
    {
        const InputModifiers current_modifiers = modifiers();
        const InputModifiers key_modifier = modifier_for_key(physical);
        const InputModifiers event_modifiers = key_modifier == InputModifiers::none
                                                   ? current_modifiers
                                                   : without_modifier(current_modifiers, key_modifier);

        if (key_modifier != InputModifiers::none)
        {
            const auto position = std::ranges::find(modifier_keys_, physical);
            if (pressed && position == modifier_keys_.end())
            {
                modifier_keys_.push_back(physical);
            }
            else if (!pressed && position != modifier_keys_.end())
            {
                modifier_keys_.erase(position);
            }
        }

        const InputKeyIdentity identity{physical.device, physical.code, physical.extended, event_modifiers};
        if (const auto update = state_machine_.handle_key(identity, pressed))
        {
            publish(*update);
        }
        if (const auto script_event = script_state_machine_.handle_key(identity, pressed))
        {
            publish_script_event(*script_event);
        }
    }

    [[nodiscard]] InputModifiers modifiers() const noexcept
    {
        InputModifiers result = InputModifiers::none;
        for (const InputPhysicalKey key : modifier_keys_)
        {
            result = result | modifier_for_key(key);
        }
        return result;
    }

    void reset_pressed_keys()
    {
        modifier_keys_.clear();
        if (const auto update = state_machine_.reset_pressed_keys())
        {
            publish(*update);
        }
        publish_script_events(script_state_machine_.reset_pressed_keys());
    }

    void poll_keyboard_state()
    {
        struct Sample
        {
            InputPhysicalKey key;
            bool pressed;
        };

        std::vector<Sample> samples;
        samples.reserve(active_plan_.polling_keys.size());
        for (const InputPollingKey &polling_key : active_plan_.polling_keys)
        {
            const SHORT state = GetAsyncKeyState(static_cast<int>(polling_key.virtual_key));
            samples.push_back({polling_key.key, (state & static_cast<SHORT>(0x8000)) != 0});
        }

        const auto process_modifiers = [this, &samples](const bool pressed)
        {
            for (const Sample sample : samples)
            {
                if (modifier_for_key(sample.key) != InputModifiers::none && sample.pressed == pressed)
                {
                    handle_input_transition(sample.key, sample.pressed);
                }
            }
        };
        process_modifiers(true);
        for (const Sample sample : samples)
        {
            if (modifier_for_key(sample.key) == InputModifiers::none)
            {
                handle_input_transition(sample.key, sample.pressed);
            }
        }
        process_modifiers(false);
    }

    [[nodiscard]] bool low_level_backend_available() const noexcept
    {
        return low_level_hook_ != nullptr && low_level_mouse_hook_ != nullptr;
    }

    void publish(InputStateSnapshot snapshot) const
    {
        snapshot.configured = configured_;
        state_changed_(snapshot);
    }

    void publish_script_event(const ScriptInputEvent &event) const
    {
        if (script_input_received_)
        {
            script_input_received_(event);
        }
    }

    void publish_script_events(const std::vector<ScriptInputEvent> &events) const
    {
        for (const ScriptInputEvent &event : events)
        {
            publish_script_event(event);
        }
    }

    StateChanged state_changed_;
    ScriptInputReceived script_input_received_;
    HostWorkerThread worker_;
    std::mutex startup_mutex_;
    std::condition_variable startup_changed_;
    std::exception_ptr startup_failure_;
    std::exception_ptr runtime_failure_;
    std::atomic<HWND> window_ = nullptr;
    HotkeyStateMachine state_machine_;
    ScriptInputStateMachine script_state_machine_;
    InputBindingPlan active_plan_;
    InputCaptureBackend active_backend_ = InputCaptureBackend::raw_input;
    std::vector<InputPhysicalKey> modifier_keys_;
    HHOOK low_level_hook_ = nullptr;
    HHOOK low_level_mouse_hook_ = nullptr;
    bool ready_ = false;
    bool configured_ = false;
    bool raw_input_registered_ = false;
    bool session_notifications_registered_ = false;
    bool class_registered_ = false;

    static std::atomic<Impl *> hook_owner_;
};

std::atomic<GlobalInputService::Impl *> GlobalInputService::Impl::hook_owner_ = nullptr;

GlobalInputService::GlobalInputService(StateChanged state_changed, ScriptInputReceived script_input_received)
    : impl_(std::make_unique<Impl>(std::move(state_changed), std::move(script_input_received)))
{
}

GlobalInputService::~GlobalInputService() = default;

void GlobalInputService::start()
{
    impl_->start();
}

InputApplyResult GlobalInputService::apply_configuration(const InputConfiguration &configuration)
{
    return impl_->apply_configuration(configuration);
}

void GlobalInputService::stop() noexcept
{
    impl_->stop();
}
} // namespace external_peepsight

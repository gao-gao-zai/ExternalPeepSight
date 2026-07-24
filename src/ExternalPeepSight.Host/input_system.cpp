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
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

constexpr wchar_t kInputWindowClassName[] = L"ExternalPeepSight.Input.Window";
constexpr UINT kApplyConfigurationMessage = WM_APP + 1U;
constexpr UINT kHookKeyboardMessage = WM_APP + 2U;
constexpr int kFirstHotkeyIdentifier = 100;
constexpr DWORD kApplyConfigurationTimeoutMs = 5'000U;
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

[[nodiscard]] std::uint16_t get_scan_code(const JsonObject &object)
{
    const double value = object.GetNamedNumber(L"scanCode");
    if (value < 1.0 || value > static_cast<double>((std::numeric_limits<std::uint16_t>::max)()) ||
        std::floor(value) != value)
    {
        throw std::invalid_argument("Input scan code is outside the supported range.");
    }
    return static_cast<std::uint16_t>(value);
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
    require_allowed_properties(key, {L"scanCode", L"extended", L"modifiers"});
    return InputKeyIdentity{get_scan_code(key), key.GetNamedBoolean(L"extended"),
                            parse_modifiers(key.GetNamedString(L"modifiers"))};
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

[[nodiscard]] bool is_modifier_key(const InputKeyIdentity &key) noexcept
{
    return (!key.extended &&
            (key.scan_code == 0x1DU || key.scan_code == 0x2AU || key.scan_code == 0x36U || key.scan_code == 0x38U)) ||
           (key.extended &&
            (key.scan_code == 0x1DU || key.scan_code == 0x38U || key.scan_code == 0x5BU || key.scan_code == 0x5CU));
}

[[nodiscard]] bool is_system_reserved(const InputKeyIdentity &key) noexcept
{
    constexpr std::uint16_t f12_scan_code = 0x58U;
    const bool primary_windows_key = key.extended && (key.scan_code == 0x5BU || key.scan_code == 0x5CU);
    if (has_modifier(key.modifiers, InputModifiers::win) || primary_windows_key || key.scan_code == f12_scan_code)
    {
        return true;
    }

    const bool alt = has_modifier(key.modifiers, InputModifiers::alt);
    const bool ctrl = has_modifier(key.modifiers, InputModifiers::ctrl);
    if ((alt && (key.scan_code == 0x0FU || key.scan_code == 0x01U)) || (ctrl && !alt && key.scan_code == 0x01U))
    {
        return true;
    }
    return ctrl && alt && key.extended && key.scan_code == 0x53U;
}

void validate_key(const InputKeyIdentity &key)
{
    if (key.scan_code == 0U || (key.modifiers & kKnownModifiers) != key.modifiers)
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
    const UINT encoded_scan_code = key.extended ? static_cast<UINT>(key.scan_code) | 0xE000U : key.scan_code;
    const UINT result = MapVirtualKeyW(encoded_scan_code, MAPVK_VSC_TO_VK_EX);
    if (result == 0U)
    {
        throw std::invalid_argument("Input scan code cannot be mapped to a virtual key.");
    }
    return result;
}

void add_action(InputBindingPlan &plan, std::vector<InputKeyIdentity> &used_keys, const InputKeyIdentity &key,
                const InputAction action, int &next_hotkey_identifier)
{
    validate_key(key);
    if (std::ranges::find(used_keys, key) != used_keys.end())
    {
        throw std::invalid_argument("Input configuration assigns the same key to multiple actions.");
    }
    used_keys.push_back(key);

    const bool requires_raw_input =
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
                        const InputHotkeyBinding &binding, const InputLogicalSwitch target, int &next_hotkey_identifier)
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
        add_action(plan, used_keys, *binding.toggle_key, {target, InputSwitchOperation::toggle},
                   next_hotkey_identifier);
        return;
    case InputActivationMode::independent:
        if (!binding.enable_key || !binding.disable_key || binding.toggle_key || binding.hold_key)
        {
            throw std::invalid_argument("An independent input binding must contain only enableKey and disableKey.");
        }
        add_action(plan, used_keys, *binding.enable_key, {target, InputSwitchOperation::enable},
                   next_hotkey_identifier);
        add_action(plan, used_keys, *binding.disable_key, {target, InputSwitchOperation::disable},
                   next_hotkey_identifier);
        return;
    case InputActivationMode::hold:
        if (!binding.hold_key || binding.toggle_key || binding.enable_key || binding.disable_key)
        {
            throw std::invalid_argument("A hold input binding must contain only holdKey.");
        }
        add_action(plan, used_keys, *binding.hold_key, {target, InputSwitchOperation::hold}, next_hotkey_identifier);
        return;
    }
    throw std::invalid_argument("Input activation mode is invalid.");
}

[[nodiscard]] InputPhysicalKey physical_key(const InputKeyIdentity &key) noexcept
{
    return {key.scan_code, key.extended};
}

[[nodiscard]] std::string describe_key(const InputKeyIdentity &key)
{
    return std::format("scanCode=0x{:04X}, extended={}, modifiers=0x{:02X}", key.scan_code, key.extended,
                       static_cast<std::uint8_t>(key.modifiers));
}

[[nodiscard]] InputModifiers modifier_for_key(const InputPhysicalKey key) noexcept
{
    if (key.scan_code == 0x1DU)
    {
        return InputModifiers::ctrl;
    }
    if (!key.extended && (key.scan_code == 0x2AU || key.scan_code == 0x36U))
    {
        return InputModifiers::shift;
    }
    if (key.scan_code == 0x38U)
    {
        return InputModifiers::alt;
    }
    if (key.extended && (key.scan_code == 0x5BU || key.scan_code == 0x5CU))
    {
        return InputModifiers::win;
    }
    return InputModifiers::none;
}
} // namespace

InputConfiguration parse_input_configuration(const std::string_view snapshot_json)
{
    if (snapshot_json.empty())
    {
        throw std::invalid_argument("Input configuration snapshot cannot be empty.");
    }

    const JsonObject root = JsonObject::Parse(winrt::to_hstring(snapshot_json));
    const IJsonValue switches_value = root.GetNamedValue(L"switches");
    if (switches_value.ValueType() != JsonValueType::Object)
    {
        throw std::invalid_argument("Input configuration requires a switches object.");
    }

    const JsonObject switches = switches_value.GetObject();
    require_allowed_properties(switches,
                               {L"visibilityRule", L"initialStateA", L"initialStateB", L"switchA", L"switchB"});
    return {
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
    add_switch_binding(plan, used_keys, configuration.switch_a, InputLogicalSwitch::a, next_hotkey_identifier);
    add_switch_binding(plan, used_keys, configuration.switch_b, InputLogicalSwitch::b, next_hotkey_identifier);
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

std::optional<RawKeyboardTransition> parse_raw_keyboard_input(const std::span<const std::byte> payload)
{
    if (payload.size() < sizeof(RAWINPUTHEADER))
    {
        throw std::invalid_argument("Raw Input payload is shorter than its header.");
    }

    RAWINPUTHEADER header{};
    std::memcpy(&header, payload.data(), sizeof(header));
    if (header.dwType != RIM_TYPEKEYBOARD)
    {
        return std::nullopt;
    }

    constexpr std::size_t keyboard_offset = offsetof(RAWINPUT, data);
    constexpr std::size_t keyboard_payload_size = keyboard_offset + sizeof(RAWKEYBOARD);
    if (payload.size() < keyboard_payload_size)
    {
        throw std::invalid_argument("Raw Input keyboard payload is incomplete.");
    }

    RAWKEYBOARD keyboard{};
    std::memcpy(&keyboard, payload.data() + keyboard_offset, sizeof(keyboard));
    std::uint16_t scan_code = keyboard.MakeCode;
    bool extended = (keyboard.Flags & (RI_KEY_E0 | RI_KEY_E1)) != 0U;
    if (scan_code == 0U)
    {
        const UINT mapped = MapVirtualKeyW(keyboard.VKey, MAPVK_VK_TO_VSC_EX);
        scan_code = static_cast<std::uint16_t>(mapped & 0xFFU);
        extended = extended || (mapped & 0xFF00U) != 0U;
    }
    if (scan_code == 0U)
    {
        return std::nullopt;
    }

    return RawKeyboardTransition{{scan_code, extended}, (keyboard.Flags & RI_KEY_BREAK) == 0U};
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

class GlobalInputService::Impl
{
  public:
    explicit Impl(StateChanged state_changed)
        : state_changed_(std::move(state_changed)),
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
            if ((event->flags & LLKHF_INJECTED) == 0U)
            {
                const bool pressed = word_parameter == WM_KEYDOWN || word_parameter == WM_SYSKEYDOWN;
                const bool extended = (event->flags & LLKHF_EXTENDED) != 0U;
                const LPARAM packed = static_cast<LPARAM>((event->scanCode & 0xFFFFU) | (extended ? 1U << 16U : 0U) |
                                                          (pressed ? 1U << 17U : 0U));
                const HWND window = service->window_.load(std::memory_order_acquire);
                if (window != nullptr)
                {
                    PostMessageW(window, kHookKeyboardMessage, 0U, packed);
                }
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

            initialize_keyboard_backend(window);
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

    void initialize_keyboard_backend(_In_ const HWND window)
    {
        RAWINPUTDEVICE keyboard{};
        keyboard.usUsagePage = 0x01U;
        keyboard.usUsage = 0x06U;
        keyboard.dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
        keyboard.hwndTarget = window;
        if (RegisterRawInputDevices(&keyboard, 1U, sizeof(keyboard)))
        {
            raw_input_registered_ = true;
            log_diagnostic(DiagnosticLevel::information, "input.raw_input_ready",
                           "Raw Input keyboard capture is active.");
            return;
        }

        hook_owner_.store(this, std::memory_order_release);
        low_level_hook_ = SetWindowsHookExW(WH_KEYBOARD_LL, low_level_keyboard_proc, GetModuleHandleW(nullptr), 0U);
        if (low_level_hook_ == nullptr)
        {
            hook_owner_.store(nullptr, std::memory_order_release);
            throw_last_error("Register Raw Input and low-level keyboard fallback");
        }
        log_diagnostic(DiagnosticLevel::warning, "input.raw_input_fallback",
                       "Raw Input registration failed; the low-level keyboard hook fallback is active.");
    }

    void cleanup() noexcept
    {
        unregister_hotkeys(active_plan_);
        if (const auto reset = state_machine_.reset_pressed_keys())
        {
            publish(*reset);
        }

        const HWND window = window_.exchange(nullptr, std::memory_order_acq_rel);
        if (session_notifications_registered_ && window != nullptr)
        {
            WTSUnRegisterSessionNotification(window);
            session_notifications_registered_ = false;
        }
        if (raw_input_registered_)
        {
            RAWINPUTDEVICE keyboard{};
            keyboard.usUsagePage = 0x01U;
            keyboard.usUsage = 0x06U;
            keyboard.dwFlags = RIDEV_REMOVE;
            keyboard.hwndTarget = nullptr;
            static_cast<void>(RegisterRawInputDevices(&keyboard, 1U, sizeof(keyboard)));
            raw_input_registered_ = false;
        }
        if (low_level_hook_ != nullptr)
        {
            hook_owner_.store(nullptr, std::memory_order_release);
            UnhookWindowsHookEx(low_level_hook_);
            low_level_hook_ = nullptr;
        }
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
            const std::uint16_t scan_code = static_cast<std::uint16_t>(long_parameter & 0xFFFF);
            const bool extended = (long_parameter & (1 << 16)) != 0;
            const bool pressed = (long_parameter & (1 << 17)) != 0;
            handle_keyboard_transition({scan_code, extended}, pressed);
            return 0;
        }
        case WM_HOTKEY:
        {
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
        InputBindingPlan candidate;
        try
        {
            candidate = build_input_binding_plan(configuration);
        }
        catch (const std::exception &error)
        {
            return {false, ERROR_INVALID_DATA, std::nullopt, error.what()};
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
        modifier_keys_.clear();
        configured_ = true;
        publish(state_machine_.configure(configuration, active_plan_.raw_bindings));
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
            throw_last_error("GetRawInputData keyboard");
        }
        if (received != size)
        {
            throw NativeError(ERROR_INVALID_DATA, "GetRawInputData keyboard length");
        }

        const std::optional<RawKeyboardTransition> transition = parse_raw_keyboard_input(buffer);
        if (!transition)
        {
            return;
        }

        handle_keyboard_transition(transition->key, transition->pressed);
    }

    void handle_keyboard_transition(const InputPhysicalKey physical, const bool pressed)
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

        if (const auto update =
                state_machine_.handle_key({physical.scan_code, physical.extended, event_modifiers}, pressed))
        {
            publish(*update);
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
    }

    void publish(InputStateSnapshot snapshot) const
    {
        snapshot.configured = configured_;
        state_changed_(snapshot);
    }

    StateChanged state_changed_;
    HostWorkerThread worker_;
    std::mutex startup_mutex_;
    std::condition_variable startup_changed_;
    std::exception_ptr startup_failure_;
    std::exception_ptr runtime_failure_;
    std::atomic<HWND> window_ = nullptr;
    HotkeyStateMachine state_machine_;
    InputBindingPlan active_plan_;
    std::vector<InputPhysicalKey> modifier_keys_;
    HHOOK low_level_hook_ = nullptr;
    bool ready_ = false;
    bool configured_ = false;
    bool raw_input_registered_ = false;
    bool session_notifications_registered_ = false;
    bool class_registered_ = false;

    static std::atomic<Impl *> hook_owner_;
};

std::atomic<GlobalInputService::Impl *> GlobalInputService::Impl::hook_owner_ = nullptr;

GlobalInputService::GlobalInputService(StateChanged state_changed)
    : impl_(std::make_unique<Impl>(std::move(state_changed)))
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

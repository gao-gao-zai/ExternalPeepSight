#pragma once

#include "host_threads.h"
#include "script_runtime.h"

#include <windows.h>

#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace external_peepsight
{
/// Identifies the modifier keys required by an input binding.
enum class InputModifiers : std::uint8_t
{
    none = 0U,
    ctrl = 1U,
    alt = 2U,
    shift = 4U,
    win = 8U,
};

/// Returns the bitwise union of two modifier sets.
[[nodiscard]] constexpr InputModifiers operator|(const InputModifiers left, const InputModifiers right) noexcept
{
    return static_cast<InputModifiers>(static_cast<std::uint8_t>(left) | static_cast<std::uint8_t>(right));
}

/// Returns the bitwise intersection of two modifier sets.
[[nodiscard]] constexpr InputModifiers operator&(const InputModifiers left, const InputModifiers right) noexcept
{
    return static_cast<InputModifiers>(static_cast<std::uint8_t>(left) & static_cast<std::uint8_t>(right));
}

/// Identifies the device that produces a configured input.
enum class InputDeviceKind : std::uint8_t
{
    keyboard,
    mouse,
};

/// Selects the Windows input API used to capture configured bindings.
enum class InputCaptureBackend : std::uint8_t
{
    raw_input,
    low_level_hook,
};

/// Identifies a bindable physical mouse button.
enum class InputMouseButton : std::uint16_t
{
    left = 1U,
    right = 2U,
    middle = 3U,
    x1 = 4U,
    x2 = 5U,
};

/// Identifies one physical keyboard key or mouse button and its required modifiers.
struct InputKeyIdentity
{
    /// Device that produces the input.
    InputDeviceKind device;
    /// Keyboard scan code or InputMouseButton value.
    std::uint16_t code;
    /// Whether the key uses an E0 or E1 extended scan-code prefix.
    bool extended;
    /// Modifiers that must be held when the key is pressed.
    InputModifiers modifiers;

    bool operator==(const InputKeyIdentity &) const = default;
};

/// Identifies one physical keyboard key or mouse button independently of modifier state.
struct InputPhysicalKey
{
    /// Device that produces the input.
    InputDeviceKind device;
    /// Keyboard scan code or InputMouseButton value.
    std::uint16_t code;
    /// Whether the key uses an E0 or E1 extended scan-code prefix.
    bool extended;

    bool operator==(const InputPhysicalKey &) const = default;
};

/// One physical keyboard or mouse transition decoded from a Raw Input payload.
struct RawInputTransition
{
    /// Physical input reported by the device.
    InputPhysicalKey key;
    /// Whether the transition is a key-down event.
    bool pressed;

    bool operator==(const RawInputTransition &) const = default;
};

/// Selects how a binding changes its logical switch.
enum class InputActivationMode
{
    unbound,
    independent,
    toggle,
    hold,
};

/// Identifies one of the two Host-owned logical switches.
enum class InputLogicalSwitch
{
    a,
    b,
};

/// Selects how the two logical switches control overlay visibility.
enum class InputVisibilityRule
{
    switch_a,
    switch_b,
    both,
    either,
};

/// Defines all keys associated with one logical switch.
struct InputHotkeyBinding
{
    /// Activation behavior for this logical switch.
    InputActivationMode mode = InputActivationMode::unbound;
    /// Key used by Toggle mode.
    std::optional<InputKeyIdentity> toggle_key;
    /// Key that enables Independent mode.
    std::optional<InputKeyIdentity> enable_key;
    /// Key that disables Independent mode.
    std::optional<InputKeyIdentity> disable_key;
    /// Key held by Hold mode.
    std::optional<InputKeyIdentity> hold_key;
};

/// Defines one active script binding and its user-selected physical key.
struct ScriptInputBinding
{
    /// Physical key selected by the user.
    InputKeyIdentity key;
    /// Scope that owns the binding slot.
    ScriptScope scope;
    /// Stable owning script identifier.
    std::string script_id;
    /// Stable script-declared binding identifier.
    std::string binding_id;
    /// Whether a key-down event is delivered.
    bool pressed;
    /// Whether a key-up event is delivered.
    bool released;

    bool operator==(const ScriptInputBinding &) const = default;
};

/// Defines the complete input configuration consumed by the Host.
struct InputConfiguration
{
    /// Windows input API used to capture configured bindings.
    InputCaptureBackend input_backend = InputCaptureBackend::raw_input;
    /// Rule that combines the two logical switches.
    InputVisibilityRule visibility_rule = InputVisibilityRule::switch_a;
    /// Initial state of logical switch A.
    bool initial_state_a = false;
    /// Initial state of logical switch B.
    bool initial_state_b = false;
    /// Binding for logical switch A.
    InputHotkeyBinding switch_a;
    /// Binding for logical switch B.
    InputHotkeyBinding switch_b;
    /// Active Lua binding slots included in the same conflict-checked plan.
    std::vector<ScriptInputBinding> script_bindings;
};

/// One normalized script input event published by the Input thread.
struct ScriptInputEvent
{
    /// Scope that owns the binding slot.
    ScriptScope scope;
    /// Stable owning script identifier.
    std::string script_id;
    /// Stable script-declared binding identifier.
    std::string binding_id;
    /// Physical transition phase.
    ScriptInputPhase phase;
};

/// Operation produced by one configured input binding.
enum class InputSwitchOperation
{
    enable,
    disable,
    toggle,
    hold,
};

/// Identifies the logical operation associated with one input event.
struct InputAction
{
    /// Logical switch changed by the action.
    InputLogicalSwitch target;
    /// State transition applied to the logical switch.
    InputSwitchOperation operation;

    bool operator==(const InputAction &) const = default;
};

/// Binding evaluated from Raw Input or the low-level hook fallback.
struct RawInputBinding
{
    /// Physical key and modifier identity matched by Raw Input.
    InputKeyIdentity key;
    /// Logical action produced by a matching transition.
    InputAction action;
};

/// Binding registered with the system-wide RegisterHotKey API.
struct RegisteredHotkeyBinding
{
    /// Identifier passed to RegisterHotKey.
    int identifier;
    /// Original physical key identity used for diagnostics.
    InputKeyIdentity key;
    /// Virtual key passed to RegisterHotKey.
    UINT virtual_key;
    /// Modifier flags passed to RegisterHotKey.
    UINT native_modifiers;
    /// Logical action produced by WM_HOTKEY.
    InputAction action;
};

/// Identifies one keyboard key sampled by GetAsyncKeyState.
struct InputPollingKey
{
    /// Physical keyboard key represented by the sampled virtual key.
    InputPhysicalKey key;
    /// Virtual-key code passed to GetAsyncKeyState.
    UINT virtual_key;

    bool operator==(const InputPollingKey &) const = default;
};

/// Validated backend assignment for one complete input configuration.
struct InputBindingPlan
{
    /// Bindings handled by the selected raw transition backend.
    std::vector<RawInputBinding> raw_bindings;
    /// Bindings handled exclusively by RegisterHotKey.
    std::vector<RegisteredHotkeyBinding> registered_hotkeys;
    /// Keyboard keys sampled as a fallback by the low-level hook backend.
    std::vector<InputPollingKey> polling_keys;
    /// Script bindings handled by Raw Input or the low-level hook backend.
    std::vector<ScriptInputBinding> script_bindings;
};

/// Current logical input state published by the Input thread.
struct InputStateSnapshot
{
    /// Current state of logical switch A.
    bool switch_a;
    /// Current state of logical switch B.
    bool switch_b;
    /// Evaluated overlay visibility.
    bool visible;
    /// Whether a valid input configuration is active.
    bool configured;

    bool operator==(const InputStateSnapshot &) const = default;
};

/// Result of applying input configuration to live Win32 registrations.
struct InputApplyResult
{
    /// Whether all native registrations were applied.
    bool applied;
    /// Win32 error associated with a registration failure.
    DWORD win32_error;
    /// Key whose registration or rollback failed.
    std::optional<InputKeyIdentity> failed_key;
    /// Stable developer-facing failure description.
    std::string message;
};

/// Parses and validates the switches section of a configuration snapshot.
[[nodiscard]] InputConfiguration parse_input_configuration(std::string_view snapshot_json);

/// Validates bindings and assigns each action to exactly one input backend.
[[nodiscard]] InputBindingPlan build_input_binding_plan(const InputConfiguration &configuration);

/// Evaluates logical switch state for the configured visibility rule.
[[nodiscard]] bool evaluate_input_visibility(InputVisibilityRule rule, bool switch_a, bool switch_b) noexcept;

/// Decodes all keyboard or mouse button transitions in one complete Raw Input payload.
[[nodiscard]] std::vector<RawInputTransition> parse_raw_input(std::span<const std::byte> payload);

/// Decodes one low-level keyboard hook message, including remote-client injected input.
[[nodiscard]] std::optional<RawInputTransition> decode_low_level_keyboard_input(WPARAM message,
                                                                                const KBDLLHOOKSTRUCT &event) noexcept;

/// Decodes one low-level mouse hook message, including remote-client injected input.
[[nodiscard]] std::optional<RawInputTransition> decode_low_level_mouse_input(WPARAM message,
                                                                             const MSLLHOOKSTRUCT &event) noexcept;

/// Deterministic logical-switch state machine shared by all input backends.
class HotkeyStateMachine
{
  public:
    /// Replaces all bindings and restores the configured initial switch state.
    [[nodiscard]] InputStateSnapshot configure(const InputConfiguration &configuration,
                                               std::vector<RawInputBinding> raw_bindings);

    /// Handles one physical key transition and suppresses repeated key-down events.
    [[nodiscard]] std::optional<InputStateSnapshot> handle_key(InputKeyIdentity key, bool pressed);

    /// Applies one action delivered by RegisterHotKey.
    [[nodiscard]] std::optional<InputStateSnapshot> trigger_registered(InputAction action);

    /// Releases all pressed keys and active Hold bindings.
    [[nodiscard]] std::optional<InputStateSnapshot> reset_pressed_keys();

    /// Returns the current logical state.
    [[nodiscard]] InputStateSnapshot snapshot(bool configured = true) const noexcept;

  private:
    struct ActiveHold
    {
        InputPhysicalKey key;
        InputLogicalSwitch target;
    };

    [[nodiscard]] std::optional<InputStateSnapshot> apply(InputAction action);
    [[nodiscard]] bool &state(InputLogicalSwitch target) noexcept;

    InputVisibilityRule visibility_rule_ = InputVisibilityRule::switch_a;
    bool switch_a_ = false;
    bool switch_b_ = false;
    std::vector<RawInputBinding> raw_bindings_;
    std::vector<InputPhysicalKey> pressed_keys_;
    std::vector<ActiveHold> active_holds_;
};

/// Deterministic script-binding state machine shared by all input backends.
class ScriptInputStateMachine
{
  public:
    /// Replaces all script bindings and clears active physical inputs.
    void configure(std::vector<ScriptInputBinding> bindings);

    /// Handles one physical transition and returns zero or one normalized event.
    [[nodiscard]] std::optional<ScriptInputEvent> handle_key(InputKeyIdentity key, bool pressed);

    /// Releases every active binding that declared a release event.
    [[nodiscard]] std::vector<ScriptInputEvent> reset_pressed_keys();

  private:
    struct ActiveInput
    {
        InputPhysicalKey key;
        ScriptInputBinding binding;
    };

    std::vector<ScriptInputBinding> bindings_;
    std::vector<ActiveInput> active_inputs_;
};

/// Owns the hidden Input-thread window and all global keyboard backends.
class GlobalInputService
{
  public:
    /// Callback invoked on the Input thread after observable state changes.
    using StateChanged = std::function<void(InputStateSnapshot)>;
    /// Callback invoked on the Input thread for a normalized script binding event.
    using ScriptInputReceived = std::function<void(ScriptInputEvent)>;

    /// Creates an input service with a non-blocking state publication callback.
    explicit GlobalInputService(StateChanged state_changed, ScriptInputReceived script_input_received = {});

    GlobalInputService(const GlobalInputService &) = delete;
    GlobalInputService &operator=(const GlobalInputService &) = delete;

    /// Stops all input capture and releases native registrations.
    ~GlobalInputService();

    /// Starts the hidden input window and waits until a backend is available.
    void start();

    /// Applies configuration synchronously on the Input thread.
    [[nodiscard]] InputApplyResult apply_configuration(const InputConfiguration &configuration);

    /// Requests shutdown and waits for the Input thread.
    void stop() noexcept;

  private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace external_peepsight

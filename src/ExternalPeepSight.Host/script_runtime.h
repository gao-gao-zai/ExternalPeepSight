#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace external_peepsight
{
/// Identifies the product object that owns one Lua script.
enum class ScriptScope : std::uint8_t
{
    profile,
    profile_set,
    global,
};

/// Identifies a script input transition.
enum class ScriptInputPhase : std::uint8_t
{
    pressed,
    released,
};

/// Identifies the device used by a script-declared default binding.
enum class ScriptBindingDeviceKind : std::uint8_t
{
    keyboard,
    mouse,
};

/// Identifies the modifier set used by a script-declared default binding.
enum class ScriptKeyModifiers : std::uint8_t
{
    none = 0U,
    ctrl = 1U,
    alt = 2U,
    shift = 4U,
    win = 8U,
};

/// Defines one normalized physical key suggested for a newly discovered binding slot.
struct ScriptBindingDefaultKey
{
    /// Device that produces the input.
    ScriptBindingDeviceKind device;
    /// Keyboard scan code or mouse button value.
    std::uint16_t code;
    /// Whether the keyboard key uses an extended scan-code prefix.
    bool extended;
    /// Modifier keys required by the binding.
    ScriptKeyModifiers modifiers;

    bool operator==(const ScriptBindingDefaultKey &) const = default;
};

/// Identifies the persistent value type of a script setting.
enum class ScriptSettingType : std::uint8_t
{
    boolean,
    integer,
    number,
    text,
    choice,
};

/// Selects the trusted UI control used to edit one script setting.
enum class ScriptUiControlType : std::uint8_t
{
    automatic,
    switch_control,
    checkbox,
    slider,
    number,
    textbox,
    select,
    segmented,
};

/// Declares one user-bindable input slot exposed by a script.
struct ScriptBindingDeclaration
{
    /// Stable script-owned identifier.
    std::string id;
    /// User-facing label.
    std::string display_name;
    /// Whether key-down events are delivered.
    bool pressed;
    /// Whether key-up events are delivered.
    bool released;
    /// Default enabled state for a newly discovered binding.
    bool default_enabled;
    /// Optional physical key used only when the binding is first discovered.
    std::optional<ScriptBindingDefaultKey> default_key;

    bool operator==(const ScriptBindingDeclaration &) const = default;
};

/// Declares one typed user setting exposed by a script.
struct ScriptSettingDeclaration
{
    /// Stable script-owned identifier.
    std::string id;
    /// User-facing label.
    std::string display_name;
    /// Persistent setting type.
    ScriptSettingType type;
    /// Canonical default value.
    std::string default_value;
    /// Fixed values accepted by a choice setting.
    std::vector<std::string> options;
    /// Optional inclusive numeric minimum.
    std::optional<double> minimum;
    /// Optional inclusive numeric maximum.
    std::optional<double> maximum;

    bool operator==(const ScriptSettingDeclaration &) const = default;
};

/// Defines a simple equality condition controlling one UI item's visibility.
struct ScriptUiVisibilityCondition
{
    /// Stable identifier of the setting evaluated by the condition.
    std::string setting_id;
    /// Canonical setting value required for the item to be visible.
    std::string equals_value;

    bool operator==(const ScriptUiVisibilityCondition &) const = default;
};

/// Defines presentation metadata for one script setting.
struct ScriptUiItemDeclaration
{
    /// Stable identifier of the setting rendered by this item.
    std::string setting_id;
    /// Trusted control selected from the script UI allowlist.
    ScriptUiControlType control;
    /// Optional user-facing supporting text.
    std::string description;
    /// Optional short unit suffix.
    std::string unit;
    /// Optional positive numeric editing step.
    std::optional<double> step;
    /// Optional visibility condition.
    std::optional<ScriptUiVisibilityCondition> visible_when;

    bool operator==(const ScriptUiItemDeclaration &) const = default;
};

/// Defines one ordered section in a script-provided settings layout.
struct ScriptUiSectionDeclaration
{
    /// Stable script-owned section identifier.
    std::string id;
    /// User-facing section title.
    std::string display_name;
    /// Optional user-facing supporting text.
    std::string description;
    /// Whether the section can be collapsed.
    bool collapsible;
    /// Initial expansion state when the section is collapsible.
    bool default_expanded;
    /// Requested responsive column count, limited to one or two.
    std::uint8_t columns;
    /// Ordered setting items rendered in this section.
    std::vector<ScriptUiItemDeclaration> items;

    bool operator==(const ScriptUiSectionDeclaration &) const = default;
};

/// Defines the complete trusted settings layout declared by one script.
struct ScriptUiDeclaration
{
    /// Ordered settings sections.
    std::vector<ScriptUiSectionDeclaration> sections;

    bool operator==(const ScriptUiDeclaration &) const = default;
};

/// Contains all declarations produced by a successfully loaded script.
struct ScriptDeclarations
{
    /// Script API version selected by the source document.
    std::string api_version = "1";
    /// User-bindable input slots.
    std::vector<ScriptBindingDeclaration> bindings;
    /// Typed user settings.
    std::vector<ScriptSettingDeclaration> settings;
    /// Optional trusted settings layout.
    std::optional<ScriptUiDeclaration> ui;

    bool operator==(const ScriptDeclarations &) const = default;
};

/// Supplies one persisted setting value to a script instance.
struct ScriptSettingValue
{
    /// Stable declaration identifier.
    std::string id;
    /// Persisted setting type.
    ScriptSettingType type;
    /// Canonical persisted value.
    std::string value;
};

/// Supplies read-only active configuration identity to one callback.
struct ScriptContext
{
    /// Active profile set identifier.
    std::string profile_set_id;
    /// Active profile identifier.
    std::string profile_id;
};

/// Identifies a product state transition requested by Lua.
enum class ScriptCommandType : std::uint8_t
{
    switch_profile,
    previous_profile,
    next_profile,
    switch_profile_set,
    previous_profile_set,
    next_profile_set,
};

/// One validated product state transition requested by Lua.
struct ScriptCommand
{
    /// Requested operation.
    ScriptCommandType type;
    /// Stable target identifier for an explicit switch.
    std::string target_id;

    bool operator==(const ScriptCommand &) const = default;
};

/// Result of invoking one optional script callback.
struct ScriptExecutionResult
{
    /// Whether the callback completed within all sandbox limits.
    bool succeeded;
    /// Developer-facing runtime error when execution failed.
    std::string error;
    /// Current visibility decision after a successful callback.
    bool allows_visible;
    /// Commands staged by the successful callback.
    std::vector<ScriptCommand> commands;
};

/// Owns one isolated, resource-limited Lua state.
///
/// Thread: All methods must be called from the same script thread.
class LuaScript
{
  public:
    /// Loads and validates one text-only script in the restricted environment.
    LuaScript(std::string source, ScriptScope scope, std::vector<ScriptSettingValue> setting_values = {});

    LuaScript(const LuaScript &) = delete;
    LuaScript &operator=(const LuaScript &) = delete;
    LuaScript(LuaScript &&) noexcept;
    LuaScript &operator=(LuaScript &&) noexcept;
    ~LuaScript();

    /// Returns declarations extracted from the loaded descriptor.
    [[nodiscard]] const ScriptDeclarations &declarations() const noexcept;

    /// Returns the current visibility decision.
    [[nodiscard]] bool allows_visible() const noexcept;

    /// Invokes the optional startup callback.
    [[nodiscard]] ScriptExecutionResult start(const ScriptContext &context);

    /// Invokes the optional input callback.
    [[nodiscard]] ScriptExecutionResult input(std::string_view binding_id, ScriptInputPhase phase,
                                              const ScriptContext &context);

  private:
    class Impl;
    std::unique_ptr<Impl> impl_;
};
} // namespace external_peepsight

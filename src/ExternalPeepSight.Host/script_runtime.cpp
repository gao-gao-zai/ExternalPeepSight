#include "script_runtime.h"

extern "C"
{
#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
}

#include <algorithm>
#include <array>
#include <cctype>
#include <cerrno>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <limits>
#include <stdexcept>
#include <unordered_map>
#include <unordered_set>
#include <utility>

namespace external_peepsight
{
namespace
{
constexpr std::size_t kMemoryLimitBytes = 4U * 1024U * 1024U;
constexpr std::size_t kMaximumItems = 64U;
constexpr std::size_t kMaximumUiSections = 16U;
constexpr std::size_t kMaximumIdentifierLength = 64U;
constexpr std::size_t kMaximumDisplayNameLength = 100U;
constexpr std::size_t kMaximumDescriptionLength = 300U;
constexpr std::size_t kMaximumUnitLength = 32U;
constexpr std::size_t kMaximumStringLength = 4096U;
constexpr std::uint64_t kInstructionLimit = 100'000U;
constexpr int kInstructionHookInterval = 1'000;
constexpr std::chrono::milliseconds kExecutionLimit{10};

[[nodiscard]] std::string lua_string(lua_State *state, const int index)
{
    std::size_t size = 0U;
    const char *value = lua_tolstring(state, index, &size);
    if (value == nullptr)
    {
        throw std::invalid_argument("Lua value must be a string.");
    }
    return std::string(value, size);
}

[[nodiscard]] bool is_ascii_letter(const unsigned char value) noexcept
{
    return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
}

void validate_identifier(const std::string_view value)
{
    if (value.empty() || value.size() > kMaximumIdentifierLength ||
        !is_ascii_letter(static_cast<unsigned char>(value.front())) ||
        !std::ranges::all_of(value,
                             [](const unsigned char character)
                             {
                                 return is_ascii_letter(character) || std::isdigit(character) != 0 ||
                                        character == '_' || character == '-';
                             }))
    {
        throw std::invalid_argument("Script identifier is invalid.");
    }
}

void validate_display_name(const std::string_view value)
{
    if (value.empty() || value.size() > kMaximumDisplayNameLength)
    {
        throw std::invalid_argument("Script display name is invalid.");
    }
}

[[nodiscard]] std::string bounded_error(lua_State *state)
{
    std::string message = lua_isstring(state, -1) != 0 ? lua_string(state, -1) : "Lua execution failed.";
    constexpr std::size_t maximum_error_length = 2048U;
    if (message.size() > maximum_error_length)
    {
        message.resize(maximum_error_length);
    }
    lua_pop(state, 1);
    return message;
}

[[nodiscard]] bool optional_boolean(lua_State *state, const int table_index, const char *name, const bool default_value)
{
    lua_getfield(state, table_index, name);
    if (lua_isnil(state, -1) != 0)
    {
        lua_pop(state, 1);
        return default_value;
    }
    if (lua_isboolean(state, -1) == 0)
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Lua declaration boolean is invalid.");
    }
    const bool value = lua_toboolean(state, -1) != 0;
    lua_pop(state, 1);
    return value;
}

[[nodiscard]] std::optional<double> optional_number(lua_State *state, const int table_index, const char *name)
{
    lua_getfield(state, table_index, name);
    if (lua_isnil(state, -1) != 0)
    {
        lua_pop(state, 1);
        return std::nullopt;
    }
    if (lua_isnumber(state, -1) == 0)
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Lua declaration number is invalid.");
    }
    const double value = lua_tonumber(state, -1);
    lua_pop(state, 1);
    if (!std::isfinite(value))
    {
        throw std::invalid_argument("Lua declaration number must be finite.");
    }
    return value;
}

[[nodiscard]] std::string optional_string(lua_State *state, const int table_index, const char *name,
                                          const std::size_t maximum_length)
{
    lua_getfield(state, table_index, name);
    if (lua_isnil(state, -1) != 0)
    {
        lua_pop(state, 1);
        return {};
    }
    if (lua_type(state, -1) != LUA_TSTRING)
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Lua declaration string is invalid.");
    }
    std::string value = lua_string(state, -1);
    lua_pop(state, 1);
    if (value.size() > maximum_length)
    {
        throw std::invalid_argument("Lua declaration string is too long.");
    }
    return value;
}

[[nodiscard]] lua_Integer optional_integer(lua_State *state, const int table_index, const char *name,
                                           const lua_Integer default_value)
{
    lua_getfield(state, table_index, name);
    if (lua_isnil(state, -1) != 0)
    {
        lua_pop(state, 1);
        return default_value;
    }
    if (lua_isinteger(state, -1) == 0)
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Lua declaration integer is invalid.");
    }
    const lua_Integer value = lua_tointeger(state, -1);
    lua_pop(state, 1);
    return value;
}

void validate_table_keys(lua_State *state, const int table_index, const std::initializer_list<std::string_view> allowed,
                         const char *error)
{
    const int absolute_index = lua_absindex(state, table_index);
    lua_pushnil(state);
    while (lua_next(state, absolute_index) != 0)
    {
        if (lua_type(state, -2) != LUA_TSTRING)
        {
            lua_pop(state, 2);
            throw std::invalid_argument(error);
        }
        const std::string key = lua_string(state, -2);
        if (std::ranges::find(allowed, key) == allowed.end())
        {
            lua_pop(state, 2);
            throw std::invalid_argument(error);
        }
        lua_pop(state, 1);
    }
}

void validate_sequence(lua_State *state, const int table_index, const lua_Integer length, const char *error)
{
    const int absolute_index = lua_absindex(state, table_index);
    lua_pushnil(state);
    while (lua_next(state, absolute_index) != 0)
    {
        if (lua_isinteger(state, -2) == 0)
        {
            lua_pop(state, 2);
            throw std::invalid_argument(error);
        }
        const lua_Integer index = lua_tointeger(state, -2);
        if (index < 1 || index > length)
        {
            lua_pop(state, 2);
            throw std::invalid_argument(error);
        }
        lua_pop(state, 1);
    }
}

struct NamedBindingKey
{
    std::string_view name;
    std::uint16_t code;
    bool extended;
};

constexpr std::array kNamedKeyboardKeys{
    NamedBindingKey{"escape", 0x01U, false},
    NamedBindingKey{"minus", 0x0CU, false},
    NamedBindingKey{"equal", 0x0DU, false},
    NamedBindingKey{"backspace", 0x0EU, false},
    NamedBindingKey{"tab", 0x0FU, false},
    NamedBindingKey{"q", 0x10U, false},
    NamedBindingKey{"w", 0x11U, false},
    NamedBindingKey{"e", 0x12U, false},
    NamedBindingKey{"r", 0x13U, false},
    NamedBindingKey{"t", 0x14U, false},
    NamedBindingKey{"y", 0x15U, false},
    NamedBindingKey{"u", 0x16U, false},
    NamedBindingKey{"i", 0x17U, false},
    NamedBindingKey{"o", 0x18U, false},
    NamedBindingKey{"p", 0x19U, false},
    NamedBindingKey{"bracketleft", 0x1AU, false},
    NamedBindingKey{"bracketright", 0x1BU, false},
    NamedBindingKey{"enter", 0x1CU, false},
    NamedBindingKey{"a", 0x1EU, false},
    NamedBindingKey{"s", 0x1FU, false},
    NamedBindingKey{"d", 0x20U, false},
    NamedBindingKey{"f", 0x21U, false},
    NamedBindingKey{"g", 0x22U, false},
    NamedBindingKey{"h", 0x23U, false},
    NamedBindingKey{"j", 0x24U, false},
    NamedBindingKey{"k", 0x25U, false},
    NamedBindingKey{"l", 0x26U, false},
    NamedBindingKey{"semicolon", 0x27U, false},
    NamedBindingKey{"quote", 0x28U, false},
    NamedBindingKey{"backquote", 0x29U, false},
    NamedBindingKey{"backslash", 0x2BU, false},
    NamedBindingKey{"z", 0x2CU, false},
    NamedBindingKey{"x", 0x2DU, false},
    NamedBindingKey{"c", 0x2EU, false},
    NamedBindingKey{"v", 0x2FU, false},
    NamedBindingKey{"b", 0x30U, false},
    NamedBindingKey{"n", 0x31U, false},
    NamedBindingKey{"m", 0x32U, false},
    NamedBindingKey{"comma", 0x33U, false},
    NamedBindingKey{"period", 0x34U, false},
    NamedBindingKey{"slash", 0x35U, false},
    NamedBindingKey{"space", 0x39U, false},
    NamedBindingKey{"f1", 0x3BU, false},
    NamedBindingKey{"f2", 0x3CU, false},
    NamedBindingKey{"f3", 0x3DU, false},
    NamedBindingKey{"f4", 0x3EU, false},
    NamedBindingKey{"f5", 0x3FU, false},
    NamedBindingKey{"f6", 0x40U, false},
    NamedBindingKey{"f7", 0x41U, false},
    NamedBindingKey{"f8", 0x42U, false},
    NamedBindingKey{"f9", 0x43U, false},
    NamedBindingKey{"f10", 0x44U, false},
    NamedBindingKey{"f11", 0x57U, false},
    NamedBindingKey{"f12", 0x58U, false},
    NamedBindingKey{"numpadenter", 0x1CU, true},
    NamedBindingKey{"numpaddivide", 0x35U, true},
    NamedBindingKey{"home", 0x47U, true},
    NamedBindingKey{"arrowup", 0x48U, true},
    NamedBindingKey{"pageup", 0x49U, true},
    NamedBindingKey{"arrowleft", 0x4BU, true},
    NamedBindingKey{"arrowright", 0x4DU, true},
    NamedBindingKey{"end", 0x4FU, true},
    NamedBindingKey{"arrowdown", 0x50U, true},
    NamedBindingKey{"pagedown", 0x51U, true},
    NamedBindingKey{"insert", 0x52U, true},
    NamedBindingKey{"delete", 0x53U, true},
};

[[nodiscard]] std::string ascii_lowercase(std::string value)
{
    std::ranges::transform(value, value.begin(),
                           [](const unsigned char character)
                           {
                               return character >= 'A' && character <= 'Z' ? static_cast<char>(character - 'A' + 'a')
                                                                           : static_cast<char>(character);
                           });
    return value;
}

[[nodiscard]] NamedBindingKey parse_keyboard_binding_key(const std::string_view name)
{
    if (name.size() == 1U && name.front() >= '0' && name.front() <= '9')
    {
        return {
            name,
            static_cast<std::uint16_t>(name.front() == '0' ? 0x0BU : 0x02U + (name.front() - '1')),
            false,
        };
    }

    const auto key = std::ranges::find_if(kNamedKeyboardKeys,
                                          [name](const NamedBindingKey &candidate) { return candidate.name == name; });
    if (key == kNamedKeyboardKeys.end())
    {
        throw std::invalid_argument("Script binding default keyboard key is invalid.");
    }
    return *key;
}

[[nodiscard]] std::uint16_t parse_mouse_binding_key(const std::string_view name)
{
    if (name == "left")
    {
        return 1U;
    }
    if (name == "right")
    {
        return 2U;
    }
    if (name == "middle")
    {
        return 3U;
    }
    if (name == "x1")
    {
        return 4U;
    }
    if (name == "x2")
    {
        return 5U;
    }
    throw std::invalid_argument("Script binding default mouse button is invalid.");
}

[[nodiscard]] ScriptKeyModifiers parse_script_key_modifiers(lua_State *state, const int table_index)
{
    lua_getfield(state, table_index, "modifiers");
    if (lua_isnil(state, -1) != 0)
    {
        lua_pop(state, 1);
        return ScriptKeyModifiers::none;
    }
    if (lua_istable(state, -1) == 0)
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Script binding default modifiers must be a table.");
    }

    const lua_Integer count = luaL_len(state, -1);
    if (count < 0 || count > 4)
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Script binding default modifiers are invalid.");
    }
    validate_sequence(state, -1, count, "Script binding default modifiers must be a sequence.");

    std::uint8_t result = 0U;
    for (lua_Integer index = 1; index <= count; ++index)
    {
        lua_geti(state, -1, index);
        const std::string modifier = ascii_lowercase(lua_string(state, -1));
        lua_pop(state, 1);

        std::uint8_t value = 0U;
        if (modifier == "ctrl")
        {
            value = static_cast<std::uint8_t>(ScriptKeyModifiers::ctrl);
        }
        else if (modifier == "alt")
        {
            value = static_cast<std::uint8_t>(ScriptKeyModifiers::alt);
        }
        else if (modifier == "shift")
        {
            value = static_cast<std::uint8_t>(ScriptKeyModifiers::shift);
        }
        else if (modifier == "win")
        {
            value = static_cast<std::uint8_t>(ScriptKeyModifiers::win);
        }
        else
        {
            lua_pop(state, 1);
            throw std::invalid_argument("Script binding default modifier is invalid.");
        }

        if ((result & value) != 0U)
        {
            lua_pop(state, 1);
            throw std::invalid_argument("Script binding default modifier is duplicated.");
        }
        result |= value;
    }
    lua_pop(state, 1);
    return static_cast<ScriptKeyModifiers>(result);
}

[[nodiscard]] std::optional<ScriptBindingDefaultKey> parse_script_default_key(lua_State *state, const int binding_index)
{
    lua_getfield(state, binding_index, "default_key");
    if (lua_isnil(state, -1) != 0)
    {
        lua_pop(state, 1);
        return std::nullopt;
    }
    if (lua_istable(state, -1) == 0)
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Script binding default key must be a table.");
    }
    validate_table_keys(state, -1, {"device", "key", "modifiers"},
                        "Script binding default key contains an unknown property.");
    const int default_index = lua_absindex(state, -1);

    lua_getfield(state, default_index, "device");
    const std::string device = ascii_lowercase(lua_string(state, -1));
    lua_pop(state, 1);
    lua_getfield(state, default_index, "key");
    const std::string key_name = ascii_lowercase(lua_string(state, -1));
    lua_pop(state, 1);
    const ScriptKeyModifiers modifiers = parse_script_key_modifiers(state, default_index);

    ScriptBindingDefaultKey result{};
    if (device == "keyboard")
    {
        const NamedBindingKey key = parse_keyboard_binding_key(key_name);
        result = {ScriptBindingDeviceKind::keyboard, key.code, key.extended, modifiers};
    }
    else if (device == "mouse")
    {
        result = {ScriptBindingDeviceKind::mouse, parse_mouse_binding_key(key_name), false, modifiers};
    }
    else
    {
        lua_pop(state, 1);
        throw std::invalid_argument("Script binding default device is invalid.");
    }

    lua_pop(state, 1);
    return result;
}

[[nodiscard]] ScriptSettingType parse_setting_type(const std::string_view value)
{
    if (value == "boolean")
    {
        return ScriptSettingType::boolean;
    }
    if (value == "integer")
    {
        return ScriptSettingType::integer;
    }
    if (value == "double")
    {
        return ScriptSettingType::number;
    }
    if (value == "string")
    {
        return ScriptSettingType::text;
    }
    if (value == "enum")
    {
        return ScriptSettingType::choice;
    }
    throw std::invalid_argument("Script setting type is invalid.");
}

[[nodiscard]] ScriptUiControlType parse_ui_control(const std::string_view value)
{
    if (value == "auto")
    {
        return ScriptUiControlType::automatic;
    }
    if (value == "switch")
    {
        return ScriptUiControlType::switch_control;
    }
    if (value == "checkbox")
    {
        return ScriptUiControlType::checkbox;
    }
    if (value == "slider")
    {
        return ScriptUiControlType::slider;
    }
    if (value == "number")
    {
        return ScriptUiControlType::number;
    }
    if (value == "textbox")
    {
        return ScriptUiControlType::textbox;
    }
    if (value == "select")
    {
        return ScriptUiControlType::select;
    }
    if (value == "segmented")
    {
        return ScriptUiControlType::segmented;
    }
    throw std::invalid_argument("Script UI control type is invalid.");
}

[[nodiscard]] std::string canonical_setting_value(lua_State *state, const int index, const ScriptSettingType type)
{
    switch (type)
    {
    case ScriptSettingType::boolean:
        if (lua_isboolean(state, index) == 0)
        {
            throw std::invalid_argument("Boolean setting default is invalid.");
        }
        return lua_toboolean(state, index) != 0 ? "true" : "false";
    case ScriptSettingType::integer:
        if (lua_isinteger(state, index) == 0)
        {
            throw std::invalid_argument("Integer setting default is invalid.");
        }
        return std::to_string(lua_tointeger(state, index));
    case ScriptSettingType::number:
    {
        if (lua_isnumber(state, index) == 0)
        {
            throw std::invalid_argument("Double setting default is invalid.");
        }
        const double value = lua_tonumber(state, index);
        if (!std::isfinite(value))
        {
            throw std::invalid_argument("Double setting default must be finite.");
        }
        char buffer[64]{};
        const int length = std::snprintf(buffer, sizeof(buffer), "%.17g", value);
        if (length <= 0)
        {
            throw std::invalid_argument("Double setting default could not be encoded.");
        }
        return std::string(buffer, static_cast<std::size_t>(length));
    }
    case ScriptSettingType::text:
    case ScriptSettingType::choice:
    {
        if (lua_type(state, index) != LUA_TSTRING)
        {
            throw std::invalid_argument("String setting default is invalid.");
        }
        std::string value = lua_string(state, index);
        if (value.size() > kMaximumStringLength)
        {
            throw std::invalid_argument("String setting default is too long.");
        }
        return value;
    }
    }
    throw std::invalid_argument("Script setting type is invalid.");
}

void push_setting_value(lua_State *state, const ScriptSettingValue &setting)
{
    switch (setting.type)
    {
    case ScriptSettingType::boolean:
        if (setting.value == "true")
        {
            lua_pushboolean(state, 1);
        }
        else if (setting.value == "false")
        {
            lua_pushboolean(state, 0);
        }
        else
        {
            throw std::invalid_argument("Persisted Boolean script setting is invalid.");
        }
        return;
    case ScriptSettingType::integer:
    {
        errno = 0;
        char *end = nullptr;
        const long long value = std::strtoll(setting.value.c_str(), &end, 10);
        if (end == setting.value.c_str() || *end != '\0' || errno == ERANGE ||
            value < static_cast<long long>((std::numeric_limits<lua_Integer>::min)()) ||
            value > static_cast<long long>((std::numeric_limits<lua_Integer>::max)()))
        {
            throw std::invalid_argument("Persisted Integer script setting is invalid.");
        }
        lua_pushinteger(state, static_cast<lua_Integer>(value));
        return;
    }
    case ScriptSettingType::number:
    {
        char *end = nullptr;
        const double value = std::strtod(setting.value.c_str(), &end);
        if (end == setting.value.c_str() || *end != '\0' || !std::isfinite(value))
        {
            throw std::invalid_argument("Persisted Double script setting is invalid.");
        }
        lua_pushnumber(state, value);
        return;
    }
    case ScriptSettingType::text:
    case ScriptSettingType::choice:
        if (setting.value.size() > kMaximumStringLength)
        {
            throw std::invalid_argument("Persisted String script setting is too long.");
        }
        lua_pushlstring(state, setting.value.data(), setting.value.size());
        return;
    }
    throw std::invalid_argument("Persisted script setting type is invalid.");
}
} // namespace

class LuaScript::Impl
{
  public:
    Impl(std::string source, const ScriptScope scope, std::vector<ScriptSettingValue> setting_values)
        : scope_(scope), setting_values_(std::move(setting_values))
    {
        state_ = lua_newstate(allocate, &memory_);
        if (state_ == nullptr)
        {
            throw std::bad_alloc();
        }
        *static_cast<Impl **>(lua_getextraspace(state_)) = this;
        try
        {
            open_sandbox();
            load(std::move(source));
        }
        catch (...)
        {
            lua_close(state_);
            state_ = nullptr;
            throw;
        }
    }

    ~Impl()
    {
        if (state_ != nullptr)
        {
            lua_close(state_);
        }
    }

    [[nodiscard]] const ScriptDeclarations &declarations() const noexcept
    {
        return declarations_;
    }

    [[nodiscard]] bool allows_visible() const noexcept
    {
        return allows_visible_;
    }

    [[nodiscard]] ScriptExecutionResult start(const ScriptContext &context)
    {
        return invoke(on_start_reference_, std::nullopt, context);
    }

    [[nodiscard]] ScriptExecutionResult input(const std::string_view binding_id, const ScriptInputPhase phase,
                                              const ScriptContext &context)
    {
        return invoke(on_input_reference_, std::pair(binding_id, phase), context);
    }

  private:
    struct MemoryState
    {
        std::size_t used_bytes = 0U;
    };

    static void *allocate(void *user_data, void *pointer, const std::size_t old_size, const std::size_t new_size)
    {
        auto &memory = *static_cast<MemoryState *>(user_data);
        if (new_size == 0U)
        {
            memory.used_bytes = old_size > memory.used_bytes ? 0U : memory.used_bytes - old_size;
            std::free(pointer);
            return nullptr;
        }

        const std::size_t retained = old_size > memory.used_bytes ? 0U : memory.used_bytes - old_size;
        if (new_size > kMemoryLimitBytes - retained)
        {
            return nullptr;
        }

        void *result = std::realloc(pointer, new_size);
        if (result != nullptr)
        {
            memory.used_bytes = retained + new_size;
        }
        return result;
    }

    static void instruction_hook(lua_State *state, lua_Debug *)
    {
        Impl *self = *static_cast<Impl **>(lua_getextraspace(state));
        self->executed_instructions_ += kInstructionHookInterval;
        if (self->executed_instructions_ > kInstructionLimit ||
            std::chrono::steady_clock::now() > self->execution_deadline_)
        {
            luaL_error(state, "Script execution limit exceeded.");
        }
    }

    static int script_descriptor(lua_State *state)
    {
        luaL_checktype(state, 1, LUA_TTABLE);
        lua_settop(state, 1);
        return 1;
    }

    static Impl &owner(lua_State *state)
    {
        return **static_cast<Impl **>(lua_getextraspace(state));
    }

    static int visibility_set(lua_State *state)
    {
        Impl &self = owner(state);
        const int value_index = lua_gettop(state) >= 2 ? 2 : 1;
        if (lua_isboolean(state, value_index) == 0)
        {
            return luaL_error(state, "Visibility value must be Boolean.");
        }
        self.pending_visibility_ = lua_toboolean(state, value_index) != 0;
        return 0;
    }

    static int visibility_toggle(lua_State *state)
    {
        Impl &self = owner(state);
        self.pending_visibility_ = !self.pending_visibility_.value_or(self.allows_visible_);
        return 0;
    }

    static int profile_switch(lua_State *state)
    {
        Impl &self = owner(state);
        try
        {
            const int value_index = lua_gettop(state) >= 2 ? 2 : 1;
            std::string target = lua_string(state, value_index);
            if (target.empty())
            {
                return luaL_error(state, "Profile identifier cannot be empty.");
            }
            self.pending_commands_.push_back({ScriptCommandType::switch_profile, std::move(target)});
            return 0;
        }
        catch (const std::exception &error)
        {
            return luaL_error(state, "%s", error.what());
        }
    }

    static int profile_previous(lua_State *state)
    {
        owner(state).pending_commands_.push_back({ScriptCommandType::previous_profile, {}});
        return 0;
    }

    static int profile_next(lua_State *state)
    {
        owner(state).pending_commands_.push_back({ScriptCommandType::next_profile, {}});
        return 0;
    }

    static int profile_set_switch(lua_State *state)
    {
        Impl &self = owner(state);
        if (self.scope_ != ScriptScope::global)
        {
            return luaL_error(state, "Only a global script can switch profile sets.");
        }
        try
        {
            const int value_index = lua_gettop(state) >= 2 ? 2 : 1;
            std::string target = lua_string(state, value_index);
            if (target.empty())
            {
                return luaL_error(state, "Profile set identifier cannot be empty.");
            }
            self.pending_commands_.push_back({ScriptCommandType::switch_profile_set, std::move(target)});
            return 0;
        }
        catch (const std::exception &error)
        {
            return luaL_error(state, "%s", error.what());
        }
    }

    static int profile_set_previous(lua_State *state)
    {
        Impl &self = owner(state);
        if (self.scope_ != ScriptScope::global)
        {
            return luaL_error(state, "Only a global script can switch profile sets.");
        }
        self.pending_commands_.push_back({ScriptCommandType::previous_profile_set, {}});
        return 0;
    }

    static int profile_set_next(lua_State *state)
    {
        Impl &self = owner(state);
        if (self.scope_ != ScriptScope::global)
        {
            return luaL_error(state, "Only a global script can switch profile sets.");
        }
        self.pending_commands_.push_back({ScriptCommandType::next_profile_set, {}});
        return 0;
    }

    void open_sandbox()
    {
        luaL_requiref(state_, "_G", luaopen_base, 1);
        lua_pop(state_, 1);
        luaL_requiref(state_, LUA_TABLIBNAME, luaopen_table, 1);
        lua_pop(state_, 1);
        luaL_requiref(state_, LUA_STRLIBNAME, luaopen_string, 1);
        lua_pop(state_, 1);
        luaL_requiref(state_, LUA_MATHLIBNAME, luaopen_math, 1);
        lua_pop(state_, 1);
        luaL_requiref(state_, LUA_UTF8LIBNAME, luaopen_utf8, 1);
        lua_pop(state_, 1);

        for (const char *name :
             {"collectgarbage", "dofile", "load", "loadfile", "getmetatable", "setmetatable", "rawget", "rawset"})
        {
            lua_pushnil(state_);
            lua_setglobal(state_, name);
        }

        lua_newtable(state_);
        lua_pushcfunction(state_, script_descriptor);
        lua_setfield(state_, -2, "script");
        lua_setglobal(state_, "eps");
    }

    void load(std::string source)
    {
        begin_execution();
        const int load_result = luaL_loadbufferx(state_, source.data(), source.size(), "ExternalPeepSight", "t");
        if (load_result != LUA_OK)
        {
            end_execution();
            throw std::invalid_argument(bounded_error(state_));
        }
        const int call_result = lua_pcall(state_, 0, 1, 0);
        end_execution();
        if (call_result != LUA_OK)
        {
            throw std::invalid_argument(bounded_error(state_));
        }
        if (lua_istable(state_, -1) == 0)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script must return eps.script { ... }.");
        }

        validate_descriptor_keys();
        parse_api_version();
        parse_bindings();
        parse_settings();
        parse_ui();
        on_start_reference_ = callback_reference("on_start");
        on_input_reference_ = callback_reference("on_input");
        lua_pop(state_, 1);
    }

    void validate_descriptor_keys()
    {
        const std::unordered_set<std::string> allowed{
            "api_version", "bindings", "settings", "ui", "on_start", "on_input",
        };
        lua_pushnil(state_);
        while (lua_next(state_, -2) != 0)
        {
            if (lua_type(state_, -2) != LUA_TSTRING || !allowed.contains(lua_string(state_, -2)))
            {
                lua_pop(state_, 2);
                throw std::invalid_argument("Script descriptor contains an unknown property.");
            }
            lua_pop(state_, 1);
        }
    }

    void parse_api_version()
    {
        lua_getfield(state_, -1, "api_version");
        if (lua_isnil(state_, -1) != 0)
        {
            lua_pop(state_, 1);
            declarations_.api_version = "1";
            return;
        }
        if (lua_type(state_, -1) != LUA_TSTRING)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script API version must be a string.");
        }
        declarations_.api_version = lua_string(state_, -1);
        lua_pop(state_, 1);
        if (declarations_.api_version != "1" && declarations_.api_version != "2")
        {
            throw std::invalid_argument("Script API version is not supported.");
        }
    }

    void parse_bindings()
    {
        lua_getfield(state_, -1, "bindings");
        if (lua_isnil(state_, -1) != 0)
        {
            lua_pop(state_, 1);
            return;
        }
        if (lua_istable(state_, -1) == 0)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script bindings must be a table.");
        }

        std::unordered_set<std::string> identifiers;
        lua_pushnil(state_);
        while (lua_next(state_, -2) != 0)
        {
            if (declarations_.bindings.size() == kMaximumItems || lua_type(state_, -2) != LUA_TSTRING ||
                lua_istable(state_, -1) == 0)
            {
                lua_pop(state_, 3);
                throw std::invalid_argument("Script binding declarations are invalid.");
            }

            ScriptBindingDeclaration declaration{};
            declaration.id = lua_string(state_, -2);
            validate_identifier(declaration.id);
            if (!identifiers.insert(declaration.id).second)
            {
                lua_pop(state_, 3);
                throw std::invalid_argument("Script binding identifier is duplicated.");
            }

            lua_getfield(state_, -1, "title");
            declaration.display_name = lua_string(state_, -1);
            lua_pop(state_, 1);
            validate_display_name(declaration.display_name);
            declaration.default_enabled = optional_boolean(state_, -1, "enabled", true);
            declaration.default_key = parse_script_default_key(state_, -1);

            lua_getfield(state_, -1, "events");
            if (lua_istable(state_, -1) == 0)
            {
                lua_pop(state_, 4);
                throw std::invalid_argument("Script binding events must be a table.");
            }
            const lua_Integer event_count = luaL_len(state_, -1);
            if (event_count < 1 || event_count > 2)
            {
                lua_pop(state_, 4);
                throw std::invalid_argument("Script binding events are invalid.");
            }
            for (lua_Integer index = 1; index <= event_count; ++index)
            {
                lua_geti(state_, -1, index);
                const std::string event = lua_string(state_, -1);
                lua_pop(state_, 1);
                if (event == "pressed" && !declaration.pressed)
                {
                    declaration.pressed = true;
                }
                else if (event == "released" && !declaration.released)
                {
                    declaration.released = true;
                }
                else
                {
                    lua_pop(state_, 4);
                    throw std::invalid_argument("Script binding event is invalid or duplicated.");
                }
            }
            lua_pop(state_, 1);
            declarations_.bindings.push_back(std::move(declaration));
            lua_pop(state_, 1);
        }
        lua_pop(state_, 1);
    }

    void parse_settings()
    {
        lua_getfield(state_, -1, "settings");
        if (lua_isnil(state_, -1) != 0)
        {
            lua_pop(state_, 1);
            return;
        }
        if (lua_istable(state_, -1) == 0)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script settings must be a table.");
        }

        std::unordered_set<std::string> identifiers;
        lua_pushnil(state_);
        while (lua_next(state_, -2) != 0)
        {
            if (declarations_.settings.size() == kMaximumItems || lua_type(state_, -2) != LUA_TSTRING ||
                lua_istable(state_, -1) == 0)
            {
                lua_pop(state_, 3);
                throw std::invalid_argument("Script setting declarations are invalid.");
            }

            ScriptSettingDeclaration declaration{};
            declaration.id = lua_string(state_, -2);
            validate_identifier(declaration.id);
            if (!identifiers.insert(declaration.id).second)
            {
                lua_pop(state_, 3);
                throw std::invalid_argument("Script setting identifier is duplicated.");
            }

            lua_getfield(state_, -1, "title");
            declaration.display_name = lua_string(state_, -1);
            lua_pop(state_, 1);
            validate_display_name(declaration.display_name);
            lua_getfield(state_, -1, "type");
            declaration.type = parse_setting_type(lua_string(state_, -1));
            lua_pop(state_, 1);
            declaration.minimum = optional_number(state_, -1, "minimum");
            declaration.maximum = optional_number(state_, -1, "maximum");
            if (declaration.minimum && declaration.maximum && *declaration.minimum > *declaration.maximum)
            {
                lua_pop(state_, 3);
                throw std::invalid_argument("Script setting minimum exceeds its maximum.");
            }

            if (declaration.type == ScriptSettingType::choice)
            {
                lua_getfield(state_, -1, "options");
                if (lua_istable(state_, -1) == 0)
                {
                    lua_pop(state_, 4);
                    throw std::invalid_argument("Enum script setting options must be a table.");
                }
                const lua_Integer option_count = luaL_len(state_, -1);
                if (option_count < 1 || option_count > static_cast<lua_Integer>(kMaximumItems))
                {
                    lua_pop(state_, 4);
                    throw std::invalid_argument("Enum script setting options are invalid.");
                }
                for (lua_Integer index = 1; index <= option_count; ++index)
                {
                    lua_geti(state_, -1, index);
                    declaration.options.push_back(lua_string(state_, -1));
                    lua_pop(state_, 1);
                }
                if (std::ranges::any_of(declaration.options, [](const std::string &value) { return value.empty(); }) ||
                    std::unordered_set<std::string>(declaration.options.begin(), declaration.options.end()).size() !=
                        declaration.options.size())
                {
                    lua_pop(state_, 4);
                    throw std::invalid_argument("Enum script setting options are invalid.");
                }
                lua_pop(state_, 1);
            }

            lua_getfield(state_, -1, "default");
            if (lua_isnil(state_, -1) != 0)
            {
                lua_pop(state_, 4);
                throw std::invalid_argument("Script setting default is required.");
            }
            declaration.default_value = canonical_setting_value(state_, -1, declaration.type);
            lua_pop(state_, 1);
            validate_setting_range(declaration, declaration.default_value);
            if (declaration.type == ScriptSettingType::choice &&
                std::ranges::find(declaration.options, declaration.default_value) == declaration.options.end())
            {
                lua_pop(state_, 3);
                throw std::invalid_argument("Enum script setting default is not one of its options.");
            }

            declarations_.settings.push_back(std::move(declaration));
            lua_pop(state_, 1);
        }
        lua_pop(state_, 1);
        merge_setting_values();
    }

    [[nodiscard]] const ScriptSettingDeclaration &find_setting(const std::string_view identifier) const
    {
        const auto setting =
            std::ranges::find_if(declarations_.settings, [identifier](const ScriptSettingDeclaration &candidate)
                                 { return candidate.id == identifier; });
        if (setting == declarations_.settings.end())
        {
            throw std::invalid_argument("Script UI references an unknown setting.");
        }
        return *setting;
    }

    static void validate_ui_control(const ScriptUiItemDeclaration &item, const ScriptSettingDeclaration &setting)
    {
        const bool numeric = setting.type == ScriptSettingType::integer || setting.type == ScriptSettingType::number;
        switch (item.control)
        {
        case ScriptUiControlType::automatic:
            break;
        case ScriptUiControlType::switch_control:
        case ScriptUiControlType::checkbox:
            if (setting.type != ScriptSettingType::boolean)
            {
                throw std::invalid_argument("Script UI Boolean control requires a Boolean setting.");
            }
            break;
        case ScriptUiControlType::slider:
            if (!numeric || !setting.minimum || !setting.maximum)
            {
                throw std::invalid_argument("Script UI slider requires a bounded numeric setting.");
            }
            break;
        case ScriptUiControlType::number:
            if (!numeric)
            {
                throw std::invalid_argument("Script UI number control requires a numeric setting.");
            }
            break;
        case ScriptUiControlType::textbox:
            if (setting.type != ScriptSettingType::text)
            {
                throw std::invalid_argument("Script UI textbox requires a String setting.");
            }
            break;
        case ScriptUiControlType::select:
            if (setting.type != ScriptSettingType::choice)
            {
                throw std::invalid_argument("Script UI select control requires an Enum setting.");
            }
            break;
        case ScriptUiControlType::segmented:
            if (setting.type != ScriptSettingType::choice || setting.options.size() > 8U)
            {
                throw std::invalid_argument(
                    "Script UI segmented control requires an Enum setting with at most 8 options.");
            }
            break;
        }

        if (item.step &&
            (!numeric || (item.control != ScriptUiControlType::automatic &&
                          item.control != ScriptUiControlType::slider && item.control != ScriptUiControlType::number)))
        {
            throw std::invalid_argument("Script UI step is only valid for numeric controls.");
        }
    }

    [[nodiscard]] std::optional<ScriptUiVisibilityCondition> parse_ui_condition(const int item_index,
                                                                                const std::string_view item_setting_id)
    {
        lua_getfield(state_, item_index, "visible_when");
        if (lua_isnil(state_, -1) != 0)
        {
            lua_pop(state_, 1);
            return std::nullopt;
        }
        if (lua_istable(state_, -1) == 0)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script UI visibility condition must be a table.");
        }
        validate_table_keys(state_, -1, {"setting", "equals"}, "Script UI visibility condition is invalid.");

        lua_getfield(state_, -1, "setting");
        const std::string setting_id = lua_string(state_, -1);
        lua_pop(state_, 1);
        validate_identifier(setting_id);
        if (setting_id == item_setting_id)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script UI item cannot control its own visibility.");
        }
        const ScriptSettingDeclaration &setting = find_setting(setting_id);

        lua_getfield(state_, -1, "equals");
        if (lua_isnil(state_, -1) != 0)
        {
            lua_pop(state_, 2);
            throw std::invalid_argument("Script UI visibility condition requires an equals value.");
        }
        std::string equals_value = canonical_setting_value(state_, -1, setting.type);
        lua_pop(state_, 1);
        validate_setting_range(setting, equals_value);
        if (setting.type == ScriptSettingType::choice &&
            std::ranges::find(setting.options, equals_value) == setting.options.end())
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script UI visibility condition Enum value is invalid.");
        }
        lua_pop(state_, 1);
        return ScriptUiVisibilityCondition{setting_id, std::move(equals_value)};
    }

    [[nodiscard]] ScriptUiItemDeclaration parse_ui_item(const int item_index,
                                                        std::unordered_set<std::string> &used_settings)
    {
        validate_table_keys(state_, item_index, {"setting", "control", "description", "unit", "step", "visible_when"},
                            "Script UI item contains an unknown property.");

        lua_getfield(state_, item_index, "setting");
        ScriptUiItemDeclaration item{};
        item.setting_id = lua_string(state_, -1);
        lua_pop(state_, 1);
        validate_identifier(item.setting_id);
        if (!used_settings.insert(item.setting_id).second)
        {
            throw std::invalid_argument("Script UI setting is referenced more than once.");
        }
        const ScriptSettingDeclaration &setting = find_setting(item.setting_id);

        lua_getfield(state_, item_index, "control");
        if (lua_isnil(state_, -1) != 0)
        {
            item.control = ScriptUiControlType::automatic;
        }
        else
        {
            item.control = parse_ui_control(lua_string(state_, -1));
        }
        lua_pop(state_, 1);

        item.description = optional_string(state_, item_index, "description", kMaximumDescriptionLength);
        item.unit = optional_string(state_, item_index, "unit", kMaximumUnitLength);
        item.step = optional_number(state_, item_index, "step");
        if (item.step && *item.step <= 0.0)
        {
            throw std::invalid_argument("Script UI step must be positive.");
        }
        item.visible_when = parse_ui_condition(item_index, item.setting_id);
        validate_ui_control(item, setting);
        return item;
    }

    [[nodiscard]] ScriptUiSectionDeclaration parse_ui_section(const int section_index,
                                                              std::unordered_set<std::string> &section_ids,
                                                              std::unordered_set<std::string> &used_settings,
                                                              std::size_t &item_count)
    {
        validate_table_keys(state_, section_index,
                            {"id", "title", "description", "collapsible", "default_expanded", "columns", "items"},
                            "Script UI section contains an unknown property.");

        ScriptUiSectionDeclaration section{};
        lua_getfield(state_, section_index, "id");
        section.id = lua_string(state_, -1);
        lua_pop(state_, 1);
        validate_identifier(section.id);
        if (!section_ids.insert(section.id).second)
        {
            throw std::invalid_argument("Script UI section identifier is duplicated.");
        }

        lua_getfield(state_, section_index, "title");
        section.display_name = lua_string(state_, -1);
        lua_pop(state_, 1);
        validate_display_name(section.display_name);
        section.description = optional_string(state_, section_index, "description", kMaximumDescriptionLength);
        section.collapsible = optional_boolean(state_, section_index, "collapsible", false);
        section.default_expanded = optional_boolean(state_, section_index, "default_expanded", true);
        const lua_Integer columns = optional_integer(state_, section_index, "columns", 1);
        if (columns < 1 || columns > 2)
        {
            throw std::invalid_argument("Script UI section columns must be one or two.");
        }
        section.columns = static_cast<std::uint8_t>(columns);

        lua_getfield(state_, section_index, "items");
        if (lua_istable(state_, -1) == 0)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script UI section items must be a table.");
        }
        const lua_Integer count = luaL_len(state_, -1);
        if (count < 1 || item_count + static_cast<std::size_t>(count) > kMaximumItems)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script UI section item count is invalid.");
        }
        validate_sequence(state_, -1, count, "Script UI section items must be a sequence.");
        const int items_index = lua_absindex(state_, -1);
        for (lua_Integer index = 1; index <= count; ++index)
        {
            lua_geti(state_, items_index, index);
            if (lua_istable(state_, -1) == 0)
            {
                lua_pop(state_, 2);
                throw std::invalid_argument("Script UI item must be a table.");
            }
            section.items.push_back(parse_ui_item(lua_absindex(state_, -1), used_settings));
            lua_pop(state_, 1);
        }
        item_count += static_cast<std::size_t>(count);
        lua_pop(state_, 1);
        return section;
    }

    void parse_ui()
    {
        lua_getfield(state_, -1, "ui");
        if (lua_isnil(state_, -1) != 0)
        {
            lua_pop(state_, 1);
            return;
        }
        if (declarations_.api_version != "2")
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script UI declarations require API version 2.");
        }
        if (lua_istable(state_, -1) == 0)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script UI declaration must be a table.");
        }
        validate_table_keys(state_, -1, {"sections"}, "Script UI declaration contains an unknown property.");

        lua_getfield(state_, -1, "sections");
        if (lua_istable(state_, -1) == 0)
        {
            lua_pop(state_, 2);
            throw std::invalid_argument("Script UI sections must be a table.");
        }
        const lua_Integer count = luaL_len(state_, -1);
        if (count < 0 || count > static_cast<lua_Integer>(kMaximumUiSections))
        {
            lua_pop(state_, 2);
            throw std::invalid_argument("Script UI section count exceeds the configured limit.");
        }
        validate_sequence(state_, -1, count, "Script UI sections must be a sequence.");

        ScriptUiDeclaration ui;
        std::unordered_set<std::string> section_ids;
        std::unordered_set<std::string> used_settings;
        std::size_t item_count = 0U;
        const int sections_index = lua_absindex(state_, -1);
        for (lua_Integer index = 1; index <= count; ++index)
        {
            lua_geti(state_, sections_index, index);
            if (lua_istable(state_, -1) == 0)
            {
                lua_pop(state_, 3);
                throw std::invalid_argument("Script UI section must be a table.");
            }
            ui.sections.push_back(parse_ui_section(lua_absindex(state_, -1), section_ids, used_settings, item_count));
            lua_pop(state_, 1);
        }
        if (used_settings.size() != declarations_.settings.size())
        {
            lua_pop(state_, 2);
            throw std::invalid_argument("Script UI must reference every declared setting exactly once.");
        }

        declarations_.ui = std::move(ui);
        lua_pop(state_, 2);
    }

    static void validate_setting_range(const ScriptSettingDeclaration &declaration, const std::string &value)
    {
        if (declaration.type != ScriptSettingType::integer && declaration.type != ScriptSettingType::number)
        {
            return;
        }
        char *end = nullptr;
        const double number = std::strtod(value.c_str(), &end);
        if (end == value.c_str() || *end != '\0' || !std::isfinite(number) ||
            (declaration.minimum && number < *declaration.minimum) ||
            (declaration.maximum && number > *declaration.maximum))
        {
            throw std::invalid_argument("Script setting value is outside its declared range.");
        }
    }

    void merge_setting_values()
    {
        std::unordered_map<std::string, ScriptSettingValue> persisted;
        for (ScriptSettingValue &value : setting_values_)
        {
            validate_identifier(value.id);
            if (!persisted.emplace(value.id, std::move(value)).second)
            {
                throw std::invalid_argument("Persisted script setting identifier is duplicated.");
            }
        }
        setting_values_.clear();

        for (const ScriptSettingDeclaration &declaration : declarations_.settings)
        {
            const auto existing = persisted.find(declaration.id);
            if (existing == persisted.end())
            {
                setting_values_.push_back({declaration.id, declaration.type, declaration.default_value});
                continue;
            }
            if (existing->second.type != declaration.type)
            {
                throw std::invalid_argument("Persisted script setting type no longer matches its declaration.");
            }
            validate_setting_range(declaration, existing->second.value);
            if (declaration.type == ScriptSettingType::choice &&
                std::ranges::find(declaration.options, existing->second.value) == declaration.options.end())
            {
                throw std::invalid_argument("Persisted enum script setting is not one of its options.");
            }
            push_setting_value(state_, existing->second);
            lua_pop(state_, 1);
            setting_values_.push_back(std::move(existing->second));
        }
    }

    [[nodiscard]] int callback_reference(const char *name)
    {
        lua_getfield(state_, -1, name);
        if (lua_isnil(state_, -1) != 0)
        {
            lua_pop(state_, 1);
            return LUA_NOREF;
        }
        if (lua_isfunction(state_, -1) == 0)
        {
            lua_pop(state_, 1);
            throw std::invalid_argument("Script callback must be a function.");
        }
        return luaL_ref(state_, LUA_REGISTRYINDEX);
    }

    void begin_execution()
    {
        executed_instructions_ = 0U;
        execution_deadline_ = std::chrono::steady_clock::now() + kExecutionLimit;
        lua_sethook(state_, instruction_hook, LUA_MASKCOUNT, kInstructionHookInterval);
    }

    void end_execution()
    {
        lua_sethook(state_, nullptr, 0, 0);
    }

    void push_context(const ScriptContext &context)
    {
        lua_newtable(state_);

        lua_newtable(state_);
        lua_pushcfunction(state_, visibility_set);
        lua_setfield(state_, -2, "set");
        lua_pushcfunction(state_, visibility_toggle);
        lua_setfield(state_, -2, "toggle");
        lua_setfield(state_, -2, "visibility");

        lua_newtable(state_);
        lua_pushlstring(state_, context.profile_id.data(), context.profile_id.size());
        lua_setfield(state_, -2, "id");
        lua_pushcfunction(state_, profile_switch);
        lua_setfield(state_, -2, "switch");
        lua_pushcfunction(state_, profile_previous);
        lua_setfield(state_, -2, "previous");
        lua_pushcfunction(state_, profile_next);
        lua_setfield(state_, -2, "next");
        lua_setfield(state_, -2, "profile");

        lua_newtable(state_);
        lua_pushlstring(state_, context.profile_set_id.data(), context.profile_set_id.size());
        lua_setfield(state_, -2, "id");
        lua_pushcfunction(state_, profile_set_switch);
        lua_setfield(state_, -2, "switch");
        lua_pushcfunction(state_, profile_set_previous);
        lua_setfield(state_, -2, "previous");
        lua_pushcfunction(state_, profile_set_next);
        lua_setfield(state_, -2, "next");
        lua_setfield(state_, -2, "profile_set");

        lua_newtable(state_);
        for (const ScriptSettingValue &setting : setting_values_)
        {
            push_setting_value(state_, setting);
            lua_setfield(state_, -2, setting.id.c_str());
        }
        lua_setfield(state_, -2, "settings");
    }

    [[nodiscard]] ScriptExecutionResult invoke(const int callback_reference,
                                               const std::optional<std::pair<std::string_view, ScriptInputPhase>> event,
                                               const ScriptContext &context)
    {
        if (callback_reference == LUA_NOREF)
        {
            return {true, {}, allows_visible_, {}};
        }

        pending_visibility_.reset();
        pending_commands_.clear();
        lua_rawgeti(state_, LUA_REGISTRYINDEX, callback_reference);
        int argument_count = 1;
        if (event)
        {
            lua_newtable(state_);
            lua_pushlstring(state_, event->first.data(), event->first.size());
            lua_setfield(state_, -2, "id");
            lua_pushstring(state_, event->second == ScriptInputPhase::pressed ? "pressed" : "released");
            lua_setfield(state_, -2, "phase");
            argument_count = 2;
        }
        push_context(context);

        begin_execution();
        const int result = lua_pcall(state_, argument_count, 0, 0);
        end_execution();
        if (result != LUA_OK)
        {
            pending_visibility_.reset();
            pending_commands_.clear();
            return {false, bounded_error(state_), allows_visible_, {}};
        }

        if (pending_visibility_)
        {
            allows_visible_ = *pending_visibility_;
        }
        return {true, {}, allows_visible_, std::exchange(pending_commands_, {})};
    }

    lua_State *state_ = nullptr;
    MemoryState memory_;
    ScriptScope scope_;
    ScriptDeclarations declarations_;
    std::vector<ScriptSettingValue> setting_values_;
    std::optional<bool> pending_visibility_;
    std::vector<ScriptCommand> pending_commands_;
    std::chrono::steady_clock::time_point execution_deadline_;
    std::uint64_t executed_instructions_ = 0U;
    int on_start_reference_ = LUA_NOREF;
    int on_input_reference_ = LUA_NOREF;
    bool allows_visible_ = true;
};

LuaScript::LuaScript(std::string source, const ScriptScope scope, std::vector<ScriptSettingValue> setting_values)
    : impl_(std::make_unique<Impl>(std::move(source), scope, std::move(setting_values)))
{
}

LuaScript::LuaScript(LuaScript &&) noexcept = default;

LuaScript &LuaScript::operator=(LuaScript &&) noexcept = default;

LuaScript::~LuaScript() = default;

const ScriptDeclarations &LuaScript::declarations() const noexcept
{
    return impl_->declarations();
}

bool LuaScript::allows_visible() const noexcept
{
    return impl_->allows_visible();
}

ScriptExecutionResult LuaScript::start(const ScriptContext &context)
{
    return impl_->start(context);
}

ScriptExecutionResult LuaScript::input(const std::string_view binding_id, const ScriptInputPhase phase,
                                       const ScriptContext &context)
{
    return impl_->input(binding_id, phase, context);
}
} // namespace external_peepsight

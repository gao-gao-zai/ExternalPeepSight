#include "script_coordinator.h"

#include "diagnostics.h"

#include <bcrypt.h>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <deque>
#include <exception>
#include <future>
#include <limits>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <type_traits>
#include <unordered_map>
#include <utility>

namespace external_peepsight
{
namespace
{
using winrt::Windows::Data::Json::IJsonValue;
using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

struct ParsedScript
{
    ScriptScope scope;
    std::string owner_id;
    std::string api_version;
    std::string source;
    std::vector<ScriptSettingValue> settings;
    std::vector<ScriptInputBinding> bindings;
    std::vector<ScriptBindingDeclaration> expected_bindings;
    std::vector<ScriptSettingDeclaration> expected_settings;
    std::optional<ScriptUiDeclaration> expected_ui;
};

struct RuntimeScript
{
    ScriptScope scope;
    std::string owner_id;
    LuaScript script;
};

struct RuntimeCandidate
{
    std::uint64_t token = 0U;
    std::string profile_set_id;
    std::string profile_id;
    bool profile_uses_lua = false;
    std::optional<RuntimeScript> global;
    std::optional<RuntimeScript> profile_set;
    std::optional<RuntimeScript> profile;
    std::vector<ScriptInputBinding> input_bindings;
};

[[nodiscard]] InputModifiers parse_modifiers(const std::wstring_view value)
{
    if (value.empty() || value == L"none")
    {
        return InputModifiers::none;
    }

    InputModifiers result = InputModifiers::none;
    std::size_t offset = 0U;
    while (offset < value.size())
    {
        const std::size_t separator = value.find(L',', offset);
        const std::size_t end = separator == std::wstring_view::npos ? value.size() : separator;
        std::wstring_view token = value.substr(offset, end - offset);
        while (!token.empty() && token.front() == L' ')
        {
            token.remove_prefix(1U);
        }
        while (!token.empty() && token.back() == L' ')
        {
            token.remove_suffix(1U);
        }

        InputModifiers modifier = InputModifiers::none;
        if (token == L"ctrl")
        {
            modifier = InputModifiers::ctrl;
        }
        else if (token == L"alt")
        {
            modifier = InputModifiers::alt;
        }
        else if (token == L"shift")
        {
            modifier = InputModifiers::shift;
        }
        else if (token == L"win")
        {
            modifier = InputModifiers::win;
        }
        else
        {
            throw std::invalid_argument("Script binding modifiers are invalid.");
        }

        if ((result & modifier) != InputModifiers::none)
        {
            throw std::invalid_argument("Script binding modifiers contain a duplicate value.");
        }
        result = result | modifier;
        offset = separator == std::wstring_view::npos ? value.size() : separator + 1U;
    }
    return result;
}

[[nodiscard]] std::wstring script_modifiers_name(const ScriptKeyModifiers modifiers)
{
    std::wstring result;
    const auto append = [&result](const std::wstring_view name)
    {
        if (!result.empty())
        {
            result += L", ";
        }
        result += name;
    };
    const std::uint8_t value = static_cast<std::uint8_t>(modifiers);
    if ((value & static_cast<std::uint8_t>(ScriptKeyModifiers::ctrl)) != 0U)
    {
        append(L"ctrl");
    }
    if ((value & static_cast<std::uint8_t>(ScriptKeyModifiers::alt)) != 0U)
    {
        append(L"alt");
    }
    if ((value & static_cast<std::uint8_t>(ScriptKeyModifiers::shift)) != 0U)
    {
        append(L"shift");
    }
    if ((value & static_cast<std::uint8_t>(ScriptKeyModifiers::win)) != 0U)
    {
        append(L"win");
    }
    return result.empty() ? L"none" : result;
}

[[nodiscard]] JsonObject script_default_key_json(const ScriptBindingDefaultKey &key)
{
    JsonObject result;
    result.SetNamedValue(L"device", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                        key.device == ScriptBindingDeviceKind::keyboard ? L"keyboard" : L"mouse"));
    result.SetNamedValue(L"code",
                         winrt::Windows::Data::Json::JsonValue::CreateNumberValue(static_cast<double>(key.code)));
    result.SetNamedValue(L"extended", winrt::Windows::Data::Json::JsonValue::CreateBooleanValue(key.extended));
    result.SetNamedValue(
        L"modifiers", winrt::Windows::Data::Json::JsonValue::CreateStringValue(script_modifiers_name(key.modifiers)));
    return result;
}

[[nodiscard]] InputKeyIdentity parse_key(const JsonObject &object)
{
    const std::wstring device_name = object.GetNamedString(L"device").c_str();
    InputDeviceKind device{};
    if (device_name == L"keyboard")
    {
        device = InputDeviceKind::keyboard;
    }
    else if (device_name == L"mouse")
    {
        device = InputDeviceKind::mouse;
    }
    else
    {
        throw std::invalid_argument("Script binding device is invalid.");
    }

    const double code = object.GetNamedNumber(L"code");
    if (code < 1.0 || code > static_cast<double>((std::numeric_limits<std::uint16_t>::max)()) ||
        std::floor(code) != code)
    {
        throw std::invalid_argument("Script binding code is invalid.");
    }
    return {
        device,
        static_cast<std::uint16_t>(code),
        object.GetNamedBoolean(L"extended"),
        parse_modifiers(object.GetNamedString(L"modifiers")),
    };
}

[[nodiscard]] ScriptSettingType parse_setting_type(const std::wstring_view value)
{
    if (value == L"boolean")
    {
        return ScriptSettingType::boolean;
    }
    if (value == L"integer")
    {
        return ScriptSettingType::integer;
    }
    if (value == L"double")
    {
        return ScriptSettingType::number;
    }
    if (value == L"string")
    {
        return ScriptSettingType::text;
    }
    if (value == L"enum")
    {
        return ScriptSettingType::choice;
    }
    throw std::invalid_argument("Script setting type is invalid.");
}

[[nodiscard]] ScriptUiControlType parse_ui_control(const std::wstring_view value)
{
    if (value == L"auto")
    {
        return ScriptUiControlType::automatic;
    }
    if (value == L"switch")
    {
        return ScriptUiControlType::switch_control;
    }
    if (value == L"checkbox")
    {
        return ScriptUiControlType::checkbox;
    }
    if (value == L"slider")
    {
        return ScriptUiControlType::slider;
    }
    if (value == L"number")
    {
        return ScriptUiControlType::number;
    }
    if (value == L"textbox")
    {
        return ScriptUiControlType::textbox;
    }
    if (value == L"select")
    {
        return ScriptUiControlType::select;
    }
    if (value == L"segmented")
    {
        return ScriptUiControlType::segmented;
    }
    throw std::invalid_argument("Script UI control type is invalid.");
}

[[nodiscard]] std::wstring ui_control_name(const ScriptUiControlType value)
{
    switch (value)
    {
    case ScriptUiControlType::automatic:
        return L"auto";
    case ScriptUiControlType::switch_control:
        return L"switch";
    case ScriptUiControlType::checkbox:
        return L"checkbox";
    case ScriptUiControlType::slider:
        return L"slider";
    case ScriptUiControlType::number:
        return L"number";
    case ScriptUiControlType::textbox:
        return L"textbox";
    case ScriptUiControlType::select:
        return L"select";
    case ScriptUiControlType::segmented:
        return L"segmented";
    }
    throw std::invalid_argument("Script UI control type is invalid.");
}

[[nodiscard]] std::string sha256_hex(const std::string_view source)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD object_size = 0U;
    DWORD result_size = 0U;
    std::vector<std::uint8_t> object;
    std::array<std::uint8_t, 32> digest{};
    const auto cleanup = [&]() noexcept
    {
        if (hash != nullptr)
        {
            BCryptDestroyHash(hash);
        }
        if (algorithm != nullptr)
        {
            BCryptCloseAlgorithmProvider(algorithm, 0U);
        }
    };

    NTSTATUS status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0U);
    if (status >= 0)
    {
        status = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size),
                                   sizeof(object_size), &result_size, 0U);
    }
    if (status >= 0)
    {
        object.resize(object_size);
        status = BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0U, 0U);
    }
    if (status >= 0)
    {
        status = BCryptHashData(hash, reinterpret_cast<PUCHAR>(const_cast<char *>(source.data())),
                                static_cast<ULONG>(source.size()), 0U);
    }
    if (status >= 0)
    {
        status = BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0U);
    }
    cleanup();
    if (status < 0)
    {
        throw std::runtime_error("Script source SHA-256 validation failed.");
    }

    constexpr char digits[] = "0123456789ABCDEF";
    std::string result(digest.size() * 2U, '0');
    for (std::size_t index = 0U; index < digest.size(); ++index)
    {
        result[index * 2U] = digits[digest[index] >> 4U];
        result[index * 2U + 1U] = digits[digest[index] & 0x0FU];
    }
    return result;
}

[[nodiscard]] std::optional<double> optional_json_number(const JsonObject &object, const std::wstring_view name)
{
    const IJsonValue value = object.GetNamedValue(name);
    if (value.ValueType() == JsonValueType::Null)
    {
        return std::nullopt;
    }
    if (value.ValueType() != JsonValueType::Number)
    {
        throw std::invalid_argument("Script UI numeric property is invalid.");
    }
    const double number = value.GetNumber();
    if (!std::isfinite(number))
    {
        throw std::invalid_argument("Script UI numeric property must be finite.");
    }
    return number;
}

[[nodiscard]] std::optional<ScriptUiVisibilityCondition> parse_ui_condition_json(const IJsonValue &value)
{
    if (value.ValueType() == JsonValueType::Null)
    {
        return std::nullopt;
    }
    if (value.ValueType() != JsonValueType::Object)
    {
        throw std::invalid_argument("Script UI visibility condition is invalid.");
    }
    const JsonObject object = value.GetObject();
    return ScriptUiVisibilityCondition{
        winrt::to_string(object.GetNamedString(L"settingId")),
        winrt::to_string(object.GetNamedString(L"equalsValue")),
    };
}

[[nodiscard]] std::optional<ScriptUiDeclaration> parse_ui_json(const IJsonValue &value)
{
    if (value.ValueType() == JsonValueType::Null)
    {
        return std::nullopt;
    }
    if (value.ValueType() != JsonValueType::Object)
    {
        throw std::invalid_argument("Script UI configuration must be an object or null.");
    }

    ScriptUiDeclaration result;
    for (const IJsonValue &section_value : value.GetObject().GetNamedArray(L"sections"))
    {
        const JsonObject section_object = section_value.GetObject();
        const double columns = section_object.GetNamedNumber(L"columns");
        if (columns < 1.0 || columns > 2.0 || std::floor(columns) != columns)
        {
            throw std::invalid_argument("Script UI section columns are invalid.");
        }
        ScriptUiSectionDeclaration section{
            winrt::to_string(section_object.GetNamedString(L"id")),
            winrt::to_string(section_object.GetNamedString(L"displayName")),
            winrt::to_string(section_object.GetNamedString(L"description")),
            section_object.GetNamedBoolean(L"collapsible"),
            section_object.GetNamedBoolean(L"defaultExpanded"),
            static_cast<std::uint8_t>(columns),
        };
        for (const IJsonValue &item_value : section_object.GetNamedArray(L"items"))
        {
            const JsonObject item_object = item_value.GetObject();
            section.items.push_back({
                winrt::to_string(item_object.GetNamedString(L"settingId")),
                parse_ui_control(item_object.GetNamedString(L"control")),
                winrt::to_string(item_object.GetNamedString(L"description")),
                winrt::to_string(item_object.GetNamedString(L"unit")),
                optional_json_number(item_object, L"step"),
                parse_ui_condition_json(item_object.GetNamedValue(L"visibleWhen")),
            });
        }
        result.sections.push_back(std::move(section));
    }
    return result;
}

[[nodiscard]] ParsedScript parse_script(const JsonObject &object, const ScriptScope scope, const std::string &owner_id)
{
    if (!object.GetNamedBoolean(L"enabled"))
    {
        throw std::logic_error("Disabled scripts must be filtered before parsing.");
    }
    const std::string api_version = winrt::to_string(object.GetNamedString(L"apiVersion"));
    if (api_version != "1" && api_version != "2")
    {
        throw std::invalid_argument("Script API version is not supported.");
    }

    ParsedScript result{
        scope,
        owner_id,
        api_version,
        winrt::to_string(object.GetNamedString(L"source")),
    };
    const std::string expected_hash = winrt::to_string(object.GetNamedString(L"sourceHash"));
    if (expected_hash.size() != 64U ||
        !std::ranges::equal(sha256_hex(result.source), expected_hash, [](const char left, const char right)
                            { return left == static_cast<char>(std::toupper(static_cast<unsigned char>(right))); }))
    {
        throw std::invalid_argument("Script source hash does not match its source.");
    }

    for (const IJsonValue &item : object.GetNamedArray(L"settings"))
    {
        const JsonObject setting = item.GetObject();
        ScriptSettingDeclaration declaration{
            winrt::to_string(setting.GetNamedString(L"id")),
            winrt::to_string(setting.GetNamedString(L"displayName")),
            parse_setting_type(setting.GetNamedString(L"type")),
            winrt::to_string(setting.GetNamedString(L"value")),
        };
        for (const IJsonValue &option : setting.GetNamedArray(L"options"))
        {
            declaration.options.push_back(winrt::to_string(option.GetString()));
        }
        const IJsonValue minimum = setting.GetNamedValue(L"minimum");
        const IJsonValue maximum = setting.GetNamedValue(L"maximum");
        if (minimum.ValueType() == JsonValueType::Number)
        {
            declaration.minimum = minimum.GetNumber();
        }
        if (maximum.ValueType() == JsonValueType::Number)
        {
            declaration.maximum = maximum.GetNumber();
        }
        result.settings.push_back({declaration.id, declaration.type, declaration.default_value});
        result.expected_settings.push_back(std::move(declaration));
    }

    for (const IJsonValue &item : object.GetNamedArray(L"bindings"))
    {
        const JsonObject binding = item.GetObject();
        ScriptBindingDeclaration declaration{
            winrt::to_string(binding.GetNamedString(L"id")),
            winrt::to_string(binding.GetNamedString(L"displayName")),
            binding.GetNamedBoolean(L"pressed"),
            binding.GetNamedBoolean(L"released"),
            binding.GetNamedBoolean(L"enabled"),
        };
        result.expected_bindings.push_back(declaration);

        const IJsonValue key = binding.GetNamedValue(L"key");
        if (declaration.default_enabled && key.ValueType() == JsonValueType::Object)
        {
            result.bindings.push_back({
                parse_key(key.GetObject()),
                scope,
                owner_id,
                declaration.id,
                declaration.pressed,
                declaration.released,
            });
        }
    }
    result.expected_ui = parse_ui_json(object.GetNamedValue(L"ui"));
    return result;
}

void validate_declarations(const ParsedScript &parsed, const ScriptDeclarations &actual)
{
    if (parsed.api_version != actual.api_version || parsed.expected_bindings.size() != actual.bindings.size() ||
        parsed.expected_settings.size() != actual.settings.size() || parsed.expected_ui != actual.ui)
    {
        throw std::invalid_argument("Stored script declarations do not match the source.");
    }

    for (const ScriptBindingDeclaration &expected : parsed.expected_bindings)
    {
        const auto actual_item = std::ranges::find_if(actual.bindings, [&expected](const auto &candidate)
                                                      { return candidate.id == expected.id; });
        if (actual_item == actual.bindings.end() || actual_item->display_name != expected.display_name ||
            actual_item->pressed != expected.pressed || actual_item->released != expected.released)
        {
            throw std::invalid_argument("Stored script binding declarations do not match the source.");
        }
    }
    for (const ScriptSettingDeclaration &expected : parsed.expected_settings)
    {
        const auto actual_item = std::ranges::find_if(actual.settings, [&expected](const auto &candidate)
                                                      { return candidate.id == expected.id; });
        if (actual_item == actual.settings.end() || actual_item->display_name != expected.display_name ||
            actual_item->type != expected.type || actual_item->options != expected.options ||
            actual_item->minimum != expected.minimum || actual_item->maximum != expected.maximum)
        {
            throw std::invalid_argument("Stored script setting declarations do not match the source.");
        }
    }
}

[[nodiscard]] std::optional<ParsedScript> optional_script(const JsonObject &owner, const std::wstring_view name,
                                                          const ScriptScope scope, const std::string &owner_id)
{
    const IJsonValue value = owner.GetNamedValue(name);
    if (value.ValueType() == JsonValueType::Null)
    {
        return std::nullopt;
    }
    if (value.ValueType() != JsonValueType::Object)
    {
        throw std::invalid_argument("Script configuration must be an object or null.");
    }
    const JsonObject object = value.GetObject();
    return object.GetNamedBoolean(L"enabled") ? std::optional(parse_script(object, scope, owner_id)) : std::nullopt;
}

[[nodiscard]] ScriptScope parse_scope(const std::wstring_view value)
{
    if (value == L"profile")
    {
        return ScriptScope::profile;
    }
    if (value == L"profileSet")
    {
        return ScriptScope::profile_set;
    }
    if (value == L"global")
    {
        return ScriptScope::global;
    }
    throw std::invalid_argument("Script scope is invalid.");
}

[[nodiscard]] std::wstring setting_type_name(const ScriptSettingType type)
{
    switch (type)
    {
    case ScriptSettingType::boolean:
        return L"boolean";
    case ScriptSettingType::integer:
        return L"integer";
    case ScriptSettingType::number:
        return L"double";
    case ScriptSettingType::text:
        return L"string";
    case ScriptSettingType::choice:
        return L"enum";
    }
    throw std::invalid_argument("Script setting type is invalid.");
}

[[nodiscard]] IJsonValue ui_to_json(const std::optional<ScriptUiDeclaration> &ui)
{
    if (!ui)
    {
        return winrt::Windows::Data::Json::JsonValue::CreateNullValue();
    }

    JsonObject root;
    JsonArray sections;
    for (const ScriptUiSectionDeclaration &section : ui->sections)
    {
        JsonObject section_object;
        section_object.SetNamedValue(
            L"id", winrt::Windows::Data::Json::JsonValue::CreateStringValue(winrt::to_hstring(section.id)));
        section_object.SetNamedValue(L"displayName", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                                         winrt::to_hstring(section.display_name)));
        section_object.SetNamedValue(L"description", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                                         winrt::to_hstring(section.description)));
        section_object.SetNamedValue(L"collapsible",
                                     winrt::Windows::Data::Json::JsonValue::CreateBooleanValue(section.collapsible));
        section_object.SetNamedValue(
            L"defaultExpanded", winrt::Windows::Data::Json::JsonValue::CreateBooleanValue(section.default_expanded));
        section_object.SetNamedValue(L"columns",
                                     winrt::Windows::Data::Json::JsonValue::CreateNumberValue(section.columns));

        JsonArray items;
        for (const ScriptUiItemDeclaration &item : section.items)
        {
            JsonObject item_object;
            item_object.SetNamedValue(L"settingId", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                                        winrt::to_hstring(item.setting_id)));
            item_object.SetNamedValue(
                L"control", winrt::Windows::Data::Json::JsonValue::CreateStringValue(ui_control_name(item.control)));
            item_object.SetNamedValue(L"description", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                                          winrt::to_hstring(item.description)));
            item_object.SetNamedValue(
                L"unit", winrt::Windows::Data::Json::JsonValue::CreateStringValue(winrt::to_hstring(item.unit)));
            item_object.SetNamedValue(L"step",
                                      item.step ? winrt::Windows::Data::Json::JsonValue::CreateNumberValue(*item.step)
                                                : winrt::Windows::Data::Json::JsonValue::CreateNullValue());
            if (item.visible_when)
            {
                JsonObject condition;
                condition.SetNamedValue(L"settingId", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                                          winrt::to_hstring(item.visible_when->setting_id)));
                condition.SetNamedValue(L"equalsValue", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                                            winrt::to_hstring(item.visible_when->equals_value)));
                item_object.SetNamedValue(L"visibleWhen", condition);
            }
            else
            {
                item_object.SetNamedValue(L"visibleWhen", winrt::Windows::Data::Json::JsonValue::CreateNullValue());
            }
            items.Append(item_object);
        }
        section_object.SetNamedValue(L"items", items);
        sections.Append(section_object);
    }
    root.SetNamedValue(L"sections", sections);
    return root;
}

[[nodiscard]] std::string validate_request_json(const std::string_view request_json)
{
    const JsonObject request = JsonObject::Parse(winrt::to_hstring(request_json));
    const ScriptScope scope = parse_scope(request.GetNamedString(L"scope"));
    const std::string source = winrt::to_string(request.GetNamedString(L"source"));
    std::vector<ScriptSettingValue> values;
    const IJsonValue settings_value = request.GetNamedValue(L"settings");
    if (settings_value.ValueType() == JsonValueType::Array)
    {
        for (const IJsonValue &item : settings_value.GetArray())
        {
            const JsonObject setting = item.GetObject();
            values.push_back({
                winrt::to_string(setting.GetNamedString(L"id")),
                parse_setting_type(setting.GetNamedString(L"type")),
                winrt::to_string(setting.GetNamedString(L"value")),
            });
        }
    }

    LuaScript script(source, scope, std::move(values));
    JsonObject response;
    response.SetNamedValue(L"apiVersion", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                              winrt::to_hstring(script.declarations().api_version)));
    JsonArray bindings;
    for (const ScriptBindingDeclaration &binding : script.declarations().bindings)
    {
        JsonObject item;
        item.SetNamedValue(L"id",
                           winrt::Windows::Data::Json::JsonValue::CreateStringValue(winrt::to_hstring(binding.id)));
        item.SetNamedValue(L"displayName", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                               winrt::to_hstring(binding.display_name)));
        item.SetNamedValue(L"pressed", winrt::Windows::Data::Json::JsonValue::CreateBooleanValue(binding.pressed));
        item.SetNamedValue(L"released", winrt::Windows::Data::Json::JsonValue::CreateBooleanValue(binding.released));
        item.SetNamedValue(L"defaultEnabled",
                           winrt::Windows::Data::Json::JsonValue::CreateBooleanValue(binding.default_enabled));
        if (binding.default_key)
        {
            item.SetNamedValue(L"defaultKey", script_default_key_json(*binding.default_key));
        }
        else
        {
            item.SetNamedValue(L"defaultKey", winrt::Windows::Data::Json::JsonValue::CreateNullValue());
        }
        bindings.Append(item);
    }
    JsonArray settings;
    for (const ScriptSettingDeclaration &setting : script.declarations().settings)
    {
        JsonObject item;
        item.SetNamedValue(L"id",
                           winrt::Windows::Data::Json::JsonValue::CreateStringValue(winrt::to_hstring(setting.id)));
        item.SetNamedValue(L"displayName", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                               winrt::to_hstring(setting.display_name)));
        item.SetNamedValue(L"type",
                           winrt::Windows::Data::Json::JsonValue::CreateStringValue(setting_type_name(setting.type)));
        item.SetNamedValue(L"defaultValue", winrt::Windows::Data::Json::JsonValue::CreateStringValue(
                                                winrt::to_hstring(setting.default_value)));
        item.SetNamedValue(L"options", JsonArray{});
        for (const std::string &option : setting.options)
        {
            item.GetNamedArray(L"options")
                .Append(winrt::Windows::Data::Json::JsonValue::CreateStringValue(winrt::to_hstring(option)));
        }
        if (setting.minimum)
        {
            item.SetNamedValue(L"minimum", winrt::Windows::Data::Json::JsonValue::CreateNumberValue(*setting.minimum));
        }
        else
        {
            item.SetNamedValue(L"minimum", winrt::Windows::Data::Json::JsonValue::CreateNullValue());
        }
        if (setting.maximum)
        {
            item.SetNamedValue(L"maximum", winrt::Windows::Data::Json::JsonValue::CreateNumberValue(*setting.maximum));
        }
        else
        {
            item.SetNamedValue(L"maximum", winrt::Windows::Data::Json::JsonValue::CreateNullValue());
        }
        settings.Append(item);
    }
    response.SetNamedValue(L"bindings", bindings);
    response.SetNamedValue(L"settings", settings);
    response.SetNamedValue(L"ui", ui_to_json(script.declarations().ui));
    return winrt::to_string(response.Stringify());
}

[[nodiscard]] RuntimeScript load_script(ParsedScript parsed, const ScriptContext &context)
{
    LuaScript script(parsed.source, parsed.scope, parsed.settings);
    validate_declarations(parsed, script.declarations());
    const ScriptExecutionResult started = script.start(context);
    if (!started.succeeded)
    {
        throw std::invalid_argument("Script on_start failed: " + started.error);
    }
    if (!started.commands.empty())
    {
        throw std::invalid_argument("Script on_start cannot switch configurations.");
    }
    return {parsed.scope, std::move(parsed.owner_id), std::move(script)};
}

[[nodiscard]] RuntimeCandidate parse_candidate(const std::string_view snapshot_json, const std::uint64_t token)
{
    const JsonObject root = JsonObject::Parse(winrt::to_hstring(snapshot_json));
    if (root.GetNamedNumber(L"schemaVersion") != 8.0)
    {
        throw std::invalid_argument("Script configuration schema version is not supported.");
    }

    const std::string active_set_id = winrt::to_string(root.GetNamedString(L"activeProfileSetId"));
    JsonObject active_set;
    bool set_found = false;
    for (const IJsonValue &item : root.GetNamedArray(L"profileSets"))
    {
        const JsonObject candidate = item.GetObject();
        if (winrt::to_string(candidate.GetNamedString(L"id")) == active_set_id)
        {
            active_set = candidate;
            set_found = true;
            break;
        }
    }
    if (!set_found)
    {
        throw std::invalid_argument("Active script profile set does not exist.");
    }

    const IJsonValue selected_profile_id = active_set.GetNamedValue(L"selectedProfileId");
    if (selected_profile_id.ValueType() != JsonValueType::String)
    {
        throw std::invalid_argument("Active script profile set requires a selected profile.");
    }
    const std::string active_profile_id = winrt::to_string(selected_profile_id.GetString());
    JsonObject active_profile;
    bool profile_found = false;
    for (const IJsonValue &item : root.GetNamedArray(L"profiles"))
    {
        const JsonObject candidate = item.GetObject();
        if (winrt::to_string(candidate.GetNamedString(L"id")) == active_profile_id)
        {
            active_profile = candidate;
            profile_found = true;
            break;
        }
    }
    if (!profile_found)
    {
        throw std::invalid_argument("Active script profile does not exist.");
    }

    RuntimeCandidate result;
    result.token = token;
    result.profile_set_id = active_set_id;
    result.profile_id = active_profile_id;
    result.profile_uses_lua = active_profile.GetNamedString(L"controlMode") == L"lua";
    const ScriptContext context{active_set_id, active_profile_id};

    if (const auto parsed = optional_script(root, L"globalScript", ScriptScope::global, "global"))
    {
        result.input_bindings.insert(result.input_bindings.end(), parsed->bindings.begin(), parsed->bindings.end());
        result.global = load_script(*parsed, context);
    }
    if (const auto parsed = optional_script(active_set, L"script", ScriptScope::profile_set, active_set_id))
    {
        result.input_bindings.insert(result.input_bindings.end(), parsed->bindings.begin(), parsed->bindings.end());
        result.profile_set = load_script(*parsed, context);
    }
    if (result.profile_uses_lua)
    {
        const auto parsed = optional_script(active_profile, L"script", ScriptScope::profile, active_profile_id);
        if (!parsed)
        {
            throw std::invalid_argument("Lua control mode requires an enabled profile script.");
        }
        result.input_bindings.insert(result.input_bindings.end(), parsed->bindings.begin(), parsed->bindings.end());
        result.profile = load_script(*parsed, context);
    }
    return result;
}

[[nodiscard]] bool combined_visibility(const RuntimeCandidate &runtime) noexcept
{
    return (!runtime.global || runtime.global->script.allows_visible()) &&
           (!runtime.profile_set || runtime.profile_set->script.allows_visible()) &&
           (!runtime.profile || runtime.profile->script.allows_visible());
}
} // namespace

class ScriptCoordinator::Impl
{
  public:
    explicit Impl(RuntimeUpdated runtime_updated)
        : runtime_updated_(std::move(runtime_updated)),
          worker_(HostThreadRole::script, [this](const std::stop_token stop_token) { run(stop_token); })
    {
        if (!runtime_updated_)
        {
            throw std::invalid_argument("Script runtime callback cannot be empty.");
        }
    }

    ~Impl()
    {
        stop();
    }

    void start()
    {
        worker_.start();
    }

    [[nodiscard]] ScriptPreparedConfiguration prepare(const std::string_view snapshot_json)
    {
        return invoke_sync<ScriptPreparedConfiguration>(
            [this, snapshot = std::string(snapshot_json)]
            {
                RuntimeCandidate candidate = parse_candidate(snapshot, next_token_++);
                const ScriptPreparedConfiguration result{
                    candidate.token,
                    candidate.input_bindings,
                    combined_visibility(candidate),
                    candidate.profile_uses_lua,
                    candidate.profile_set_id,
                    candidate.profile_id,
                };
                prepared_.insert_or_assign(candidate.token, std::move(candidate));
                return result;
            });
    }

    [[nodiscard]] std::string validate(const std::string_view request_json)
    {
        return invoke_sync<std::string>([request = std::string(request_json)]
                                        { return validate_request_json(request); });
    }

    void commit(const std::uint64_t token)
    {
        invoke_sync<void>(
            [this, token]
            {
                const auto candidate = prepared_.find(token);
                if (candidate == prepared_.end())
                {
                    throw std::invalid_argument("Prepared script configuration does not exist.");
                }
                active_ = std::move(candidate->second);
                prepared_.clear();
            });
    }

    void discard(const std::uint64_t token) noexcept
    {
        try
        {
            invoke_sync<void>([this, token] { prepared_.erase(token); });
        }
        catch (const std::exception &error)
        {
            log_diagnostic(DiagnosticLevel::error, "script.discard_failed", error.what());
        }
    }

    void dispatch_input(ScriptInputEvent event)
    {
        enqueue(
            [this, event = std::move(event)]
            {
                if (!active_)
                {
                    return;
                }

                RuntimeScript *target = nullptr;
                switch (event.scope)
                {
                case ScriptScope::global:
                    target = active_->global ? &*active_->global : nullptr;
                    break;
                case ScriptScope::profile_set:
                    target = active_->profile_set ? &*active_->profile_set : nullptr;
                    break;
                case ScriptScope::profile:
                    target = active_->profile ? &*active_->profile : nullptr;
                    break;
                }
                if (target == nullptr || target->owner_id != event.script_id)
                {
                    return;
                }

                const ScriptExecutionResult result =
                    target->script.input(event.binding_id, event.phase, {active_->profile_set_id, active_->profile_id});
                runtime_updated_({
                    combined_visibility(*active_),
                    result.commands,
                    result.succeeded,
                    result.error,
                });
            });
    }

    void stop() noexcept
    {
        worker_.request_stop();
        queue_changed_.notify_all();
        worker_.join();
    }

  private:
    template <typename Result, typename Operation> Result invoke_sync(Operation operation)
    {
        auto promise = std::make_shared<std::promise<Result>>();
        std::future<Result> future = promise->get_future();
        enqueue(
            [operation = std::move(operation), promise]() mutable
            {
                try
                {
                    if constexpr (std::is_void_v<Result>)
                    {
                        operation();
                        promise->set_value();
                    }
                    else
                    {
                        promise->set_value(operation());
                    }
                }
                catch (...)
                {
                    promise->set_exception(std::current_exception());
                }
            });
        return future.get();
    }

    void enqueue(std::function<void()> task)
    {
        {
            std::scoped_lock lock(queue_mutex_);
            tasks_.push_back(std::move(task));
        }
        queue_changed_.notify_one();
    }

    void run(const std::stop_token stop_token)
    {
        while (true)
        {
            std::function<void()> task;
            {
                std::unique_lock lock(queue_mutex_);
                queue_changed_.wait(lock, stop_token, [this] { return !tasks_.empty(); });
                if (tasks_.empty())
                {
                    if (stop_token.stop_requested())
                    {
                        break;
                    }
                    continue;
                }
                task = std::move(tasks_.front());
                tasks_.pop_front();
            }
            task();
        }
        active_.reset();
        prepared_.clear();
    }

    RuntimeUpdated runtime_updated_;
    HostWorkerThread worker_;
    std::mutex queue_mutex_;
    std::condition_variable_any queue_changed_;
    std::deque<std::function<void()>> tasks_;
    std::unordered_map<std::uint64_t, RuntimeCandidate> prepared_;
    std::optional<RuntimeCandidate> active_;
    std::uint64_t next_token_ = 1U;
};

ScriptCoordinator::ScriptCoordinator(RuntimeUpdated runtime_updated)
    : impl_(std::make_unique<Impl>(std::move(runtime_updated)))
{
}

ScriptCoordinator::~ScriptCoordinator() = default;

void ScriptCoordinator::start()
{
    impl_->start();
}

ScriptPreparedConfiguration ScriptCoordinator::prepare(const std::string_view snapshot_json)
{
    return impl_->prepare(snapshot_json);
}

std::string ScriptCoordinator::validate(const std::string_view request_json)
{
    return impl_->validate(request_json);
}

void ScriptCoordinator::commit(const std::uint64_t token)
{
    impl_->commit(token);
}

void ScriptCoordinator::discard(const std::uint64_t token) noexcept
{
    impl_->discard(token);
}

void ScriptCoordinator::dispatch_input(ScriptInputEvent event)
{
    impl_->dispatch_input(std::move(event));
}

void ScriptCoordinator::stop() noexcept
{
    impl_->stop();
}
} // namespace external_peepsight

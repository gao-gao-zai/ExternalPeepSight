#include "script_command_configuration.h"

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

#include <algorithm>
#include <stdexcept>
#include <string>

namespace external_peepsight
{
namespace
{
using winrt::Windows::Data::Json::IJsonValue;
using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValue;
using winrt::Windows::Data::Json::JsonValueType;

[[nodiscard]] std::uint32_t find_object_index(const JsonArray &items, const std::wstring_view id,
                                              const std::string_view error)
{
    for (std::uint32_t index = 0U; index < items.Size(); ++index)
    {
        if (items.GetObjectAt(index).GetNamedString(L"id") == id)
        {
            return index;
        }
    }
    throw std::invalid_argument(std::string(error));
}

[[nodiscard]] std::uint32_t find_string_index(const JsonArray &items, const std::wstring_view id,
                                              const std::string_view error)
{
    for (std::uint32_t index = 0U; index < items.Size(); ++index)
    {
        if (items.GetStringAt(index) == id)
        {
            return index;
        }
    }
    throw std::invalid_argument(std::string(error));
}

[[nodiscard]] std::uint32_t wrapped_index(const std::uint32_t current, const std::uint32_t count, const bool next)
{
    if (count == 0U)
    {
        throw std::invalid_argument("Script switch target collection cannot be empty.");
    }
    return next ? (current + 1U) % count : (current + count - 1U) % count;
}

[[nodiscard]] JsonObject active_profile_set(const JsonObject &root)
{
    const JsonArray sets = root.GetNamedArray(L"profileSets");
    const std::wstring active_id = root.GetNamedString(L"activeProfileSetId").c_str();
    return sets.GetObjectAt(find_object_index(sets, active_id, "Active profile set does not exist."));
}

void switch_profile(JsonObject &root, const std::wstring_view target_id)
{
    JsonObject set = active_profile_set(root);
    const JsonArray profile_ids = set.GetNamedArray(L"profileIds");
    static_cast<void>(
        find_string_index(profile_ids, target_id, "Requested profile does not belong to the active profile set."));
    const JsonArray profiles = root.GetNamedArray(L"profiles");
    static_cast<void>(find_object_index(profiles, target_id, "Requested profile does not exist."));
    set.SetNamedValue(L"selectedProfileId", JsonValue::CreateStringValue(target_id));
}

void move_profile(JsonObject &root, const bool next)
{
    JsonObject set = active_profile_set(root);
    const JsonArray profile_ids = set.GetNamedArray(L"profileIds");
    const IJsonValue selected = set.GetNamedValue(L"selectedProfileId");
    if (selected.ValueType() != JsonValueType::String)
    {
        throw std::invalid_argument("Active profile set does not have a selected profile.");
    }
    const std::uint32_t current =
        find_string_index(profile_ids, selected.GetString(), "Selected profile does not belong to the active set.");
    set.SetNamedValue(L"selectedProfileId", JsonValue::CreateStringValue(profile_ids.GetStringAt(
                                                wrapped_index(current, profile_ids.Size(), next))));
}

void switch_profile_set(JsonObject &root, const std::wstring_view target_id)
{
    const JsonArray sets = root.GetNamedArray(L"profileSets");
    static_cast<void>(find_object_index(sets, target_id, "Requested profile set does not exist."));
    root.SetNamedValue(L"activeProfileSetId", JsonValue::CreateStringValue(target_id));
}

void move_profile_set(JsonObject &root, const bool next)
{
    const JsonArray sets = root.GetNamedArray(L"profileSets");
    const std::wstring active_id = root.GetNamedString(L"activeProfileSetId").c_str();
    const std::uint32_t current = find_object_index(sets, active_id, "Active profile set does not exist.");
    const JsonObject target = sets.GetObjectAt(wrapped_index(current, sets.Size(), next));
    root.SetNamedValue(L"activeProfileSetId", JsonValue::CreateStringValue(target.GetNamedString(L"id")));
}
} // namespace

std::string apply_script_commands(const std::string_view snapshot_json, const std::vector<ScriptCommand> &commands)
{
    JsonObject root = JsonObject::Parse(winrt::to_hstring(snapshot_json));
    if (root.GetNamedNumber(L"schemaVersion") != 9.0)
    {
        throw std::invalid_argument("Script command configuration schema version is not supported.");
    }

    for (const ScriptCommand &command : commands)
    {
        switch (command.type)
        {
        case ScriptCommandType::switch_profile:
            switch_profile(root, winrt::to_hstring(command.target_id));
            break;
        case ScriptCommandType::previous_profile:
            move_profile(root, false);
            break;
        case ScriptCommandType::next_profile:
            move_profile(root, true);
            break;
        case ScriptCommandType::switch_profile_set:
            switch_profile_set(root, winrt::to_hstring(command.target_id));
            break;
        case ScriptCommandType::previous_profile_set:
            move_profile_set(root, false);
            break;
        case ScriptCommandType::next_profile_set:
            move_profile_set(root, true);
            break;
        }
    }

    return winrt::to_string(root.Stringify());
}
} // namespace external_peepsight

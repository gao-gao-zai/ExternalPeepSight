#pragma once

#include "script_runtime.h"

#include <string>
#include <string_view>
#include <vector>

namespace external_peepsight
{
/// Applies validated Lua configuration-switch commands to a schema 7 snapshot.
///
/// Commands are evaluated in order. Previous and next operations wrap around the
/// stable array order stored in the configuration document.
/// <param name="snapshot_json">Current validated configuration snapshot.</param>
/// <param name="commands">Commands produced by one successful Lua callback.</param>
/// <returns>The updated configuration snapshot.</returns>
/// <exception cref="std::invalid_argument">A target identifier is unavailable in the active scope.</exception>
[[nodiscard]] std::string apply_script_commands(std::string_view snapshot_json,
                                                const std::vector<ScriptCommand> &commands);
} // namespace external_peepsight

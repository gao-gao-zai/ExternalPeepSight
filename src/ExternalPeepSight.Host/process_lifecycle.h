#pragma once

#include <filesystem>
#include <string_view>

namespace external_peepsight
{
/// Launches the settings UI beside the Host executable.
///
/// The optional path overrides sibling executable discovery.
[[nodiscard]] bool launch_settings_ui(const std::filesystem::path &override_path, std::wstring_view instance_id);
} // namespace external_peepsight

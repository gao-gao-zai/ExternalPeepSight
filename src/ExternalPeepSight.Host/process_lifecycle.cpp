#include "process_lifecycle.h"

#include "diagnostics.h"

#include <shellapi.h>
#include <windows.h>

#include <vector>

namespace external_peepsight
{
namespace
{
[[nodiscard]] std::filesystem::path sibling_ui_path()
{
    std::vector<wchar_t> buffer(32'768);
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0U || length == buffer.size())
    {
        throw_last_error("GetModuleFileNameW");
    }

    std::filesystem::path path(std::wstring_view(buffer.data(), length));
    path.replace_filename(L"ExternalPeepSight.UI.exe");
    return path;
}
} // namespace

bool launch_settings_ui(const std::filesystem::path &override_path, const std::wstring_view instance_id)
{
    const std::filesystem::path executable = override_path.empty() ? sibling_ui_path() : override_path;
    if (!std::filesystem::is_regular_file(executable))
    {
        log_diagnostic(DiagnosticLevel::warning, "ui.executable_missing",
                       "The settings UI executable was not found beside the Host.");
        return false;
    }

    const std::wstring parameters = L"--connect-existing --instance-id=" + std::wstring(instance_id);
    SHELLEXECUTEINFOW launch{};
    launch.cbSize = sizeof(launch);
    launch.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC;
    launch.lpVerb = L"open";
    launch.lpFile = executable.c_str();
    launch.lpParameters = parameters.c_str();
    launch.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&launch))
    {
        throw_last_error("ShellExecuteExW settings UI");
    }
    if (launch.hProcess != nullptr)
    {
        CloseHandle(launch.hProcess);
    }
    return true;
}
} // namespace external_peepsight

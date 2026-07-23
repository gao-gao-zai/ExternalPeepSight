#include "tray_icon.h"

#include "diagnostics.h"
#include "resource.h"

#include <shellapi.h>

#include <array>
#include <stdexcept>
#include <string>

namespace external_peepsight
{
namespace
{
constexpr UINT kTrayIconId = 1U;
constexpr UINT kOpenSettingsCommand = 1'001U;
constexpr UINT kExitHostCommand = 1'002U;

[[nodiscard]] std::wstring load_string(const UINT identifier)
{
    std::array<wchar_t, 128> buffer{};
    const int length =
        LoadStringW(GetModuleHandleW(nullptr), identifier, buffer.data(), static_cast<int>(buffer.size()));
    if (length == 0)
    {
        throw_last_error("LoadStringW tray command");
    }
    return std::wstring(buffer.data(), static_cast<std::size_t>(length));
}
} // namespace

TrayIcon::~TrayIcon()
{
    reset();
}

void TrayIcon::initialize(_In_ const HWND owner, const UINT callback_message)
{
    if (owner == nullptr || callback_message < WM_APP)
    {
        throw std::invalid_argument("Tray icon owner or callback message is invalid.");
    }
    owner_ = owner;
    callback_message_ = callback_message;
    add();
}

void TrayIcon::restore_after_taskbar_created()
{
    if (owner_ != nullptr)
    {
        added_ = false;
        add();
    }
}

void TrayIcon::reset() noexcept
{
    if (!added_)
    {
        return;
    }

    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = owner_;
    data.uID = kTrayIconId;
    Shell_NotifyIconW(NIM_DELETE, &data);
    added_ = false;
}

TrayCommand TrayIcon::show_context_menu()
{
    POINT cursor{};
    if (!GetCursorPos(&cursor))
    {
        throw_last_error("GetCursorPos tray menu");
    }

    HMENU menu = CreatePopupMenu();
    if (menu == nullptr)
    {
        throw_last_error("CreatePopupMenu");
    }

    const std::wstring open_settings = load_string(IDS_TRAY_OPEN_SETTINGS);
    const std::wstring exit_host = load_string(IDS_TRAY_EXIT);
    if (!AppendMenuW(menu, MF_STRING | MF_DEFAULT, kOpenSettingsCommand, open_settings.c_str()) ||
        !AppendMenuW(menu, MF_SEPARATOR, 0U, nullptr) ||
        !AppendMenuW(menu, MF_STRING, kExitHostCommand, exit_host.c_str()))
    {
        const DWORD error = GetLastError();
        DestroyMenu(menu);
        throw NativeError(error, "AppendMenuW tray menu");
    }

    SetForegroundWindow(owner_);
    const UINT selected =
        TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, cursor.x, cursor.y, 0, owner_, nullptr);
    DestroyMenu(menu);
    PostMessageW(owner_, WM_NULL, 0U, 0);

    if (selected == kOpenSettingsCommand)
    {
        return TrayCommand::open_settings;
    }
    if (selected == kExitHostCommand)
    {
        return TrayCommand::exit_host;
    }
    return TrayCommand::none;
}

void TrayIcon::add()
{
    NOTIFYICONDATAW data{};
    data.cbSize = sizeof(data);
    data.hWnd = owner_;
    data.uID = kTrayIconId;
    data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    data.uCallbackMessage = callback_message_;
    data.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wcscpy_s(data.szTip, L"ExternalPeepSight");
    if (!Shell_NotifyIconW(NIM_ADD, &data))
    {
        throw_last_error("Shell_NotifyIconW NIM_ADD");
    }
    added_ = true;

    data.uVersion = NOTIFYICON_VERSION_4;
    if (!Shell_NotifyIconW(NIM_SETVERSION, &data))
    {
        log_diagnostic(DiagnosticLevel::warning, "tray.version_failed",
                       "Notification icon version negotiation failed.");
    }
}
} // namespace external_peepsight

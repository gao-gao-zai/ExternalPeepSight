#pragma once

#include <windows.h>

namespace external_peepsight
{
/// Command selected from the Host notification-area menu.
enum class TrayCommand
{
    none,
    open_settings,
    exit_host,
};

/// Owns the Host notification-area icon and context menu.
class TrayIcon
{
  public:
    TrayIcon() = default;

    TrayIcon(const TrayIcon &) = delete;
    TrayIcon &operator=(const TrayIcon &) = delete;

    /// Removes the notification-area icon.
    ~TrayIcon();

    /// Adds the notification-area icon for the owner window.
    void initialize(_In_ HWND owner, UINT callback_message);

    /// Re-adds the icon after Explorer recreates the taskbar.
    void restore_after_taskbar_created();

    /// Removes the notification-area icon. Repeated calls are allowed.
    void reset() noexcept;

    /// Shows the context menu and returns the selected command.
    [[nodiscard]] TrayCommand show_context_menu();

  private:
    void add();

    HWND owner_ = nullptr;
    UINT callback_message_ = 0U;
    bool added_ = false;
};
} // namespace external_peepsight

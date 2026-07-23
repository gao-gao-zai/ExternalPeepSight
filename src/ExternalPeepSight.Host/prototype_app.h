#pragma once

#include <windows.h>

namespace external_peepsight
{
/// Runs the phase-one overlay prototype on the current thread.
///
/// The current thread owns COM, every prototype HWND, Direct2D rendering, and
/// the WinEvent foreground hook for the lifetime of the application.
[[nodiscard]] int run_overlay_prototype(_In_ HINSTANCE instance);
} // namespace external_peepsight

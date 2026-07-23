#pragma once

#include <windows.h>

namespace external_peepsight
{
/// Runs the native overlay Host until shutdown.
///
/// The current thread owns the message loop, HWNDs, and WinEvent hook. The
/// render worker owns COM and all DirectX resources.
[[nodiscard]] int run_overlay_prototype(_In_ HINSTANCE instance);
} // namespace external_peepsight

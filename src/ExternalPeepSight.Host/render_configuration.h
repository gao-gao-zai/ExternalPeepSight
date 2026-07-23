#pragma once

#include "prototype_contracts.h"

#include <array>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>
#include <windows.h>

namespace external_peepsight
{
/// Represents an RGBA color using unpremultiplied byte channels.
struct RenderColor
{
    /// Red channel.
    std::uint8_t red;
    /// Green channel.
    std::uint8_t green;
    /// Blue channel.
    std::uint8_t blue;
    /// Alpha channel.
    std::uint8_t alpha;

    bool operator==(const RenderColor &) const = default;
};

/// Selects the visual rendered by the active profile.
enum class RenderMode
{
    crosshair,
    image,
};

/// Selects the monitor-local origin used for visual placement.
enum class RenderAnchor
{
    screen_center,
    top_left,
};

/// Selects the decoded image resource type.
enum class RenderAssetKind
{
    png,
    svg,
};

/// Selects the monitor edge used for Toast placement.
enum class RenderToastPosition
{
    top_left,
    top_center,
    top_right,
    bottom_left,
    bottom_center,
    bottom_right,
};

/// Defines the active profile's crosshair rendering state.
struct RenderCrosshair
{
    /// Position origin.
    RenderAnchor anchor;
    /// Monitor-local physical-pixel offset.
    PointPx offset_px;
    /// Whether the center point is rendered.
    bool center_visible;
    /// Center point color.
    RenderColor center_color;
    /// Center point radius in physical pixels.
    float center_radius_px;
    /// Independently configured arms.
    std::array<CrosshairArmDefinition, 4> arms;
    /// Per-arm colors in the same order as `arms`.
    std::array<RenderColor, 4> arm_colors;
};

/// Validated image bytes and intrinsic dimensions retained by a render snapshot.
struct RenderAsset
{
    /// Stable asset identifier from the configuration document.
    std::string id;
    /// Decoding path selected from the declared media type.
    RenderAssetKind kind;
    /// Validated SHA-256 in uppercase hexadecimal form.
    std::string sha256;
    /// Intrinsic width in physical pixels.
    std::uint32_t width_px;
    /// Intrinsic height in physical pixels.
    std::uint32_t height_px;
    /// Immutable encoded PNG or UTF-8 SVG bytes.
    std::vector<std::uint8_t> bytes;
};

/// Defines active image placement and its validated resource.
struct RenderImage
{
    /// Position origin.
    RenderAnchor anchor;
    /// Monitor-local physical-pixel offset.
    PointPx offset_px;
    /// Uniform scale applied to intrinsic dimensions.
    float scale;
    /// Whether the decoder preserves the resource aspect ratio.
    bool keep_aspect_ratio;
    /// Validated active image resource.
    std::optional<RenderAsset> asset;
};

/// Defines the appearance and lifetime of Host-rendered Toast messages.
struct RenderToastConfiguration
{
    /// Whether Toast messages are rendered.
    bool enabled;
    /// Monitor edge alignment.
    RenderToastPosition position;
    /// Default lifetime in milliseconds.
    std::uint32_t duration_ms;
    /// DirectWrite font family.
    std::wstring font_family;
    /// Font size in physical pixels at 96 DPI.
    float font_size_px;
    /// Text color.
    RenderColor foreground;
    /// Background color.
    RenderColor background;
};

/// Immutable rendering configuration parsed from one configuration snapshot.
struct RenderConfiguration
{
    /// Active profile identifier.
    std::string profile_id;
    /// Active rendering mode.
    RenderMode mode;
    /// Crosshair state retained for mode changes.
    RenderCrosshair crosshair;
    /// Image state retained for mode changes.
    RenderImage image;
    /// Global Toast appearance.
    RenderToastConfiguration toasts;
};

/// Parses and validates rendering state and referenced assets before publication.
///
/// Asset paths are resolved only beneath `asset_root`. PNG and SVG bytes are
/// size-limited, hashed, and structurally validated before this function returns.
[[nodiscard]] RenderConfiguration parse_render_configuration(std::string_view snapshot_json,
                                                             const std::filesystem::path &asset_root);

/// Creates the built-in rendering state used before the first IPC snapshot.
[[nodiscard]] RenderConfiguration make_default_render_configuration();

/// Calculates the tightly bounded visual HWND for the active profile.
///
/// The monitor and returned rectangles use virtual-desktop physical pixels.
[[nodiscard]] RECT calculate_render_visual_bounds(const RECT &monitor_bounds_px,
                                                  const RenderConfiguration &configuration) noexcept;
} // namespace external_peepsight

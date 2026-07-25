#include "render_configuration.h"

#include <bcrypt.h>

#include <gtest/gtest.h>

#include <array>
#include <cstdint>
#include <filesystem>
#include <format>
#include <fstream>
#include <string>
#include <vector>

namespace
{
constexpr std::array<std::uint8_t, 153> kPng{
    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00,
    0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08, 0x08, 0x06, 0x00, 0x00, 0x00, 0xC4, 0x0F, 0xBE, 0x8B, 0x00,
    0x00, 0x00, 0x01, 0x73, 0x52, 0x47, 0x42, 0x00, 0xAE, 0xCE, 0x1C, 0xE9, 0x00, 0x00, 0x00, 0x04, 0x67,
    0x41, 0x4D, 0x41, 0x00, 0x00, 0xB1, 0x8F, 0x0B, 0xFC, 0x61, 0x05, 0x00, 0x00, 0x00, 0x09, 0x70, 0x48,
    0x59, 0x73, 0x00, 0x00, 0x0E, 0xC3, 0x00, 0x00, 0x0E, 0xC3, 0x01, 0xC7, 0x6F, 0xA8, 0x64, 0x00, 0x00,
    0x00, 0x2E, 0x49, 0x44, 0x41, 0x54, 0x28, 0x53, 0x63, 0x60, 0x38, 0xF1, 0xFF, 0x0E, 0xC3, 0x89, 0xFF,
    0x6E, 0x38, 0xF0, 0x1D, 0x06, 0x38, 0x03, 0x9B, 0x24, 0x88, 0xC6, 0x10, 0x40, 0x67, 0x63, 0xD1, 0x85,
    0x6A, 0x1A, 0x29, 0x0A, 0xF0, 0x5A, 0x81, 0xD7, 0x91, 0xD8, 0x24, 0xE1, 0x8A, 0x00, 0x58, 0x90, 0x8C,
    0xA1, 0x10, 0xA4, 0xDA, 0xAE, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
};

class TemporaryDirectory
{
  public:
    TemporaryDirectory()
        : path_(std::filesystem::temp_directory_path() /
                std::format("ExternalPeepSight-render-tests-{}", GetCurrentProcessId()))
    {
        std::filesystem::remove_all(path_);
        std::filesystem::create_directories(path_);
    }

    ~TemporaryDirectory()
    {
        std::error_code ignored;
        std::filesystem::remove_all(path_, ignored);
    }

    [[nodiscard]] const std::filesystem::path &path() const noexcept
    {
        return path_;
    }

  private:
    std::filesystem::path path_;
};

[[nodiscard]] std::string sha256(const std::vector<std::uint8_t> &bytes)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD object_size = 0U;
    DWORD returned = 0U;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0U) < 0 ||
        BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size), sizeof(object_size),
                          &returned, 0U) < 0)
    {
        throw std::runtime_error("Unable to initialize SHA-256 for test data.");
    }
    std::vector<std::uint8_t> object(object_size);
    if (BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0U, 0U) < 0 ||
        BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), 0U) < 0)
    {
        BCryptCloseAlgorithmProvider(algorithm, 0U);
        throw std::runtime_error("Unable to hash test data.");
    }
    std::array<std::uint8_t, 32> digest{};
    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0U) < 0)
    {
        BCryptDestroyHash(hash);
        BCryptCloseAlgorithmProvider(algorithm, 0U);
        throw std::runtime_error("Unable to finish hashing test data.");
    }
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0U);

    constexpr char digits[] = "0123456789ABCDEF";
    std::string result(digest.size() * 2U, '0');
    for (std::size_t index = 0U; index < digest.size(); ++index)
    {
        result[index * 2U] = digits[digest[index] >> 4U];
        result[index * 2U + 1U] = digits[digest[index] & 0x0FU];
    }
    return result;
}

void write_bytes(const std::filesystem::path &path, const std::vector<std::uint8_t> &bytes)
{
    std::ofstream output(path, std::ios::binary);
    ASSERT_TRUE(output);
    output.write(reinterpret_cast<const char *>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    ASSERT_TRUE(output);
}

[[nodiscard]] std::string snapshot(const std::string_view mode, const std::string_view asset_id,
                                   const std::string_view file_name, const std::string_view media_type,
                                   const std::vector<std::uint8_t> &bytes, const std::string_view hash)
{
    const std::string asset_value =
        asset_id.empty() ? ""
                         : std::format(R"({{"id":"{}","fileName":"{}","mediaType":"{}","sizeBytes":{},"sha256":"{}"}})",
                                       asset_id, file_name, media_type, bytes.size(), hash);
    const std::string image_asset = asset_id.empty() ? "null" : std::format("\"{}\"", asset_id);
    return std::format(
        R"({{
          "schemaVersion":6,
          "inputBackend":"rawInput",
          "profiles":[{{
            "id":"11111111-1111-1111-1111-111111111111",
            "name":"Test",
            "activeMode":"{}",
            "crosshair":{{
              "anchor":"screenCenter",
              "offsetPx":{{"x":-3,"y":4}},
              "center":{{"visible":true,"color":"#11223344","radiusPx":3}},
              "arms":[
                {{"orbitAngleOffsetDeg":0,"rotationAngleOffsetDeg":0,"gapPx":6,"lengthPx":12,"widthPx":2,"color":"#FFFFFFFF","visible":true}},
                {{"orbitAngleOffsetDeg":0,"rotationAngleOffsetDeg":0,"gapPx":7,"lengthPx":13,"widthPx":3,"color":"#FF0000FF","visible":true}},
                {{"orbitAngleOffsetDeg":0,"rotationAngleOffsetDeg":0,"gapPx":8,"lengthPx":14,"widthPx":4,"color":"#00FF00FF","visible":false}},
                {{"orbitAngleOffsetDeg":0,"rotationAngleOffsetDeg":0,"gapPx":9,"lengthPx":15,"widthPx":5,"color":"#0000FFFF","visible":true}}
              ],
              "linked":false
            }},
            "image":{{
              "assetId":{},
              "anchor":"topLeft",
              "offsetPx":{{"x":100,"y":200}},
              "scale":2,
              "keepAspectRatio":true
            }},
            "switches":{{
              "visibilityRule":"switchA",
              "initialStateA":false,
              "initialStateB":false,
              "switchA":{{"mode":"unbound","toggleKey":null,"enableKey":null,"disableKey":null,"holdKey":null}},
              "switchB":{{"mode":"unbound","toggleKey":null,"enableKey":null,"disableKey":null,"holdKey":null}}
            }}
          }}],
          "profileSets":[{{
            "id":"22222222-2222-2222-2222-222222222222",
            "name":"Set",
            "profileIds":["11111111-1111-1111-1111-111111111111"],
            "selectedProfileId":"11111111-1111-1111-1111-111111111111"
          }}],
          "assets":[{}],
          "monitorSelection":{{"mode":"focus","monitorIds":[],"focusSource":"foregroundWindowThenMouse"}},
          "toasts":{{
            "enabled":true,
            "position":"bottomRight",
            "durationMs":2500,
            "fontFamily":"Segoe UI",
            "fontSizePx":20,
            "foreground":"#FFFFFFFF",
            "background":"#000000B4"
          }}
        }})",
        mode, image_asset, asset_value);
}

TEST(RenderConfiguration, ParsesIndependentCrosshairAndToastSettings)
{
    const external_peepsight::RenderConfiguration configuration =
        external_peepsight::parse_render_configuration(snapshot("crosshair", "", "", "", {}, ""), {});

    EXPECT_EQ(external_peepsight::RenderMode::crosshair, configuration.mode);
    EXPECT_FLOAT_EQ(-3.0F, configuration.crosshair.offset_px.x);
    EXPECT_EQ(external_peepsight::RenderColor({0x11U, 0x22U, 0x33U, 0x44U}), configuration.crosshair.center_color);
    EXPECT_FLOAT_EQ(7.0F, configuration.crosshair.arms[1].gap_px);
    EXPECT_FALSE(configuration.crosshair.arms[2].visible);
    EXPECT_EQ(external_peepsight::RenderToastPosition::bottom_right, configuration.toasts.position);
    EXPECT_EQ(2'500U, configuration.toasts.duration_ms);
}

TEST(RenderConfiguration, LoadsAndValidatesPngBeforePublication)
{
    TemporaryDirectory directory;
    const std::vector<std::uint8_t> bytes(kPng.begin(), kPng.end());
    write_bytes(directory.path() / "reticle.png", bytes);
    const std::string json = snapshot("image", "asset-1", "reticle.png", "image/png", bytes, sha256(bytes));

    const external_peepsight::RenderConfiguration configuration =
        external_peepsight::parse_render_configuration(json, directory.path());

    ASSERT_TRUE(configuration.image.asset);
    EXPECT_EQ(external_peepsight::RenderAssetKind::png, configuration.image.asset->kind);
    EXPECT_EQ(8U, configuration.image.asset->width_px);
    EXPECT_EQ(8U, configuration.image.asset->height_px);
    EXPECT_EQ(bytes, configuration.image.asset->bytes);
}

TEST(RenderConfiguration, AcceptsStaticSvgAndRejectsExternalReferences)
{
    TemporaryDirectory directory;
    const std::string valid_svg =
        R"(<svg xmlns="http://www.w3.org/2000/svg" width="32" height="16"><path d="M0 0L32 16"/></svg>)";
    const std::vector<std::uint8_t> valid_bytes(valid_svg.begin(), valid_svg.end());
    write_bytes(directory.path() / "reticle.svg", valid_bytes);
    const auto valid = external_peepsight::parse_render_configuration(
        snapshot("image", "asset-2", "reticle.svg", "image/svg+xml", valid_bytes, sha256(valid_bytes)),
        directory.path());

    ASSERT_TRUE(valid.image.asset);
    EXPECT_EQ(32U, valid.image.asset->width_px);
    EXPECT_EQ(16U, valid.image.asset->height_px);

    const std::string external_svg =
        R"(<svg xmlns="http://www.w3.org/2000/svg"><image href="https://example.invalid/a.png"/></svg>)";
    const std::vector<std::uint8_t> external_bytes(external_svg.begin(), external_svg.end());
    write_bytes(directory.path() / "external.svg", external_bytes);
    EXPECT_THROW(
        static_cast<void>(external_peepsight::parse_render_configuration(
            snapshot("image", "asset-3", "external.svg", "image/svg+xml", external_bytes, sha256(external_bytes)),
            directory.path())),
        std::invalid_argument);

    const std::string relative_svg = R"(<svg xmlns="http://www.w3.org/2000/svg"><image href="relative.png"/></svg>)";
    const std::vector<std::uint8_t> relative_bytes(relative_svg.begin(), relative_svg.end());
    write_bytes(directory.path() / "relative.svg", relative_bytes);
    EXPECT_THROW(
        static_cast<void>(external_peepsight::parse_render_configuration(
            snapshot("image", "asset-4", "relative.svg", "image/svg+xml", relative_bytes, sha256(relative_bytes)),
            directory.path())),
        std::invalid_argument);
}

TEST(RenderConfiguration, RejectsHashMismatchAndAssetTraversal)
{
    TemporaryDirectory directory;
    const std::vector<std::uint8_t> bytes(kPng.begin(), kPng.end());
    write_bytes(directory.path() / "reticle.png", bytes);

    EXPECT_THROW(
        static_cast<void>(external_peepsight::parse_render_configuration(
            snapshot("image", "asset-4", "reticle.png", "image/png", bytes, std::string(64U, '0')), directory.path())),
        std::invalid_argument);
    EXPECT_THROW(
        static_cast<void>(external_peepsight::parse_render_configuration(
            snapshot("image", "asset-5", "../reticle.png", "image/png", bytes, sha256(bytes)), directory.path())),
        std::invalid_argument);
}

TEST(RenderConfiguration, ImageBoundsRemainTightAndUseFullMonitorCoordinates)
{
    external_peepsight::RenderConfiguration configuration = external_peepsight::make_default_render_configuration();
    configuration.mode = external_peepsight::RenderMode::image;
    configuration.image.anchor = external_peepsight::RenderAnchor::screen_center;
    configuration.image.offset_px = {0.0F, 0.0F};
    configuration.image.scale = 2.0F;
    configuration.image.asset = external_peepsight::RenderAsset{
        "asset", external_peepsight::RenderAssetKind::png, std::string(64U, 'A'), 32U, 16U, {}};

    const RECT bounds = external_peepsight::calculate_render_visual_bounds({-1920, 0, 0, 1080}, configuration);

    EXPECT_EQ(-994, bounds.left);
    EXPECT_EQ(522, bounds.top);
    EXPECT_EQ(-926, bounds.right);
    EXPECT_EQ(558, bounds.bottom);
}
} // namespace

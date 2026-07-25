#include "render_configuration.h"

#include <bcrypt.h>
#include <objbase.h>
#include <xmllite.h>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/base.h>

#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <charconv>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <cwctype>
#include <fstream>
#include <limits>
#include <memory>
#include <ranges>
#include <span>
#include <stdexcept>
#include <unordered_map>
#include <utility>

namespace external_peepsight
{
namespace
{
using Microsoft::WRL::ComPtr;
using winrt::Windows::Data::Json::IJsonValue;
using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValueType;

constexpr std::size_t kMaximumAssetBytes = 10U * 1024U * 1024U;
constexpr std::uint32_t kMaximumImageDimensionPx = 8192U;
constexpr std::uint64_t kMaximumImagePixels = 64U * 1024U * 1024U;
constexpr float kMaximumCoordinatePx = 100'000.0F;
constexpr float kMaximumStrokePx = 2'000.0F;
constexpr float kMaximumScale = 100.0F;
constexpr std::uint32_t kMaximumSvgElements = 10'000U;
constexpr std::array<float, 4> kDefaultOrbitAnglesDeg{0.0F, 90.0F, 180.0F, 270.0F};

struct AssetReferenceValue
{
    std::string id;
    std::filesystem::path file_name;
    RenderAssetKind kind;
    std::uint64_t size_bytes;
    std::string sha256;
};

[[nodiscard]] std::wstring lower_copy(const std::wstring_view value)
{
    std::wstring result(value);
    std::ranges::transform(result, result.begin(),
                           [](const wchar_t character) { return static_cast<wchar_t>(std::towlower(character)); });
    return result;
}

[[nodiscard]] std::string upper_copy(const std::string_view value)
{
    std::string result(value);
    std::ranges::transform(result, result.begin(),
                           [](const unsigned char character) { return static_cast<char>(std::toupper(character)); });
    return result;
}

[[nodiscard]] bool is_simple_file_name(const std::filesystem::path &path)
{
    return !path.empty() && path == path.filename() && path.native() != L"." && path.native() != L"..";
}

[[nodiscard]] float checked_number(const JsonObject &object, const std::wstring_view property_name, const float minimum,
                                   const float maximum)
{
    const double value = object.GetNamedNumber(property_name);
    if (!std::isfinite(value) || value < minimum || value > maximum)
    {
        throw std::invalid_argument("Render configuration number is outside the supported range.");
    }
    return static_cast<float>(value);
}

[[nodiscard]] std::uint64_t checked_integer(const JsonObject &object, const std::wstring_view property_name,
                                            const std::uint64_t minimum, const std::uint64_t maximum)
{
    const double value = object.GetNamedNumber(property_name);
    if (!std::isfinite(value) || std::floor(value) != value || value < static_cast<double>(minimum) ||
        value > static_cast<double>(maximum))
    {
        throw std::invalid_argument("Render configuration integer is outside the supported range.");
    }
    return static_cast<std::uint64_t>(value);
}

[[nodiscard]] RenderColor parse_color(const std::wstring_view value)
{
    if (value.size() != 9U || value.front() != L'#')
    {
        throw std::invalid_argument("Render color must use #RRGGBBAA format.");
    }

    const auto channel = [&value](const std::size_t offset)
    {
        unsigned int parsed = 0U;
        const std::string encoded = winrt::to_string(value.substr(offset, 2U));
        const auto result = std::from_chars(encoded.data(), encoded.data() + encoded.size(), parsed, 16);
        if (result.ec != std::errc{} || result.ptr != encoded.data() + encoded.size())
        {
            throw std::invalid_argument("Render color contains a non-hexadecimal channel.");
        }
        return static_cast<std::uint8_t>(parsed);
    };
    return {channel(1U), channel(3U), channel(5U), channel(7U)};
}

[[nodiscard]] PointPx parse_offset(const JsonObject &object)
{
    return {
        checked_number(object, L"x", -kMaximumCoordinatePx, kMaximumCoordinatePx),
        checked_number(object, L"y", -kMaximumCoordinatePx, kMaximumCoordinatePx),
    };
}

[[nodiscard]] RenderAnchor parse_anchor(const std::wstring_view value)
{
    if (value == L"screenCenter")
    {
        return RenderAnchor::screen_center;
    }
    if (value == L"topLeft")
    {
        return RenderAnchor::top_left;
    }
    throw std::invalid_argument("Render anchor is invalid.");
}

[[nodiscard]] RenderToastPosition parse_toast_position(const std::wstring_view value)
{
    if (value == L"topLeft")
    {
        return RenderToastPosition::top_left;
    }
    if (value == L"topCenter")
    {
        return RenderToastPosition::top_center;
    }
    if (value == L"topRight")
    {
        return RenderToastPosition::top_right;
    }
    if (value == L"bottomLeft")
    {
        return RenderToastPosition::bottom_left;
    }
    if (value == L"bottomCenter")
    {
        return RenderToastPosition::bottom_center;
    }
    if (value == L"bottomRight")
    {
        return RenderToastPosition::bottom_right;
    }
    throw std::invalid_argument("Toast position is invalid.");
}

[[nodiscard]] std::vector<std::uint8_t> read_asset_bytes(const std::filesystem::path &path,
                                                         const std::uint64_t expected_size)
{
    std::error_code error;
    const std::uintmax_t size = std::filesystem::file_size(path, error);
    if (error || size != expected_size || size == 0U || size > kMaximumAssetBytes)
    {
        throw std::invalid_argument("Asset file size does not match its configuration metadata.");
    }

    std::ifstream input(path, std::ios::binary);
    if (!input)
    {
        throw std::invalid_argument("Asset file cannot be opened.");
    }
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(size));
    input.read(reinterpret_cast<char *>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    if (!input || input.gcount() != static_cast<std::streamsize>(bytes.size()))
    {
        throw std::invalid_argument("Asset file could not be read completely.");
    }
    return bytes;
}

[[nodiscard]] std::string sha256_hex(const std::vector<std::uint8_t> &bytes)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD object_size = 0U;
    DWORD result_size = 0U;
    std::vector<std::uint8_t> object;
    std::array<std::uint8_t, 32> digest{};
    const auto cleanup = [&]() noexcept
    {
        if (hash != nullptr)
        {
            BCryptDestroyHash(hash);
        }
        if (algorithm != nullptr)
        {
            BCryptCloseAlgorithmProvider(algorithm, 0U);
        }
    };

    NTSTATUS status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0U);
    if (status >= 0)
    {
        status = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&object_size),
                                   sizeof(object_size), &result_size, 0U);
    }
    if (status >= 0)
    {
        object.resize(object_size);
        status = BCryptCreateHash(algorithm, &hash, object.data(), object_size, nullptr, 0U, 0U);
    }
    if (status >= 0)
    {
        status = BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), 0U);
    }
    if (status >= 0)
    {
        status = BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0U);
    }
    cleanup();
    if (status < 0)
    {
        throw std::runtime_error("SHA-256 validation failed.");
    }

    constexpr char digits[] = "0123456789ABCDEF";
    std::string encoded(digest.size() * 2U, '0');
    for (std::size_t index = 0U; index < digest.size(); ++index)
    {
        encoded[index * 2U] = digits[digest[index] >> 4U];
        encoded[index * 2U + 1U] = digits[digest[index] & 0x0FU];
    }
    return encoded;
}

[[nodiscard]] std::uint32_t read_big_endian_u32(const std::uint8_t *value) noexcept
{
    return static_cast<std::uint32_t>(value[0]) << 24U | static_cast<std::uint32_t>(value[1]) << 16U |
           static_cast<std::uint32_t>(value[2]) << 8U | static_cast<std::uint32_t>(value[3]);
}

[[nodiscard]] std::pair<std::uint32_t, std::uint32_t> validate_png(const std::vector<std::uint8_t> &bytes)
{
    constexpr std::array<std::uint8_t, 8> signature{0x89U, 0x50U, 0x4EU, 0x47U, 0x0DU, 0x0AU, 0x1AU, 0x0AU};
    if (bytes.size() < 33U || !std::ranges::equal(signature, std::span(bytes).first(signature.size())) ||
        std::memcmp(bytes.data() + 12U, "IHDR", 4U) != 0)
    {
        throw std::invalid_argument("PNG asset signature or IHDR chunk is invalid.");
    }
    const std::uint32_t width = read_big_endian_u32(bytes.data() + 16U);
    const std::uint32_t height = read_big_endian_u32(bytes.data() + 20U);
    if (width == 0U || height == 0U || width > kMaximumImageDimensionPx || height > kMaximumImageDimensionPx ||
        static_cast<std::uint64_t>(width) * height > kMaximumImagePixels)
    {
        throw std::invalid_argument("PNG dimensions exceed the supported limit.");
    }
    return {width, height};
}

[[nodiscard]] bool is_external_reference(const std::wstring_view value)
{
    const std::wstring normalized = lower_copy(value);
    if (normalized.empty() || normalized.front() == L'#')
    {
        return false;
    }
    return true;
}

[[nodiscard]] std::optional<float> parse_svg_dimension(const std::wstring_view value)
{
    std::wstring normalized(value);
    while (!normalized.empty() && std::iswspace(normalized.back()))
    {
        normalized.pop_back();
    }
    if (normalized.ends_with(L"px"))
    {
        normalized.resize(normalized.size() - 2U);
    }
    if (normalized.empty())
    {
        return std::nullopt;
    }
    wchar_t *end = nullptr;
    const float parsed = std::wcstof(normalized.c_str(), &end);
    if (end == normalized.c_str() || *end != L'\0' || !std::isfinite(parsed) || parsed <= 0.0F ||
        parsed > static_cast<float>(kMaximumImageDimensionPx))
    {
        return std::nullopt;
    }
    return parsed;
}

[[nodiscard]] std::pair<std::uint32_t, std::uint32_t> validate_svg(const std::vector<std::uint8_t> &bytes)
{
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, bytes.size());
    if (memory == nullptr)
    {
        throw std::bad_alloc();
    }
    void *destination = GlobalLock(memory);
    if (destination == nullptr)
    {
        GlobalFree(memory);
        throw std::bad_alloc();
    }
    std::memcpy(destination, bytes.data(), bytes.size());
    GlobalUnlock(memory);

    ComPtr<IStream> stream;
    if (FAILED(CreateStreamOnHGlobal(memory, TRUE, &stream)))
    {
        GlobalFree(memory);
        throw std::runtime_error("SVG validation stream could not be created.");
    }

    ComPtr<IXmlReader> reader;
    if (FAILED(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void **>(reader.GetAddressOf()), nullptr)) ||
        FAILED(reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit)) ||
        FAILED(reader->SetInput(stream.Get())))
    {
        throw std::invalid_argument("SVG XML reader could not be initialized.");
    }

    bool root_seen = false;
    std::uint32_t element_count = 0U;
    std::uint32_t depth = 0U;
    float width = 100.0F;
    float height = 100.0F;
    XmlNodeType node_type = XmlNodeType_None;
    HRESULT result = S_OK;
    while (S_OK == (result = reader->Read(&node_type)))
    {
        if (node_type != XmlNodeType_Element)
        {
            if (node_type == XmlNodeType_EndElement && depth > 0U)
            {
                --depth;
            }
            continue;
        }
        if (++depth > 64U)
        {
            throw std::invalid_argument("SVG nesting depth exceeds the supported limit.");
        }
        if (++element_count > kMaximumSvgElements)
        {
            throw std::invalid_argument("SVG element count exceeds the supported limit.");
        }

        const wchar_t *local_name = nullptr;
        UINT local_name_length = 0U;
        if (FAILED(reader->GetLocalName(&local_name, &local_name_length)))
        {
            throw std::invalid_argument("SVG element name is invalid.");
        }
        const std::wstring element = lower_copy(std::wstring_view(local_name, local_name_length));
        if (!root_seen)
        {
            if (element != L"svg")
            {
                throw std::invalid_argument("SVG root element is invalid.");
            }
            root_seen = true;
        }
        if (element == L"script" || element == L"foreignobject")
        {
            throw std::invalid_argument("SVG contains executable or foreign content.");
        }

        if (reader->MoveToFirstAttribute() == S_OK)
        {
            do
            {
                const wchar_t *attribute_name = nullptr;
                UINT attribute_name_length = 0U;
                const wchar_t *attribute_value = nullptr;
                UINT attribute_value_length = 0U;
                if (FAILED(reader->GetLocalName(&attribute_name, &attribute_name_length)) ||
                    FAILED(reader->GetValue(&attribute_value, &attribute_value_length)))
                {
                    throw std::invalid_argument("SVG attribute is invalid.");
                }
                const std::wstring name = lower_copy(std::wstring_view(attribute_name, attribute_name_length));
                const std::wstring_view value(attribute_value, attribute_value_length);
                const std::wstring normalized_value = lower_copy(value);
                if (name.starts_with(L"on"))
                {
                    throw std::invalid_argument("SVG event attributes are not supported.");
                }
                if ((name == L"href" || name == L"src") && is_external_reference(value))
                {
                    throw std::invalid_argument("SVG external references are not supported.");
                }
                if ((name == L"style" || name == L"fill" || name == L"stroke") &&
                    normalized_value.find(L"url(") != std::wstring::npos &&
                    normalized_value.find(L"url(#") == std::wstring::npos)
                {
                    throw std::invalid_argument("SVG external paint references are not supported.");
                }
                if (element == L"svg" && name == L"width")
                {
                    width = parse_svg_dimension(value).value_or(width);
                }
                if (element == L"svg" && name == L"height")
                {
                    height = parse_svg_dimension(value).value_or(height);
                }
            } while (reader->MoveToNextAttribute() == S_OK);
            static_cast<void>(reader->MoveToElement());
        }
        if (reader->IsEmptyElement() && depth > 0U)
        {
            --depth;
        }
    }
    if (FAILED(result) || !root_seen)
    {
        throw std::invalid_argument("SVG XML is malformed.");
    }
    return {static_cast<std::uint32_t>(std::ceil(width)), static_cast<std::uint32_t>(std::ceil(height))};
}

[[nodiscard]] RenderAsset load_asset(const AssetReferenceValue &reference, const std::filesystem::path &asset_root)
{
    if (asset_root.empty())
    {
        throw std::invalid_argument("An asset root is required by the active image profile.");
    }
    const std::filesystem::path root = std::filesystem::weakly_canonical(asset_root);
    const std::filesystem::path path = std::filesystem::weakly_canonical(root / reference.file_name);
    const auto [root_end, path_end] = std::mismatch(root.begin(), root.end(), path.begin(), path.end());
    if (root_end != root.end())
    {
        throw std::invalid_argument("Asset path escapes the configured asset root.");
    }
    UNREFERENCED_PARAMETER(path_end);

    std::vector<std::uint8_t> bytes = read_asset_bytes(path, reference.size_bytes);
    if (sha256_hex(bytes) != reference.sha256)
    {
        throw std::invalid_argument("Asset SHA-256 does not match its configuration metadata.");
    }
    const auto dimensions = reference.kind == RenderAssetKind::png ? validate_png(bytes) : validate_svg(bytes);
    return {reference.id, reference.kind, reference.sha256, dimensions.first, dimensions.second, std::move(bytes)};
}

[[nodiscard]] AssetReferenceValue parse_asset_reference(const JsonObject &object)
{
    const std::filesystem::path file_name(object.GetNamedString(L"fileName").c_str());
    if (!is_simple_file_name(file_name))
    {
        throw std::invalid_argument("Asset fileName must be a simple file name.");
    }
    const std::wstring media_type = object.GetNamedString(L"mediaType").c_str();
    RenderAssetKind kind{};
    if (media_type == L"image/png")
    {
        kind = RenderAssetKind::png;
    }
    else if (media_type == L"image/svg+xml")
    {
        kind = RenderAssetKind::svg;
    }
    else
    {
        throw std::invalid_argument("Asset media type is not supported.");
    }
    const std::string hash = upper_copy(winrt::to_string(object.GetNamedString(L"sha256")));
    if (hash.size() != 64U ||
        !std::ranges::all_of(hash, [](const unsigned char character) { return std::isxdigit(character) != 0; }))
    {
        throw std::invalid_argument("Asset SHA-256 metadata is invalid.");
    }
    return {
        winrt::to_string(object.GetNamedString(L"id")),
        file_name,
        kind,
        checked_integer(object, L"sizeBytes", 1U, kMaximumAssetBytes),
        hash,
    };
}

[[nodiscard]] RenderCrosshair parse_crosshair(const JsonObject &object)
{
    const JsonObject center = object.GetNamedObject(L"center");
    const JsonArray arm_values = object.GetNamedArray(L"arms");
    if (arm_values.Size() != 4U)
    {
        throw std::invalid_argument("A crosshair must contain exactly four arms.");
    }

    RenderCrosshair result{
        parse_anchor(object.GetNamedString(L"anchor")),
        parse_offset(object.GetNamedObject(L"offsetPx")),
        center.GetNamedBoolean(L"visible"),
        parse_color(center.GetNamedString(L"color")),
        checked_number(center, L"radiusPx", 0.0F, kMaximumStrokePx),
        {},
        {},
    };
    for (std::uint32_t index = 0U; index < arm_values.Size(); ++index)
    {
        const JsonObject arm = arm_values.GetObjectAt(index);
        result.arms[index] = {
            kDefaultOrbitAnglesDeg[index] + checked_number(arm, L"orbitAngleOffsetDeg", -720.0F, 720.0F),
            checked_number(arm, L"rotationAngleOffsetDeg", -720.0F, 720.0F),
            checked_number(arm, L"gapPx", -10'000.0F, 10'000.0F),
            checked_number(arm, L"lengthPx", 0.0F, 10'000.0F),
            checked_number(arm, L"widthPx", 1.0F, 1'000.0F),
            arm.GetNamedBoolean(L"visible"),
        };
        result.arm_colors[index] = parse_color(arm.GetNamedString(L"color"));
    }
    return result;
}

[[nodiscard]] RenderToastConfiguration parse_toasts(const JsonObject &object)
{
    const std::wstring family = object.GetNamedString(L"fontFamily").c_str();
    if (family.empty() || family.size() > 100U)
    {
        throw std::invalid_argument("Toast font family is invalid.");
    }
    return {
        object.GetNamedBoolean(L"enabled"),
        parse_toast_position(object.GetNamedString(L"position")),
        static_cast<std::uint32_t>(checked_integer(object, L"durationMs", 100U, 60'000U)),
        family,
        checked_number(object, L"fontSizePx", 6.0F, 200.0F),
        parse_color(object.GetNamedString(L"foreground")),
        parse_color(object.GetNamedString(L"background")),
    };
}

[[nodiscard]] RenderConfiguration parse_active_profile(
    const JsonObject &root, const std::unordered_map<std::string, AssetReferenceValue> &assets,
    const std::filesystem::path &asset_root)
{
    const JsonArray profiles = root.GetNamedArray(L"profiles");
    if (profiles.Size() == 0U)
    {
        throw std::invalid_argument("At least one profile is required for rendering.");
    }

    std::string selected_id;
    const std::string active_set_id = winrt::to_string(root.GetNamedString(L"activeProfileSetId"));
    const JsonArray sets = root.GetNamedArray(L"profileSets");
    for (const auto &item : sets)
    {
        const JsonObject profile_set = item.GetObject();
        if (winrt::to_string(profile_set.GetNamedString(L"id")) != active_set_id)
        {
            continue;
        }
        const IJsonValue selected = profile_set.GetNamedValue(L"selectedProfileId");
        if (selected.ValueType() == JsonValueType::String)
        {
            selected_id = winrt::to_string(selected.GetString());
        }
        break;
    }
    JsonObject selected_profile = profiles.GetObjectAt(0U);
    if (!selected_id.empty())
    {
        bool found = false;
        for (const auto &item : profiles)
        {
            const JsonObject profile = item.GetObject();
            if (winrt::to_string(profile.GetNamedString(L"id")) == selected_id)
            {
                selected_profile = profile;
                found = true;
                break;
            }
        }
        if (!found)
        {
            throw std::invalid_argument("Selected profile does not exist.");
        }
    }

    const std::wstring mode_name = selected_profile.GetNamedString(L"activeMode").c_str();
    RenderMode mode{};
    if (mode_name == L"crosshair")
    {
        mode = RenderMode::crosshair;
    }
    else if (mode_name == L"image")
    {
        mode = RenderMode::image;
    }
    else
    {
        throw std::invalid_argument("Active render mode is invalid.");
    }

    const JsonObject image = selected_profile.GetNamedObject(L"image");
    RenderImage render_image{
        parse_anchor(image.GetNamedString(L"anchor")),
        parse_offset(image.GetNamedObject(L"offsetPx")),
        checked_number(image, L"scale", 0.01F, kMaximumScale),
        image.GetNamedBoolean(L"keepAspectRatio"),
        std::nullopt,
    };
    const IJsonValue asset_id = image.GetNamedValue(L"assetId");
    if (mode == RenderMode::image)
    {
        if (asset_id.ValueType() != JsonValueType::String)
        {
            throw std::invalid_argument("Image mode requires an assetId.");
        }
        const std::string id = winrt::to_string(asset_id.GetString());
        const auto reference = assets.find(id);
        if (reference == assets.end())
        {
            throw std::invalid_argument("Image mode references an unknown asset.");
        }
        render_image.asset = load_asset(reference->second, asset_root);
    }

    return {
        winrt::to_string(selected_profile.GetNamedString(L"id")),
        mode,
        parse_crosshair(selected_profile.GetNamedObject(L"crosshair")),
        std::move(render_image),
        parse_toasts(root.GetNamedObject(L"toasts")),
    };
}
} // namespace

RenderConfiguration parse_render_configuration(const std::string_view snapshot_json,
                                               const std::filesystem::path &asset_root)
{
    const JsonObject root = JsonObject::Parse(winrt::to_hstring(snapshot_json));
    if (checked_integer(root, L"schemaVersion", 8U, 8U) != 8U)
    {
        throw std::invalid_argument("Configuration schema version is not supported.");
    }

    std::unordered_map<std::string, AssetReferenceValue> assets;
    for (const auto &item : root.GetNamedArray(L"assets"))
    {
        AssetReferenceValue reference = parse_asset_reference(item.GetObject());
        if (!assets.emplace(reference.id, std::move(reference)).second)
        {
            throw std::invalid_argument("Asset identifiers must be unique.");
        }
    }
    return parse_active_profile(root, assets, asset_root);
}

RenderConfiguration make_default_render_configuration()
{
    constexpr RenderColor white{255U, 255U, 255U, 255U};
    const std::array<CrosshairArmDefinition, 4> arms{
        CrosshairArmDefinition{0.0F, 0.0F, 6.0F, 12.0F, 2.0F, true},
        CrosshairArmDefinition{90.0F, 0.0F, 6.0F, 12.0F, 2.0F, true},
        CrosshairArmDefinition{180.0F, 0.0F, 6.0F, 12.0F, 2.0F, true},
        CrosshairArmDefinition{270.0F, 0.0F, 6.0F, 12.0F, 2.0F, true},
    };
    return {
        "built-in-default",
        RenderMode::crosshair,
        {RenderAnchor::screen_center, {0.0F, 0.0F}, true, white, 2.0F, arms, {white, white, white, white}},
        {RenderAnchor::screen_center, {0.0F, 0.0F}, 1.0F, true, std::nullopt},
        {true, RenderToastPosition::top_center, 1'500U, L"Segoe UI", 18.0F, white, {0U, 0U, 0U, 180U}},
    };
}

RECT calculate_render_visual_bounds(const RECT &monitor_bounds_px, const RenderConfiguration &configuration) noexcept
{
    if (configuration.mode == RenderMode::crosshair)
    {
        return calculate_crosshair_visual_bounds(
            monitor_bounds_px, configuration.crosshair.anchor == RenderAnchor::screen_center,
            configuration.crosshair.offset_px, configuration.crosshair.center_visible,
            configuration.crosshair.center_radius_px, configuration.crosshair.arms);
    }

    if (!configuration.image.asset)
    {
        return {monitor_bounds_px.left, monitor_bounds_px.top, monitor_bounds_px.left + 1L, monitor_bounds_px.top + 1L};
    }
    const LONG monitor_width = monitor_bounds_px.right - monitor_bounds_px.left;
    const LONG monitor_height = monitor_bounds_px.bottom - monitor_bounds_px.top;
    const float anchor_x =
        configuration.image.anchor == RenderAnchor::screen_center ? static_cast<float>(monitor_width) / 2.0F : 0.0F;
    const float anchor_y =
        configuration.image.anchor == RenderAnchor::screen_center ? static_cast<float>(monitor_height) / 2.0F : 0.0F;
    const float width = static_cast<float>(configuration.image.asset->width_px) * configuration.image.scale;
    const float height = static_cast<float>(configuration.image.asset->height_px) * configuration.image.scale;
    const float center_x = anchor_x + configuration.image.offset_px.x;
    const float center_y = anchor_y + configuration.image.offset_px.y;
    constexpr float padding = 2.0F;
    const LONG left = monitor_bounds_px.left + static_cast<LONG>(std::floor(center_x - width / 2.0F - padding));
    const LONG top = monitor_bounds_px.top + static_cast<LONG>(std::floor(center_y - height / 2.0F - padding));
    const LONG right = monitor_bounds_px.left + static_cast<LONG>(std::ceil(center_x + width / 2.0F + padding));
    const LONG bottom = monitor_bounds_px.top + static_cast<LONG>(std::ceil(center_y + height / 2.0F + padding));
    return {
        (std::clamp)(left, monitor_bounds_px.left, monitor_bounds_px.right - 1L),
        (std::clamp)(top, monitor_bounds_px.top, monitor_bounds_px.bottom - 1L),
        (std::clamp)(right, monitor_bounds_px.left + 1L, monitor_bounds_px.right),
        (std::clamp)(bottom, monitor_bounds_px.top + 1L, monitor_bounds_px.bottom),
    };
}
} // namespace external_peepsight

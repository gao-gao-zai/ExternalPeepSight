#include "prototype_app.h"

#include "prototype_contracts.h"

#include <d2d1_1.h>
#include <d2d1helper.h>
#include <d3d11.h>
#include <dcomp.h>
#include <dxgi1_2.h>
#include <psapi.h>
#include <shellapi.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace external_peepsight
{
namespace
{
using Microsoft::WRL::ComPtr;

constexpr wchar_t kControllerClassName[] = L"ExternalPeepSight.Prototype.Controller";
constexpr wchar_t kOverlayClassName[] = L"ExternalPeepSight.Prototype.Overlay";
constexpr UINT kRebuildMonitorsMessage = WM_APP + 1U;
constexpr UINT kForegroundChangedMessage = WM_APP + 2U;
constexpr UINT_PTR kMetricsTimerId = 1U;
constexpr UINT_PTR kShutdownTimerId = 2U;
constexpr int kExitHotkeyId = 1;
constexpr UINT kSmokeTestDurationMs = 1'500U;
constexpr UINT kMetricsIntervalMs = 1'000U;

constexpr std::array<std::uint8_t, 153> kPrototypePng{
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

std::atomic<HWND> g_foreground_hook_target{nullptr};

class HResultError final : public std::runtime_error
{
  public:
    HResultError(const HRESULT result, const char *operation) : std::runtime_error(operation), result_(result) {}

    [[nodiscard]] HRESULT result() const noexcept
    {
        return result_;
    }

  private:
    HRESULT result_;
};

void throw_if_failed(const HRESULT result, const char *operation)
{
    if (FAILED(result))
    {
        throw HResultError(result, operation);
    }
}

[[noreturn]] void throw_last_error(const char *operation)
{
    throw HResultError(HRESULT_FROM_WIN32(GetLastError()), operation);
}

[[nodiscard]] std::uint64_t file_time_value(const FILETIME value) noexcept
{
    ULARGE_INTEGER converted{};
    converted.LowPart = value.dwLowDateTime;
    converted.HighPart = value.dwHighDateTime;
    return converted.QuadPart;
}

enum class DisplayMode
{
    focus_monitor,
    all_monitors,
};

struct PrototypeOptions
{
    DisplayMode display_mode = DisplayMode::focus_monitor;
    UINT automatic_exit_ms = 0U;
    std::filesystem::path metrics_output;
    bool suppress_dialogs = false;
};

[[nodiscard]] UINT parse_duration_seconds(const std::wstring_view value)
{
    std::wstring buffer(value);
    wchar_t *end = nullptr;
    const unsigned long seconds = std::wcstoul(buffer.c_str(), &end, 10);
    if (end == buffer.c_str() || *end != L'\0' || seconds == 0UL || seconds > 86'400UL)
    {
        throw std::invalid_argument("Invalid --diagnostics-seconds value.");
    }

    return static_cast<UINT>(seconds * 1'000UL);
}

[[nodiscard]] PrototypeOptions parse_options()
{
    int argument_count = 0;
    wchar_t **arguments = CommandLineToArgvW(GetCommandLineW(), &argument_count);
    if (arguments == nullptr)
    {
        throw_last_error("CommandLineToArgvW");
    }

    PrototypeOptions options;
    try
    {
        for (int index = 1; index < argument_count; ++index)
        {
            const std::wstring_view argument(arguments[index]);
            if (argument == L"--focus-monitor")
            {
                options.display_mode = DisplayMode::focus_monitor;
            }
            else if (argument == L"--all-monitors")
            {
                options.display_mode = DisplayMode::all_monitors;
            }
            else if (argument == L"--smoke-test")
            {
                options.automatic_exit_ms = kSmokeTestDurationMs;
                options.suppress_dialogs = true;
            }
            else if (argument.starts_with(L"--diagnostics-seconds="))
            {
                const std::wstring_view value = argument.substr(std::wstring_view(L"--diagnostics-seconds=").size());
                options.automatic_exit_ms = parse_duration_seconds(value);
            }
            else if (argument.starts_with(L"--metrics-output="))
            {
                const std::wstring_view value = argument.substr(std::wstring_view(L"--metrics-output=").size());
                if (value.empty())
                {
                    throw std::invalid_argument("Metrics output path cannot be empty.");
                }
                options.metrics_output = std::filesystem::path(value);
            }
            else
            {
                throw std::invalid_argument("Unknown prototype command-line argument.");
            }
        }
    }
    catch (...)
    {
        LocalFree(arguments);
        throw;
    }

    LocalFree(arguments);
    return options;
}

struct MonitorDescriptor
{
    HMONITOR handle = nullptr;
    RECT bounds{};
    std::wstring device_name;
};

BOOL CALLBACK enumerate_monitor(_In_ HMONITOR monitor, _In_opt_ HDC device_context, _In_ LPRECT monitor_rect,
                                _In_ LPARAM context)
{
    UNREFERENCED_PARAMETER(device_context);
    UNREFERENCED_PARAMETER(monitor_rect);

    auto &monitors = *reinterpret_cast<std::vector<MonitorDescriptor> *>(context);
    MONITORINFOEXW information{};
    information.cbSize = sizeof(information);
    if (!GetMonitorInfoW(monitor, &information))
    {
        return FALSE;
    }

    monitors.push_back({monitor, information.rcMonitor, information.szDevice});
    return TRUE;
}

[[nodiscard]] std::vector<MonitorDescriptor> enumerate_monitors()
{
    std::vector<MonitorDescriptor> monitors;
    if (!EnumDisplayMonitors(nullptr, nullptr, enumerate_monitor, reinterpret_cast<LPARAM>(&monitors)))
    {
        throw_last_error("EnumDisplayMonitors");
    }

    std::ranges::sort(monitors,
                      [](const MonitorDescriptor &left, const MonitorDescriptor &right)
                      {
                          if (left.bounds.top != right.bounds.top)
                          {
                              return left.bounds.top < right.bounds.top;
                          }
                          return left.bounds.left < right.bounds.left;
                      });

    if (monitors.empty())
    {
        throw std::runtime_error("No display monitors were enumerated.");
    }

    return monitors;
}

struct PresentStatistics
{
    std::uint64_t count = 0U;
    double total_microseconds = 0.0;
    double maximum_microseconds = 0.0;
};

struct SurfaceResources
{
    ComPtr<IDXGISwapChain1> swap_chain;
    ComPtr<IDCompositionTarget> composition_target;
    ComPtr<IDCompositionVisual> visual;
    ComPtr<ID2D1Bitmap1> target_bitmap;
};

class GraphicsDevice
{
  public:
    void initialize()
    {
        reset();

        constexpr UINT creation_flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        constexpr std::array feature_levels{
            D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL_10_0,
        };

        D3D_FEATURE_LEVEL selected_level{};
        HRESULT result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, creation_flags,
                                           feature_levels.data(), static_cast<UINT>(feature_levels.size()),
                                           D3D11_SDK_VERSION, &d3d_device_, &selected_level, &d3d_context_);
        if (FAILED(result))
        {
            result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, creation_flags, feature_levels.data(),
                                       static_cast<UINT>(feature_levels.size()), D3D11_SDK_VERSION, &d3d_device_,
                                       &selected_level, &d3d_context_);
        }
        throw_if_failed(result, "D3D11CreateDevice");
        UNREFERENCED_PARAMETER(selected_level);

        ComPtr<IDXGIDevice> dxgi_device;
        throw_if_failed(d3d_device_.As(&dxgi_device), "Query IDXGIDevice");

        ComPtr<IDXGIDevice1> dxgi_device_one;
        if (SUCCEEDED(dxgi_device.As(&dxgi_device_one)))
        {
            throw_if_failed(dxgi_device_one->SetMaximumFrameLatency(1U), "IDXGIDevice1::SetMaximumFrameLatency");
        }

        ComPtr<IDXGIAdapter> adapter;
        throw_if_failed(dxgi_device->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
        throw_if_failed(adapter->GetParent(IID_PPV_ARGS(&dxgi_factory_)), "IDXGIAdapter::GetParent");

        D2D1_FACTORY_OPTIONS factory_options{};
        throw_if_failed(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), &factory_options,
                                          reinterpret_cast<void **>(d2d_factory_.GetAddressOf())),
                        "D2D1CreateFactory");
        throw_if_failed(d2d_factory_->CreateDevice(dxgi_device.Get(), &d2d_device_), "ID2D1Factory1::CreateDevice");
        throw_if_failed(d2d_device_->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2d_context_),
                        "ID2D1Device::CreateDeviceContext");

        throw_if_failed(DCompositionCreateDevice(dxgi_device.Get(), __uuidof(IDCompositionDevice),
                                                 reinterpret_cast<void **>(composition_device_.GetAddressOf())),
                        "DCompositionCreateDevice");

        result = CoCreateInstance(CLSID_WICImagingFactory2, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic_factory_));
        if (result == REGDB_E_CLASSNOTREG)
        {
            result =
                CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic_factory_));
        }
        throw_if_failed(result, "Create WIC imaging factory");

        throw_if_failed(d2d_context_->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::Black, 0.85F), &outline_brush_),
                        "Create outline brush");
        throw_if_failed(d2d_context_->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), &crosshair_brush_),
                        "Create crosshair brush");
        create_prototype_bitmap();
    }

    void reset() noexcept
    {
        if (d2d_context_)
        {
            d2d_context_->SetTarget(nullptr);
        }
        prototype_bitmap_.Reset();
        crosshair_brush_.Reset();
        outline_brush_.Reset();
        wic_factory_.Reset();
        composition_device_.Reset();
        d2d_context_.Reset();
        d2d_device_.Reset();
        d2d_factory_.Reset();
        dxgi_factory_.Reset();
        d3d_context_.Reset();
        d3d_device_.Reset();
    }

    [[nodiscard]] SurfaceResources create_surface(_In_ HWND window, const UINT width_px, const UINT height_px)
    {
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Width = width_px;
        description.Height = height_px;
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.Stereo = FALSE;
        description.SampleDesc.Count = 1U;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.BufferCount = 2U;
        description.Scaling = DXGI_SCALING_STRETCH;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        description.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;

        SurfaceResources surface;
        throw_if_failed(
            dxgi_factory_->CreateSwapChainForComposition(d3d_device_.Get(), &description, nullptr, &surface.swap_chain),
            "IDXGIFactory2::CreateSwapChainForComposition");
        throw_if_failed(composition_device_->CreateTargetForHwnd(window, TRUE, &surface.composition_target),
                        "IDCompositionDevice::CreateTargetForHwnd");
        throw_if_failed(composition_device_->CreateVisual(&surface.visual), "IDCompositionDevice::CreateVisual");
        throw_if_failed(surface.visual->SetContent(surface.swap_chain.Get()), "IDCompositionVisual::SetContent");
        throw_if_failed(surface.composition_target->SetRoot(surface.visual.Get()), "IDCompositionTarget::SetRoot");
        throw_if_failed(composition_device_->Commit(), "IDCompositionDevice::Commit");

        create_target_bitmap(surface);
        render(surface, width_px, height_px);
        return surface;
    }

    void release_surface(SurfaceResources &surface) noexcept
    {
        if (d2d_context_)
        {
            d2d_context_->SetTarget(nullptr);
        }
        surface.target_bitmap.Reset();
        surface.visual.Reset();
        surface.composition_target.Reset();
        surface.swap_chain.Reset();
    }

    [[nodiscard]] PresentStatistics present_statistics() const noexcept
    {
        return present_statistics_;
    }

  private:
    void create_prototype_bitmap()
    {
        std::vector<std::uint8_t> mutable_png(kPrototypePng.begin(), kPrototypePng.end());

        ComPtr<IWICStream> stream;
        throw_if_failed(wic_factory_->CreateStream(&stream), "IWICImagingFactory::CreateStream");
        throw_if_failed(stream->InitializeFromMemory(mutable_png.data(), static_cast<DWORD>(mutable_png.size())),
                        "IWICStream::InitializeFromMemory");

        ComPtr<IWICBitmapDecoder> decoder;
        throw_if_failed(
            wic_factory_->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad, &decoder),
            "IWICImagingFactory::CreateDecoderFromStream");

        ComPtr<IWICBitmapFrameDecode> frame;
        throw_if_failed(decoder->GetFrame(0U, &frame), "IWICBitmapDecoder::GetFrame");

        ComPtr<IWICFormatConverter> converter;
        throw_if_failed(wic_factory_->CreateFormatConverter(&converter), "IWICImagingFactory::CreateFormatConverter");
        throw_if_failed(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone,
                                              nullptr, 0.0, WICBitmapPaletteTypeCustom),
                        "IWICFormatConverter::Initialize");
        throw_if_failed(d2d_context_->CreateBitmapFromWicBitmap(converter.Get(), nullptr, &prototype_bitmap_),
                        "ID2D1DeviceContext::CreateBitmapFromWicBitmap");
    }

    void create_target_bitmap(SurfaceResources &surface)
    {
        ComPtr<IDXGISurface> dxgi_surface;
        throw_if_failed(surface.swap_chain->GetBuffer(0U, IID_PPV_ARGS(&dxgi_surface)), "IDXGISwapChain1::GetBuffer");

        const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0F, 96.0F);
        throw_if_failed(
            d2d_context_->CreateBitmapFromDxgiSurface(dxgi_surface.Get(), &properties, &surface.target_bitmap),
            "ID2D1DeviceContext::CreateBitmapFromDxgiSurface");
    }

    [[nodiscard]] HRESULT draw_surface(SurfaceResources &surface, const UINT width_px, const UINT height_px)
    {
        d2d_context_->SetTarget(surface.target_bitmap.Get());
        d2d_context_->SetTransform(D2D1::Matrix3x2F::Identity());
        d2d_context_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        d2d_context_->BeginDraw();
        d2d_context_->Clear(D2D1::ColorF(0.0F, 0.0F, 0.0F, 0.0F));

        const CrosshairGeometry geometry =
            calculate_crosshair_geometry(static_cast<float>(width_px), static_cast<float>(height_px), 7.0F, 14.0F);
        for (const LineSegmentPx &arm : geometry.arms)
        {
            const D2D1_POINT_2F start = D2D1::Point2F(arm.start.x, arm.start.y);
            const D2D1_POINT_2F end = D2D1::Point2F(arm.end.x, arm.end.y);
            d2d_context_->DrawLine(start, end, outline_brush_.Get(), 4.0F);
            d2d_context_->DrawLine(start, end, crosshair_brush_.Get(), 2.0F);
        }

        const D2D1_ELLIPSE outline = D2D1::Ellipse(D2D1::Point2F(geometry.center.x, geometry.center.y), 3.5F, 3.5F);
        const D2D1_ELLIPSE center = D2D1::Ellipse(D2D1::Point2F(geometry.center.x, geometry.center.y), 2.0F, 2.0F);
        d2d_context_->FillEllipse(outline, outline_brush_.Get());
        d2d_context_->FillEllipse(center, crosshair_brush_.Get());

        const D2D1_RECT_F image_destination = D2D1::RectF(geometry.center.x + 28.0F, geometry.center.y - 12.0F,
                                                          geometry.center.x + 52.0F, geometry.center.y + 12.0F);
        d2d_context_->DrawBitmap(prototype_bitmap_.Get(), image_destination, 1.0F,
                                 D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR);

        const HRESULT result = d2d_context_->EndDraw();
        d2d_context_->SetTarget(nullptr);
        return result;
    }

    void render(SurfaceResources &surface, const UINT width_px, const UINT height_px)
    {
        HRESULT result = draw_surface(surface, width_px, height_px);
        if (result == D2DERR_RECREATE_TARGET)
        {
            surface.target_bitmap.Reset();
            create_target_bitmap(surface);
            result = draw_surface(surface, width_px, height_px);
        }
        throw_if_failed(result, "ID2D1DeviceContext::EndDraw");

        LARGE_INTEGER before{};
        LARGE_INTEGER after{};
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&before);

        DXGI_PRESENT_PARAMETERS parameters{};
        result = surface.swap_chain->Present1(1U, 0U, &parameters);

        QueryPerformanceCounter(&after);
        if (result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET)
        {
            throw HResultError(result, "IDXGISwapChain1::Present1 device lost");
        }
        throw_if_failed(result, "IDXGISwapChain1::Present1");
        throw_if_failed(composition_device_->Commit(), "IDCompositionDevice::Commit");

        const double elapsed_microseconds = static_cast<double>(after.QuadPart - before.QuadPart) * 1'000'000.0 /
                                            static_cast<double>(frequency.QuadPart);
        ++present_statistics_.count;
        present_statistics_.total_microseconds += elapsed_microseconds;
        present_statistics_.maximum_microseconds =
            (std::max)(present_statistics_.maximum_microseconds, elapsed_microseconds);
    }

    ComPtr<ID3D11Device> d3d_device_;
    ComPtr<ID3D11DeviceContext> d3d_context_;
    ComPtr<IDXGIFactory2> dxgi_factory_;
    ComPtr<ID2D1Factory1> d2d_factory_;
    ComPtr<ID2D1Device> d2d_device_;
    ComPtr<ID2D1DeviceContext> d2d_context_;
    ComPtr<IDCompositionDevice> composition_device_;
    ComPtr<IWICImagingFactory> wic_factory_;
    ComPtr<ID2D1SolidColorBrush> outline_brush_;
    ComPtr<ID2D1SolidColorBrush> crosshair_brush_;
    ComPtr<ID2D1Bitmap1> prototype_bitmap_;
    PresentStatistics present_statistics_;
};

class OverlayWindow
{
  public:
    OverlayWindow(_In_ HINSTANCE instance, _In_ HWND controller, GraphicsDevice &graphics,
                  const MonitorDescriptor &monitor)
        : controller_(controller), graphics_(graphics), monitor_(monitor)
    {
        const int width = monitor_.bounds.right - monitor_.bounds.left;
        const int height = monitor_.bounds.bottom - monitor_.bounds.top;
        window_ =
            CreateWindowExW(overlay_extended_style(), kOverlayClassName, L"", overlay_window_style(),
                            monitor_.bounds.left, monitor_.bounds.top, width, height, nullptr, nullptr, instance, this);
        if (window_ == nullptr)
        {
            throw_last_error("Create overlay window");
        }
        if (!SetLayeredWindowAttributes(window_, 0U, 255U, LWA_ALPHA))
        {
            throw_last_error("SetLayeredWindowAttributes");
        }

        surface_ = graphics_.create_surface(window_, static_cast<UINT>(width), static_cast<UINT>(height));
    }

    OverlayWindow(const OverlayWindow &) = delete;
    OverlayWindow &operator=(const OverlayWindow &) = delete;

    ~OverlayWindow()
    {
        graphics_.release_surface(surface_);
        if (window_ != nullptr)
        {
            DestroyWindow(window_);
        }
    }

    [[nodiscard]] HMONITOR monitor_handle() const noexcept
    {
        return monitor_.handle;
    }

    void set_visible(const bool visible)
    {
        if (!visible)
        {
            ShowWindow(window_, SW_HIDE);
            visible_ = false;
            return;
        }

        const int width = monitor_.bounds.right - monitor_.bounds.left;
        const int height = monitor_.bounds.bottom - monitor_.bounds.top;
        if (!SetWindowPos(window_, HWND_TOPMOST, monitor_.bounds.left, monitor_.bounds.top, width, height,
                          SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW))
        {
            throw_last_error("SetWindowPos overlay");
        }
        ShowWindow(window_, SW_SHOWNOACTIVATE);
        visible_ = true;
    }

    void reassert_topmost()
    {
        if (!visible_)
        {
            return;
        }

        if (!SetWindowPos(window_, HWND_TOPMOST, 0, 0, 0, 0,
                          SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
        {
            throw_last_error("Reassert overlay TOPMOST");
        }
    }

    static LRESULT CALLBACK window_proc(_In_ HWND window, _In_ UINT message, _In_ WPARAM word_parameter,
                                        _In_ LPARAM long_parameter) noexcept
    {
        OverlayWindow *overlay = reinterpret_cast<OverlayWindow *>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            const auto *create = reinterpret_cast<const CREATESTRUCTW *>(long_parameter);
            overlay = static_cast<OverlayWindow *>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(overlay));
        }

        switch (message)
        {
        case WM_NCHITTEST:
            return overlay_hit_test_result();
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATEANDEAT;
        case WM_ERASEBKGND:
            return 1;
        case WM_PAINT:
        {
            PAINTSTRUCT paint{};
            BeginPaint(window, &paint);
            EndPaint(window, &paint);
            return 0;
        }
        case WM_DISPLAYCHANGE:
        case WM_DPICHANGED:
        case WM_DEVICECHANGE:
            if (overlay != nullptr)
            {
                PostMessageW(overlay->controller_, kRebuildMonitorsMessage, 0U, 0);
            }
            return 0;
        case WM_CLOSE:
            return 0;
        default:
            return DefWindowProcW(window, message, word_parameter, long_parameter);
        }
    }

  private:
    HWND controller_ = nullptr;
    GraphicsDevice &graphics_;
    MonitorDescriptor monitor_;
    HWND window_ = nullptr;
    SurfaceResources surface_;
    bool visible_ = false;
};

class MetricsRecorder
{
  public:
    void initialize(const std::filesystem::path &output)
    {
        if (output.empty())
        {
            return;
        }

        const std::filesystem::path parent = output.parent_path();
        if (!parent.empty())
        {
            std::filesystem::create_directories(parent);
        }
        output_.open(output, std::ios::out | std::ios::trunc);
        if (!output_)
        {
            throw std::runtime_error("Unable to open metrics output.");
        }

        output_ << "elapsed_ms,cpu_percent_normalized,working_set_bytes,private_working_set_bytes,private_bytes,"
                   "present_count,"
                   "average_present_us,max_present_us\n";

        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        frequency_ = frequency.QuadPart;
        QueryPerformanceCounter(&start_counter_);
        previous_counter_ = start_counter_;

        FILETIME creation{};
        FILETIME exit{};
        FILETIME kernel{};
        FILETIME user{};
        if (!GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user))
        {
            throw_last_error("GetProcessTimes");
        }
        previous_process_time_ = file_time_value(kernel) + file_time_value(user);
        processor_count_ = (std::max)(DWORD{1U}, GetActiveProcessorCount(ALL_PROCESSOR_GROUPS));
    }

    void sample(const PresentStatistics &present_statistics)
    {
        if (!output_)
        {
            return;
        }

        LARGE_INTEGER current_counter{};
        QueryPerformanceCounter(&current_counter);

        FILETIME creation{};
        FILETIME exit{};
        FILETIME kernel{};
        FILETIME user{};
        if (!GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user))
        {
            throw_last_error("GetProcessTimes");
        }

        PROCESS_MEMORY_COUNTERS_EX2 memory{};
        memory.cb = sizeof(memory);
        if (!K32GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&memory),
                                     sizeof(memory)))
        {
            throw_last_error("K32GetProcessMemoryInfo");
        }

        const std::uint64_t process_time = file_time_value(kernel) + file_time_value(user);
        const double elapsed_seconds = static_cast<double>(current_counter.QuadPart - previous_counter_.QuadPart) /
                                       static_cast<double>(frequency_);
        const double process_seconds = static_cast<double>(process_time - previous_process_time_) / 10'000'000.0;
        const double normalized_cpu =
            elapsed_seconds > 0.0 ? process_seconds * 100.0 / elapsed_seconds / static_cast<double>(processor_count_)
                                  : 0.0;
        const double elapsed_milliseconds = static_cast<double>(current_counter.QuadPart - start_counter_.QuadPart) *
                                            1'000.0 / static_cast<double>(frequency_);
        const double average_present =
            present_statistics.count == 0U
                ? 0.0
                : present_statistics.total_microseconds / static_cast<double>(present_statistics.count);

        output_ << std::fixed << std::setprecision(3) << elapsed_milliseconds << ',' << normalized_cpu << ','
                << memory.WorkingSetSize << ',' << memory.PrivateWorkingSetSize << ',' << memory.PrivateUsage << ','
                << present_statistics.count << ',' << average_present << ',' << present_statistics.maximum_microseconds
                << '\n';
        output_.flush();

        previous_counter_ = current_counter;
        previous_process_time_ = process_time;
    }

  private:
    std::ofstream output_;
    LARGE_INTEGER start_counter_{};
    LARGE_INTEGER previous_counter_{};
    std::int64_t frequency_ = 0;
    std::uint64_t previous_process_time_ = 0U;
    DWORD processor_count_ = 1U;
};

class OverlayApplication
{
  public:
    OverlayApplication(_In_ HINSTANCE instance, PrototypeOptions options)
        : instance_(instance), options_(std::move(options))
    {
    }

    OverlayApplication(const OverlayApplication &) = delete;
    OverlayApplication &operator=(const OverlayApplication &) = delete;

    ~OverlayApplication()
    {
        shutdown();
    }

    [[nodiscard]] int run()
    {
        initialize();

        MSG message{};
        while (true)
        {
            const BOOL result = GetMessageW(&message, nullptr, 0U, 0U);
            if (result == -1)
            {
                throw_last_error("GetMessage");
            }
            if (result == 0)
            {
                break;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        metrics_.sample(graphics_.present_statistics());
        return exit_code_;
    }

    static LRESULT CALLBACK controller_window_proc(_In_ HWND window, _In_ UINT message, _In_ WPARAM word_parameter,
                                                   _In_ LPARAM long_parameter) noexcept
    {
        OverlayApplication *application =
            reinterpret_cast<OverlayApplication *>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            const auto *create = reinterpret_cast<const CREATESTRUCTW *>(long_parameter);
            application = static_cast<OverlayApplication *>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(application));
        }

        if (application == nullptr)
        {
            return DefWindowProcW(window, message, word_parameter, long_parameter);
        }

        try
        {
            return application->handle_controller_message(window, message, word_parameter, long_parameter);
        }
        catch (const std::exception &error)
        {
            application->handle_fatal_error(error);
            return 0;
        }
    }

  private:
    void initialize()
    {
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        {
            const DWORD error = GetLastError();
            if (error != ERROR_ACCESS_DENIED)
            {
                throw HResultError(HRESULT_FROM_WIN32(error), "SetProcessDpiAwarenessContext");
            }
        }

        throw_if_failed(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED), "CoInitializeEx");
        com_initialized_ = true;

        register_window_classes();
        controller_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kControllerClassName, L"", WS_POPUP, 0, 0, 0,
                                      0, nullptr, nullptr, instance_, this);
        if (controller_ == nullptr)
        {
            throw_last_error("Create controller window");
        }

        g_foreground_hook_target.store(controller_, std::memory_order_release);
        graphics_.initialize();
        metrics_.initialize(options_.metrics_output);
        rebuild_overlays();

        foreground_hook_ =
            SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, foreground_event_callback, 0U,
                            0U, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (foreground_hook_ == nullptr)
        {
            throw_last_error("SetWinEventHook");
        }

        if (SetTimer(controller_, kMetricsTimerId, kMetricsIntervalMs, nullptr) == 0U)
        {
            throw_last_error("Set metrics timer");
        }
        if (options_.automatic_exit_ms != 0U &&
            SetTimer(controller_, kShutdownTimerId, options_.automatic_exit_ms, nullptr) == 0U)
        {
            throw_last_error("Set shutdown timer");
        }

        hotkey_registered_ =
            RegisterHotKey(controller_, kExitHotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_F12) != FALSE;
    }

    void register_window_classes()
    {
        WNDCLASSEXW controller_class{};
        controller_class.cbSize = sizeof(controller_class);
        controller_class.lpfnWndProc = controller_window_proc;
        controller_class.hInstance = instance_;
        controller_class.lpszClassName = kControllerClassName;
        if (RegisterClassExW(&controller_class) == 0U)
        {
            throw_last_error("Register controller window class");
        }
        controller_class_registered_ = true;

        WNDCLASSEXW overlay_class{};
        overlay_class.cbSize = sizeof(overlay_class);
        overlay_class.lpfnWndProc = OverlayWindow::window_proc;
        overlay_class.hInstance = instance_;
        overlay_class.lpszClassName = kOverlayClassName;
        if (RegisterClassExW(&overlay_class) == 0U)
        {
            throw_last_error("Register overlay window class");
        }
        overlay_class_registered_ = true;
    }

    void rebuild_overlays()
    {
        overlays_.clear();
        try
        {
            build_overlays_once();
        }
        catch (const HResultError &error)
        {
            if (error.result() != DXGI_ERROR_DEVICE_REMOVED && error.result() != DXGI_ERROR_DEVICE_RESET)
            {
                throw;
            }

            overlays_.clear();
            graphics_.initialize();
            build_overlays_once();
        }
    }

    void build_overlays_once()
    {
        for (const MonitorDescriptor &monitor : enumerate_monitors())
        {
            overlays_.push_back(std::make_unique<OverlayWindow>(instance_, controller_, graphics_, monitor));
        }
        update_overlay_visibility();
    }

    [[nodiscard]] HMONITOR active_monitor() const noexcept
    {
        HWND foreground = GetForegroundWindow();
        if (foreground != nullptr)
        {
            DWORD process_id = 0U;
            GetWindowThreadProcessId(foreground, &process_id);
            if (process_id != GetCurrentProcessId())
            {
                const HMONITOR monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONULL);
                if (monitor != nullptr)
                {
                    return monitor;
                }
            }
        }

        POINT cursor{};
        if (GetCursorPos(&cursor))
        {
            return MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        }

        return overlays_.empty() ? nullptr : overlays_.front()->monitor_handle();
    }

    void update_overlay_visibility()
    {
        const HMONITOR selected_monitor = active_monitor();
        for (const std::unique_ptr<OverlayWindow> &overlay : overlays_)
        {
            const bool visible =
                options_.display_mode == DisplayMode::all_monitors || overlay->monitor_handle() == selected_monitor;
            overlay->set_visible(visible);
        }
    }

    void reassert_topmost()
    {
        update_overlay_visibility();
        for (const std::unique_ptr<OverlayWindow> &overlay : overlays_)
        {
            overlay->reassert_topmost();
        }
    }

    LRESULT handle_controller_message(_In_ HWND window, const UINT message, const WPARAM word_parameter,
                                      const LPARAM long_parameter)
    {
        UNREFERENCED_PARAMETER(long_parameter);

        switch (message)
        {
        case kRebuildMonitorsMessage:
        case WM_DISPLAYCHANGE:
        case WM_DEVICECHANGE:
            rebuild_overlays();
            return 0;
        case kForegroundChangedMessage:
            reassert_topmost();
            return 0;
        case WM_TIMER:
            if (word_parameter == kMetricsTimerId)
            {
                metrics_.sample(graphics_.present_statistics());
            }
            else if (word_parameter == kShutdownTimerId)
            {
                DestroyWindow(window);
            }
            return 0;
        case WM_HOTKEY:
            if (word_parameter == kExitHotkeyId)
            {
                DestroyWindow(window);
            }
            return 0;
        case WM_CLOSE:
            DestroyWindow(window);
            return 0;
        case WM_DESTROY:
            controller_ = nullptr;
            PostQuitMessage(exit_code_);
            return 0;
        default:
            return DefWindowProcW(window, message, word_parameter, long_parameter);
        }
    }

    void handle_fatal_error(const std::exception &error) noexcept
    {
        OutputDebugStringA("ExternalPeepSight.Host prototype failure: ");
        OutputDebugStringA(error.what());
        OutputDebugStringA("\n");
        exit_code_ = EXIT_FAILURE;

        if (!options_.suppress_dialogs)
        {
            MessageBoxW(nullptr, L"The overlay prototype failed. See debugger output for the failing operation.",
                        L"ExternalPeepSight.Host", MB_OK | MB_ICONERROR);
        }

        if (controller_ != nullptr)
        {
            DestroyWindow(controller_);
        }
        else
        {
            PostQuitMessage(exit_code_);
        }
    }

    void shutdown() noexcept
    {
        g_foreground_hook_target.store(nullptr, std::memory_order_release);

        if (foreground_hook_ != nullptr)
        {
            UnhookWinEvent(foreground_hook_);
            foreground_hook_ = nullptr;
        }
        if (controller_ != nullptr)
        {
            KillTimer(controller_, kMetricsTimerId);
            KillTimer(controller_, kShutdownTimerId);
            if (hotkey_registered_)
            {
                UnregisterHotKey(controller_, kExitHotkeyId);
            }
        }

        overlays_.clear();
        graphics_.reset();

        if (controller_ != nullptr)
        {
            DestroyWindow(controller_);
            controller_ = nullptr;
        }
        if (overlay_class_registered_)
        {
            UnregisterClassW(kOverlayClassName, instance_);
            overlay_class_registered_ = false;
        }
        if (controller_class_registered_)
        {
            UnregisterClassW(kControllerClassName, instance_);
            controller_class_registered_ = false;
        }
        if (com_initialized_)
        {
            CoUninitialize();
            com_initialized_ = false;
        }
    }

    static void CALLBACK foreground_event_callback(_In_ HWINEVENTHOOK hook, _In_ DWORD event, _In_ HWND window,
                                                   _In_ LONG object_id, _In_ LONG child_id, _In_ DWORD event_thread_id,
                                                   _In_ DWORD event_time_ms) noexcept
    {
        UNREFERENCED_PARAMETER(hook);
        UNREFERENCED_PARAMETER(event);
        UNREFERENCED_PARAMETER(window);
        UNREFERENCED_PARAMETER(object_id);
        UNREFERENCED_PARAMETER(child_id);
        UNREFERENCED_PARAMETER(event_thread_id);
        UNREFERENCED_PARAMETER(event_time_ms);

        const HWND target = g_foreground_hook_target.load(std::memory_order_acquire);
        if (target != nullptr)
        {
            PostMessageW(target, kForegroundChangedMessage, 0U, 0);
        }
    }

    HINSTANCE instance_ = nullptr;
    PrototypeOptions options_;
    HWND controller_ = nullptr;
    HWINEVENTHOOK foreground_hook_ = nullptr;
    GraphicsDevice graphics_;
    MetricsRecorder metrics_;
    std::vector<std::unique_ptr<OverlayWindow>> overlays_;
    int exit_code_ = EXIT_SUCCESS;
    bool com_initialized_ = false;
    bool controller_class_registered_ = false;
    bool overlay_class_registered_ = false;
    bool hotkey_registered_ = false;
};
} // namespace

int run_overlay_prototype(_In_ const HINSTANCE instance)
{
    PrototypeOptions options;
    try
    {
        options = parse_options();
        OverlayApplication application(instance, options);
        return application.run();
    }
    catch (const std::exception &error)
    {
        OutputDebugStringA("ExternalPeepSight.Host startup failure: ");
        OutputDebugStringA(error.what());
        OutputDebugStringA("\n");
        if (!options.suppress_dialogs)
        {
            MessageBoxW(nullptr, L"The overlay prototype could not start. See debugger output for details.",
                        L"ExternalPeepSight.Host", MB_OK | MB_ICONERROR);
        }
        return EXIT_FAILURE;
    }
}
} // namespace external_peepsight

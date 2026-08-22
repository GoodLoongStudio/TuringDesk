#include "turingdesk/IndependentWallpaperHost.h"

#include "turingdesk/VideoWallpaperPlayer.h"

#include <d2d1.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <sstream>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace turingdesk {
namespace {

constexpr wchar_t kSurfaceClass[] = L"TuringDesk.Native.IndependentWallpaperSurface";

} // namespace

struct IndependentWallpaperHost::Impl {
    struct Slot {
        Impl* owner{};
        HWND window{};
        wallpaper::ResolvedMonitorWallpaper wallpaper;
        ComPtr<ID2D1HwndRenderTarget> renderTarget;
        ComPtr<ID2D1SolidColorBrush> brush;
        ComPtr<ID2D1Bitmap> bitmap;
        std::unique_ptr<VideoWallpaperPlayer> video;
        std::wstring error;
    };

    HWND parent{};
    wallpaper::ScaleMode scaleMode{wallpaper::ScaleMode::Cover};
    float focalX{0.5f};
    float focalY{0.5f};
    IndependentVideoSettings videoSettings;
    bool paused{};
    float time{};
    std::wstring lastError;
    ComPtr<ID2D1Factory> d2dFactory;
    ComPtr<IWICImagingFactory> wicFactory;
    std::vector<std::unique_ptr<Slot>> slots;

    bool EnsureFactories() {
        if (!d2dFactory && FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory.GetAddressOf()))) {
            lastError = L"Independent renderer 无法创建 Direct2D factory";
            return false;
        }
        if (!wicFactory && FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                                    IID_PPV_ARGS(wicFactory.GetAddressOf())))) {
            lastError = L"Independent renderer 无法创建 WIC factory";
            return false;
        }
        return true;
    }

    bool EnsureSurfaceClass() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpfnWndProc = &Impl::SurfaceProc;
        wc.lpszClassName = kSurfaceClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        if (RegisterClassExW(&wc)) return true;
        return GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
    }

    static LRESULT CALLBACK SurfaceProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* slot = reinterpret_cast<Slot*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            slot = static_cast<Slot*>(create->lpCreateParams);
            if (slot) {
                slot->window = hwnd;
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(slot));
            }
        }
        if (!slot || !slot->owner) return DefWindowProcW(hwnd, message, wParam, lParam);
        switch (message) {
        case WM_NCHITTEST:
            return HTTRANSPARENT;
        case WM_ERASEBKGND:
            return 1;
        case WM_SIZE:
            if (slot->renderTarget) slot->renderTarget->Resize(D2D1::SizeU(LOWORD(lParam), HIWORD(lParam)));
            return 0;
        case WM_PAINT: {
            PAINTSTRUCT paint{};
            BeginPaint(hwnd, &paint);
            if (slot->wallpaper.kind != wallpaper::ResolvedWallpaperKind::Video) slot->owner->DrawSlot(*slot);
            EndPaint(hwnd, &paint);
            return 0;
        }
        case WM_DESTROY:
            slot->window = nullptr;
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    bool EnsureRenderTarget(Slot& slot) {
        if (slot.renderTarget) return true;
        if (!slot.window || !d2dFactory) return false;
        RECT rc{};
        if (!GetClientRect(slot.window, &rc)) return false;
        const UINT width = static_cast<UINT>(std::max<LONG>(1, rc.right - rc.left));
        const UINT height = static_cast<UINT>(std::max<LONG>(1, rc.bottom - rc.top));
        const auto properties = D2D1::HwndRenderTargetProperties(
            slot.window, D2D1::SizeU(width, height), D2D1_PRESENT_OPTIONS_IMMEDIATELY);
        if (FAILED(d2dFactory->CreateHwndRenderTarget(D2D1::RenderTargetProperties(), properties, slot.renderTarget.GetAddressOf()))) {
            slot.error = L"创建显示器 Direct2D surface 失败";
            return false;
        }
        if (FAILED(slot.renderTarget->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), slot.brush.GetAddressOf()))) {
            slot.renderTarget.Reset();
            slot.error = L"创建显示器绘制 brush 失败";
            return false;
        }
        slot.renderTarget->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        return true;
    }

    bool LoadImage(Slot& slot) {
        if (slot.wallpaper.source.empty() || !EnsureRenderTarget(slot)) return false;
        ComPtr<IWICBitmapDecoder> decoder;
        if (FAILED(wicFactory->CreateDecoderFromFilename(slot.wallpaper.source.c_str(), nullptr, GENERIC_READ,
                                                         WICDecodeMetadataCacheOnLoad, decoder.GetAddressOf()))) return false;
        ComPtr<IWICBitmapFrameDecode> frame;
        if (FAILED(decoder->GetFrame(0, frame.GetAddressOf()))) return false;
        ComPtr<IWICFormatConverter> converter;
        if (FAILED(wicFactory->CreateFormatConverter(converter.GetAddressOf()))) return false;
        if (FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone,
                                         nullptr, 0.0, WICBitmapPaletteTypeMedianCut))) return false;
        return SUCCEEDED(slot.renderTarget->CreateBitmapFromWicBitmap(converter.Get(), nullptr, slot.bitmap.GetAddressOf()));
    }

    void FallbackSlotToAurora(Slot& slot, const std::wstring& reason) {
        if (slot.video) {
            slot.video->Stop();
            slot.video.reset();
        }
        slot.bitmap.Reset();
        slot.wallpaper.kind = wallpaper::ResolvedWallpaperKind::Scene;
        slot.wallpaper.sceneKey = L"aurora";
        slot.wallpaper.fallback = true;
        if (slot.wallpaper.fallbackReason.empty()) slot.wallpaper.fallbackReason = reason;
        slot.error = reason;
        if (slot.window) InvalidateRect(slot.window, nullptr, FALSE);
    }

    void ApplyVideoSettings(VideoWallpaperPlayer& player) const {
        player.SetLooping(videoSettings.looping);
        player.SetMuted(videoSettings.muted);
        player.SetVolume(videoSettings.volume);
        player.SetPlaybackRate(videoSettings.rate);
        player.SetScaling(scaleMode, focalX, focalY);
    }

    bool PrepareSlot(Slot& slot) {
        if (slot.wallpaper.kind == wallpaper::ResolvedWallpaperKind::Video) {
            slot.video = std::make_unique<VideoWallpaperPlayer>();
            ApplyVideoSettings(*slot.video);
            if (!slot.video->Start(slot.window, slot.wallpaper.source.wstring())) {
                const auto mediaError = slot.video->LastErrorText();
                FallbackSlotToAurora(slot, mediaError.empty() ? L"独立视频无法启动，已回退 Aurora" : mediaError);
            } else {
                slot.video->SetPaused(paused);
            }
            return true;
        }
        if (slot.wallpaper.kind == wallpaper::ResolvedWallpaperKind::Image) {
            if (!LoadImage(slot)) FallbackSlotToAurora(slot, L"独立图片无法解码，已回退 Aurora");
            return true;
        }
        return EnsureRenderTarget(slot);
    }

    void FillBackground(Slot& slot, const D2D1_SIZE_F& size, const D2D1_COLOR_F& color) {
        slot.brush->SetColor(color);
        slot.renderTarget->FillRectangle(D2D1::RectF(0.0f, 0.0f, size.width, size.height), slot.brush.Get());
    }

    void DrawImage(Slot& slot, const D2D1_SIZE_F& target) {
        if (!slot.bitmap) return;
        FillBackground(slot, target, D2D1::ColorF(0.0f, 0.0f, 0.0f));
        const auto sourceSize = slot.bitmap->GetSize();
        const auto placement = wallpaper::ComputePlacement(sourceSize.width, sourceSize.height, target.width, target.height,
                                                            scaleMode, focalX, focalY);
        const D2D1_RECT_F source = D2D1::RectF(placement.source.left, placement.source.top,
                                               placement.source.right, placement.source.bottom);
        if (placement.tiled) {
            const float tileWidth = std::max(1.0f, placement.destination.right - placement.destination.left);
            const float tileHeight = std::max(1.0f, placement.destination.bottom - placement.destination.top);
            constexpr int kMaxTileDraws = 4096;
            int draws = 0;
            for (float y = 0.0f; y < target.height && draws < kMaxTileDraws; y += tileHeight) {
                for (float x = 0.0f; x < target.width && draws < kMaxTileDraws; x += tileWidth) {
                    slot.renderTarget->DrawBitmap(slot.bitmap.Get(), D2D1::RectF(x, y, x + tileWidth, y + tileHeight),
                                                  1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, &source);
                    ++draws;
                }
            }
            return;
        }
        slot.renderTarget->DrawBitmap(slot.bitmap.Get(),
            D2D1::RectF(placement.destination.left, placement.destination.top,
                        placement.destination.right, placement.destination.bottom),
            1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, &source);
    }

    void DrawAurora(Slot& slot, const D2D1_SIZE_F& size) {
        FillBackground(slot, size, D2D1::ColorF(0.008f, 0.014f, 0.050f));
        const std::array<D2D1_COLOR_F, 6> colors = {
            D2D1::ColorF(0.04f, 0.95f, 0.72f, 0.28f), D2D1::ColorF(0.10f, 0.52f, 1.00f, 0.30f),
            D2D1::ColorF(0.62f, 0.18f, 1.00f, 0.27f), D2D1::ColorF(0.05f, 0.82f, 0.98f, 0.24f),
            D2D1::ColorF(0.20f, 0.98f, 0.50f, 0.22f), D2D1::ColorF(0.86f, 0.16f, 0.94f, 0.20f),
        };
        for (int i = 0; i < static_cast<int>(colors.size()); ++i) {
            const float phase = time * (0.30f + i * 0.018f) + i * 1.13f;
            const float x = size.width * (0.08f + i * 0.18f) + static_cast<float>(std::sin(phase)) * size.width * 0.14f;
            const float y = size.height * (0.34f + 0.22f * static_cast<float>(std::sin(phase * 0.77f + i)));
            slot.brush->SetColor(colors[static_cast<std::size_t>(i)]);
            slot.renderTarget->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), size.width * 0.30f, size.height * 0.38f), slot.brush.Get());
        }
        for (int i = 0; i < 4; ++i) {
            const float y = size.height * (0.18f + i * 0.19f) + static_cast<float>(std::sin(time * 0.45f + i)) * 32.0f;
            slot.brush->SetColor(D2D1::ColorF(0.30f, 0.90f, 1.00f, 0.16f));
            slot.renderTarget->FillRectangle(D2D1::RectF(0.0f, y, size.width, y + 22.0f), slot.brush.Get());
        }
    }

    void DrawNeon(Slot& slot, const D2D1_SIZE_F& size) {
        FillBackground(slot, size, D2D1::ColorF(0.004f, 0.006f, 0.020f));
        constexpr float spacing = 58.0f;
        const float offset = static_cast<float>(std::fmod(time * 30.0f, spacing));
        slot.brush->SetColor(D2D1::ColorF(0.02f, 0.82f, 1.00f, 0.42f));
        for (float x = -spacing + offset; x < size.width + spacing; x += spacing)
            slot.renderTarget->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), slot.brush.Get(), 1.4f);
        slot.brush->SetColor(D2D1::ColorF(0.92f, 0.04f, 0.84f, 0.34f));
        for (float y = -spacing + offset; y < size.height + spacing; y += spacing)
            slot.renderTarget->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), slot.brush.Get(), 1.2f);
    }

    void DrawGrid(Slot& slot, const D2D1_SIZE_F& size) {
        FillBackground(slot, size, D2D1::ColorF(0.020f, 0.026f, 0.034f));
        constexpr float spacing = 64.0f;
        slot.brush->SetColor(D2D1::ColorF(0.22f, 0.32f, 0.40f, 0.62f));
        for (float x = 0; x < size.width; x += spacing)
            slot.renderTarget->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), slot.brush.Get());
        for (float y = 0; y < size.height; y += spacing)
            slot.renderTarget->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), slot.brush.Get());
        const float pulse = 0.5f + 0.5f * static_cast<float>(std::sin(time * 0.72f));
        slot.brush->SetColor(D2D1::ColorF(0.15f, 0.66f, 0.82f, 0.14f + pulse * 0.10f));
        slot.renderTarget->FillEllipse(D2D1::Ellipse(D2D1::Point2F(size.width * 0.5f, size.height * 0.5f),
                                                     size.width * (0.18f + pulse * 0.05f),
                                                     size.height * (0.20f + pulse * 0.05f)), slot.brush.Get());
    }

    void DrawSlot(Slot& slot) {
        if (!EnsureRenderTarget(slot) || !slot.brush || !slot.renderTarget) return;
        RECT rc{};
        GetClientRect(slot.window, &rc);
        const D2D1_SIZE_F size = D2D1::SizeF(static_cast<float>(std::max<LONG>(1, rc.right - rc.left)),
                                              static_cast<float>(std::max<LONG>(1, rc.bottom - rc.top)));
        slot.renderTarget->BeginDraw();
        slot.renderTarget->SetTransform(D2D1::Matrix3x2F::Identity());
        slot.renderTarget->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f));
        if (slot.wallpaper.kind == wallpaper::ResolvedWallpaperKind::Image && slot.bitmap) DrawImage(slot, size);
        else if (slot.wallpaper.sceneKey == L"neon") DrawNeon(slot, size);
        else if (slot.wallpaper.sceneKey == L"grid") DrawGrid(slot, size);
        else DrawAurora(slot, size);
        const HRESULT hr = slot.renderTarget->EndDraw();
        if (hr == D2DERR_RECREATE_TARGET) {
            slot.bitmap.Reset();
            slot.brush.Reset();
            slot.renderTarget.Reset();
            if (slot.wallpaper.kind == wallpaper::ResolvedWallpaperKind::Image && !LoadImage(slot))
                FallbackSlotToAurora(slot, L"Direct2D 设备恢复后图片重载失败，已回退 Aurora");
        }
    }

    bool Start(HWND parentWindow, const std::vector<wallpaper::ResolvedMonitorWallpaper>& wallpapers,
               wallpaper::ScaleMode nextScaleMode, float nextFocalX, float nextFocalY,
               const IndependentVideoSettings& settings) {
        Stop();
        lastError.clear();
        if (!parentWindow || wallpapers.empty()) {
            lastError = L"Independent wallpaper 参数无效";
            return false;
        }
        if (!EnsureFactories() || !EnsureSurfaceClass()) return false;
        parent = parentWindow;
        scaleMode = nextScaleMode;
        focalX = wallpaper::ClampFocal(nextFocalX);
        focalY = wallpaper::ClampFocal(nextFocalY);
        videoSettings = settings;

        RECT parentRect{};
        if (!GetClientRect(parent, &parentRect)) {
            lastError = L"Independent parent 没有有效客户区";
            return false;
        }

        slots.reserve(wallpapers.size());
        for (const auto& resolved : wallpapers) {
            RECT clipped{};
            if (!IntersectRect(&clipped, &resolved.region, &parentRect)) continue;
            if (clipped.right <= clipped.left || clipped.bottom <= clipped.top) continue;

            auto slot = std::make_unique<Slot>();
            slot->owner = this;
            slot->wallpaper = resolved;
            slot->wallpaper.region = clipped;
            slot->window = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                kSurfaceClass,
                L"",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                clipped.left,
                clipped.top,
                clipped.right - clipped.left,
                clipped.bottom - clipped.top,
                parent,
                nullptr,
                GetModuleHandleW(nullptr),
                slot.get());
            if (!slot->window) {
                lastError = L"创建 Independent 显示器 Surface 失败，Win32=" + std::to_wstring(GetLastError());
                Stop();
                return false;
            }
            PrepareSlot(*slot);
            ShowWindow(slot->window, SW_SHOWNOACTIVATE);
            SetWindowPos(slot->window, HWND_TOP, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            slots.push_back(std::move(slot));
        }
        return !slots.empty();
    }

    void Stop() {
        for (auto& slot : slots) if (slot && slot->video) slot->video->Stop();
        for (auto& slot : slots) {
            if (slot && slot->window && IsWindow(slot->window)) DestroyWindow(slot->window);
        }
        slots.clear();
        parent = nullptr;
        paused = false;
        time = 0.0f;
    }
};

IndependentWallpaperHost::IndependentWallpaperHost() : impl_(std::make_unique<Impl>()) {}
IndependentWallpaperHost::~IndependentWallpaperHost() = default;

bool IndependentWallpaperHost::Start(HWND parentWindow,
                                     const std::vector<wallpaper::ResolvedMonitorWallpaper>& wallpapers,
                                     wallpaper::ScaleMode scaleMode,
                                     float focalX,
                                     float focalY,
                                     const IndependentVideoSettings& videoSettings) {
    return impl_ && impl_->Start(parentWindow, wallpapers, scaleMode, focalX, focalY, videoSettings);
}

void IndependentWallpaperHost::Stop() {
    if (impl_) impl_->Stop();
}

void IndependentWallpaperHost::Tick(int targetFps) {
    if (!impl_ || impl_->paused || impl_->slots.empty()) return;
    impl_->time += 1.0f / static_cast<float>(std::max(1, targetFps));
    for (auto& slot : impl_->slots) {
        if (!slot) continue;
        if (slot->video) {
            slot->video->Tick();
            const auto error = slot->video->LastErrorText();
            if (!error.empty()) slot->error = error;
        } else if (slot->window) {
            InvalidateRect(slot->window, nullptr, FALSE);
        }
    }
}

void IndependentWallpaperHost::SetPaused(bool paused) {
    if (!impl_) return;
    impl_->paused = paused;
    for (auto& slot : impl_->slots) if (slot && slot->video) slot->video->SetPaused(paused);
}

void IndependentWallpaperHost::SetVideoSettings(const IndependentVideoSettings& settings) {
    if (!impl_) return;
    impl_->videoSettings = settings;
    for (auto& slot : impl_->slots) {
        if (!slot || !slot->video) continue;
        impl_->ApplyVideoSettings(*slot->video);
    }
}

bool IndependentWallpaperHost::RestartVideos() {
    if (!impl_) return false;
    bool found = false;
    bool ok = true;
    for (auto& slot : impl_->slots) {
        if (!slot || !slot->video) continue;
        found = true;
        if (!slot->video->Restart()) ok = false;
    }
    return found && ok;
}

bool IndependentWallpaperHost::SeekVideosRelativeSeconds(double seconds) {
    if (!impl_) return false;
    bool found = false;
    bool ok = true;
    for (auto& slot : impl_->slots) {
        if (!slot || !slot->video) continue;
        found = true;
        if (!slot->video->SeekRelativeSeconds(seconds)) ok = false;
    }
    return found && ok;
}

bool IndependentWallpaperHost::Active() const {
    if (!impl_ || impl_->slots.empty()) return false;
    return std::all_of(impl_->slots.begin(), impl_->slots.end(), [](const auto& slot) {
        return slot && slot->window && IsWindow(slot->window);
    });
}

bool IndependentWallpaperHost::HasVideo() const {
    if (!impl_) return false;
    return std::any_of(impl_->slots.begin(), impl_->slots.end(), [](const auto& slot) {
        return slot && slot->video != nullptr;
    });
}

std::wstring IndependentWallpaperHost::LastErrorText() const {
    if (!impl_) return L"Independent wallpaper host unavailable";
    if (!impl_->lastError.empty()) return impl_->lastError;
    for (const auto& slot : impl_->slots) if (slot && !slot->error.empty()) return slot->error;
    return {};
}

std::wstring IndependentWallpaperHost::DiagnosticsText() const {
    if (!impl_) return L"Independent wallpaper host unavailable";
    std::size_t videos = 0;
    std::size_t images = 0;
    std::size_t scenes = 0;
    std::size_t fallbacks = 0;
    for (const auto& slot : impl_->slots) {
        if (!slot) continue;
        if (slot->video) ++videos;
        else if (slot->wallpaper.kind == wallpaper::ResolvedWallpaperKind::Image) ++images;
        else ++scenes;
        if (slot->wallpaper.fallback) ++fallbacks;
    }
    std::wostringstream text;
    text << impl_->slots.size() << L" 个独立 Surface · Scene " << scenes << L" · 图片 " << images << L" · 视频 " << videos;
    if (fallbacks > 0) text << L" · fallback " << fallbacks;
    const auto error = LastErrorText();
    if (!error.empty()) text << L" · " << error;
    return text.str();
}

} // namespace turingdesk

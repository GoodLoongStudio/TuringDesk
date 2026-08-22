#include "turingdesk/VideoWallpaperSet.h"
#include "turingdesk/VideoWallpaperPlayer.h"

#include <algorithm>

namespace turingdesk {
namespace {

constexpr wchar_t kSurfaceClass[] = L"TuringDesk.Native.VideoWallpaperSurface";

bool SameRect(const RECT& a, const RECT& b) noexcept {
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}

} // namespace

VideoWallpaperSet::VideoWallpaperSet() = default;
VideoWallpaperSet::~VideoWallpaperSet() { Stop(); }

LRESULT CALLBACK VideoWallpaperSet::SurfaceProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_NCHITTEST:
        return HTTRANSPARENT;
    case WM_ERASEBKGND:
        return 1;
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

bool VideoWallpaperSet::EnsureSurfaceClass() {
    const HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.hInstance = instance;
    wc.lpfnWndProc = &VideoWallpaperSet::SurfaceProc;
    wc.lpszClassName = kSurfaceClass;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    if (RegisterClassExW(&wc)) return true;
    return GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
}

bool VideoWallpaperSet::Start(HWND parentWindow, const std::wstring& path, const std::vector<RECT>& requestedRegions,
                              wallpaper::ScaleMode scaleMode, float focalX, float focalY) {
    Stop();
    lastError_.clear();
    scaleMode_ = scaleMode;
    focalX_ = wallpaper::ClampFocal(focalX);
    focalY_ = wallpaper::ClampFocal(focalY);
    if (!parentWindow || path.empty()) {
        lastError_ = L"视频 Surface 参数无效";
        return false;
    }

    RECT parentRect{};
    if (!GetClientRect(parentWindow, &parentRect) || parentRect.right <= parentRect.left || parentRect.bottom <= parentRect.top) {
        lastError_ = L"视频父窗口没有有效可绘制区域";
        return false;
    }

    std::vector<RECT> regions;
    regions.reserve(requestedRegions.size());
    for (const auto& region : requestedRegions) {
        RECT clipped{};
        if (!IntersectRect(&clipped, &region, &parentRect)) continue;
        if (clipped.right > clipped.left && clipped.bottom > clipped.top) regions.push_back(clipped);
    }
    if (regions.empty()) regions.push_back(parentRect);

    parent_ = parentWindow;
    const bool directParent = regions.size() == 1 && SameRect(regions.front(), parentRect);
    if (!directParent && !EnsureSurfaceClass()) {
        lastError_ = L"无法注册多显示器视频 Surface";
        parent_ = nullptr;
        return false;
    }

    for (const auto& region : regions) {
        Slot slot;
        if (directParent) {
            slot.surface = parentWindow;
            slot.ownsSurface = false;
        } else {
            slot.surface = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                kSurfaceClass,
                L"",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                region.left,
                region.top,
                region.right - region.left,
                region.bottom - region.top,
                parentWindow,
                nullptr,
                GetModuleHandleW(nullptr),
                nullptr);
            slot.ownsSurface = slot.surface != nullptr;
            if (!slot.surface) {
                lastError_ = L"创建显示器视频 Surface 失败，Win32=" + std::to_wstring(GetLastError());
                Stop();
                return false;
            }
        }

        slot.player = std::make_unique<VideoWallpaperPlayer>();
        slot.player->SetScaling(scaleMode_, focalX_, focalY_);
        if (!slot.player->Start(slot.surface, path)) {
            lastError_ = slot.player->LastErrorText();
            if (lastError_.empty()) lastError_ = L"Media Foundation 无法启动多屏视频";
            if (slot.ownsSurface && IsWindow(slot.surface)) DestroyWindow(slot.surface);
            Stop();
            return false;
        }
        slot.player->SetScaling(scaleMode_, focalX_, focalY_);
        slots_.push_back(std::move(slot));
    }

    for (auto& slot : slots_) {
        if (slot.ownsSurface && IsWindow(slot.surface)) {
            ShowWindow(slot.surface, SW_SHOWNOACTIVATE);
            SetWindowPos(slot.surface, HWND_TOP, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }
    return !slots_.empty();
}

void VideoWallpaperSet::Stop() {
    for (auto& slot : slots_) {
        if (slot.player) slot.player->Stop();
    }
    for (auto& slot : slots_) {
        if (slot.ownsSurface && slot.surface && IsWindow(slot.surface)) DestroyWindow(slot.surface);
    }
    slots_.clear();
    parent_ = nullptr;
}

void VideoWallpaperSet::Tick() {
    for (auto& slot : slots_) {
        if (slot.player) slot.player->Tick();
    }
}

void VideoWallpaperSet::SetPaused(bool paused) {
    for (auto& slot : slots_) {
        if (slot.player) slot.player->SetPaused(paused);
    }
}

void VideoWallpaperSet::SetScaling(wallpaper::ScaleMode scaleMode, float focalX, float focalY) {
    scaleMode_ = scaleMode;
    focalX_ = wallpaper::ClampFocal(focalX);
    focalY_ = wallpaper::ClampFocal(focalY);
    for (auto& slot : slots_) {
        if (slot.player) slot.player->SetScaling(scaleMode_, focalX_, focalY_);
    }
}

bool VideoWallpaperSet::Active() const {
    if (slots_.empty()) return false;
    return std::all_of(slots_.begin(), slots_.end(), [](const Slot& slot) {
        return slot.player && slot.player->Active();
    });
}

std::wstring VideoWallpaperSet::LastErrorText() const {
    if (!lastError_.empty()) return lastError_;
    for (const auto& slot : slots_) {
        if (!slot.player) continue;
        const auto error = slot.player->LastErrorText();
        if (!error.empty()) return error;
    }
    return {};
}

} // namespace turingdesk

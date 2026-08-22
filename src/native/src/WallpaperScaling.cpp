#include "turingdesk/WallpaperScaling.h"

#include <algorithm>
#include <cmath>
#include <cwchar>

namespace turingdesk::wallpaper {
namespace {

RectF FullSource(float width, float height) noexcept {
    return RectF{0.0f, 0.0f, std::max(0.0f, width), std::max(0.0f, height)};
}

RectF FullTarget(float width, float height) noexcept {
    return RectF{0.0f, 0.0f, std::max(0.0f, width), std::max(0.0f, height)};
}

bool Near(float a, float b, float epsilon = 0.01f) noexcept {
    return std::fabs(a - b) <= epsilon;
}

} // namespace

ScaleMode ParseScaleMode(const std::wstring& value) noexcept {
    if (_wcsicmp(value.c_str(), L"contain") == 0) return ScaleMode::Contain;
    if (_wcsicmp(value.c_str(), L"stretch") == 0) return ScaleMode::Stretch;
    if (_wcsicmp(value.c_str(), L"center") == 0) return ScaleMode::Center;
    if (_wcsicmp(value.c_str(), L"tile") == 0) return ScaleMode::Tile;
    return ScaleMode::Cover;
}

const wchar_t* ScaleModeKey(ScaleMode mode) noexcept {
    switch (mode) {
    case ScaleMode::Contain: return L"contain";
    case ScaleMode::Stretch: return L"stretch";
    case ScaleMode::Center: return L"center";
    case ScaleMode::Tile: return L"tile";
    case ScaleMode::Cover: break;
    }
    return L"cover";
}

const wchar_t* ScaleModeDisplayName(ScaleMode mode) noexcept {
    switch (mode) {
    case ScaleMode::Contain: return L"适应 · Contain";
    case ScaleMode::Stretch: return L"拉伸 · Stretch";
    case ScaleMode::Center: return L"居中原尺寸 · Center";
    case ScaleMode::Tile: return L"平铺 · Tile";
    case ScaleMode::Cover: break;
    }
    return L"填充裁切 · Cover";
}

float ClampFocal(float value) noexcept {
    return std::clamp(value, 0.0f, 1.0f);
}

Placement ComputePlacement(float sourceWidth, float sourceHeight,
                           float targetWidth, float targetHeight,
                           ScaleMode mode, float focalX, float focalY) noexcept {
    sourceWidth = std::max(1.0f, sourceWidth);
    sourceHeight = std::max(1.0f, sourceHeight);
    targetWidth = std::max(1.0f, targetWidth);
    targetHeight = std::max(1.0f, targetHeight);
    focalX = ClampFocal(focalX);
    focalY = ClampFocal(focalY);

    Placement result;
    result.source = FullSource(sourceWidth, sourceHeight);
    result.destination = FullTarget(targetWidth, targetHeight);

    if (mode == ScaleMode::Stretch) return result;

    if (mode == ScaleMode::Tile) {
        result.destination = RectF{0.0f, 0.0f, sourceWidth, sourceHeight};
        result.tiled = true;
        return result;
    }

    if (mode == ScaleMode::Center) {
        const float left = (targetWidth - sourceWidth) * focalX;
        const float top = (targetHeight - sourceHeight) * focalY;
        result.destination = RectF{left, top, left + sourceWidth, top + sourceHeight};
        return result;
    }

    const float sourceAspect = sourceWidth / sourceHeight;
    const float targetAspect = targetWidth / targetHeight;

    if (mode == ScaleMode::Contain) {
        const float scale = std::min(targetWidth / sourceWidth, targetHeight / sourceHeight);
        const float width = sourceWidth * scale;
        const float height = sourceHeight * scale;
        const float left = (targetWidth - width) * focalX;
        const float top = (targetHeight - height) * focalY;
        result.destination = RectF{left, top, left + width, top + height};
        return result;
    }

    // Cover fills the destination and crops the source around the focal point.
    if (sourceAspect > targetAspect) {
        const float cropWidth = sourceHeight * targetAspect;
        const float left = (sourceWidth - cropWidth) * focalX;
        result.source = RectF{left, 0.0f, left + cropWidth, sourceHeight};
    } else if (sourceAspect < targetAspect) {
        const float cropHeight = sourceWidth / targetAspect;
        const float top = (sourceHeight - cropHeight) * focalY;
        result.source = RectF{0.0f, top, sourceWidth, top + cropHeight};
    }
    return result;
}

bool SelfTestScalingGeometry() noexcept {
    const auto cover = ComputePlacement(3840.0f, 2160.0f, 1080.0f, 1920.0f,
                                        ScaleMode::Cover, 0.5f, 0.5f);
    if (!Near(cover.destination.right, 1080.0f) || !Near(cover.destination.bottom, 1920.0f)) return false;
    if (!(cover.source.left > 0.0f) || !(cover.source.right < 3840.0f)) return false;

    const auto contain = ComputePlacement(1920.0f, 1080.0f, 1920.0f, 1200.0f,
                                          ScaleMode::Contain, 0.5f, 0.5f);
    if (!Near(contain.destination.left, 0.0f) || !Near(contain.destination.right, 1920.0f)) return false;
    if (!Near(contain.destination.top, 60.0f) || !Near(contain.destination.bottom, 1140.0f)) return false;

    const auto topLeft = ComputePlacement(1000.0f, 500.0f, 500.0f, 500.0f,
                                          ScaleMode::Cover, 0.0f, 0.0f);
    if (!Near(topLeft.source.left, 0.0f)) return false;

    const auto center = ComputePlacement(320.0f, 240.0f, 1920.0f, 1080.0f,
                                         ScaleMode::Center, 0.5f, 0.5f);
    if (!Near(center.destination.left, 800.0f) || !Near(center.destination.top, 420.0f)) return false;

    const auto tile = ComputePlacement(320.0f, 240.0f, 1920.0f, 1080.0f,
                                       ScaleMode::Tile, 0.5f, 0.5f);
    return tile.tiled && Near(tile.destination.right, 320.0f) && Near(tile.destination.bottom, 240.0f) &&
           ParseScaleMode(L"stretch") == ScaleMode::Stretch &&
           ParseScaleMode(L"unknown") == ScaleMode::Cover &&
           Near(ClampFocal(-1.0f), 0.0f) && Near(ClampFocal(2.0f), 1.0f);
}

} // namespace turingdesk::wallpaper

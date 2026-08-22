#pragma once

#include <string>

namespace turingdesk::wallpaper {

enum class ScaleMode {
    Cover,
    Contain,
    Stretch,
    Center,
    Tile,
};

struct RectF {
    float left{};
    float top{};
    float right{};
    float bottom{};
};

struct Placement {
    RectF source;
    RectF destination;
    bool tiled{};
};

ScaleMode ParseScaleMode(const std::wstring& value) noexcept;
const wchar_t* ScaleModeKey(ScaleMode mode) noexcept;
const wchar_t* ScaleModeDisplayName(ScaleMode mode) noexcept;
float ClampFocal(float value) noexcept;
Placement ComputePlacement(float sourceWidth, float sourceHeight,
                           float targetWidth, float targetHeight,
                           ScaleMode mode, float focalX, float focalY) noexcept;
bool SelfTestScalingGeometry() noexcept;

} // namespace turingdesk::wallpaper

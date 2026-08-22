#pragma once

#include <windows.h>
#include <memory>
#include <string>
#include <vector>

#include "turingdesk/WallpaperIndependentLayout.h"
#include "turingdesk/WallpaperScaling.h"

namespace turingdesk {

struct IndependentVideoSettings {
    bool looping{true};
    bool muted{true};
    float volume{0.0f};
    float rate{1.0f};
};

class IndependentWallpaperHost {
public:
    IndependentWallpaperHost();
    ~IndependentWallpaperHost();

    IndependentWallpaperHost(const IndependentWallpaperHost&) = delete;
    IndependentWallpaperHost& operator=(const IndependentWallpaperHost&) = delete;

    bool Start(HWND parentWindow,
               const std::vector<wallpaper::ResolvedMonitorWallpaper>& wallpapers,
               wallpaper::ScaleMode scaleMode,
               float focalX,
               float focalY,
               const IndependentVideoSettings& videoSettings);
    void Stop();
    void Tick(int targetFps);
    void SetPaused(bool paused);
    void SetVideoSettings(const IndependentVideoSettings& settings);
    bool RestartVideos();
    bool SeekVideosRelativeSeconds(double seconds);

    bool Active() const;
    bool HasVideo() const;
    std::wstring LastErrorText() const;
    std::wstring DiagnosticsText() const;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk

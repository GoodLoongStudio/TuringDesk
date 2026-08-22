#pragma once
#include <windows.h>
#include <memory>
#include <string>

#include "turingdesk/WallpaperScaling.h"

namespace turingdesk {

class VideoWallpaperPlayer {
public:
    VideoWallpaperPlayer();
    ~VideoWallpaperPlayer();

    VideoWallpaperPlayer(const VideoWallpaperPlayer&) = delete;
    VideoWallpaperPlayer& operator=(const VideoWallpaperPlayer&) = delete;

    bool Start(HWND targetWindow, const std::wstring& path);
    void Stop();
    void Tick();
    void UpdateVideo();
    void SetPaused(bool paused);
    void SetScaling(wallpaper::ScaleMode mode, float focalX = 0.5f, float focalY = 0.5f);
    SIZE NativeVideoSize() const;
    bool Active() const;
    HRESULT LastError() const;
    std::wstring LastErrorText() const;

    static bool MediaFoundationAvailable();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk

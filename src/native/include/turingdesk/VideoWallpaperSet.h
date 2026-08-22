#pragma once

#include <windows.h>
#include <memory>
#include <string>
#include <vector>

#include "turingdesk/WallpaperScaling.h"

namespace turingdesk {

class VideoWallpaperPlayer;

class VideoWallpaperSet {
public:
    VideoWallpaperSet();
    ~VideoWallpaperSet();

    VideoWallpaperSet(const VideoWallpaperSet&) = delete;
    VideoWallpaperSet& operator=(const VideoWallpaperSet&) = delete;

    bool Start(HWND parentWindow, const std::wstring& path, const std::vector<RECT>& regions,
               wallpaper::ScaleMode scaleMode = wallpaper::ScaleMode::Cover,
               float focalX = 0.5f, float focalY = 0.5f);
    void Stop();
    void Tick();
    void SetPaused(bool paused);
    void SetScaling(wallpaper::ScaleMode scaleMode, float focalX = 0.5f, float focalY = 0.5f);
    void SetLooping(bool looping);
    void SetMuted(bool muted);
    void SetVolume(float volume);
    void SetPlaybackRate(float rate);
    bool Restart();
    bool Active() const;
    std::wstring LastErrorText() const;
    std::wstring DiagnosticsText() const;

private:
    struct Slot {
        HWND surface{};
        bool ownsSurface{};
        std::unique_ptr<VideoWallpaperPlayer> player;
    };

    bool EnsureSurfaceClass();
    static LRESULT CALLBACK SurfaceProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);

    HWND parent_{};
    std::vector<Slot> slots_;
    std::wstring lastError_;
    wallpaper::ScaleMode scaleMode_{wallpaper::ScaleMode::Cover};
    float focalX_{0.5f};
    float focalY_{0.5f};
    bool looping_{true};
    bool muted_{true};
    float volume_{0.0f};
    float playbackRate_{1.0f};
};

} // namespace turingdesk

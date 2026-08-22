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
    void SetLooping(bool looping);
    void SetMuted(bool muted);
    void SetVolume(float volume);
    void SetPlaybackRate(float rate);
    bool Restart();
    bool SeekRelativeSeconds(double seconds);

    SIZE NativeVideoSize() const;
    bool Active() const;
    bool Looping() const;
    bool Muted() const;
    float Volume() const;
    float PlaybackRate() const;
    double PositionSeconds() const;
    double DurationSeconds() const;
    HRESULT LastError() const;
    std::wstring LastErrorText() const;
    std::wstring DiagnosticsText() const;

    static bool MediaFoundationAvailable();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk

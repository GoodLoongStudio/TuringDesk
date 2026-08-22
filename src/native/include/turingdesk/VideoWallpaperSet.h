#pragma once

#include <windows.h>
#include <memory>
#include <string>
#include <vector>

namespace turingdesk {

class VideoWallpaperPlayer;

class VideoWallpaperSet {
public:
    VideoWallpaperSet();
    ~VideoWallpaperSet();

    VideoWallpaperSet(const VideoWallpaperSet&) = delete;
    VideoWallpaperSet& operator=(const VideoWallpaperSet&) = delete;

    bool Start(HWND parentWindow, const std::wstring& path, const std::vector<RECT>& regions);
    void Stop();
    void Tick();
    void SetPaused(bool paused);
    bool Active() const;
    std::wstring LastErrorText() const;

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
};

} // namespace turingdesk

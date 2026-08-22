#pragma once

#include <windows.h>
#include <functional>
#include <memory>
#include <string>
#include <vector>

#include "turingdesk/WallpaperLibrary.h"

namespace turingdesk::wallpaper {

struct WallpaperLibraryTarget {
    std::wstring monitorId;
    std::wstring displayName;
    bool primary{};
};

class WallpaperLibraryWindow {
public:
    using ApplyCallback = std::function<void(const WallpaperLibraryItem&, const std::wstring& targetMonitorId)>;

    WallpaperLibraryWindow();
    ~WallpaperLibraryWindow();

    WallpaperLibraryWindow(const WallpaperLibraryWindow&) = delete;
    WallpaperLibraryWindow& operator=(const WallpaperLibraryWindow&) = delete;

    bool Show(HINSTANCE instance, WallpaperLibrary* library,
              const std::vector<WallpaperLibraryTarget>& targets,
              ApplyCallback applyCallback);
    void SetTargets(const std::vector<WallpaperLibraryTarget>& targets);
    void Close();
    void Refresh();
    bool Visible() const noexcept;
    HWND Window() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk::wallpaper

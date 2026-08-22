#pragma once

#include <windows.h>
#include <functional>
#include <memory>

#include "turingdesk/WallpaperLibrary.h"

namespace turingdesk::wallpaper {

class WallpaperLibraryWindow {
public:
    using ApplyCallback = std::function<void(const WallpaperLibraryItem&)>;

    WallpaperLibraryWindow();
    ~WallpaperLibraryWindow();

    WallpaperLibraryWindow(const WallpaperLibraryWindow&) = delete;
    WallpaperLibraryWindow& operator=(const WallpaperLibraryWindow&) = delete;

    bool Show(HINSTANCE instance, WallpaperLibrary* library, ApplyCallback applyCallback);
    void Close();
    void Refresh();
    bool Visible() const noexcept;
    HWND Window() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk::wallpaper

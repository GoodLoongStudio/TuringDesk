#pragma once

#include <windows.h>
#include <memory>

namespace turingdesk::wallpaper {

class WallpaperApplicationRulesWindow {
public:
    WallpaperApplicationRulesWindow();
    ~WallpaperApplicationRulesWindow();

    WallpaperApplicationRulesWindow(const WallpaperApplicationRulesWindow&) = delete;
    WallpaperApplicationRulesWindow& operator=(const WallpaperApplicationRulesWindow&) = delete;

    bool Show(HINSTANCE instance);
    void Close();
    void Refresh();
    bool Visible() const noexcept;
    HWND Window() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk::wallpaper

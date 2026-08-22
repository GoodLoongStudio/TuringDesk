#pragma once

#include <memory>

namespace turingdesk::wallpaper {

// Lightweight companion for the legacy native wallpaper renderer. It observes
// the already-persisted wallpaper/library/monitor-assignment state and owns only
// isolated WebView2 wallpaper child processes. Native Scene/image/video rendering
// remains in WallpaperEngine.cpp.
class WallpaperWebRuntimeCoordinator {
public:
    WallpaperWebRuntimeCoordinator();
    ~WallpaperWebRuntimeCoordinator();

    WallpaperWebRuntimeCoordinator(const WallpaperWebRuntimeCoordinator&) = delete;
    WallpaperWebRuntimeCoordinator& operator=(const WallpaperWebRuntimeCoordinator&) = delete;

    bool Start();
    void Stop();
    bool Running() const noexcept;

    static bool SelfTest();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk::wallpaper

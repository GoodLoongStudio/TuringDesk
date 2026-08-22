#pragma once

#include <memory>
#include <string>

#include "turingdesk/WallpaperLibrary.h"

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

// Persists a Web library item into the same global/per-monitor wallpaper state
// consumed by the native engine and the coordinator. Global Web uses the legacy
// Image string as a source carrier while Scene="web" distinguishes the backend;
// this avoids a breaking wallpaper.ini schema migration.
bool ActivateWebWallpaperItem(const WallpaperLibraryItem& item,
                              const std::wstring& targetMonitorId,
                              std::wstring* error = nullptr);

} // namespace turingdesk::wallpaper

#pragma once

#include <windows.h>
#include <string>
#include <vector>

namespace turingdesk::wallpaper {

struct WebWallpaperRequest {
    RECT region{};
    std::wstring source;
    std::wstring itemId;
    bool muted{true};
};

class WebWallpaperProcessSet {
public:
    WebWallpaperProcessSet();
    ~WebWallpaperProcessSet();

    WebWallpaperProcessSet(const WebWallpaperProcessSet&) = delete;
    WebWallpaperProcessSet& operator=(const WebWallpaperProcessSet&) = delete;

    bool Start(HWND parentWindow, const std::vector<WebWallpaperRequest>& requests);
    void Stop();
    void Tick();
    void SetPaused(bool paused);

    bool Active() const noexcept;
    std::wstring LastErrorText() const;
    std::wstring DiagnosticsText() const;

    static bool IsRemoteHttpsSource(const std::wstring& source) noexcept;
    static bool IsSupportedSource(const std::wstring& source) noexcept;
    static bool SelfTest() noexcept;

private:
    struct Slot;
    bool StartSlot(Slot& slot, bool recovery);
    void StopSlot(Slot& slot);
    HWND FindSlotWindow(const Slot& slot) const;

    HWND parent_{};
    HANDLE job_{};
    bool paused_{};
    std::vector<Slot> slots_;
    std::wstring lastError_;
};

// Runs the isolated WebView2 child mode inside TuringDeskWallpaper.exe. Returns
// -1 when the current command line is not a web-host invocation.
int TryRunWebWallpaperChild(HINSTANCE instance);

} // namespace turingdesk::wallpaper

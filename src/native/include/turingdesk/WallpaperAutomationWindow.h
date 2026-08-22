#pragma once

#include <windows.h>
#include <functional>
#include <memory>
#include <optional>

#include "turingdesk/WallpaperAutomation.h"
#include "turingdesk/WallpaperLibrary.h"

namespace turingdesk::wallpaper {

class WallpaperAutomationWindow {
public:
    using CaptureProfileCallback = std::function<std::optional<WallpaperProfile>(const std::wstring& name)>;
    using DecisionCallback = std::function<void(const AutomationDecision&)>;

    WallpaperAutomationWindow();
    ~WallpaperAutomationWindow();

    WallpaperAutomationWindow(const WallpaperAutomationWindow&) = delete;
    WallpaperAutomationWindow& operator=(const WallpaperAutomationWindow&) = delete;

    bool Show(HINSTANCE instance,
              WallpaperAutomationStore* automation,
              WallpaperLibrary* library,
              CaptureProfileCallback captureProfile,
              DecisionCallback applyDecision);
    void Close();
    void Refresh();
    bool Visible() const noexcept;
    HWND Window() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk::wallpaper

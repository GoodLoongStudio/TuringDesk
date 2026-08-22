#pragma once

#include <windows.h>
#include <string>

namespace turingdesk::wallpaper {

enum class PerformanceAction {
    Normal = 0,
    Throttle = 1,
    Pause = 2,
    Stop = 3,
};

struct PerformanceConfig {
    int fpsCap{30};
    int throttleFps{15};
    PerformanceAction fullscreenAction{PerformanceAction::Pause};
    PerformanceAction maximizedAction{PerformanceAction::Throttle};
    PerformanceAction remoteSessionAction{PerformanceAction::Throttle};
    PerformanceAction batterySaverAction{PerformanceAction::Throttle};
    PerformanceAction lockedSessionAction{PerformanceAction::Stop};
    PerformanceAction idleAction{PerformanceAction::Throttle};
    DWORD idleThresholdSeconds{120};
};

struct PerformanceSnapshot {
    bool fullscreen{};
    bool maximized{};
    bool remoteSession{};
    bool batterySaver{};
    bool sessionLocked{};
    bool idle{};
    PerformanceAction action{PerformanceAction::Normal};
    int targetFps{30};
    std::wstring reason;
};

int NormalizeFpsCap(int fps) noexcept;
PerformanceAction ParsePerformanceAction(const std::wstring& value) noexcept;
const wchar_t* PerformanceActionKey(PerformanceAction action) noexcept;
const wchar_t* PerformanceActionDisplayName(PerformanceAction action) noexcept;
PerformanceAction StrongerAction(PerformanceAction a, PerformanceAction b) noexcept;
PerformanceSnapshot ResolvePerformanceSnapshot(const PerformanceConfig& config,
                                               bool fullscreen, bool maximized,
                                               bool remoteSession, bool batterySaver,
                                               bool sessionLocked, bool idle);

class WallpaperPerformancePolicy {
public:
    void SetSessionLocked(bool locked) noexcept;
    bool SessionLocked() const noexcept;
    PerformanceSnapshot Evaluate(HWND wallpaperWindow, HWND settingsWindow,
                                 const PerformanceConfig& config) const;

    static bool SelfTest() noexcept;

private:
    bool sessionLocked_{};
};

} // namespace turingdesk::wallpaper

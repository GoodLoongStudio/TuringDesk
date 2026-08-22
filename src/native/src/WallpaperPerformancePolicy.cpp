#include "turingdesk/WallpaperPerformancePolicy.h"
#include "turingdesk/WallpaperApplicationRules.h"

#include <algorithm>
#include <cstdlib>
#include <cwchar>

namespace turingdesk::wallpaper {
namespace {

bool IsIgnoredForeground(HWND foreground, HWND wallpaperWindow, HWND settingsWindow) {
    if (!foreground || foreground == wallpaperWindow || foreground == settingsWindow || IsIconic(foreground)) return true;

    wchar_t className[128]{};
    GetClassNameW(foreground, className, static_cast<int>(std::size(className)));
    return _wcsicmp(className, L"Progman") == 0 ||
           _wcsicmp(className, L"WorkerW") == 0 ||
           _wcsicmp(className, L"Shell_TrayWnd") == 0;
}

bool ForegroundCoversMonitor(HWND foreground) {
    RECT windowRect{};
    if (!GetWindowRect(foreground, &windowRect)) return false;
    const HMONITOR monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
    MONITORINFO info{sizeof(info)};
    if (!GetMonitorInfoW(monitor, &info)) return false;
    constexpr LONG tolerance = 4;
    return windowRect.left <= info.rcMonitor.left + tolerance &&
           windowRect.top <= info.rcMonitor.top + tolerance &&
           windowRect.right >= info.rcMonitor.right - tolerance &&
           windowRect.bottom >= info.rcMonitor.bottom - tolerance;
}

bool BatterySaverEnabled() {
    SYSTEM_POWER_STATUS status{};
    if (!GetSystemPowerStatus(&status)) return false;
    return status.SystemStatusFlag != 0;
}

bool UserIdleFor(DWORD thresholdSeconds) {
    if (thresholdSeconds == 0) return false;
    LASTINPUTINFO input{};
    input.cbSize = sizeof(input);
    if (!GetLastInputInfo(&input)) return false;
    const DWORD idleMs = GetTickCount() - input.dwTime;
    return static_cast<ULONGLONG>(idleMs) >= static_cast<ULONGLONG>(thresholdSeconds) * 1000ULL;
}

void ApplyRule(PerformanceSnapshot& snapshot, bool active, PerformanceAction action, const wchar_t* reason) {
    if (!active || action == PerformanceAction::Normal) return;
    if (static_cast<int>(action) > static_cast<int>(snapshot.action)) {
        snapshot.action = action;
        snapshot.reason = reason;
    } else if (action == snapshot.action && snapshot.reason.empty()) {
        snapshot.reason = reason;
    }
}

int TargetFpsForAction(const PerformanceConfig& config, PerformanceAction action) {
    const int normalFps = NormalizeFpsCap(config.fpsCap);
    const int throttleFps = std::min(normalFps, NormalizeFpsCap(config.throttleFps));
    return action == PerformanceAction::Throttle ? throttleFps :
           action == PerformanceAction::Normal ? normalFps : 0;
}

bool SameRootWindow(HWND a, HWND b) {
    if (!a || !b) return false;
    return a == b || GetAncestor(a, GA_ROOT) == GetAncestor(b, GA_ROOT);
}

std::wstring ApplicationRuleReason(const ApplicationRuleMatch& match) {
    std::wstring result = L"应用规则：";
    result += match.displayName.empty() ? match.executable : match.displayName;
    result += L" · ";
    result += WallpaperApplicationRules::TriggerDisplayName(match.trigger);
    result += L" → ";
    result += PerformanceActionDisplayName(match.action);
    return result;
}

PerformanceSnapshot ApplyApplicationOverride(const PerformanceConfig& config,
                                             const PerformanceSnapshot& base,
                                             const ApplicationRuleMatch& match,
                                             HWND foreground) {
    if (!match.matched) return base;

    // Foreground/fullscreen/maximized rules are explicit overrides for the generic
    // foreground-window defaults. A background "running" rule may make policy
    // stricter, but a Running+Continue rule must not accidentally neutralize a
    // different foreground application's fullscreen policy.
    const bool ownsForeground = SameRootWindow(match.window, foreground);
    const bool replacesWindowDefaults = match.trigger != ApplicationRuleTrigger::Running || ownsForeground;

    PerformanceSnapshot result = base;
    if (replacesWindowDefaults) {
        PerformanceConfig protectedConfig = config;
        protectedConfig.fullscreenAction = PerformanceAction::Normal;
        protectedConfig.maximizedAction = PerformanceAction::Normal;
        result = ResolvePerformanceSnapshot(protectedConfig,
                                            base.fullscreen, base.maximized,
                                            base.remoteSession, base.batterySaver,
                                            base.sessionLocked, base.idle);
    }

    const PerformanceAction protectedAction = result.action;
    const std::wstring protectedReason = result.reason;
    if (match.action != PerformanceAction::Normal &&
        static_cast<int>(match.action) > static_cast<int>(result.action)) {
        result.action = match.action;
    }
    result.targetFps = TargetFpsForAction(config, result.action);

    const std::wstring appReason = ApplicationRuleReason(match);
    if (static_cast<int>(protectedAction) > static_cast<int>(match.action) && !protectedReason.empty()) {
        result.reason = appReason + L"；系统保护：" + protectedReason;
    } else {
        result.reason = appReason;
    }
    return result;
}

} // namespace

int NormalizeFpsCap(int fps) noexcept {
    constexpr int allowed[] = {15, 30, 45, 60, 120};
    int best = allowed[0];
    int distance = std::abs(fps - best);
    for (int candidate : allowed) {
        const int next = std::abs(fps - candidate);
        if (next < distance) {
            best = candidate;
            distance = next;
        }
    }
    return best;
}

PerformanceAction ParsePerformanceAction(const std::wstring& value) noexcept {
    if (_wcsicmp(value.c_str(), L"stop") == 0) return PerformanceAction::Stop;
    if (_wcsicmp(value.c_str(), L"pause") == 0) return PerformanceAction::Pause;
    if (_wcsicmp(value.c_str(), L"throttle") == 0) return PerformanceAction::Throttle;
    return PerformanceAction::Normal;
}

const wchar_t* PerformanceActionKey(PerformanceAction action) noexcept {
    switch (action) {
    case PerformanceAction::Stop: return L"stop";
    case PerformanceAction::Pause: return L"pause";
    case PerformanceAction::Throttle: return L"throttle";
    case PerformanceAction::Normal: break;
    }
    return L"normal";
}

const wchar_t* PerformanceActionDisplayName(PerformanceAction action) noexcept {
    switch (action) {
    case PerformanceAction::Stop: return L"停止";
    case PerformanceAction::Pause: return L"暂停";
    case PerformanceAction::Throttle: return L"降频";
    case PerformanceAction::Normal: break;
    }
    return L"正常";
}

PerformanceAction StrongerAction(PerformanceAction a, PerformanceAction b) noexcept {
    return static_cast<int>(a) >= static_cast<int>(b) ? a : b;
}

PerformanceSnapshot ResolvePerformanceSnapshot(const PerformanceConfig& config,
                                               bool fullscreen, bool maximized,
                                               bool remoteSession, bool batterySaver,
                                               bool sessionLocked, bool idle) {
    PerformanceSnapshot snapshot;
    snapshot.fullscreen = fullscreen;
    snapshot.maximized = maximized;
    snapshot.remoteSession = remoteSession;
    snapshot.batterySaver = batterySaver;
    snapshot.sessionLocked = sessionLocked;
    snapshot.idle = idle;
    snapshot.action = PerformanceAction::Normal;

    ApplyRule(snapshot, fullscreen, config.fullscreenAction, L"全屏应用");
    ApplyRule(snapshot, maximized, config.maximizedAction, L"最大化应用");
    ApplyRule(snapshot, remoteSession, config.remoteSessionAction, L"远程桌面会话");
    ApplyRule(snapshot, batterySaver, config.batterySaverAction, L"Windows 节能模式");
    ApplyRule(snapshot, sessionLocked, config.lockedSessionAction, L"Windows 会话已锁定");
    ApplyRule(snapshot, idle, config.idleAction, L"用户长时间无输入");

    snapshot.targetFps = TargetFpsForAction(config, snapshot.action);
    return snapshot;
}

void WallpaperPerformancePolicy::SetSessionLocked(bool locked) noexcept {
    sessionLocked_ = locked;
}

bool WallpaperPerformancePolicy::SessionLocked() const noexcept {
    return sessionLocked_;
}

PerformanceSnapshot WallpaperPerformancePolicy::Evaluate(HWND wallpaperWindow, HWND settingsWindow,
                                                         const PerformanceConfig& config) const {
    bool fullscreen = false;
    bool maximized = false;
    const HWND foreground = GetForegroundWindow();
    if (!IsIgnoredForeground(foreground, wallpaperWindow, settingsWindow)) {
        fullscreen = ForegroundCoversMonitor(foreground);
        maximized = !fullscreen && IsZoomed(foreground) != FALSE;
    }

    const bool remoteSession = GetSystemMetrics(SM_REMOTESESSION) != 0;
    const bool batterySaver = BatterySaverEnabled();
    const bool idle = UserIdleFor(config.idleThresholdSeconds);
    const auto base = ResolvePerformanceSnapshot(config, fullscreen, maximized, remoteSession,
                                                 batterySaver, sessionLocked_, idle);
    const auto appMatch = EvaluateCachedApplicationRules(wallpaperWindow, settingsWindow);
    return ApplyApplicationOverride(config, base, appMatch, foreground);
}

bool WallpaperPerformancePolicy::SelfTest() noexcept {
    if (NormalizeFpsCap(31) != 30 || NormalizeFpsCap(58) != 60 || NormalizeFpsCap(100) != 120) return false;
    if (ParsePerformanceAction(L"pause") != PerformanceAction::Pause) return false;
    if (StrongerAction(PerformanceAction::Throttle, PerformanceAction::Stop) != PerformanceAction::Stop) return false;

    PerformanceConfig config;
    config.fpsCap = 60;
    config.throttleFps = 15;
    config.fullscreenAction = PerformanceAction::Pause;
    config.lockedSessionAction = PerformanceAction::Stop;

    const auto normal = ResolvePerformanceSnapshot(config, false, false, false, false, false, false);
    if (normal.action != PerformanceAction::Normal || normal.targetFps != 60) return false;

    const auto throttled = ResolvePerformanceSnapshot(config, false, true, false, false, false, false);
    if (throttled.action != PerformanceAction::Throttle || throttled.targetFps != 15) return false;

    const auto stopped = ResolvePerformanceSnapshot(config, true, false, false, false, true, false);
    if (stopped.action != PerformanceAction::Stop || stopped.targetFps != 0 || stopped.reason.empty()) return false;

    ApplicationRuleMatch continueRule;
    continueRule.matched = true;
    continueRule.executable = L"game.exe";
    continueRule.displayName = L"Game";
    continueRule.trigger = ApplicationRuleTrigger::Fullscreen;
    continueRule.action = PerformanceAction::Normal;
    const auto overridden = ApplyApplicationOverride(config, ResolvePerformanceSnapshot(config, true, false, false, false, false, false), continueRule, nullptr);
    if (overridden.action != PerformanceAction::Normal || overridden.targetFps != 60) return false;

    const auto protectedBase = ResolvePerformanceSnapshot(config, true, false, false, false, true, false);
    const auto protectedResult = ApplyApplicationOverride(config, protectedBase, continueRule, nullptr);
    return protectedResult.action == PerformanceAction::Stop && protectedResult.targetFps == 0 &&
           protectedResult.reason.find(L"系统保护") != std::wstring::npos;
}

} // namespace turingdesk::wallpaper

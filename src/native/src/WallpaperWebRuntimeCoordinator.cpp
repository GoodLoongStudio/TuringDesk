#include "turingdesk/WallpaperWebRuntimeCoordinator.h"

#include "turingdesk/WallpaperIndependentLayout.h"
#include "turingdesk/WallpaperMonitorAssignments.h"
#include "turingdesk/WallpaperMonitorLayout.h"
#include "turingdesk/WallpaperPerformancePolicy.h"
#include "turingdesk/WebWallpaperHost.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <filesystem>
#include <iterator>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

constexpr wchar_t kWallpaperHostClass[] = L"TuringDesk.Native.WallpaperHost";
constexpr wchar_t kWallpaperSettingsClass[] = L"TuringDesk.Native.WallpaperSettings";
constexpr std::chrono::milliseconds kTickInterval{250};
constexpr ULONGLONG kStateRefreshMs = 1000;
constexpr ULONGLONG kRecoveryCooldownMs = 3000;
constexpr ULONGLONG kRecoveryStableResetMs = 30000;
constexpr unsigned kMaxRecoveryAttempts = 3;

fs::path WallpaperConfigPath() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path dir = (length > 0 && length < std::size(local))
        ? fs::path(local) / L"TuringDesk"
        : fs::temp_directory_path() / L"TuringDesk";
    std::error_code ec;
    fs::create_directories(dir, ec);
    return dir / L"wallpaper.ini";
}

std::wstring ReadText(const fs::path& path, const wchar_t* section, const wchar_t* key, const wchar_t* fallback) {
    std::vector<wchar_t> buffer(32768);
    GetPrivateProfileStringW(section, key, fallback, buffer.data(), static_cast<DWORD>(buffer.size()), path.c_str());
    return buffer.data();
}

int ReadInt(const fs::path& path, const wchar_t* key, int fallback) {
    return static_cast<int>(GetPrivateProfileIntW(L"Wallpaper", key, static_cast<UINT>(fallback), path.c_str()));
}

struct RuntimeState {
    bool enabled{true};
    std::wstring scene{L"aurora"};
    std::wstring source;
    LayoutMode layout{LayoutMode::Span};
    PerformanceConfig performance;
};

RuntimeState LoadRuntimeState(const fs::path& path) {
    RuntimeState state;
    state.enabled = ReadInt(path, L"Enabled", 1) != 0;
    state.scene = ReadText(path, L"Wallpaper", L"Scene", L"aurora");
    state.source = ReadText(path, L"Wallpaper", L"Image", L"");
    state.layout = ParseLayoutMode(ReadText(path, L"Wallpaper", L"Layout", L"span"));
    state.performance.fpsCap = NormalizeFpsCap(ReadInt(path, L"FpsCap", 30));
    state.performance.throttleFps = NormalizeFpsCap(ReadInt(path, L"ThrottleFps", 15));
    state.performance.fullscreenAction = ParsePerformanceAction(ReadText(path, L"Wallpaper", L"FullscreenAction", L"pause"));
    state.performance.maximizedAction = ParsePerformanceAction(ReadText(path, L"Wallpaper", L"MaximizedAction", L"throttle"));
    state.performance.remoteSessionAction = ParsePerformanceAction(ReadText(path, L"Wallpaper", L"RemoteSessionAction", L"throttle"));
    state.performance.batterySaverAction = ParsePerformanceAction(ReadText(path, L"Wallpaper", L"BatterySaverAction", L"throttle"));
    state.performance.lockedSessionAction = ParsePerformanceAction(ReadText(path, L"Wallpaper", L"LockedSessionAction", L"stop"));
    state.performance.idleAction = ParsePerformanceAction(ReadText(path, L"Wallpaper", L"IdleAction", L"throttle"));
    state.performance.idleThresholdSeconds = static_cast<DWORD>(std::clamp(ReadInt(path, L"IdleThresholdSeconds", 120), 30, 3600));
    return state;
}

GlobalWallpaperDescriptor GlobalFallback(const RuntimeState& state) {
    GlobalWallpaperDescriptor result;
    if (_wcsicmp(state.scene.c_str(), L"web") == 0 && WebWallpaperProcessSet::IsSupportedSource(state.source)) {
        result.kind = ResolvedWallpaperKind::Web;
        result.source = state.source;
    } else {
        result.kind = ResolvedWallpaperKind::Scene;
        result.sceneKey = _wcsicmp(state.scene.c_str(), L"neon") == 0 ? L"neon" :
                          _wcsicmp(state.scene.c_str(), L"grid") == 0 ? L"grid" : L"aurora";
    }
    return result;
}

std::vector<WebWallpaperRequest> DesiredRequests(HWND host, const RuntimeState& state) {
    std::vector<WebWallpaperRequest> requests;
    if (!host || !IsWindow(host) || !state.enabled) return requests;

    const MonitorTopology topology = QueryMonitorTopology();
    if (!topology.Valid()) return requests;

    if (state.layout != LayoutMode::Independent) {
        if (_wcsicmp(state.scene.c_str(), L"web") != 0 || !WebWallpaperProcessSet::IsSupportedSource(state.source)) return requests;
        const auto regions = DrawRegionsInHost(topology, state.layout);
        requests.reserve(regions.size());
        for (std::size_t i = 0; i < regions.size(); ++i) {
            WebWallpaperRequest request;
            request.region = regions[i];
            request.source = state.source;
            request.itemId = L"global-web-" + std::to_wstring(i);
            request.muted = true;
            requests.push_back(std::move(request));
        }
        return requests;
    }

    WallpaperLibrary library;
    WallpaperMonitorAssignments assignments;
    std::wstring ignored;
    if (!library.Load(&ignored) || !assignments.Load(&ignored)) return requests;
    const auto resolved = ResolveIndependentWallpapers(topology, assignments, library, GlobalFallback(state));
    for (const auto& item : resolved) {
        if (item.kind != ResolvedWallpaperKind::Web || !WebWallpaperProcessSet::IsSupportedSource(item.source.wstring())) continue;
        WebWallpaperRequest request;
        request.region = item.region;
        request.source = item.source.wstring();
        request.itemId = item.wallpaperId.empty() ? item.monitorId : item.wallpaperId;
        request.muted = true;
        requests.push_back(std::move(request));
    }
    return requests;
}

bool SameRequest(const WebWallpaperRequest& a, const WebWallpaperRequest& b) {
    return a.region.left == b.region.left && a.region.top == b.region.top &&
           a.region.right == b.region.right && a.region.bottom == b.region.bottom &&
           a.source == b.source && a.itemId == b.itemId && a.muted == b.muted;
}

bool SameRequests(const std::vector<WebWallpaperRequest>& a, const std::vector<WebWallpaperRequest>& b) {
    return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(), SameRequest);
}

void WriteDiagnostics(const std::wstring& value) {
    const fs::path path = WallpaperConfigPath();
    WritePrivateProfileStringW(L"Diagnostics", L"WebRuntime", value.c_str(), path.c_str());
}

void EnsureIndependentHostBounds(HWND host) {
    if (!host || !IsWindow(host)) return;
    const HWND parent = GetParent(host);
    if (!parent || !IsWindow(parent)) return;
    const MonitorTopology topology = QueryMonitorTopology();
    if (!topology.Valid()) return;
    const RECT desktopBounds = HostDesktopBounds(topology, LayoutMode::Independent);
    const RECT parentBounds = DesktopRectToParentClient(parent, desktopBounds);
    const LONG width = parentBounds.right - parentBounds.left;
    const LONG height = parentBounds.bottom - parentBounds.top;
    if (width <= 0 || height <= 0) return;
    SetWindowPos(host, nullptr, parentBounds.left, parentBounds.top, width, height,
                 SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

bool PersistGlobalWeb(const WallpaperLibraryItem& item, std::wstring* error) {
    const fs::path path = WallpaperConfigPath();
    bool ok = true;
    ok = WritePrivateProfileStringW(L"Wallpaper", L"Enabled", L"1", path.c_str()) != FALSE && ok;
    ok = WritePrivateProfileStringW(L"Wallpaper", L"Scene", L"web", path.c_str()) != FALSE && ok;
    ok = WritePrivateProfileStringW(L"Wallpaper", L"Image", item.source.c_str(), path.c_str()) != FALSE && ok;
    ok = WritePrivateProfileStringW(L"Wallpaper", L"Video", L"", path.c_str()) != FALSE && ok;
    WritePrivateProfileStringW(nullptr, nullptr, nullptr, path.c_str());
    if (!ok && error) *error = L"无法保存 Web 壁纸全局状态";
    return ok;
}

bool PersistMonitorWeb(const WallpaperLibraryItem& item, const std::wstring& targetMonitorId, std::wstring* error) {
    WallpaperMonitorAssignments assignments;
    if (!assignments.Load(error)) return false;
    if (!assignments.AssignById(targetMonitorId, item.id, {}, error)) return false;

    const fs::path path = WallpaperConfigPath();
    bool ok = true;
    ok = WritePrivateProfileStringW(L"Wallpaper", L"Enabled", L"1", path.c_str()) != FALSE && ok;
    ok = WritePrivateProfileStringW(L"Wallpaper", L"Layout", L"independent", path.c_str()) != FALSE && ok;
    WritePrivateProfileStringW(nullptr, nullptr, nullptr, path.c_str());
    if (!ok && error) *error = L"无法切换到 Independent Web 壁纸布局";
    return ok;
}

} // namespace

struct WallpaperWebRuntimeCoordinator::Impl {
    std::jthread worker;
    std::atomic_bool running{};

    void Run(std::stop_token stopToken) {
        running.store(true, std::memory_order_release);
        WebWallpaperProcessSet web;
        WallpaperPerformancePolicy performance;
        HWND host = nullptr;
        std::vector<WebWallpaperRequest> activeRequests;
        RuntimeState state;
        ULONGLONG nextRefresh = 0;
        ULONGLONG nextRecovery = 0;
        ULONGLONG healthySince = 0;
        unsigned recoveryAttempts = 0;

        auto resetRecovery = [&] {
            nextRecovery = 0;
            healthySince = 0;
            recoveryAttempts = 0;
        };

        auto startActiveRequests = [&](ULONGLONG now, bool recovery) {
            if (state.layout == LayoutMode::Independent && !activeRequests.empty())
                EnsureIndependentHostBounds(host);
            web.Stop();
            if (activeRequests.empty()) {
                resetRecovery();
                WriteDiagnostics(L"未启用 Web 壁纸");
                return true;
            }
            if (recovery) ++recoveryAttempts;
            if (!web.Start(host, activeRequests)) {
                nextRecovery = now + kRecoveryCooldownMs;
                healthySince = 0;
                WriteDiagnostics(L"Web runtime 启动失败：" + web.LastErrorText());
                return false;
            }
            nextRecovery = 0;
            healthySince = now;
            WriteDiagnostics(web.DiagnosticsText());
            return true;
        };

        while (!stopToken.stop_requested()) {
            HWND currentHost = FindWindowW(kWallpaperHostClass, nullptr);
            if (currentHost != host) {
                web.Stop();
                activeRequests.clear();
                host = currentHost;
                nextRefresh = 0;
                resetRecovery();
            }

            const ULONGLONG now = GetTickCount64();
            if (host && IsWindow(host) && now >= nextRefresh) {
                state = LoadRuntimeState(WallpaperConfigPath());
                const auto desired = DesiredRequests(host, state);
                if (!SameRequests(desired, activeRequests)) {
                    activeRequests = desired;
                    resetRecovery();
                    startActiveRequests(now, false);
                } else if (state.layout == LayoutMode::Independent && !activeRequests.empty()) {
                    EnsureIndependentHostBounds(host);
                }
                nextRefresh = now + kStateRefreshMs;
            }

            if (host && IsWindow(host) && !activeRequests.empty()) {
                web.Tick();

                if (!web.Active()) {
                    healthySince = 0;
                    if (recoveryAttempts >= kMaxRecoveryAttempts) {
                        WriteDiagnostics(L"Web runtime 连续恢复 3 次失败，已停止自动恢复：" + web.LastErrorText());
                    } else {
                        if (nextRecovery == 0) nextRecovery = now + kRecoveryCooldownMs;
                        if (now >= nextRecovery) startActiveRequests(now, true);
                    }
                } else {
                    if (healthySince == 0) healthySince = now;
                    if (recoveryAttempts > 0 && now - healthySince >= kRecoveryStableResetMs) {
                        recoveryAttempts = 0;
                        nextRecovery = 0;
                        WriteDiagnostics(web.DiagnosticsText() + L" · 已稳定运行，恢复计数已清零");
                    }
                }

                const HWND settings = FindWindowW(kWallpaperSettingsClass, nullptr);
                const auto snapshot = performance.Evaluate(host, settings, state.performance);
                const bool pause = !IsWindowVisible(host) ||
                    snapshot.action == PerformanceAction::Pause || snapshot.action == PerformanceAction::Stop;
                web.SetPaused(pause);
                const auto error = web.LastErrorText();
                if (!error.empty() && recoveryAttempts < kMaxRecoveryAttempts)
                    WriteDiagnostics(L"运行异常：" + error);
            }

            std::this_thread::sleep_for(kTickInterval);
        }

        web.Stop();
        WriteDiagnostics(L"Web runtime stopped");
        running.store(false, std::memory_order_release);
    }
};

WallpaperWebRuntimeCoordinator::WallpaperWebRuntimeCoordinator() : impl_(std::make_unique<Impl>()) {}
WallpaperWebRuntimeCoordinator::~WallpaperWebRuntimeCoordinator() { Stop(); }

bool WallpaperWebRuntimeCoordinator::Start() {
    if (!impl_) return false;
    if (impl_->worker.joinable()) return true;
    impl_->worker = std::jthread([impl = impl_.get()](std::stop_token token) { impl->Run(token); });
    return true;
}

void WallpaperWebRuntimeCoordinator::Stop() {
    if (!impl_ || !impl_->worker.joinable()) return;
    impl_->worker.request_stop();
    impl_->worker.join();
}

bool WallpaperWebRuntimeCoordinator::Running() const noexcept {
    return impl_ && impl_->running.load(std::memory_order_acquire);
}

bool WallpaperWebRuntimeCoordinator::SelfTest() {
    if (!WebWallpaperProcessSet::SelfTest()) return false;
    if (!WallpaperLibrary::IsTrustedWebUrl(L"https://example.com/wallpaper")) return false;
    if (WallpaperLibrary::IsTrustedWebUrl(L"http://example.com/wallpaper")) return false;

    WebWallpaperRequest a;
    a.region = {0, 0, 1920, 1080};
    a.source = L"https://example.com/wallpaper";
    a.itemId = L"web-a";
    WebWallpaperRequest b = a;
    if (!SameRequests({a}, {b})) return false;
    b.region.right = 1280;
    return !SameRequests({a}, {b});
}

bool ActivateWebWallpaperItem(const WallpaperLibraryItem& item,
                              const std::wstring& targetMonitorId,
                              std::wstring* error) {
    if (error) error->clear();
    if (item.kind != LibraryWallpaperKind::Web) {
        if (error) *error = L"该壁纸库项目不是 Web 类型";
        return false;
    }
    if (!WebWallpaperProcessSet::IsSupportedSource(item.source.wstring())) {
        if (error) *error = L"Web 壁纸源不可用；只允许存在的本地 HTML 或受信 HTTPS URL";
        return false;
    }
    return targetMonitorId.empty() ? PersistGlobalWeb(item, error)
                                   : PersistMonitorWeb(item, targetMonitorId, error);
}

} // namespace turingdesk::wallpaper

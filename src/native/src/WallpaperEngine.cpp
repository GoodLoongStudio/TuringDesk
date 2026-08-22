#include <windows.h>
#include <shellapi.h>
#include <commdlg.h>
#include <d2d1.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <wtsapi32.h>
#include "turingdesk/VideoWallpaperPlayer.h"
#include "turingdesk/VideoWallpaperSet.h"
#include "turingdesk/WallpaperMonitorLayout.h"
#include "turingdesk/WallpaperScaling.h"
#include "turingdesk/WallpaperPerformancePolicy.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cwchar>
#include <filesystem>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace {

constexpr wchar_t kControlClass[] = L"TuringDesk.Native.WallpaperControl";
constexpr wchar_t kHostClass[] = L"TuringDesk.Native.WallpaperHost";
constexpr wchar_t kSettingsClass[] = L"TuringDesk.Native.WallpaperSettings";
constexpr wchar_t kSelfTestClass[] = L"TuringDesk.Native.WallpaperSelfTest";
constexpr wchar_t kMutexName[] = L"Local\\TuringDesk.Native.Wallpaper.Singleton";
constexpr UINT kShowSettings = WM_APP + 81;
constexpr UINT kTrayMessage = WM_APP + 82;
constexpr UINT kSetEnabled = WM_APP + 83;
constexpr UINT_PTR kRenderTimer = 1;
constexpr UINT kTrayId = 1;
constexpr int kSceneComboId = 4101;
constexpr int kImageButtonId = 4102;
constexpr int kApplyButtonId = 4103;
constexpr int kToggleButtonId = 4104;
constexpr int kCloseButtonId = 4105;
constexpr int kPauseCheckId = 4106;
constexpr int kLayoutComboId = 4107;
constexpr int kScaleComboId = 4108;
constexpr int kHorizontalComboId = 4109;
constexpr int kVerticalComboId = 4110;
constexpr int kFpsComboId = 4111;
constexpr int kFullscreenActionComboId = 4112;
constexpr int kMaximizedActionComboId = 4113;
constexpr int kTraySettings = 4201;
constexpr int kTrayToggle = 4202;
constexpr int kTrayExit = 4203;
constexpr int kConfigVersion = 7;
constexpr LONG_PTR kRaisedDesktopFlag = WS_EX_NOREDIRECTIONBITMAP;

HMENU ControlId(int id) {
    return reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
}

struct Config {
    bool enabled{true};
    bool pauseFullscreen{true}; // V6 compatibility; V7 uses fullscreenAction.
    std::wstring scene{L"aurora"};
    std::wstring image;
    std::wstring video;
    std::wstring layout{L"span"};
    std::wstring scale{L"cover"};
    float focalX{0.5f};
    float focalY{0.5f};
    int fpsCap{30};
    int throttleFps{15};
    turingdesk::wallpaper::PerformanceAction fullscreenAction{turingdesk::wallpaper::PerformanceAction::Pause};
    turingdesk::wallpaper::PerformanceAction maximizedAction{turingdesk::wallpaper::PerformanceAction::Throttle};
    turingdesk::wallpaper::PerformanceAction remoteSessionAction{turingdesk::wallpaper::PerformanceAction::Throttle};
    turingdesk::wallpaper::PerformanceAction batterySaverAction{turingdesk::wallpaper::PerformanceAction::Throttle};
    turingdesk::wallpaper::PerformanceAction lockedSessionAction{turingdesk::wallpaper::PerformanceAction::Stop};
    turingdesk::wallpaper::PerformanceAction idleAction{turingdesk::wallpaper::PerformanceAction::Throttle};
    DWORD idleThresholdSeconds{120};
};

enum class MountMode {
    None,
    RaisedDesktop,
    LegacyWorkerW,
    ProgmanFallback,
};

struct DesktopLayer {
    HWND progman{};
    HWND defView{};
    HWND workerW{};
    HWND legacyDefViewParent{};
    bool raised{};
};

fs::path ConfigPath() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path dir = (length > 0 && length < std::size(local))
        ? fs::path(local) / L"TuringDesk"
        : fs::temp_directory_path() / L"TuringDesk";
    std::error_code ec;
    fs::create_directories(dir, ec);
    return dir / L"wallpaper.ini";
}

bool ValidScene(const std::wstring& scene) {
    return scene == L"aurora" || scene == L"neon" || scene == L"grid" || scene == L"image" || scene == L"video";
}

std::wstring FloatText(float value) {
    wchar_t text[32]{};
    swprintf_s(text, L"%.3f", value);
    return text;
}

float ReadProfileFloat(const std::wstring& path, const wchar_t* key, float fallback) {
    wchar_t text[64]{};
    const std::wstring fallbackText = FloatText(fallback);
    GetPrivateProfileStringW(L"Wallpaper", key, fallbackText.c_str(), text,
                             static_cast<DWORD>(std::size(text)), path.c_str());
    wchar_t* end = nullptr;
    const float value = std::wcstof(text, &end);
    if (end == text) return fallback;
    return turingdesk::wallpaper::ClampFocal(value);
}

std::wstring ReadProfileText(const std::wstring& path, const wchar_t* key, const wchar_t* fallback) {
    wchar_t text[256]{};
    GetPrivateProfileStringW(L"Wallpaper", key, fallback, text, static_cast<DWORD>(std::size(text)), path.c_str());
    return text;
}

void SaveConfig(const Config& config) {
    const auto path = ConfigPath().wstring();
    const auto version = std::to_wstring(kConfigVersion);
    WritePrivateProfileStringW(L"Wallpaper", L"Version", version.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Enabled", config.enabled ? L"1" : L"0", path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"PauseFullscreen",
                               config.fullscreenAction == turingdesk::wallpaper::PerformanceAction::Pause ? L"1" : L"0", path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Scene", config.scene.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Image", config.image.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Video", config.video.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Layout", config.layout.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Scale", config.scale.c_str(), path.c_str());
    const auto focalX = FloatText(config.focalX);
    const auto focalY = FloatText(config.focalY);
    WritePrivateProfileStringW(L"Wallpaper", L"FocalX", focalX.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"FocalY", focalY.c_str(), path.c_str());

    const auto fps = std::to_wstring(turingdesk::wallpaper::NormalizeFpsCap(config.fpsCap));
    const auto throttleFps = std::to_wstring(turingdesk::wallpaper::NormalizeFpsCap(config.throttleFps));
    const auto idleSeconds = std::to_wstring(config.idleThresholdSeconds);
    WritePrivateProfileStringW(L"Wallpaper", L"FpsCap", fps.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"ThrottleFps", throttleFps.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"FullscreenAction", turingdesk::wallpaper::PerformanceActionKey(config.fullscreenAction), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"MaximizedAction", turingdesk::wallpaper::PerformanceActionKey(config.maximizedAction), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"RemoteSessionAction", turingdesk::wallpaper::PerformanceActionKey(config.remoteSessionAction), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"BatterySaverAction", turingdesk::wallpaper::PerformanceActionKey(config.batterySaverAction), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"LockedSessionAction", turingdesk::wallpaper::PerformanceActionKey(config.lockedSessionAction), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"IdleAction", turingdesk::wallpaper::PerformanceActionKey(config.idleAction), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"IdleThresholdSeconds", idleSeconds.c_str(), path.c_str());
}

Config LoadConfig() {
    Config config;
    const auto path = ConfigPath().wstring();
    const int version = GetPrivateProfileIntW(L"Wallpaper", L"Version", 0, path.c_str());
    config.enabled = GetPrivateProfileIntW(L"Wallpaper", L"Enabled", 1, path.c_str()) != 0;
    config.pauseFullscreen = GetPrivateProfileIntW(L"Wallpaper", L"PauseFullscreen", 1, path.c_str()) != 0;

    config.scene = ReadProfileText(path, L"Scene", L"aurora");
    config.image = ReadProfileText(path, L"Image", L"");
    config.video = ReadProfileText(path, L"Video", L"");
    config.layout = turingdesk::wallpaper::LayoutModeKey(
        turingdesk::wallpaper::ParseLayoutMode(ReadProfileText(path, L"Layout", L"span")));
    config.scale = turingdesk::wallpaper::ScaleModeKey(
        turingdesk::wallpaper::ParseScaleMode(ReadProfileText(path, L"Scale", L"cover")));
    config.focalX = ReadProfileFloat(path, L"FocalX", 0.5f);
    config.focalY = ReadProfileFloat(path, L"FocalY", 0.5f);
    config.fpsCap = turingdesk::wallpaper::NormalizeFpsCap(GetPrivateProfileIntW(L"Wallpaper", L"FpsCap", 30, path.c_str()));
    config.throttleFps = turingdesk::wallpaper::NormalizeFpsCap(GetPrivateProfileIntW(L"Wallpaper", L"ThrottleFps", 15, path.c_str()));
    const int idleSeconds = static_cast<int>(GetPrivateProfileIntW(L"Wallpaper", L"IdleThresholdSeconds", 120, path.c_str()));
    config.idleThresholdSeconds = static_cast<DWORD>(std::clamp(idleSeconds, 30, 3600));

    if (version >= 7) {
        config.fullscreenAction = turingdesk::wallpaper::ParsePerformanceAction(ReadProfileText(path, L"FullscreenAction", L"pause"));
        config.maximizedAction = turingdesk::wallpaper::ParsePerformanceAction(ReadProfileText(path, L"MaximizedAction", L"throttle"));
        config.remoteSessionAction = turingdesk::wallpaper::ParsePerformanceAction(ReadProfileText(path, L"RemoteSessionAction", L"throttle"));
        config.batterySaverAction = turingdesk::wallpaper::ParsePerformanceAction(ReadProfileText(path, L"BatterySaverAction", L"throttle"));
        config.lockedSessionAction = turingdesk::wallpaper::ParsePerformanceAction(ReadProfileText(path, L"LockedSessionAction", L"stop"));
        config.idleAction = turingdesk::wallpaper::ParsePerformanceAction(ReadProfileText(path, L"IdleAction", L"throttle"));
    } else {
        config.fullscreenAction = config.pauseFullscreen
            ? turingdesk::wallpaper::PerformanceAction::Pause
            : turingdesk::wallpaper::PerformanceAction::Normal;
    }

    if (!ValidScene(config.scene)) config.scene = L"aurora";
    if (config.scene == L"image" && config.image.empty()) config.scene = L"aurora";
    if (config.scene == L"video" && config.video.empty()) config.scene = L"aurora";

    if (version < kConfigVersion) SaveConfig(config);
    return config;
}

bool HasExtendedStyle(HWND hwnd, LONG_PTR flag) {
    if (!hwnd) return false;
    return (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & flag) != 0;
}

void SpawnWallpaperLayer(HWND progman, bool raised) {
    if (!progman) return;
    DWORD_PTR ignored = 0;
    if (raised) {
        SendMessageTimeoutW(progman, 0x052C, 0xD, 0x1, SMTO_NORMAL, 1000, &ignored);
    } else {
        SendMessageTimeoutW(progman, 0x052C, 0, 0, SMTO_NORMAL, 1000, &ignored);
        SendMessageTimeoutW(progman, 0x052C, 0xD, 0x1, SMTO_NORMAL, 1000, &ignored);
    }
}

DesktopLayer DiscoverDesktopLayer() {
    DesktopLayer layer;
    layer.progman = FindWindowW(L"Progman", nullptr);
    if (!layer.progman) return layer;

    layer.raised = HasExtendedStyle(layer.progman, kRaisedDesktopFlag);
    SpawnWallpaperLayer(layer.progman, layer.raised);

    for (int attempt = 0; attempt < 4; ++attempt) {
        if (layer.raised) {
            layer.defView = FindWindowExW(layer.progman, nullptr, L"SHELLDLL_DefView", nullptr);
            layer.workerW = FindWindowExW(layer.progman, nullptr, L"WorkerW", nullptr);
            if (layer.defView && layer.workerW) return layer;
        }

        struct LegacySearch {
            HWND defView{};
            HWND defViewParent{};
            HWND worker{};
        } search;

        EnumWindows([](HWND top, LPARAM raw) -> BOOL {
            auto* result = reinterpret_cast<LegacySearch*>(raw);
            const HWND defView = FindWindowExW(top, nullptr, L"SHELLDLL_DefView", nullptr);
            if (!defView) return TRUE;
            result->defView = defView;
            result->defViewParent = top;
            result->worker = FindWindowExW(nullptr, top, L"WorkerW", nullptr);
            return result->worker ? FALSE : TRUE;
        }, reinterpret_cast<LPARAM>(&search));

        if (!layer.defView) layer.defView = search.defView;
        layer.legacyDefViewParent = search.defViewParent;
        if (!layer.workerW) layer.workerW = search.worker;

        if ((layer.raised && layer.defView) || (!layer.raised && layer.workerW)) return layer;
        Sleep(50);
        SpawnWallpaperLayer(layer.progman, layer.raised);
    }
    return layer;
}

bool TrySetParent(HWND child, HWND parent) {
    SetLastError(ERROR_SUCCESS);
    const HWND previous = SetParent(child, parent);
    return previous != nullptr || GetLastError() == ERROR_SUCCESS;
}

HWND LastChildWindow(HWND parent) {
    HWND last = nullptr;
    for (HWND child = GetWindow(parent, GW_CHILD); child; child = GetWindow(child, GW_HWNDNEXT)) last = child;
    return last;
}

void EnsureWorkerBottom(const DesktopLayer& layer) {
    if (!layer.raised || !layer.progman || !layer.workerW) return;
    if (LastChildWindow(layer.progman) == layer.workerW) return;
    SetWindowPos(layer.workerW, HWND_BOTTOM, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}

std::wstring MountModeText(MountMode mode) {
    switch (mode) {
    case MountMode::RaisedDesktop: return L"Windows 11 Raised Desktop";
    case MountMode::LegacyWorkerW: return L"WorkerW";
    case MountMode::ProgmanFallback: return L"Progman fallback";
    case MountMode::None: break;
    }
    return L"未挂载";
}

void SaveMountDiagnostics(MountMode mode, const std::wstring& error,
                          const turingdesk::wallpaper::MonitorTopology* topology = nullptr,
                          turingdesk::wallpaper::LayoutMode layout = turingdesk::wallpaper::LayoutMode::Span) {
    const auto path = ConfigPath().wstring();
    const auto modeText = MountModeText(mode);
    WritePrivateProfileStringW(L"Diagnostics", L"MountMode", modeText.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Diagnostics", L"LastMountError", error.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Diagnostics", L"LayoutMode", turingdesk::wallpaper::LayoutModeKey(layout), path.c_str());
    if (topology) {
        const auto monitorCount = std::to_wstring(topology->monitors.size());
        const auto description = turingdesk::wallpaper::DescribeMonitorTopology(*topology);
        WritePrivateProfileStringW(L"Diagnostics", L"MonitorCount", monitorCount.c_str(), path.c_str());
        WritePrivateProfileStringW(L"Diagnostics", L"MonitorTopology", description.c_str(), path.c_str());
    }
}

class WallpaperApp {
public:
    explicit WallpaperApp(HINSTANCE instance) : instance_(instance), config_(LoadConfig()) {}

    ~WallpaperApp() {
        if (control_) WTSUnRegisterSessionNotification(control_);
        videoSet_.Stop();
        RemoveTray();
        if (settings_ && IsWindow(settings_)) DestroyWindow(settings_);
        if (host_ && IsWindow(host_)) DestroyWindow(host_);
        if (control_ && IsWindow(control_)) DestroyWindow(control_);
    }

    bool Create() {
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory_.GetAddressOf()))) return false;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_PPV_ARGS(wicFactory_.GetAddressOf())))) return false;

        taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
        topology_ = turingdesk::wallpaper::QueryMonitorTopology();

        WNDCLASSEXW controlClass{};
        controlClass.cbSize = sizeof(controlClass);
        controlClass.hInstance = instance_;
        controlClass.lpfnWndProc = &WallpaperApp::ControlProc;
        controlClass.lpszClassName = kControlClass;
        controlClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!RegisterClassExW(&controlClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        WNDCLASSEXW hostClass{};
        hostClass.cbSize = sizeof(hostClass);
        hostClass.hInstance = instance_;
        hostClass.lpfnWndProc = &WallpaperApp::HostProc;
        hostClass.lpszClassName = kHostClass;
        hostClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!RegisterClassExW(&hostClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        control_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kControlClass,
                                   L"TuringDesk Wallpaper Control", WS_POPUP,
                                   0, 0, 1, 1, nullptr, nullptr, instance_, this);
        if (!control_) return false;
        WTSRegisterSessionNotification(control_, NOTIFY_FOR_THIS_SESSION);

        host_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                                kHostClass, L"TuringDesk Wallpaper Host", WS_POPUP,
                                0, 0, 1, 1, nullptr, nullptr, instance_, this);
        if (!host_) return false;
        if (!SetLayeredWindowAttributes(host_, 0, 255, LWA_ALPHA)) return false;

        AttachToDesktop();
        AddTray();
        ApplyConfig(config_, false);
        SetRenderTimerFps(config_.fpsCap);
        return true;
    }

    int Run() {
        MSG msg{};
        while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        return static_cast<int>(msg.wParam);
    }

    void ShowSettings() {
        if (settings_ && IsWindow(settings_)) {
            ShowWindow(settings_, SW_RESTORE);
            SetForegroundWindow(settings_);
            RefreshSettings();
            return;
        }

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance_;
        wc.lpfnWndProc = &WallpaperApp::SettingsProc;
        wc.lpszClassName = kSettingsClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return;

        settings_ = CreateWindowExW(WS_EX_TOOLWINDOW, kSettingsClass, L"TuringDesk 壁纸",
                                    WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
                                    CW_USEDEFAULT, CW_USEDEFAULT, 760, 700,
                                    nullptr, nullptr, instance_, this);
        if (!settings_) return;

        const HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto label = [&](const wchar_t* text, int x, int y, int w, int h) {
            HWND control = CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                           x, y, w, h, settings_, nullptr, instance_, nullptr);
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };

        label(L"TuringDesk 壁纸", 20, 18, 240, 26);
        label(L"场景", 20, 62, 72, 24);
        sceneCombo_ = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                      116, 58, 350, 180, settings_, ControlId(kSceneComboId), instance_, nullptr);
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Aurora Flow · 极光流动"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Neon Flow · 霓虹网格"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Quiet Grid · 静谧网格"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"图片壁纸"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"视频壁纸 · Media Foundation"));
        imageButton_ = CreateWindowExW(0, L"BUTTON", L"选择图片 / 视频…", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       486, 58, 210, 30, settings_, ControlId(kImageButtonId), instance_, nullptr);

        label(L"多屏布局", 20, 106, 88, 24);
        layoutCombo_ = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                       116, 102, 350, 150, settings_, ControlId(kLayoutComboId), instance_, nullptr);
        SendMessageW(layoutCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"跨屏延展 · Span"));
        SendMessageW(layoutCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"每屏独立填充 · Clone"));
        SendMessageW(layoutCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"仅主显示器 · Primary"));

        label(L"缩放", 20, 150, 72, 24);
        scaleCombo_ = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                      116, 146, 350, 180, settings_, ControlId(kScaleComboId), instance_, nullptr);
        SendMessageW(scaleCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"填充裁切 · Cover"));
        SendMessageW(scaleCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"适应 · Contain"));
        SendMessageW(scaleCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"拉伸 · Stretch"));
        SendMessageW(scaleCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"居中原尺寸 · Center"));
        SendMessageW(scaleCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"平铺 · Tile"));

        label(L"焦点", 20, 194, 72, 24);
        horizontalCombo_ = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                           116, 190, 168, 120, settings_, ControlId(kHorizontalComboId), instance_, nullptr);
        SendMessageW(horizontalCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"左"));
        SendMessageW(horizontalCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"水平居中"));
        SendMessageW(horizontalCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"右"));
        verticalCombo_ = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                         298, 190, 168, 120, settings_, ControlId(kVerticalComboId), instance_, nullptr);
        SendMessageW(verticalCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"上"));
        SendMessageW(verticalCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"垂直居中"));
        SendMessageW(verticalCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"下"));

        label(L"帧率上限", 20, 238, 88, 24);
        fpsCombo_ = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                    116, 234, 168, 160, settings_, ControlId(kFpsComboId), instance_, nullptr);
        for (const wchar_t* fps : {L"15 FPS", L"30 FPS", L"45 FPS", L"60 FPS", L"120 FPS"})
            SendMessageW(fpsCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(fps));

        label(L"全屏应用", 20, 282, 88, 24);
        fullscreenActionCombo_ = CreateActionCombo(116, 278, kFullscreenActionComboId);
        label(L"最大化应用", 330, 282, 96, 24);
        maximizedActionCombo_ = CreateActionCombo(438, 278, kMaximizedActionComboId);

        label(L"系统策略", 20, 326, 88, 24);
        label(L"远程桌面/节能/Idle 默认降频；锁屏默认停止。降频目标 15 FPS。", 116, 326, 580, 42);
        status_ = label(L"", 20, 382, 688, 150);

        applyButton_ = CreateWindowExW(0, L"BUTTON", L"应用到桌面", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                       408, 574, 112, 36, settings_, ControlId(kApplyButtonId), instance_, nullptr);
        toggleButton_ = CreateWindowExW(0, L"BUTTON", L"停止", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                        530, 574, 84, 36, settings_, ControlId(kToggleButtonId), instance_, nullptr);
        closeButton_ = CreateWindowExW(0, L"BUTTON", L"关闭", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       624, 574, 84, 36, settings_, ControlId(kCloseButtonId), instance_, nullptr);

        for (HWND control : {sceneCombo_, imageButton_, layoutCombo_, scaleCombo_, horizontalCombo_, verticalCombo_,
                             fpsCombo_, fullscreenActionCombo_, maximizedActionCombo_, applyButton_, toggleButton_, closeButton_}) {
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        }
        SendMessageW(status_, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);

        RefreshSettings();
        ShowWindow(settings_, SW_SHOWNORMAL);
        SetForegroundWindow(settings_);
    }

    void SetEnabled(bool enabled) {
        config_.enabled = enabled;
        SaveConfig(config_);
        if (enabled) {
            AttachToDesktop();
            ShowWindow(host_, SW_SHOWNOACTIVATE);
            performanceStopped_ = false;
            if (config_.scene == L"video") {
                if (!videoSet_.Active()) StartVideo();
                videoSet_.SetPaused(false);
            } else {
                InvalidateRect(host_, nullptr, FALSE);
                UpdateWindow(host_);
            }
        } else {
            videoSet_.SetPaused(true);
            ShowWindow(host_, SW_HIDE);
        }
        RefreshSettings();
    }

private:
    HWND CreateActionCombo(int x, int y, int id) {
        HWND combo = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                     x, y, 176, 150, settings_, ControlId(id), instance_, nullptr);
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"继续运行"));
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"降频"));
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"暂停"));
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"停止/隐藏"));
        return combo;
    }

    static int ActionIndex(turingdesk::wallpaper::PerformanceAction action) {
        return static_cast<int>(action);
    }

    static turingdesk::wallpaper::PerformanceAction ActionFromCombo(HWND combo) {
        const int selected = combo ? static_cast<int>(SendMessageW(combo, CB_GETCURSEL, 0, 0)) : 0;
        if (selected == 3) return turingdesk::wallpaper::PerformanceAction::Stop;
        if (selected == 2) return turingdesk::wallpaper::PerformanceAction::Pause;
        if (selected == 1) return turingdesk::wallpaper::PerformanceAction::Throttle;
        return turingdesk::wallpaper::PerformanceAction::Normal;
    }

    turingdesk::wallpaper::PerformanceConfig CurrentPerformanceConfig() const {
        turingdesk::wallpaper::PerformanceConfig result;
        result.fpsCap = config_.fpsCap;
        result.throttleFps = config_.throttleFps;
        result.fullscreenAction = config_.fullscreenAction;
        result.maximizedAction = config_.maximizedAction;
        result.remoteSessionAction = config_.remoteSessionAction;
        result.batterySaverAction = config_.batterySaverAction;
        result.lockedSessionAction = config_.lockedSessionAction;
        result.idleAction = config_.idleAction;
        result.idleThresholdSeconds = config_.idleThresholdSeconds;
        return result;
    }

    void SetRenderTimerFps(int fps) {
        if (!control_) return;
        const UINT interval = fps > 0 ? static_cast<UINT>(std::max(8, 1000 / turingdesk::wallpaper::NormalizeFpsCap(fps))) : 250U;
        if (renderTimerIntervalMs_ == interval) return;
        KillTimer(control_, kRenderTimer);
        SetTimer(control_, kRenderTimer, interval, nullptr);
        renderTimerIntervalMs_ = interval;
    }

    void ApplyPerformanceSnapshot(const turingdesk::wallpaper::PerformanceSnapshot& snapshot) {
        currentPerformance_ = snapshot;
        SetRenderTimerFps(snapshot.targetFps);

        const bool stop = snapshot.action == turingdesk::wallpaper::PerformanceAction::Stop;
        const bool pause = snapshot.action == turingdesk::wallpaper::PerformanceAction::Pause || stop;
        if (config_.scene == L"video") videoSet_.SetPaused(pause);

        if (stop) {
            if (!performanceStopped_) {
                ShowWindow(host_, SW_HIDE);
                performanceStopped_ = true;
            }
            return;
        }

        if (performanceStopped_) {
            AttachToDesktop();
            ShowWindow(host_, SW_SHOWNOACTIVATE);
            performanceStopped_ = false;
            if (config_.scene == L"video" && !videoSet_.Active()) StartVideo();
        }

        if (snapshot.action == turingdesk::wallpaper::PerformanceAction::Pause) return;
        if (config_.scene != L"video" && config_.image.empty()) {
            const int fps = std::max(1, snapshot.targetFps);
            time_ += 1.0f / static_cast<float>(fps);
            InvalidateRect(host_, nullptr, FALSE);
        }
    }

    static LRESULT CALLBACK ControlProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<WallpaperApp*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<WallpaperApp*>(create->lpCreateParams);
            self->control_ = hwnd;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        return self ? self->HandleControl(message, wParam, lParam) : DefWindowProcW(hwnd, message, wParam, lParam);
    }

    static LRESULT CALLBACK HostProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<WallpaperApp*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<WallpaperApp*>(create->lpCreateParams);
            self->host_ = hwnd;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        return self ? self->HandleHost(message, wParam, lParam) : DefWindowProcW(hwnd, message, wParam, lParam);
    }

    static LRESULT CALLBACK SettingsProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<WallpaperApp*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<WallpaperApp*>(create->lpCreateParams);
            self->settings_ = hwnd;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (!self) return DefWindowProcW(hwnd, message, wParam, lParam);

        if (message == WM_COMMAND) {
            switch (LOWORD(wParam)) {
            case kImageButtonId:
                if (HIWORD(wParam) == BN_CLICKED) self->ChooseImage();
                return 0;
            case kApplyButtonId:
                if (HIWORD(wParam) == BN_CLICKED) self->ApplyFromSettings();
                return 0;
            case kToggleButtonId:
                if (HIWORD(wParam) == BN_CLICKED) self->SetEnabled(!self->config_.enabled);
                return 0;
            case kCloseButtonId:
                if (HIWORD(wParam) == BN_CLICKED) ShowWindow(hwnd, SW_HIDE);
                return 0;
            }
        }
        if (message == WM_CLOSE) {
            ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        if (message == WM_DESTROY) {
            self->settings_ = nullptr;
            self->sceneCombo_ = nullptr;
            self->imageButton_ = nullptr;
            self->layoutCombo_ = nullptr;
            self->scaleCombo_ = nullptr;
            self->horizontalCombo_ = nullptr;
            self->verticalCombo_ = nullptr;
            self->fpsCombo_ = nullptr;
            self->fullscreenActionCombo_ = nullptr;
            self->maximizedActionCombo_ = nullptr;
            self->applyButton_ = nullptr;
            self->toggleButton_ = nullptr;
            self->closeButton_ = nullptr;
            self->status_ = nullptr;
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    LRESULT HandleControl(UINT message, WPARAM wParam, LPARAM lParam) {
        if (taskbarCreated_ != 0 && message == taskbarCreated_) {
            trayAdded_ = false;
            AddTray();
            topology_ = turingdesk::wallpaper::QueryMonitorTopology();
            AttachToDesktop();
            if (config_.scene == L"video") StartVideo();
            return 0;
        }

        switch (message) {
        case kShowSettings:
            ShowSettings();
            return 0;
        case kSetEnabled:
            SetEnabled(wParam != 0);
            return 0;
        case kTrayMessage:
            HandleTray(static_cast<UINT>(lParam));
            return 0;
        case WM_WTSSESSION_CHANGE:
            if (wParam == WTS_SESSION_LOCK) performancePolicy_.SetSessionLocked(true);
            else if (wParam == WTS_SESSION_UNLOCK) performancePolicy_.SetSessionLocked(false);
            ApplyPerformanceSnapshot(performancePolicy_.Evaluate(host_, settings_, CurrentPerformanceConfig()));
            RefreshSettings();
            return 0;
        case WM_TIMER:
            if (wParam == kRenderTimer) {
                ++healthTicks_;
                if (healthTicks_ >= 150) {
                    healthTicks_ = 0;
                    if (!mountOk_ || !attachedParent_ || !IsWindow(attachedParent_) || GetParent(host_) != attachedParent_) {
                        topology_ = turingdesk::wallpaper::QueryMonitorTopology();
                        AttachToDesktop();
                        if (config_.scene == L"video") StartVideo();
                        RefreshSettings();
                    }
                }
                if (config_.enabled) {
                    if (config_.scene == L"video") {
                        videoSet_.Tick();
                        const auto mediaError = videoSet_.LastErrorText();
                        if (mediaError != lastMediaError_) {
                            lastMediaError_ = mediaError;
                            RefreshSettings();
                        }
                    }
                    ApplyPerformanceSnapshot(performancePolicy_.Evaluate(host_, settings_, CurrentPerformanceConfig()));
                }
            }
            return 0;
        case WM_DISPLAYCHANGE:
            topology_ = turingdesk::wallpaper::QueryMonitorTopology();
            AttachToDesktop();
            if (config_.scene == L"video") StartVideo();
            RefreshSettings();
            return 0;
        case WM_CLOSE:
            RemoveTray();
            if (settings_ && IsWindow(settings_)) DestroyWindow(settings_);
            if (host_ && IsWindow(host_)) DestroyWindow(host_);
            DestroyWindow(control_);
            return 0;
        case WM_DESTROY:
            WTSUnRegisterSessionNotification(control_);
            KillTimer(control_, kRenderTimer);
            control_ = nullptr;
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(control_, message, wParam, lParam);
    }

    LRESULT HandleHost(UINT message, WPARAM wParam, LPARAM lParam) {
        switch (message) {
        case WM_NCHITTEST:
            return HTTRANSPARENT;
        case WM_DISPLAYCHANGE:
            topology_ = turingdesk::wallpaper::QueryMonitorTopology();
            AttachToDesktop();
            return 0;
        case WM_SIZE:
            if (renderTarget_) renderTarget_->Resize(D2D1::SizeU(LOWORD(lParam), HIWORD(lParam)));
            return 0;
        case WM_PAINT: {
            PAINTSTRUCT paint{};
            BeginPaint(host_, &paint);
            Draw();
            EndPaint(host_, &paint);
            return 0;
        }
        case WM_ERASEBKGND:
            return 1;
        case WM_DESTROY:
            host_ = nullptr;
            return 0;
        }
        return DefWindowProcW(host_, message, wParam, lParam);
    }

    void ResetGraphics() {
        imageBitmap_.Reset();
        brush_.Reset();
        renderTarget_.Reset();
    }

    bool PrepareChildWindow() {
        LONG_PTR style = GetWindowLongPtrW(host_, GWL_STYLE);
        style &= ~static_cast<LONG_PTR>(WS_POPUP);
        style |= WS_CHILD;
        SetWindowLongPtrW(host_, GWL_STYLE, style);

        LONG_PTR exStyle = GetWindowLongPtrW(host_, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
        SetWindowLongPtrW(host_, GWL_EXSTYLE, exStyle);
        return SetLayeredWindowAttributes(host_, 0, 255, LWA_ALPHA) != FALSE;
    }

    bool AttachToDesktop() {
        mountOk_ = false;
        lastMountError_.clear();
        const auto layoutMode = turingdesk::wallpaper::ParseLayoutMode(config_.layout);

        if (!host_ || !IsWindow(host_)) {
            lastMountError_ = L"Wallpaper host window 不存在";
            SaveMountDiagnostics(MountMode::None, lastMountError_, &topology_, layoutMode);
            return false;
        }

        topology_ = turingdesk::wallpaper::QueryMonitorTopology();
        if (!topology_.Valid()) {
            mountMode_ = MountMode::None;
            attachedParent_ = nullptr;
            lastMountError_ = L"没有检测到有效的 Windows 显示器拓扑";
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        const DesktopLayer layer = DiscoverDesktopLayer();
        if (!layer.progman) {
            mountMode_ = MountMode::None;
            attachedParent_ = nullptr;
            lastMountError_ = L"找不到 Windows Progman 桌面窗口";
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        const HWND oldParent = GetParent(host_);
        const MountMode oldMode = mountMode_;
        HWND targetParent = nullptr;
        HWND insertAfter = HWND_BOTTOM;
        MountMode nextMode = MountMode::None;

        if (layer.raised && layer.defView) {
            targetParent = layer.progman;
            insertAfter = layer.defView;
            nextMode = MountMode::RaisedDesktop;
        } else if (layer.workerW) {
            targetParent = layer.workerW;
            insertAfter = HWND_TOP;
            nextMode = MountMode::LegacyWorkerW;
        } else if (layer.progman) {
            targetParent = layer.progman;
            insertAfter = (layer.defView && GetParent(layer.defView) == layer.progman) ? layer.defView : HWND_BOTTOM;
            nextMode = MountMode::ProgmanFallback;
        }

        if (!targetParent || !PrepareChildWindow()) {
            mountMode_ = MountMode::None;
            attachedParent_ = nullptr;
            lastMountError_ = L"无法准备桌面 Layered HWND";
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        if (!TrySetParent(host_, targetParent)) {
            mountMode_ = MountMode::None;
            attachedParent_ = nullptr;
            lastMountError_ = L"SetParent 失败，Win32=" + std::to_wstring(GetLastError());
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        mountMode_ = nextMode;
        attachedParent_ = targetParent;
        if (oldParent != targetParent || oldMode != mountMode_) ResetGraphics();

        const RECT desktopBounds = turingdesk::wallpaper::HostDesktopBounds(topology_, layoutMode);
        const RECT parentBounds = turingdesk::wallpaper::DesktopRectToParentClient(targetParent, desktopBounds);
        const LONG width = parentBounds.right - parentBounds.left;
        const LONG height = parentBounds.bottom - parentBounds.top;
        if (width <= 0 || height <= 0) {
            lastMountError_ = L"显示器布局映射到桌面层后尺寸无效";
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        UINT flags = SWP_NOACTIVATE | SWP_FRAMECHANGED;
        flags |= config_.enabled ? SWP_SHOWWINDOW : SWP_HIDEWINDOW;
        if (!SetWindowPos(host_, insertAfter, parentBounds.left, parentBounds.top, width, height, flags)) {
            lastMountError_ = L"SetWindowPos 失败，Win32=" + std::to_wstring(GetLastError());
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        if (mountMode_ == MountMode::RaisedDesktop) EnsureWorkerBottom(layer);

        if (GetParent(host_) != targetParent) {
            lastMountError_ = L"桌面父窗口校验失败";
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        RECT hostRect{};
        if (!GetClientRect(host_, &hostRect) || hostRect.right <= hostRect.left || hostRect.bottom <= hostRect.top) {
            lastMountError_ = L"Wallpaper HWND 没有可绘制区域";
            SaveMountDiagnostics(mountMode_, lastMountError_, &topology_, layoutMode);
            return false;
        }

        mountOk_ = true;
        lastMountError_.clear();
        SaveMountDiagnostics(mountMode_, L"", &topology_, layoutMode);
        if (config_.enabled && !performanceStopped_) {
            ShowWindow(host_, SW_SHOWNOACTIVATE);
            InvalidateRect(host_, nullptr, FALSE);
            UpdateWindow(host_);
        }
        return true;
    }

    void AddTray() {
        if (trayAdded_ || !control_) return;
        tray_ = {};
        tray_.cbSize = sizeof(tray_);
        tray_.hWnd = control_;
        tray_.uID = kTrayId;
        tray_.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        tray_.uCallbackMessage = kTrayMessage;
        tray_.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        wcscpy_s(tray_.szTip, L"TuringDesk Wallpaper");
        trayAdded_ = Shell_NotifyIconW(NIM_ADD, &tray_) != FALSE;
    }

    void RemoveTray() {
        if (!trayAdded_) return;
        Shell_NotifyIconW(NIM_DELETE, &tray_) != FALSE;
        trayAdded_ = false;
    }

    void HandleTray(UINT mouseMessage) {
        if (mouseMessage == WM_LBUTTONDBLCLK || mouseMessage == WM_LBUTTONUP) {
            ShowSettings();
            return;
        }
        if (mouseMessage != WM_RBUTTONUP && mouseMessage != WM_CONTEXTMENU) return;

        HMENU menu = CreatePopupMenu();
        if (!menu) return;
        AppendMenuW(menu, MF_STRING, kTraySettings, L"壁纸设置");
        AppendMenuW(menu, MF_STRING, kTrayToggle, config_.enabled ? L"停止壁纸" : L"恢复壁纸");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, kTrayExit, L"退出壁纸引擎");

        POINT point{};
        GetCursorPos(&point);
        SetForegroundWindow(control_);
        const int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                                           point.x, point.y, 0, control_, nullptr);
        DestroyMenu(menu);
        if (command == kTraySettings) ShowSettings();
        else if (command == kTrayToggle) SetEnabled(!config_.enabled);
        else if (command == kTrayExit) PostMessageW(control_, WM_CLOSE, 0, 0);
    }

    void EnsureRenderTarget() {
        if (renderTarget_ || !host_) return;
        RECT rc{};
        GetClientRect(host_, &rc);
        const UINT width = static_cast<UINT>(std::max<LONG>(1, rc.right - rc.left));
        const UINT height = static_cast<UINT>(std::max<LONG>(1, rc.bottom - rc.top));
        const auto properties = D2D1::HwndRenderTargetProperties(host_, D2D1::SizeU(width, height), D2D1_PRESENT_OPTIONS_IMMEDIATELY);
        if (FAILED(d2dFactory_->CreateHwndRenderTarget(D2D1::RenderTargetProperties(), properties, renderTarget_.GetAddressOf()))) return;
        renderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), brush_.GetAddressOf());
        LoadImage();
    }

    void LoadImage() {
        imageBitmap_.Reset();
        if (!renderTarget_ || config_.image.empty()) return;
        ComPtr<IWICBitmapDecoder> decoder;
        if (FAILED(wicFactory_->CreateDecoderFromFilename(config_.image.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, decoder.GetAddressOf()))) return;
        ComPtr<IWICBitmapFrameDecode> frame;
        if (FAILED(decoder->GetFrame(0, frame.GetAddressOf()))) return;
        ComPtr<IWICFormatConverter> converter;
        if (FAILED(wicFactory_->CreateFormatConverter(converter.GetAddressOf()))) return;
        if (FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone,
                                         nullptr, 0.0, WICBitmapPaletteTypeMedianCut))) return;
        renderTarget_->CreateBitmapFromWicBitmap(converter.Get(), nullptr, imageBitmap_.GetAddressOf());
    }

    void Draw() {
        EnsureRenderTarget();
        if (!renderTarget_ || !brush_ || !config_.enabled || performanceStopped_) return;
        if (config_.scene == L"video" && videoSet_.Active()) return;

        renderTarget_->BeginDraw();
        renderTarget_->SetTransform(D2D1::Matrix3x2F::Identity());
        renderTarget_->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f));

        auto regions = turingdesk::wallpaper::DrawRegionsInHost(topology_, turingdesk::wallpaper::ParseLayoutMode(config_.layout));
        if (regions.empty()) {
            const auto size = renderTarget_->GetSize();
            regions.push_back(RECT{0, 0, static_cast<LONG>(size.width), static_cast<LONG>(size.height)});
        }

        for (const RECT& region : regions) {
            if (region.right <= region.left || region.bottom <= region.top) continue;
            const D2D1_RECT_F clip = D2D1::RectF(static_cast<float>(region.left), static_cast<float>(region.top),
                                                  static_cast<float>(region.right), static_cast<float>(region.bottom));
            renderTarget_->PushAxisAlignedClip(clip, D2D1_ANTIALIAS_MODE_ALIASED);
            renderTarget_->SetTransform(D2D1::Matrix3x2F::Translation(static_cast<float>(region.left), static_cast<float>(region.top)));
            const D2D1_SIZE_F size = D2D1::SizeF(static_cast<float>(region.right - region.left), static_cast<float>(region.bottom - region.top));

            if (!config_.image.empty() && imageBitmap_) DrawImage(size);
            else if (config_.scene == L"neon") DrawNeon(size);
            else if (config_.scene == L"grid") DrawGrid(size);
            else DrawAurora(size);

            renderTarget_->SetTransform(D2D1::Matrix3x2F::Identity());
            renderTarget_->PopAxisAlignedClip();
        }

        const HRESULT hr = renderTarget_->EndDraw();
        if (hr == D2DERR_RECREATE_TARGET) ResetGraphics();
    }

    void FillRegionBackground(const D2D1_SIZE_F& size, const D2D1_COLOR_F& color) {
        brush_->SetColor(color);
        renderTarget_->FillRectangle(D2D1::RectF(0.0f, 0.0f, size.width, size.height), brush_.Get());
    }

    void DrawImage(const D2D1_SIZE_F& target) {
        FillRegionBackground(target, D2D1::ColorF(0.0f, 0.0f, 0.0f));
        const auto sourceSize = imageBitmap_->GetSize();
        const auto mode = turingdesk::wallpaper::ParseScaleMode(config_.scale);
        const auto placement = turingdesk::wallpaper::ComputePlacement(sourceSize.width, sourceSize.height, target.width, target.height,
                                                                        mode, config_.focalX, config_.focalY);
        const D2D1_RECT_F source = D2D1::RectF(placement.source.left, placement.source.top, placement.source.right, placement.source.bottom);

        if (placement.tiled) {
            const float tileWidth = std::max(1.0f, placement.destination.right - placement.destination.left);
            const float tileHeight = std::max(1.0f, placement.destination.bottom - placement.destination.top);
            constexpr int kMaxTileDraws = 4096;
            int draws = 0;
            for (float y = 0.0f; y < target.height && draws < kMaxTileDraws; y += tileHeight) {
                for (float x = 0.0f; x < target.width && draws < kMaxTileDraws; x += tileWidth) {
                    const D2D1_RECT_F destination = D2D1::RectF(x, y, x + tileWidth, y + tileHeight);
                    renderTarget_->DrawBitmap(imageBitmap_.Get(), destination, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, &source);
                    ++draws;
                }
            }
            return;
        }

        const D2D1_RECT_F destination = D2D1::RectF(placement.destination.left, placement.destination.top,
                                                     placement.destination.right, placement.destination.bottom);
        renderTarget_->DrawBitmap(imageBitmap_.Get(), destination, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, &source);
    }

    void DrawAurora(const D2D1_SIZE_F& size) {
        FillRegionBackground(size, D2D1::ColorF(0.008f, 0.014f, 0.050f));
        const std::array<D2D1_COLOR_F, 6> colors = {
            D2D1::ColorF(0.04f, 0.95f, 0.72f, 0.28f), D2D1::ColorF(0.10f, 0.52f, 1.00f, 0.30f),
            D2D1::ColorF(0.62f, 0.18f, 1.00f, 0.27f), D2D1::ColorF(0.05f, 0.82f, 0.98f, 0.24f),
            D2D1::ColorF(0.20f, 0.98f, 0.50f, 0.22f), D2D1::ColorF(0.86f, 0.16f, 0.94f, 0.20f),
        };
        for (int i = 0; i < static_cast<int>(colors.size()); ++i) {
            const float phase = time_ * (0.30f + i * 0.018f) + i * 1.13f;
            const float x = size.width * (0.08f + i * 0.18f) + static_cast<float>(std::sin(phase)) * size.width * 0.14f;
            const float y = size.height * (0.34f + 0.22f * static_cast<float>(std::sin(phase * 0.77f + i)));
            brush_->SetColor(colors[static_cast<std::size_t>(i)]);
            renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), size.width * 0.30f, size.height * 0.38f), brush_.Get());
        }
        for (int i = 0; i < 4; ++i) {
            const float y = size.height * (0.18f + i * 0.19f) + static_cast<float>(std::sin(time_ * 0.45f + i)) * 32.0f;
            brush_->SetColor(D2D1::ColorF(0.30f, 0.90f, 1.00f, 0.16f));
            renderTarget_->FillRectangle(D2D1::RectF(0.0f, y, size.width, y + 22.0f), brush_.Get());
        }
    }

    void DrawNeon(const D2D1_SIZE_F& size) {
        FillRegionBackground(size, D2D1::ColorF(0.004f, 0.006f, 0.020f));
        constexpr float spacing = 58.0f;
        const float offset = static_cast<float>(std::fmod(time_ * 30.0f, spacing));
        brush_->SetColor(D2D1::ColorF(0.02f, 0.82f, 1.00f, 0.42f));
        for (float x = -spacing + offset; x < size.width + spacing; x += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get(), 1.4f);
        brush_->SetColor(D2D1::ColorF(0.92f, 0.04f, 0.84f, 0.34f));
        for (float y = -spacing + offset; y < size.height + spacing; y += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get(), 1.2f);
        for (int i = 0; i < 7; ++i) {
            const float x = static_cast<float>(std::fmod(time_ * (82.0f + i * 13.0f) + i * size.width * 0.17f, size.width + 320.0f)) - 160.0f;
            brush_->SetColor((i % 2) == 0 ? D2D1::ColorF(0.00f, 0.82f, 1.00f, 0.22f) : D2D1::ColorF(1.00f, 0.08f, 0.80f, 0.20f));
            renderTarget_->FillRectangle(D2D1::RectF(x, size.height * 0.10f, x + 120.0f, size.height * 0.90f), brush_.Get());
        }
    }

    void DrawGrid(const D2D1_SIZE_F& size) {
        FillRegionBackground(size, D2D1::ColorF(0.020f, 0.026f, 0.034f));
        constexpr float spacing = 64.0f;
        brush_->SetColor(D2D1::ColorF(0.22f, 0.32f, 0.40f, 0.62f));
        for (float x = 0; x < size.width; x += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get());
        for (float y = 0; y < size.height; y += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get());
        const float pulse = 0.5f + 0.5f * static_cast<float>(std::sin(time_ * 0.72f));
        brush_->SetColor(D2D1::ColorF(0.15f, 0.66f, 0.82f, 0.14f + pulse * 0.10f));
        renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(size.width * 0.5f, size.height * 0.5f),
                                                 size.width * (0.18f + pulse * 0.05f), size.height * (0.20f + pulse * 0.05f)), brush_.Get());
    }

    bool ApplyConfig(const Config& next, bool persist = true) {
        videoSet_.Stop();
        lastMediaError_.clear();
        config_ = next;
        config_.layout = turingdesk::wallpaper::LayoutModeKey(turingdesk::wallpaper::ParseLayoutMode(config_.layout));
        config_.scale = turingdesk::wallpaper::ScaleModeKey(turingdesk::wallpaper::ParseScaleMode(config_.scale));
        config_.focalX = turingdesk::wallpaper::ClampFocal(config_.focalX);
        config_.focalY = turingdesk::wallpaper::ClampFocal(config_.focalY);
        config_.fpsCap = turingdesk::wallpaper::NormalizeFpsCap(config_.fpsCap);
        config_.throttleFps = turingdesk::wallpaper::NormalizeFpsCap(config_.throttleFps);
        if (persist) SaveConfig(config_);
        pendingImage_.clear();
        pendingVideo_.clear();
        imageBitmap_.Reset();
        if (renderTarget_) LoadImage();
        performanceStopped_ = false;
        const bool mounted = AttachToDesktop();
        if (config_.enabled && mounted) {
            ShowWindow(host_, SW_SHOWNOACTIVATE);
            InvalidateRect(host_, nullptr, FALSE);
            UpdateWindow(host_);
            if (config_.scene == L"video") StartVideo();
        } else if (!config_.enabled) {
            ShowWindow(host_, SW_HIDE);
        }
        SetRenderTimerFps(config_.fpsCap);
        RefreshSettings();
        return mounted;
    }

    bool StartVideo() {
        videoSet_.Stop();
        lastMediaError_.clear();
        if (config_.video.empty() || !fs::exists(config_.video)) {
            lastMediaError_ = L"视频文件不存在";
            return false;
        }
        auto regions = turingdesk::wallpaper::DrawRegionsInHost(topology_, turingdesk::wallpaper::ParseLayoutMode(config_.layout));
        if (!videoSet_.Start(host_, config_.video, regions, turingdesk::wallpaper::ParseScaleMode(config_.scale), config_.focalX, config_.focalY)) {
            lastMediaError_ = videoSet_.LastErrorText();
            if (lastMediaError_.empty()) lastMediaError_ = L"Media Foundation 无法启动该视频";
            return false;
        }
        videoSet_.SetPaused(false);
        return true;
    }

    void ChooseImage() {
        wchar_t path[32768]{};
        OPENFILENAMEW dialog{};
        dialog.lStructSize = sizeof(dialog);
        dialog.hwndOwner = settings_;
        dialog.lpstrFile = path;
        dialog.nMaxFile = static_cast<DWORD>(std::size(path));
        dialog.lpstrFilter = L"图片和视频\0*.jpg;*.jpeg;*.png;*.bmp;*.mp4;*.mov;*.wmv;*.m4v\0视频\0*.mp4;*.mov;*.wmv;*.m4v\0图片\0*.jpg;*.jpeg;*.png;*.bmp\0所有文件\0*.*\0";
        dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
        if (!GetOpenFileNameW(&dialog)) return;
        const std::wstring selectedPath = path;
        const auto extension = fs::path(selectedPath).extension().wstring();
        const bool isVideo = _wcsicmp(extension.c_str(), L".mp4") == 0 || _wcsicmp(extension.c_str(), L".mov") == 0 ||
                             _wcsicmp(extension.c_str(), L".wmv") == 0 || _wcsicmp(extension.c_str(), L".m4v") == 0;
        if (isVideo) {
            pendingVideo_ = selectedPath;
            SendMessageW(sceneCombo_, CB_SETCURSEL, 4, 0);
        } else {
            pendingImage_ = selectedPath;
            SendMessageW(sceneCombo_, CB_SETCURSEL, 3, 0);
        }
        if (status_) SetWindowTextW(status_, selectedPath.c_str());
    }

    void ApplyFromSettings() {
        const int selected = static_cast<int>(SendMessageW(sceneCombo_, CB_GETCURSEL, 0, 0));
        const int selectedLayout = static_cast<int>(SendMessageW(layoutCombo_, CB_GETCURSEL, 0, 0));
        const int selectedScale = static_cast<int>(SendMessageW(scaleCombo_, CB_GETCURSEL, 0, 0));
        const int selectedHorizontal = static_cast<int>(SendMessageW(horizontalCombo_, CB_GETCURSEL, 0, 0));
        const int selectedVertical = static_cast<int>(SendMessageW(verticalCombo_, CB_GETCURSEL, 0, 0));
        const int selectedFps = static_cast<int>(SendMessageW(fpsCombo_, CB_GETCURSEL, 0, 0));
        constexpr int fpsValues[] = {15, 30, 45, 60, 120};

        Config next = config_;
        next.enabled = true;
        next.layout = selectedLayout == 1 ? L"clone" : selectedLayout == 2 ? L"primary" : L"span";
        next.scale = selectedScale == 1 ? L"contain" : selectedScale == 2 ? L"stretch" :
                     selectedScale == 3 ? L"center" : selectedScale == 4 ? L"tile" : L"cover";
        next.focalX = selectedHorizontal == 0 ? 0.0f : selectedHorizontal == 2 ? 1.0f : 0.5f;
        next.focalY = selectedVertical == 0 ? 0.0f : selectedVertical == 2 ? 1.0f : 0.5f;
        if (selectedFps >= 0 && selectedFps < static_cast<int>(std::size(fpsValues))) next.fpsCap = fpsValues[selectedFps];
        next.fullscreenAction = ActionFromCombo(fullscreenActionCombo_);
        next.maximizedAction = ActionFromCombo(maximizedActionCombo_);
        next.pauseFullscreen = next.fullscreenAction == turingdesk::wallpaper::PerformanceAction::Pause;
        next.image.clear();
        next.video.clear();

        if (selected == 1) next.scene = L"neon";
        else if (selected == 2) next.scene = L"grid";
        else if (selected == 3) {
            next.scene = L"image";
            next.image = pendingImage_.empty() ? config_.image : pendingImage_;
            if (next.image.empty()) {
                SetWindowTextW(status_, L"请先选择一张 JPG / PNG / BMP 图片。");
                return;
            }
        } else if (selected == 4) {
            next.scene = L"video";
            next.video = pendingVideo_.empty() ? config_.video : pendingVideo_;
            if (next.video.empty()) {
                SetWindowTextW(status_, L"请先选择一个 MP4 / MOV / WMV / M4V 视频。");
                return;
            }
        } else {
            next.scene = L"aurora";
        }
        ApplyConfig(next);
    }

    void RefreshSettings() {
        if (!settings_ || !IsWindow(settings_) || !sceneCombo_) return;
        int selected = 0;
        if (!config_.video.empty() || config_.scene == L"video") selected = 4;
        else if (!config_.image.empty() || config_.scene == L"image") selected = 3;
        else if (config_.scene == L"neon") selected = 1;
        else if (config_.scene == L"grid") selected = 2;
        SendMessageW(sceneCombo_, CB_SETCURSEL, selected, 0);

        const auto layoutMode = turingdesk::wallpaper::ParseLayoutMode(config_.layout);
        const int layoutSelected = layoutMode == turingdesk::wallpaper::LayoutMode::Clone ? 1 :
                                   layoutMode == turingdesk::wallpaper::LayoutMode::PrimaryOnly ? 2 : 0;
        if (layoutCombo_) SendMessageW(layoutCombo_, CB_SETCURSEL, layoutSelected, 0);

        const auto scaleMode = turingdesk::wallpaper::ParseScaleMode(config_.scale);
        const int scaleSelected = scaleMode == turingdesk::wallpaper::ScaleMode::Contain ? 1 :
                                  scaleMode == turingdesk::wallpaper::ScaleMode::Stretch ? 2 :
                                  scaleMode == turingdesk::wallpaper::ScaleMode::Center ? 3 :
                                  scaleMode == turingdesk::wallpaper::ScaleMode::Tile ? 4 : 0;
        if (scaleCombo_) SendMessageW(scaleCombo_, CB_SETCURSEL, scaleSelected, 0);
        if (horizontalCombo_) SendMessageW(horizontalCombo_, CB_SETCURSEL, config_.focalX < 0.25f ? 0 : config_.focalX > 0.75f ? 2 : 1, 0);
        if (verticalCombo_) SendMessageW(verticalCombo_, CB_SETCURSEL, config_.focalY < 0.25f ? 0 : config_.focalY > 0.75f ? 2 : 1, 0);

        constexpr int fpsValues[] = {15, 30, 45, 60, 120};
        int fpsSelected = 1;
        for (int i = 0; i < static_cast<int>(std::size(fpsValues)); ++i) if (fpsValues[i] == config_.fpsCap) fpsSelected = i;
        if (fpsCombo_) SendMessageW(fpsCombo_, CB_SETCURSEL, fpsSelected, 0);
        if (fullscreenActionCombo_) SendMessageW(fullscreenActionCombo_, CB_SETCURSEL, ActionIndex(config_.fullscreenAction), 0);
        if (maximizedActionCombo_) SendMessageW(maximizedActionCombo_, CB_SETCURSEL, ActionIndex(config_.maximizedAction), 0);
        SetWindowTextW(toggleButton_, config_.enabled ? L"停止" : L"恢复");

        std::wstring status;
        if (!config_.enabled) {
            status = L"已停止；Windows 原壁纸正常显示。";
        } else if (!mountOk_) {
            status = L"应用失败：" + (lastMountError_.empty() ? L"没有挂载到 Windows 桌面层" : lastMountError_);
        } else {
            const wchar_t* scene = selected == 0 ? L"Aurora Flow" : selected == 1 ? L"Neon Flow" : selected == 2 ? L"Quiet Grid" : selected == 3 ? L"图片壁纸" : L"视频壁纸";
            status = std::wstring(scene) + L" · " + turingdesk::wallpaper::LayoutModeDisplayName(layoutMode) + L" · " +
                     turingdesk::wallpaper::ScaleModeDisplayName(scaleMode) + L" · " + std::to_wstring(topology_.monitors.size()) + L" 屏";
            status += L"\r\n性能：" + std::wstring(turingdesk::wallpaper::PerformanceActionDisplayName(currentPerformance_.action));
            if (currentPerformance_.targetFps > 0) status += L" · " + std::to_wstring(currentPerformance_.targetFps) + L" FPS";
            if (!currentPerformance_.reason.empty()) status += L" · 原因：" + currentPerformance_.reason;
            if (selected == 4) {
                if (!lastMediaError_.empty()) status = L"视频壁纸错误：" + lastMediaError_;
                else if (scaleMode == turingdesk::wallpaper::ScaleMode::Tile) status += L" · 视频 Tile 当前安全降级为 Center";
            }
            status += L"\r\n" + turingdesk::wallpaper::DescribeMonitorTopology(topology_);
        }
        SetWindowTextW(status_, status.c_str());
    }

    HINSTANCE instance_{};
    HWND control_{};
    HWND host_{};
    HWND attachedParent_{};
    HWND settings_{};
    HWND sceneCombo_{};
    HWND imageButton_{};
    HWND layoutCombo_{};
    HWND scaleCombo_{};
    HWND horizontalCombo_{};
    HWND verticalCombo_{};
    HWND fpsCombo_{};
    HWND fullscreenActionCombo_{};
    HWND maximizedActionCombo_{};
    HWND applyButton_{};
    HWND toggleButton_{};
    HWND closeButton_{};
    HWND status_{};
    NOTIFYICONDATAW tray_{};
    bool trayAdded_{};
    bool mountOk_{};
    bool performanceStopped_{};
    UINT taskbarCreated_{};
    UINT renderTimerIntervalMs_{};
    unsigned healthTicks_{};
    MountMode mountMode_{MountMode::None};
    std::wstring lastMountError_;
    Config config_;
    turingdesk::wallpaper::MonitorTopology topology_;
    turingdesk::wallpaper::WallpaperPerformancePolicy performancePolicy_;
    turingdesk::wallpaper::PerformanceSnapshot currentPerformance_;
    std::wstring pendingImage_;
    std::wstring pendingVideo_;
    std::wstring lastMediaError_;
    turingdesk::VideoWallpaperSet videoSet_;
    float time_{};
    ComPtr<ID2D1Factory> d2dFactory_;
    ComPtr<ID2D1HwndRenderTarget> renderTarget_;
    ComPtr<ID2D1SolidColorBrush> brush_;
    ComPtr<IWICImagingFactory> wicFactory_;
    ComPtr<ID2D1Bitmap> imageBitmap_;
};

bool HasArg(std::wstring_view commandLine, std::wstring_view argument) {
    return commandLine.find(argument) != std::wstring_view::npos;
}

LRESULT CALLBACK SelfTestProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

int RunSelfTest(HINSTANCE instance) {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) return 21;

    ComPtr<ID2D1Factory> d2d;
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2d.GetAddressOf()))) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 22;
    }
    ComPtr<IWICImagingFactory> wic;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(wic.GetAddressOf())))) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 23;
    }

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.hInstance = instance;
    wc.lpfnWndProc = SelfTestProc;
    wc.lpszClassName = kSelfTestClass;
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 24;
    }
    HWND test = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW, kSelfTestClass, L"", WS_POPUP,
                                0, 0, 16, 16, nullptr, nullptr, instance, nullptr);
    if (!test) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 25;
    }
    const bool layered = SetLayeredWindowAttributes(test, 0, 255, LWA_ALPHA) != FALSE;
    DestroyWindow(test);

    const bool pathOk = !ConfigPath().empty();
    const bool layoutGeometryOk = turingdesk::wallpaper::SelfTestMonitorLayoutGeometry();
    const bool scalingGeometryOk = turingdesk::wallpaper::SelfTestScalingGeometry();
    const bool performancePolicyOk = turingdesk::wallpaper::WallpaperPerformancePolicy::SelfTest();
    const auto topology = turingdesk::wallpaper::QueryMonitorTopology();
    const bool topologyOk = topology.Valid();
    const bool mediaFoundationOk = turingdesk::VideoWallpaperPlayer::MediaFoundationAvailable();
    wic.Reset();
    d2d.Reset();
    if (SUCCEEDED(com)) CoUninitialize();
    if (!layered) return 26;
    if (!pathOk) return 27;
    if (!layoutGeometryOk) return 29;
    if (!topologyOk) return 30;
    if (!scalingGeometryOk) return 31;
    if (!performancePolicyOk) return 32;
    return mediaFoundationOk ? 0 : 28;
}

void SendExistingCommand(std::wstring_view args) {
    const HWND existing = FindWindowW(kControlClass, nullptr);
    if (!existing) return;
    if (HasArg(args, L"--settings")) PostMessageW(existing, kShowSettings, 0, 0);
    else if (HasArg(args, L"--stop")) PostMessageW(existing, kSetEnabled, FALSE, 0);
    else if (HasArg(args, L"--resume")) PostMessageW(existing, kSetEnabled, TRUE, 0);
    else PostMessageW(existing, kShowSettings, 0, 0);
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (HasArg(args, L"--self-test")) return RunSelfTest(instance);

    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    HANDLE mutex = CreateMutexW(nullptr, FALSE, kMutexName);
    if (!mutex) return 2;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        SendExistingCommand(args);
        CloseHandle(mutex);
        return 0;
    }

    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) {
        CloseHandle(mutex);
        return 3;
    }

    WallpaperApp app(instance);
    if (!app.Create()) {
        if (SUCCEEDED(com)) CoUninitialize();
        CloseHandle(mutex);
        return 4;
    }

    if (HasArg(args, L"--stop")) app.SetEnabled(false);
    else if (HasArg(args, L"--resume")) app.SetEnabled(true);
    if (HasArg(args, L"--settings")) app.ShowSettings();

    const int result = app.Run();
    if (SUCCEEDED(com)) CoUninitialize();
    CloseHandle(mutex);
    return result;
}

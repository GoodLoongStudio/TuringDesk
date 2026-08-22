#include <windows.h>
#include <shellapi.h>
#include <commdlg.h>
#include <d2d1.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cwchar>
#include <filesystem>
#include <iterator>
#include <string>
#include <string_view>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace {

constexpr wchar_t kControlClass[] = L"TuringDesk.Native.WallpaperControl";
constexpr wchar_t kHostClass[] = L"TuringDesk.Native.WallpaperHost";
constexpr wchar_t kSettingsClass[] = L"TuringDesk.Native.WallpaperSettings";
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
constexpr int kTraySettings = 4201;
constexpr int kTrayToggle = 4202;
constexpr int kTrayExit = 4203;
constexpr int kConfigVersion = 2;

HMENU ControlId(int id) {
    return reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
}

struct Config {
    bool enabled{true};
    bool pauseFullscreen{true};
    std::wstring scene{L"aurora"};
    std::wstring image;
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
    return scene == L"aurora" || scene == L"neon" || scene == L"grid" || scene == L"image";
}

void SaveConfig(const Config& config) {
    const auto path = ConfigPath().wstring();
    WritePrivateProfileStringW(L"Wallpaper", L"Version", std::to_wstring(kConfigVersion).c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Enabled", config.enabled ? L"1" : L"0", path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"PauseFullscreen", config.pauseFullscreen ? L"1" : L"0", path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Scene", config.scene.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Image", config.image.c_str(), path.c_str());
}

Config LoadConfig() {
    Config config;
    const auto path = ConfigPath().wstring();
    const int version = GetPrivateProfileIntW(L"Wallpaper", L"Version", 0, path.c_str());
    config.enabled = GetPrivateProfileIntW(L"Wallpaper", L"Enabled", 1, path.c_str()) != 0;
    config.pauseFullscreen = GetPrivateProfileIntW(L"Wallpaper", L"PauseFullscreen", 1, path.c_str()) != 0;

    wchar_t text[32768]{};
    GetPrivateProfileStringW(L"Wallpaper", L"Scene", L"aurora", text, static_cast<DWORD>(std::size(text)), path.c_str());
    config.scene = text;
    GetPrivateProfileStringW(L"Wallpaper", L"Image", L"", text, static_cast<DWORD>(std::size(text)), path.c_str());
    config.image = text;

    if (!ValidScene(config.scene)) config.scene = L"aurora";
    if (config.scene == L"image" && config.image.empty()) config.scene = L"aurora";

    // Migrate the previous prototype into an obviously enabled, usable default once.
    if (version < kConfigVersion) {
        config.enabled = true;
        if (!ValidScene(config.scene)) config.scene = L"aurora";
        SaveConfig(config);
    }
    return config;
}

void SpawnWallpaperWorker(HWND progman) {
    if (!progman) return;
    DWORD_PTR ignored = 0;
    SendMessageTimeoutW(progman, 0x052C, 0, 0, SMTO_NORMAL, 1000, &ignored);
    SendMessageTimeoutW(progman, 0x052C, 0xD, 0, SMTO_NORMAL, 1000, &ignored);
    SendMessageTimeoutW(progman, 0x052C, 0xD, 1, SMTO_NORMAL, 1000, &ignored);
}

HWND FindDesktopParent() {
    const HWND progman = FindWindowW(L"Progman", nullptr);
    SpawnWallpaperWorker(progman);

    struct Search {
        HWND worker{};
        HWND defViewParent{};
    } search;

    EnumWindows([](HWND top, LPARAM raw) -> BOOL {
        auto* result = reinterpret_cast<Search*>(raw);
        const HWND defView = FindWindowExW(top, nullptr, L"SHELLDLL_DefView", nullptr);
        if (!defView) return TRUE;
        result->defViewParent = top;
        const HWND worker = FindWindowExW(nullptr, top, L"WorkerW", nullptr);
        if (worker) result->worker = worker;
        return FALSE;
    }, reinterpret_cast<LPARAM>(&search));

    if (search.worker) return search.worker;
    if (search.defViewParent) return search.defViewParent;
    return progman;
}

bool ForegroundIsFullscreen(HWND wallpaper, HWND settings) {
    const HWND foreground = GetForegroundWindow();
    if (!foreground || foreground == wallpaper || foreground == settings || IsIconic(foreground)) return false;

    wchar_t className[128]{};
    GetClassNameW(foreground, className, static_cast<int>(std::size(className)));
    if (_wcsicmp(className, L"Progman") == 0 || _wcsicmp(className, L"WorkerW") == 0 ||
        _wcsicmp(className, L"Shell_TrayWnd") == 0) return false;

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

class WallpaperApp {
public:
    explicit WallpaperApp(HINSTANCE instance) : instance_(instance), config_(LoadConfig()) {}

    ~WallpaperApp() {
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

        host_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kHostClass,
                                L"TuringDesk Wallpaper Host", WS_POPUP,
                                0, 0, 1, 1, nullptr, nullptr, instance_, this);
        if (!host_) return false;

        AttachToDesktop();
        AddTray();
        ApplyConfig(config_, false);
        SetTimer(control_, kRenderTimer, 33, nullptr);
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
                                    CW_USEDEFAULT, CW_USEDEFAULT, 520, 320,
                                    nullptr, nullptr, instance_, this);
        if (!settings_) return;

        const HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto label = [&](const wchar_t* text, int x, int y, int w, int h) {
            HWND control = CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                           x, y, w, h, settings_, nullptr, instance_, nullptr);
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };

        label(L"TuringDesk 壁纸", 20, 18, 220, 26);
        label(L"场景", 20, 62, 56, 24);
        sceneCombo_ = CreateWindowExW(0, L"COMBOBOX", L"",
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                      82, 58, 250, 180, settings_, ControlId(kSceneComboId), instance_, nullptr);
        SendMessageW(sceneCombo_, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Aurora Flow · 极光流动"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Neon Flow · 霓虹网格"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Quiet Grid · 静谧网格"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"图片壁纸"));

        imageButton_ = CreateWindowExW(0, L"BUTTON", L"选择图片…",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       346, 58, 128, 30, settings_, ControlId(kImageButtonId), instance_, nullptr);
        pauseCheck_ = CreateWindowExW(0, L"BUTTON", L"全屏应用时暂停动态壁纸",
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                                      82, 104, 260, 26, settings_, ControlId(kPauseCheckId), instance_, nullptr);
        status_ = label(L"", 20, 146, 454, 52);

        applyButton_ = CreateWindowExW(0, L"BUTTON", L"应用到桌面",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                       185, 218, 100, 34, settings_, ControlId(kApplyButtonId), instance_, nullptr);
        toggleButton_ = CreateWindowExW(0, L"BUTTON", L"停止",
                                        WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                        293, 218, 84, 34, settings_, ControlId(kToggleButtonId), instance_, nullptr);
        closeButton_ = CreateWindowExW(0, L"BUTTON", L"关闭",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       385, 218, 84, 34, settings_, ControlId(kCloseButtonId), instance_, nullptr);

        for (HWND control : {sceneCombo_, imageButton_, pauseCheck_, applyButton_, toggleButton_, closeButton_}) {
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        }

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
            SetWindowPos(host_, HWND_BOTTOM, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(host_, nullptr, FALSE);
        } else {
            ShowWindow(host_, SW_HIDE);
        }
        RefreshSettings();
    }

private:
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
            self->pauseCheck_ = nullptr;
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
            AttachToDesktop();
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
        case WM_TIMER:
            if (wParam == kRenderTimer) {
                ++healthTicks_;
                if (healthTicks_ >= 150) {
                    healthTicks_ = 0;
                    if (!parent_ || !IsWindow(parent_) || GetParent(host_) != parent_) AttachToDesktop();
                }
                if (config_.enabled && config_.image.empty()) {
                    const bool paused = config_.pauseFullscreen && ForegroundIsFullscreen(host_, settings_);
                    if (!paused) {
                        time_ += 0.033f;
                        InvalidateRect(host_, nullptr, FALSE);
                    }
                }
            }
            return 0;
        case WM_DISPLAYCHANGE:
            AttachToDesktop();
            return 0;
        case WM_CLOSE:
            RemoveTray();
            if (settings_ && IsWindow(settings_)) DestroyWindow(settings_);
            if (host_ && IsWindow(host_)) DestroyWindow(host_);
            DestroyWindow(control_);
            return 0;
        case WM_DESTROY:
            KillTimer(control_, kRenderTimer);
            control_ = nullptr;
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(control_, message, wParam, lParam);
    }

    LRESULT HandleHost(UINT message, WPARAM wParam, LPARAM lParam) {
        switch (message) {
        case WM_DISPLAYCHANGE:
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

    void AttachToDesktop() {
        if (!host_ || !IsWindow(host_)) return;
        const HWND nextParent = FindDesktopParent();
        if (!nextParent) return;

        if (GetParent(host_) != nextParent) {
            SetParent(host_, nextParent);
            LONG_PTR style = GetWindowLongPtrW(host_, GWL_STYLE);
            style &= ~static_cast<LONG_PTR>(WS_POPUP);
            style |= WS_CHILD;
            SetWindowLongPtrW(host_, GWL_STYLE, style);
            parent_ = nextParent;
            renderTarget_.Reset();
            brush_.Reset();
            imageBitmap_.Reset();
        } else {
            parent_ = nextParent;
        }

        RECT area{};
        if (!GetClientRect(parent_, &area) || area.right <= area.left || area.bottom <= area.top) return;
        SetWindowPos(host_, HWND_BOTTOM, 0, 0,
                     std::max<LONG>(1, area.right - area.left),
                     std::max<LONG>(1, area.bottom - area.top),
                     SWP_NOACTIVATE | SWP_FRAMECHANGED | (config_.enabled ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
        if (config_.enabled) InvalidateRect(host_, nullptr, FALSE);
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
        Shell_NotifyIconW(NIM_DELETE, &tray_);
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
        const auto properties = D2D1::HwndRenderTargetProperties(host_, D2D1::SizeU(width, height),
                                                                 D2D1_PRESENT_OPTIONS_IMMEDIATELY);
        if (FAILED(d2dFactory_->CreateHwndRenderTarget(D2D1::RenderTargetProperties(), properties,
                                                       renderTarget_.GetAddressOf()))) return;
        renderTarget_->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), brush_.GetAddressOf());
        LoadImage();
    }

    void LoadImage() {
        imageBitmap_.Reset();
        if (!renderTarget_ || config_.image.empty()) return;
        ComPtr<IWICBitmapDecoder> decoder;
        if (FAILED(wicFactory_->CreateDecoderFromFilename(config_.image.c_str(), nullptr, GENERIC_READ,
                                                          WICDecodeMetadataCacheOnLoad, decoder.GetAddressOf()))) return;
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
        if (!renderTarget_ || !config_.enabled) return;

        renderTarget_->BeginDraw();
        if (!config_.image.empty() && imageBitmap_) DrawImage();
        else if (config_.scene == L"neon") DrawNeon();
        else if (config_.scene == L"grid") DrawGrid();
        else DrawAurora();

        const HRESULT hr = renderTarget_->EndDraw();
        if (hr == D2DERR_RECREATE_TARGET) {
            imageBitmap_.Reset();
            brush_.Reset();
            renderTarget_.Reset();
        }
    }

    void DrawImage() {
        renderTarget_->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f));
        const auto target = renderTarget_->GetSize();
        const auto source = imageBitmap_->GetSize();
        const float scale = std::max(target.width / std::max(1.0f, source.width),
                                     target.height / std::max(1.0f, source.height));
        const float width = source.width * scale;
        const float height = source.height * scale;
        const D2D1_RECT_F dest = D2D1::RectF((target.width - width) * 0.5f,
                                              (target.height - height) * 0.5f,
                                              (target.width + width) * 0.5f,
                                              (target.height + height) * 0.5f);
        renderTarget_->DrawBitmap(imageBitmap_.Get(), dest, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
    }

    void DrawAurora() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.012f, 0.020f, 0.065f));

        const std::array<D2D1_COLOR_F, 6> colors = {
            D2D1::ColorF(0.06f, 0.82f, 0.72f, 0.24f),
            D2D1::ColorF(0.16f, 0.48f, 1.00f, 0.24f),
            D2D1::ColorF(0.56f, 0.22f, 0.96f, 0.22f),
            D2D1::ColorF(0.08f, 0.72f, 0.96f, 0.20f),
            D2D1::ColorF(0.22f, 0.92f, 0.56f, 0.18f),
            D2D1::ColorF(0.72f, 0.24f, 0.92f, 0.18f),
        };

        for (int i = 0; i < static_cast<int>(colors.size()); ++i) {
            const float phase = time_ * (0.22f + i * 0.018f) + i * 1.13f;
            const float x = size.width * (0.12f + i * 0.17f) + static_cast<float>(std::sin(phase)) * size.width * 0.12f;
            const float y = size.height * (0.36f + 0.20f * static_cast<float>(std::sin(phase * 0.77f + i)));
            brush_->SetColor(colors[static_cast<std::size_t>(i)]);
            renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), size.width * 0.28f, size.height * 0.34f), brush_.Get());
        }

        for (int i = 0; i < 4; ++i) {
            const float y = size.height * (0.22f + i * 0.18f) + static_cast<float>(std::sin(time_ * 0.35f + i)) * 26.0f;
            brush_->SetColor(D2D1::ColorF(0.32f, 0.88f, 0.96f, 0.12f));
            renderTarget_->FillRectangle(D2D1::RectF(0.0f, y, size.width, y + 18.0f), brush_.Get());
        }
    }

    void DrawNeon() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.006f, 0.008f, 0.024f));
        constexpr float spacing = 58.0f;
        const float offset = static_cast<float>(std::fmod(time_ * 24.0f, spacing));

        brush_->SetColor(D2D1::ColorF(0.04f, 0.72f, 0.94f, 0.34f));
        for (float x = -spacing + offset; x < size.width + spacing; x += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get(), 1.2f);
        brush_->SetColor(D2D1::ColorF(0.78f, 0.12f, 0.94f, 0.28f));
        for (float y = -spacing + offset; y < size.height + spacing; y += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get(), 1.2f);

        for (int i = 0; i < 6; ++i) {
            const float x = static_cast<float>(std::fmod(time_ * (82.0f + i * 13.0f) + i * size.width * 0.19f,
                                                         size.width + 320.0f)) - 160.0f;
            brush_->SetColor(i % 2 == 0
                ? D2D1::ColorF(0.00f, 0.86f, 1.00f, 0.18f)
                : D2D1::ColorF(1.00f, 0.10f, 0.78f, 0.18f));
            renderTarget_->FillRectangle(D2D1::RectF(x, size.height * 0.12f, x + 110.0f, size.height * 0.88f), brush_.Get());
        }
    }

    void DrawGrid() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.024f, 0.030f, 0.038f));
        constexpr float spacing = 64.0f;
        brush_->SetColor(D2D1::ColorF(0.20f, 0.28f, 0.34f, 0.58f));
        for (float x = 0; x < size.width; x += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get());
        for (float y = 0; y < size.height; y += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get());

        const float pulse = 0.5f + 0.5f * static_cast<float>(std::sin(time_ * 0.72f));
        brush_->SetColor(D2D1::ColorF(0.18f, 0.58f, 0.72f, 0.12f + pulse * 0.08f));
        renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(size.width * 0.5f, size.height * 0.5f),
                                                 size.width * (0.16f + pulse * 0.04f),
                                                 size.height * (0.18f + pulse * 0.04f)), brush_.Get());
    }

    void ApplyConfig(const Config& next, bool persist = true) {
        config_ = next;
        if (persist) SaveConfig(config_);
        pendingImage_.clear();
        imageBitmap_.Reset();
        if (renderTarget_) LoadImage();
        AttachToDesktop();
        if (config_.enabled) ShowWindow(host_, SW_SHOWNOACTIVATE);
        else ShowWindow(host_, SW_HIDE);
        InvalidateRect(host_, nullptr, FALSE);
        RefreshSettings();
    }

    void ChooseImage() {
        wchar_t path[32768]{};
        OPENFILENAMEW dialog{};
        dialog.lStructSize = sizeof(dialog);
        dialog.hwndOwner = settings_;
        dialog.lpstrFile = path;
        dialog.nMaxFile = static_cast<DWORD>(std::size(path));
        dialog.lpstrFilter = L"图片\0*.jpg;*.jpeg;*.png;*.bmp\0所有文件\0*.*\0";
        dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
        if (!GetOpenFileNameW(&dialog)) return;
        pendingImage_ = path;
        SendMessageW(sceneCombo_, CB_SETCURSEL, 3, 0);
        if (status_) SetWindowTextW(status_, pendingImage_.c_str());
    }

    void ApplyFromSettings() {
        const int selected = static_cast<int>(SendMessageW(sceneCombo_, CB_GETCURSEL, 0, 0));
        Config next = config_;
        next.enabled = true;
        next.pauseFullscreen = SendMessageW(pauseCheck_, BM_GETCHECK, 0, 0) == BST_CHECKED;
        next.image.clear();

        if (selected == 1) next.scene = L"neon";
        else if (selected == 2) next.scene = L"grid";
        else if (selected == 3) {
            next.scene = L"image";
            next.image = pendingImage_.empty() ? config_.image : pendingImage_;
            if (next.image.empty()) {
                SetWindowTextW(status_, L"请先选择一张 JPG / PNG / BMP 图片。");
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
        if (!config_.image.empty() || config_.scene == L"image") selected = 3;
        else if (config_.scene == L"neon") selected = 1;
        else if (config_.scene == L"grid") selected = 2;
        SendMessageW(sceneCombo_, CB_SETCURSEL, selected, 0);
        SendMessageW(pauseCheck_, BM_SETCHECK, config_.pauseFullscreen ? BST_CHECKED : BST_UNCHECKED, 0);
        SetWindowTextW(toggleButton_, config_.enabled ? L"停止" : L"恢复");

        std::wstring status;
        if (!config_.enabled) {
            status = L"已停止；Windows 原壁纸正常显示。";
        } else if (selected == 3) {
            status = L"图片壁纸已应用到桌面。";
        } else if (selected == 0) {
            status = L"Aurora Flow 已应用 · 原生 Direct2D · 约 30 FPS。";
        } else if (selected == 1) {
            status = L"Neon Flow 已应用 · 原生 Direct2D · 约 30 FPS。";
        } else {
            status = L"Quiet Grid 已应用 · 原生 Direct2D · 约 30 FPS。";
        }
        SetWindowTextW(status_, status.c_str());
    }

    HINSTANCE instance_{};
    HWND control_{};
    HWND host_{};
    HWND parent_{};
    HWND settings_{};
    HWND sceneCombo_{};
    HWND imageButton_{};
    HWND pauseCheck_{};
    HWND applyButton_{};
    HWND toggleButton_{};
    HWND closeButton_{};
    HWND status_{};
    NOTIFYICONDATAW tray_{};
    bool trayAdded_{};
    UINT taskbarCreated_{};
    unsigned healthTicks_{};
    Config config_;
    std::wstring pendingImage_;
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

int RunSelfTest() {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) return 21;

    ComPtr<ID2D1Factory> d2d;
    const HRESULT d2dResult = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2d.GetAddressOf());
    if (FAILED(d2dResult)) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 22;
    }

    ComPtr<IWICImagingFactory> wic;
    const HRESULT wicResult = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                               IID_PPV_ARGS(wic.GetAddressOf()));
    if (FAILED(wicResult)) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 23;
    }

    const auto configPath = ConfigPath();
    const bool pathOk = !configPath.empty();
    wic.Reset();
    d2d.Reset();
    if (SUCCEEDED(com)) CoUninitialize();
    return pathOk ? 0 : 24;
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
    if (HasArg(args, L"--self-test")) return RunSelfTest();

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

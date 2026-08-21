#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <commdlg.h>
#include <d2d1.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cwchar>
#include <filesystem>
#include <iterator>
#include <string>
#include <string_view>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace {

constexpr wchar_t kHostClass[] = L"TuringDesk.Native.WallpaperHost";
constexpr wchar_t kSettingsClass[] = L"TuringDesk.Native.WallpaperSettings";
constexpr wchar_t kMutexName[] = L"Local\\TuringDesk.Native.Wallpaper.Singleton";
constexpr UINT kShowSettings = WM_APP + 81;
constexpr UINT kTrayMessage = WM_APP + 82;
constexpr UINT_PTR kRenderTimer = 1;
constexpr UINT kTrayId = 1;
constexpr int kSceneComboId = 4101;
constexpr int kImageButtonId = 4102;
constexpr int kApplyButtonId = 4103;
constexpr int kStopButtonId = 4104;
constexpr int kCloseButtonId = 4105;
constexpr int kTraySettings = 4201;
constexpr int kTrayToggle = 4202;
constexpr int kTrayExit = 4203;

struct Config {
    bool enabled{true};
    std::wstring scene{L"aurora"};
    std::wstring image;
};

fs::path ConfigPath() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path dir = length ? fs::path(local) / L"TuringDesk" : fs::temp_directory_path() / L"TuringDesk";
    std::error_code ec;
    fs::create_directories(dir, ec);
    return dir / L"wallpaper.ini";
}

Config LoadConfig() {
    Config config;
    const auto path = ConfigPath().wstring();
    config.enabled = GetPrivateProfileIntW(L"Wallpaper", L"Enabled", 1, path.c_str()) != 0;
    wchar_t text[32768]{};
    GetPrivateProfileStringW(L"Wallpaper", L"Scene", L"aurora", text, static_cast<DWORD>(std::size(text)), path.c_str());
    config.scene = text;
    GetPrivateProfileStringW(L"Wallpaper", L"Image", L"", text, static_cast<DWORD>(std::size(text)), path.c_str());
    config.image = text;
    return config;
}

void SaveConfig(const Config& config) {
    const auto path = ConfigPath().wstring();
    WritePrivateProfileStringW(L"Wallpaper", L"Enabled", config.enabled ? L"1" : L"0", path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Scene", config.scene.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Image", config.image.c_str(), path.c_str());
}

HWND FindDesktopWorker() {
    const HWND progman = FindWindowW(L"Progman", nullptr);
    if (progman) {
        DWORD_PTR ignored = 0;
        SendMessageTimeoutW(progman, 0x052C, 0, 0, SMTO_NORMAL, 1000, &ignored);
    }

    struct Search { HWND worker{}; } search;
    EnumWindows([](HWND top, LPARAM raw) -> BOOL {
        auto* result = reinterpret_cast<Search*>(raw);
        if (!FindWindowExW(top, nullptr, L"SHELLDLL_DefView", nullptr)) return TRUE;
        result->worker = FindWindowExW(nullptr, top, L"WorkerW", nullptr);
        return FALSE;
    }, reinterpret_cast<LPARAM>(&search));
    return search.worker ? search.worker : progman;
}

bool ForegroundIsFullscreen(HWND wallpaper) {
    const HWND foreground = GetForegroundWindow();
    if (!foreground || foreground == wallpaper || IsIconic(foreground)) return false;

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
    ~WallpaperApp() { RemoveTray(); }

    bool Create() {
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory_.GetAddressOf()))) return false;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                    IID_PPV_ARGS(wicFactory_.GetAddressOf())))) return false;

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance_;
        wc.lpfnWndProc = &WallpaperApp::HostProc;
        wc.lpszClassName = kHostClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        parent_ = FindDesktopWorker();
        RECT area{};
        if (parent_) GetClientRect(parent_, &area);
        if (area.right <= area.left || area.bottom <= area.top) {
            area.left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            area.top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            area.right = area.left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
            area.bottom = area.top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }

        const DWORD style = parent_ ? WS_CHILD : WS_POPUP;
        hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kHostClass, L"TuringDesk Wallpaper",
                                style, parent_ ? 0 : area.left, parent_ ? 0 : area.top,
                                area.right - area.left, area.bottom - area.top,
                                parent_, nullptr, instance_, this);
        if (!hwnd_) return false;

        AddTray();
        ApplyConfig(config_);
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
                                    CW_USEDEFAULT, CW_USEDEFAULT, 460, 260,
                                    nullptr, nullptr, instance_, this);
        if (!settings_) return;

        const HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto label = [&](const wchar_t* text, int x, int y, int w, int h) {
            HWND control = CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                           x, y, w, h, settings_, nullptr, instance_, nullptr);
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };

        label(L"TuringDesk 轻量壁纸引擎", 18, 16, 250, 24);
        label(L"场景", 18, 58, 54, 24);
        sceneCombo_ = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                      80, 54, 230, 180, settings_, reinterpret_cast<HMENU>(kSceneComboId), instance_, nullptr);
        SendMessageW(sceneCombo_, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Aurora Flow"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Neon Flow"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Quiet Grid"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"图片壁纸"));

        imageButton_ = CreateWindowExW(0, L"BUTTON", L"选择图片…", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                                       322, 54, 110, 28, settings_, reinterpret_cast<HMENU>(kImageButtonId), instance_, nullptr);
        applyButton_ = CreateWindowExW(0, L"BUTTON", L"应用", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                       205, 164, 70, 30, settings_, reinterpret_cast<HMENU>(kApplyButtonId), instance_, nullptr);
        stopButton_ = CreateWindowExW(0, L"BUTTON", L"停止", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                                      283, 164, 70, 30, settings_, reinterpret_cast<HMENU>(kStopButtonId), instance_, nullptr);
        closeButton_ = CreateWindowExW(0, L"BUTTON", L"关闭", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                                       361, 164, 70, 30, settings_, reinterpret_cast<HMENU>(kCloseButtonId), instance_, nullptr);
        for (HWND control : {imageButton_, applyButton_, stopButton_, closeButton_}) {
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        }
        status_ = label(L"", 18, 104, 414, 46);

        RefreshSettings();
        ShowWindow(settings_, SW_SHOWNORMAL);
        SetForegroundWindow(settings_);
    }

private:
    static LRESULT CALLBACK HostProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<WallpaperApp*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<WallpaperApp*>(create->lpCreateParams);
            self->hwnd_ = hwnd;
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
            case kStopButtonId:
                if (HIWORD(wParam) == BN_CLICKED) self->SetEnabled(false);
                return 0;
            case kCloseButtonId:
                if (HIWORD(wParam) == BN_CLICKED) ShowWindow(hwnd, SW_HIDE);
                return 0;
            }
        }
        if (message == WM_CLOSE) { ShowWindow(hwnd, SW_HIDE); return 0; }
        if (message == WM_DESTROY) { self->settings_ = nullptr; return 0; }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    LRESULT HandleHost(UINT message, WPARAM wParam, LPARAM lParam) {
        switch (message) {
        case kShowSettings:
            ShowSettings();
            return 0;
        case kTrayMessage:
            HandleTray(static_cast<UINT>(lParam));
            return 0;
        case WM_TIMER:
            if (config_.enabled && config_.image.empty() && !ForegroundIsFullscreen(hwnd_)) {
                time_ += 0.033f;
                InvalidateRect(hwnd_, nullptr, FALSE);
            }
            return 0;
        case WM_DISPLAYCHANGE:
            ResizeToDesktop();
            return 0;
        case WM_SIZE:
            if (renderTarget_) renderTarget_->Resize(D2D1::SizeU(LOWORD(lParam), HIWORD(lParam)));
            return 0;
        case WM_PAINT: {
            PAINTSTRUCT paint{};
            BeginPaint(hwnd_, &paint);
            Draw();
            EndPaint(hwnd_, &paint);
            return 0;
        }
        case WM_ERASEBKGND:
            return 1;
        case WM_CLOSE:
            DestroyWindow(hwnd_);
            return 0;
        case WM_DESTROY:
            KillTimer(hwnd_, kRenderTimer);
            RemoveTray();
            if (settings_ && IsWindow(settings_)) DestroyWindow(settings_);
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd_, message, wParam, lParam);
    }

    void AddTray() {
        tray_ = {};
        tray_.cbSize = sizeof(tray_);
        tray_.hWnd = hwnd_;
        tray_.uID = kTrayId;
        tray_.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        tray_.uCallbackMessage = kTrayMessage;
        tray_.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        wcscpy_s(tray_.szTip, L"TuringDesk Wallpaper");
        Shell_NotifyIconW(NIM_ADD, &tray_);
        trayAdded_ = true;
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
        const int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                                           point.x, point.y, 0, hwnd_, nullptr);
        DestroyMenu(menu);
        if (command == kTraySettings) ShowSettings();
        else if (command == kTrayToggle) SetEnabled(!config_.enabled);
        else if (command == kTrayExit) DestroyWindow(hwnd_);
    }

    void SetEnabled(bool enabled) {
        config_.enabled = enabled;
        SaveConfig(config_);
        if (enabled) {
            ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
            InvalidateRect(hwnd_, nullptr, FALSE);
        } else {
            ShowWindow(hwnd_, SW_HIDE);
        }
        RefreshSettings();
    }

    void ResizeToDesktop() {
        RECT area{};
        if (parent_ && GetClientRect(parent_, &area)) {
            MoveWindow(hwnd_, 0, 0, std::max<LONG>(1, area.right - area.left), std::max<LONG>(1, area.bottom - area.top), TRUE);
            return;
        }
        const int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        const int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        const int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        const int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        SetWindowPos(hwnd_, HWND_BOTTOM, x, y, width, height, SWP_NOACTIVATE);
    }

    void EnsureRenderTarget() {
        if (renderTarget_) return;
        RECT rc{};
        GetClientRect(hwnd_, &rc);
        const UINT width = static_cast<UINT>(std::max<LONG>(1, rc.right - rc.left));
        const UINT height = static_cast<UINT>(std::max<LONG>(1, rc.bottom - rc.top));
        if (FAILED(d2dFactory_->CreateHwndRenderTarget(D2D1::RenderTargetProperties(),
                                                       D2D1::HwndRenderTargetProperties(hwnd_, D2D1::SizeU(width, height)),
                                                       renderTarget_.GetAddressOf()))) return;
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(1.0f, 1.0f, 1.0f, 1.0f), brush_.GetAddressOf());
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
        const D2D1_RECT_F dest = D2D1::RectF((target.width - width) * 0.5f, (target.height - height) * 0.5f,
                                              (target.width + width) * 0.5f, (target.height + height) * 0.5f);
        renderTarget_->DrawBitmap(imageBitmap_.Get(), dest, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
    }

    void DrawAurora() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.018f, 0.025f, 0.070f));
        for (int i = 0; i < 9; ++i) {
            const float phase = time_ * (0.16f + i * 0.008f) + i * 0.73f;
            const float x = size.width * (0.08f + i * 0.11f) + static_cast<float>(std::sin(phase)) * size.width * 0.08f;
            const float y = size.height * (0.42f + 0.22f * static_cast<float>(std::sin(phase * 0.71f + i)));
            brush_->SetColor(D2D1::ColorF(0.10f + 0.03f * (i % 3), 0.38f + 0.04f * (i % 4),
                                          0.72f - 0.03f * (i % 2), 0.085f));
            renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), size.width * 0.19f, size.height * 0.24f), brush_.Get());
        }
    }

    void DrawNeon() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.010f, 0.012f, 0.026f));
        constexpr float spacing = 54.0f;
        const float offset = static_cast<float>(std::fmod(time_ * 18.0, spacing));
        brush_->SetColor(D2D1::ColorF(0.07f, 0.24f, 0.36f, 0.46f));
        for (float x = -spacing + offset; x < size.width + spacing; x += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get());
        for (float y = -spacing + offset; y < size.height + spacing; y += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get());
        for (int i = 0; i < 5; ++i) {
            const float x = static_cast<float>(std::fmod(time_ * (70.0 + i * 11.0) + i * size.width * 0.21,
                                                         size.width + 280.0)) - 140.0f;
            brush_->SetColor(D2D1::ColorF(0.12f + 0.07f * i, 0.44f, 0.94f - 0.10f * i, 0.20f));
            renderTarget_->FillRectangle(D2D1::RectF(x, size.height * 0.18f, x + 96.0f, size.height * 0.82f), brush_.Get());
        }
    }

    void DrawGrid() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.028f, 0.035f, 0.045f));
        constexpr float spacing = 64.0f;
        brush_->SetColor(D2D1::ColorF(0.16f, 0.20f, 0.24f, 0.52f));
        for (float x = 0; x < size.width; x += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get());
        for (float y = 0; y < size.height; y += spacing)
            renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get());
        const float pulse = 0.5f + 0.5f * static_cast<float>(std::sin(time_ * 0.7f));
        brush_->SetColor(D2D1::ColorF(0.28f, 0.58f, 0.72f, 0.08f + pulse * 0.06f));
        renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(size.width * 0.5f, size.height * 0.5f),
                                                 size.width * (0.15f + pulse * 0.04f),
                                                 size.height * (0.15f + pulse * 0.04f)), brush_.Get());
    }

    void ApplyConfig(const Config& next) {
        config_ = next;
        SaveConfig(config_);
        imageBitmap_.Reset();
        if (renderTarget_) LoadImage();
        KillTimer(hwnd_, kRenderTimer);
        SetTimer(hwnd_, kRenderTimer, config_.image.empty() ? 33 : 1000, nullptr);
        if (config_.enabled) ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
        else ShowWindow(hwnd_, SW_HIDE);
        InvalidateRect(hwnd_, nullptr, FALSE);
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
        SetWindowTextW(status_, pendingImage_.c_str());
    }

    void ApplyFromSettings() {
        const int selected = static_cast<int>(SendMessageW(sceneCombo_, CB_GETCURSEL, 0, 0));
        Config next = config_;
        next.enabled = true;
        next.image.clear();
        if (selected == 1) next.scene = L"neon";
        else if (selected == 2) next.scene = L"grid";
        else if (selected == 3) {
            next.scene = L"image";
            next.image = pendingImage_.empty() ? config_.image : pendingImage_;
            if (next.image.empty()) {
                SetWindowTextW(status_, L"请先选择一张图片。");
                return;
            }
        } else next.scene = L"aurora";
        ApplyConfig(next);
    }

    void RefreshSettings() {
        if (!sceneCombo_) return;
        int selected = 0;
        if (!config_.image.empty() || config_.scene == L"image") selected = 3;
        else if (config_.scene == L"neon") selected = 1;
        else if (config_.scene == L"grid") selected = 2;
        SendMessageW(sceneCombo_, CB_SETCURSEL, selected, 0);
        if (stopButton_) SetWindowTextW(stopButton_, config_.enabled ? L"停止" : L"已停止");
        if (status_) {
            const std::wstring status = config_.enabled
                ? (selected == 3 ? L"图片壁纸已启用" : L"动态场景已启用 · 30 FPS · 全屏应用自动暂停")
                : L"壁纸已停止；系统原壁纸已恢复显示";
            SetWindowTextW(status_, status.c_str());
        }
    }

    HINSTANCE instance_{};
    HWND hwnd_{};
    HWND parent_{};
    HWND settings_{};
    HWND sceneCombo_{};
    HWND imageButton_{};
    HWND applyButton_{};
    HWND stopButton_{};
    HWND closeButton_{};
    HWND status_{};
    NOTIFYICONDATAW tray_{};
    bool trayAdded_{};
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

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    HANDLE mutex = CreateMutexW(nullptr, FALSE, kMutexName);
    if (!mutex) return 2;
    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (HWND existing = FindWindowW(kHostClass, nullptr); existing && HasArg(args, L"--settings")) {
            PostMessageW(existing, kShowSettings, 0, 0);
        }
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
    if (HasArg(args, L"--settings")) app.ShowSettings();
    const int result = app.Run();
    if (SUCCEEDED(com)) CoUninitialize();
    CloseHandle(mutex);
    return result;
}

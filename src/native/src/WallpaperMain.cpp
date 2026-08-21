#include <windows.h>
#include <commdlg.h>
#include <d2d1.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <string>
#include <string_view>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace {

constexpr wchar_t kWallpaperClass[] = L"TuringDesk.Native.WallpaperHost";
constexpr wchar_t kSettingsClass[] = L"TuringDesk.Native.WallpaperSettings";
constexpr wchar_t kWallpaperMutex[] = L"Local\\TuringDesk.Native.Wallpaper.Singleton";
constexpr UINT kShowSettings = WM_APP + 81;
constexpr UINT_PTR kRenderTimer = 1;
constexpr int kSceneComboId = 4101;
constexpr int kImageButtonId = 4102;
constexpr int kApplyButtonId = 4103;
constexpr int kStopButtonId = 4104;
constexpr int kCloseButtonId = 4105;

struct WallpaperConfig {
    bool enabled{true};
    std::wstring scene{L"aurora"};
    std::wstring imagePath;
};

fs::path ConfigPath() {
    wchar_t local[32768]{};
    const DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path dir = count ? fs::path(local) / L"TuringDesk" : fs::temp_directory_path() / L"TuringDesk";
    std::error_code ec;
    fs::create_directories(dir, ec);
    return dir / L"wallpaper.ini";
}

WallpaperConfig LoadConfig() {
    WallpaperConfig config;
    const auto path = ConfigPath().wstring();
    config.enabled = GetPrivateProfileIntW(L"Wallpaper", L"Enabled", 1, path.c_str()) != 0;
    wchar_t value[32768]{};
    GetPrivateProfileStringW(L"Wallpaper", L"Scene", L"aurora", value, static_cast<DWORD>(std::size(value)), path.c_str());
    config.scene = value;
    GetPrivateProfileStringW(L"Wallpaper", L"Image", L"", value, static_cast<DWORD>(std::size(value)), path.c_str());
    config.imagePath = value;
    return config;
}

void SaveConfig(const WallpaperConfig& config) {
    const auto path = ConfigPath().wstring();
    WritePrivateProfileStringW(L"Wallpaper", L"Enabled", config.enabled ? L"1" : L"0", path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Scene", config.scene.c_str(), path.c_str());
    WritePrivateProfileStringW(L"Wallpaper", L"Image", config.imagePath.c_str(), path.c_str());
}

HWND FindDesktopWorker() {
    HWND progman = FindWindowW(L"Progman", nullptr);
    if (progman) {
        DWORD_PTR ignored = 0;
        SendMessageTimeoutW(progman, 0x052C, 0, 0, SMTO_NORMAL, 1000, &ignored);
    }

    struct Context { HWND worker{}; } context;
    EnumWindows([](HWND top, LPARAM param) -> BOOL {
        auto* ctx = reinterpret_cast<Context*>(param);
        HWND defView = FindWindowExW(top, nullptr, L"SHELLDLL_DefView", nullptr);
        if (!defView) return TRUE;
        HWND worker = FindWindowExW(nullptr, top, L"WorkerW", nullptr);
        if (worker) ctx->worker = worker;
        return FALSE;
    }, reinterpret_cast<LPARAM>(&context));

    return context.worker ? context.worker : progman;
}

bool ForegroundCoversMonitor(HWND wallpaper) {
    HWND foreground = GetForegroundWindow();
    if (!foreground || foreground == wallpaper || GetAncestor(foreground, GA_ROOT) == wallpaper) return false;
    wchar_t className[128]{};
    GetClassNameW(foreground, className, static_cast<int>(std::size(className)));
    if (_wcsicmp(className, L"Progman") == 0 || _wcsicmp(className, L"WorkerW") == 0 ||
        _wcsicmp(className, L"Shell_TrayWnd") == 0) return false;

    RECT rect{};
    if (!GetWindowRect(foreground, &rect) || IsIconic(foreground)) return false;
    HMONITOR monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
    MONITORINFO info{sizeof(info)};
    if (!GetMonitorInfoW(monitor, &info)) return false;
    constexpr int tolerance = 4;
    return rect.left <= info.rcMonitor.left + tolerance && rect.top <= info.rcMonitor.top + tolerance &&
           rect.right >= info.rcMonitor.right - tolerance && rect.bottom >= info.rcMonitor.bottom - tolerance;
}

class WallpaperHost {
public:
    explicit WallpaperHost(HINSTANCE instance) : instance_(instance), config_(LoadConfig()) {}

    bool Create() {
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory_.GetAddressOf()))) return false;
        CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(wicFactory_.GetAddressOf()));

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance_;
        wc.lpfnWndProc = &WallpaperHost::WndProc;
        wc.lpszClassName = kWallpaperClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        HWND parent = FindDesktopWorker();
        RECT area{};
        if (parent) GetClientRect(parent, &area);
        if (area.right <= area.left || area.bottom <= area.top) {
            area = {0, 0, GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)};
        }

        const DWORD style = parent ? (WS_CHILD | WS_VISIBLE) : (WS_POPUP | WS_VISIBLE);
        const DWORD exStyle = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        hwnd_ = CreateWindowExW(exStyle, kWallpaperClass, L"TuringDesk Wallpaper", style,
                                0, 0, area.right - area.left, area.bottom - area.top,
                                parent, nullptr, instance_, this);
        if (!hwnd_) return false;
        if (!parent) SetWindowPos(hwnd_, HWND_BOTTOM, area.left, area.top, area.right - area.left, area.bottom - area.top,
                                  SWP_NOACTIVATE | SWP_SHOWWINDOW);
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
            ShowWindow(settings_, SW_SHOWNORMAL);
            SetForegroundWindow(settings_);
            return;
        }

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance_;
        wc.lpfnWndProc = &WallpaperHost::SettingsProc;
        wc.lpszClassName = kSettingsClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        RegisterClassExW(&wc);

        settings_ = CreateWindowExW(WS_EX_TOOLWINDOW, kSettingsClass, L"TuringDesk 壁纸",
                                    WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU,
                                    CW_USEDEFAULT, CW_USEDEFAULT, 440, 250,
                                    nullptr, nullptr, instance_, this);
        if (!settings_) return;

        const auto font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto makeStatic = [&](const wchar_t* text, int x, int y, int w, int h) {
            HWND control = CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                           x, y, w, h, settings_, nullptr, instance_, nullptr);
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };
        makeStatic(L"轻量壁纸引擎", 18, 16, 180, 24);
        makeStatic(L"场景", 18, 58, 60, 24);

        sceneCombo_ = CreateWindowExW(0, L"COMBOBOX", L"",
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                      84, 54, 220, 160, settings_, reinterpret_cast<HMENU>(kSceneComboId), instance_, nullptr);
        SendMessageW(sceneCombo_, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Aurora Flow"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Neon Flow"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Quiet Grid"));
        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"图片壁纸"));

        imageButton_ = CreateWindowExW(0, L"BUTTON", L"选择图片…", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       315, 54, 100, 28, settings_, reinterpret_cast<HMENU>(kImageButtonId), instance_, nullptr);
        applyButton_ = CreateWindowExW(0, L"BUTTON", L"应用", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                       190, 150, 70, 30, settings_, reinterpret_cast<HMENU>(kApplyButtonId), instance_, nullptr);
        stopButton_ = CreateWindowExW(0, L"BUTTON", L"停止", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                      268, 150, 70, 30, settings_, reinterpret_cast<HMENU>(kStopButtonId), instance_, nullptr);
        closeSettingsButton_ = CreateWindowExW(0, L"BUTTON", L"关闭", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                               346, 150, 70, 30, settings_, reinterpret_cast<HMENU>(kCloseButtonId), instance_, nullptr);
        for (HWND button : {imageButton_, applyButton_, stopButton_, closeSettingsButton_}) {
            SendMessageW(button, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        }

        status_ = makeStatic(L"", 18, 100, 398, 36);
        RefreshSettings();
        ShowWindow(settings_, SW_SHOWNORMAL);
        SetForegroundWindow(settings_);
    }

private:
    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<WallpaperHost*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<WallpaperHost*>(create->lpCreateParams);
            self->hwnd_ = hwnd;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        return self ? self->HandleMessage(message, wParam, lParam) : DefWindowProcW(hwnd, message, wParam, lParam);
    }

    static LRESULT CALLBACK SettingsProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<WallpaperHost*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<WallpaperHost*>(create->lpCreateParams);
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
                if (HIWORD(wParam) == BN_CLICKED) {
                    self->config_.enabled = false;
                    SaveConfig(self->config_);
                    self->ApplyConfig(self->config_);
                    self->RefreshSettings();
                }
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

    LRESULT HandleMessage(UINT message, WPARAM, LPARAM lParam) {
        switch (message) {
        case kShowSettings:
            ShowSettings();
            return 0;
        case WM_TIMER:
            if (config_.enabled && config_.imagePath.empty() && !ForegroundCoversMonitor(hwnd_)) {
                time_ += 0.033f;
                InvalidateRect(hwnd_, nullptr, FALSE);
            }
            return 0;
        case WM_DISPLAYCHANGE:
            ResizeToParent();
            return 0;
        case WM_SIZE:
            if (renderTarget_) renderTarget_->Resize(D2D1::SizeU(LOWORD(lParam), HIWORD(lParam)));
            return 0;
        case WM_PAINT: {
            PAINTSTRUCT ps{};
            BeginPaint(hwnd_, &ps);
            Draw();
            EndPaint(hwnd_, &ps);
            return 0;
        }
        case WM_ERASEBKGND:
            return 1;
        case WM_CLOSE:
            DestroyWindow(hwnd_);
            return 0;
        case WM_DESTROY:
            KillTimer(hwnd_, kRenderTimer);
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd_, message, 0, lParam);
    }

    void ResizeToParent() {
        HWND parent = GetParent(hwnd_);
        RECT rc{};
        if (parent && GetClientRect(parent, &rc)) {
            MoveWindow(hwnd_, 0, 0, rc.right - rc.left, rc.bottom - rc.top, TRUE);
        } else {
            const int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            const int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            const int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            const int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            SetWindowPos(hwnd_, HWND_BOTTOM, x, y, w, h, SWP_NOACTIVATE);
        }
    }

    void EnsureRenderTarget() {
        if (renderTarget_) return;
        RECT rc{};
        GetClientRect(hwnd_, &rc);
        const auto size = D2D1::SizeU(std::max<LONG>(1, rc.right - rc.left), std::max<LONG>(1, rc.bottom - rc.top));
        d2dFactory_->CreateHwndRenderTarget(D2D1::RenderTargetProperties(),
                                            D2D1::HwndRenderTargetProperties(hwnd_, size),
                                            renderTarget_.GetAddressOf());
        if (renderTarget_) {
            renderTarget_->CreateSolidColorBrush(D2D1::ColorF(1, 1, 1, 1), brush_.GetAddressOf());
            LoadImageBitmap();
        }
    }

    void LoadImageBitmap() {
        imageBitmap_.Reset();
        if (!renderTarget_ || !wicFactory_ || config_.imagePath.empty()) return;
        ComPtr<IWICBitmapDecoder> decoder;
        if (FAILED(wicFactory_->CreateDecoderFromFilename(config_.imagePath.c_str(), nullptr, GENERIC_READ,
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
        if (!renderTarget_) return;
        renderTarget_->BeginDraw();
        if (!config_.enabled) {
            renderTarget_->Clear(D2D1::ColorF(0.035f, 0.04f, 0.055f));
        } else if (!config_.imagePath.empty() && imageBitmap_) {
            renderTarget_->Clear(D2D1::ColorF(0, 0, 0));
            const auto size = renderTarget_->GetSize();
            const auto imageSize = imageBitmap_->GetSize();
            const float scale = std::max(size.width / std::max(1.0f, imageSize.width), size.height / std::max(1.0f, imageSize.height));
            const float width = imageSize.width * scale;
            const float height = imageSize.height * scale;
            const D2D1_RECT_F dest = D2D1::RectF((size.width - width) * 0.5f, (size.height - height) * 0.5f,
                                                  (size.width + width) * 0.5f, (size.height + height) * 0.5f);
            renderTarget_->DrawBitmap(imageBitmap_.Get(), dest, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
        } else if (config_.scene == L"neon") {
            DrawNeon();
        } else if (config_.scene == L"grid") {
            DrawGrid();
        } else {
            DrawAurora();
        }
        const HRESULT hr = renderTarget_->EndDraw();
        if (hr == D2DERR_RECREATE_TARGET) {
            imageBitmap_.Reset();
            brush_.Reset();
            renderTarget_.Reset();
        }
    }

    void DrawAurora() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.018f, 0.025f, 0.07f));
        for (int i = 0; i < 9; ++i) {
            const float phase = time_ * (0.16f + i * 0.008f) + i * 0.73f;
            const float x = size.width * (0.08f + i * 0.11f) + std::sin(phase) * size.width * 0.08f;
            const float y = size.height * (0.42f + 0.22f * std::sin(phase * 0.71f + i));
            const float radiusX = size.width * (0.18f + 0.018f * (i % 3));
            const float radiusY = size.height * (0.22f + 0.02f * (i % 2));
            const float r = 0.12f + 0.03f * (i % 3);
            const float g = 0.42f + 0.04f * (i % 4);
            const float b = 0.62f + 0.03f * (i % 2);
            brush_->SetColor(D2D1::ColorF(r, g, b, 0.075f));
            renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), radiusX, radiusY), brush_.Get());
        }
    }

    void DrawNeon() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.012f, 0.012f, 0.025f));
        brush_->SetColor(D2D1::ColorF(0.08f, 0.22f, 0.32f, 0.42f));
        const float spacing = 52.0f;
        const float offset = std::fmod(time_ * 18.0f, spacing);
        for (float x = -spacing + offset; x < size.width + spacing; x += spacing) {
            renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get(), 1.0f);
        }
        for (float y = -spacing + offset; y < size.height + spacing; y += spacing) {
            renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get(), 1.0f);
        }
        for (int i = 0; i < 5; ++i) {
            const float x = std::fmod(time_ * (70.0f + i * 11.0f) + i * size.width * 0.21f, size.width + 280.0f) - 140.0f;
            brush_->SetColor(D2D1::ColorF(0.1f + 0.08f * i, 0.45f, 0.95f - 0.1f * i, 0.22f));
            renderTarget_->FillRectangle(D2D1::RectF(x, size.height * 0.18f, x + 100.0f, size.height * 0.82f), brush_.Get());
        }
    }

    void DrawGrid() {
        const auto size = renderTarget_->GetSize();
        renderTarget_->Clear(D2D1::ColorF(0.028f, 0.035f, 0.045f));
        brush_->SetColor(D2D1::ColorF(0.16f, 0.20f, 0.24f, 0.52f));
        constexpr float spacing = 64.0f;
        for (float x = 0; x < size.width; x += spacing) renderTarget_->DrawLine(D2D1::Point2F(x, 0), D2D1::Point2F(x, size.height), brush_.Get(), 1.0f);
        for (float y = 0; y < size.height; y += spacing) renderTarget_->DrawLine(D2D1::Point2F(0, y), D2D1::Point2F(size.width, y), brush_.Get(), 1.0f);
        const float pulse = 0.5f + 0.5f * std::sin(time_ * 0.7f);
        brush_->SetColor(D2D1::ColorF(0.28f, 0.58f, 0.72f, 0.08f + pulse * 0.06f));
        renderTarget_->FillEllipse(D2D1::Ellipse(D2D1::Point2F(size.width * 0.5f, size.height * 0.5f),
                                                 size.width * (0.15f + pulse * 0.04f), size.height * (0.15f + pulse * 0.04f)), brush_.Get());
    }

    void ApplyConfig(const WallpaperConfig& config) {
        config_ = config;
        SaveConfig(config_);
        imageBitmap_.Reset();
        LoadImageBitmap();
        KillTimer(hwnd_, kRenderTimer);
        if (config_.enabled && config_.imagePath.empty()) SetTimer(hwnd_, kRenderTimer, 33, nullptr);
        else SetTimer(hwnd_, kRenderTimer, 1000, nullptr);
        InvalidateRect(hwnd_, nullptr, FALSE);
    }

    void ChooseImage() {
        wchar_t path[MAX_PATH * 8]{};
        OPENFILENAMEW dialog{};
        dialog.lStructSize = sizeof(dialog);
        dialog.hwndOwner = settings_;
        dialog.lpstrFile = path;
        dialog.nMaxFile = static_cast<DWORD>(std::size(path));
        dialog.lpstrFilter = L"图片\0*.jpg;*.jpeg;*.png;*.bmp;*.gif\0所有文件\0*.*\0";
        dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
        if (!GetOpenFileNameW(&dialog)) return;
        pendingImage_ = path;
        SendMessageW(sceneCombo_, CB_SETCURSEL, 3, 0);
        SetWindowTextW(status_, pendingImage_.c_str());
    }

    void ApplyFromSettings() {
        const int selection = static_cast<int>(SendMessageW(sceneCombo_, CB_GETCURSEL, 0, 0));
        WallpaperConfig next = config_;
        next.enabled = true;
        next.imagePath.clear();
        if (selection == 1) next.scene = L"neon";
        else if (selection == 2) next.scene = L"grid";
        else if (selection == 3) {
            next.scene = L"image";
            next.imagePath = pendingImage_.empty() ? config_.imagePath : pendingImage_;
            if (next.imagePath.empty()) {
                SetWindowTextW(status_, L"请先选择一张图片。");
                return;
            }
        } else next.scene = L"aurora";
        ApplyConfig(next);
        RefreshSettings();
    }

    void RefreshSettings() {
        if (!sceneCombo_) return;
        int selection = 0;
        if (!config_.imagePath.empty() || config_.scene == L"image") selection = 3;
        else if (config_.scene == L"neon") selection = 1;
        else if (config_.scene == L"grid") selection = 2;
        SendMessageW(sceneCombo_, CB_SETCURSEL, selection, 0);
        const std::wstring status = config_.enabled
            ? (selection == 3 ? L"已启用图片壁纸" : L"已启用动态场景 · 30 FPS · 全屏应用时暂停")
            : L"壁纸已停止";
        if (status_) SetWindowTextW(status_, status.c_str());
    }

    HINSTANCE instance_{};
    HWND hwnd_{};
    HWND settings_{};
    HWND sceneCombo_{};
    HWND imageButton_{};
    HWND applyButton_{};
    HWND stopButton_{};
    HWND closeSettingsButton_{};
    HWND status_{};
    WallpaperConfig config_;
    std::wstring pendingImage_;
    float time_{};
    ComPtr<ID2D1Factory> d2dFactory_;
    ComPtr<ID2D1HwndRenderTarget> renderTarget_;
    ComPtr<ID2D1SolidColorBrush> brush_;
    ComPtr<IWICImagingFactory> wicFactory_;
    ComPtr<ID2D1Bitmap> imageBitmap_;
};

bool HasArg(std::wstring_view commandLine, std::wstring_view arg) {
    return commandLine.find(arg) != std::wstring_view::npos;
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    HANDLE mutex = CreateMutexW(nullptr, FALSE, kWallpaperMutex);
    if (!mutex) return 2;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (HWND existing = FindWindowW(kWallpaperClass, nullptr)) {
            if (HasArg(commandLine ? std::wstring_view(commandLine) : std::wstring_view{}, L"--settings")) {
                PostMessageW(existing, kShowSettings, 0, 0);
            }
        }
        CloseHandle(mutex);
        return 0;
    }

    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) {
        CloseHandle(mutex);
        return 3;
    }

    WallpaperHost host(instance);
    if (!host.Create()) {
        if (SUCCEEDED(com)) CoUninitialize();
        CloseHandle(mutex);
        return 4;
    }
    if (HasArg(commandLine ? std::wstring_view(commandLine) : std::wstring_view{}, L"--settings")) host.ShowSettings();
    const int result = host.Run();
    if (SUCCEEDED(com)) CoUninitialize();
    CloseHandle(mutex);
    return result;
}

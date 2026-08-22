#include "turingdesk/HarnessProcessManager.h"
#include <windows.h>
#include <objbase.h>
#include <WebView2.h>
#include <wrl.h>
#include <wrl/client.h>
#include <filesystem>
#include <iterator>
#include <string>
#include <string_view>

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace {

constexpr wchar_t kWindowClass[] = L"TuringDesk.Native.HarnessWindow";
constexpr wchar_t kMutexName[] = L"Local\\TuringDesk.Native.Harness.Singleton";
constexpr UINT_PTR kReadyTimerId = 1;
constexpr UINT kReadyPollMs = 250;
// The pinned Harness dependency tree is already present locally, so smoke tests
// should become ready quickly. Keep a generous two-minute ceiling for slower
// ARM64 machines while still surfacing a broken bundle promptly.
constexpr DWORD kSmokeTimeoutMs = 120000;

fs::path UserDataDirectory() {
    wchar_t localAppData[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData,
                                                  static_cast<DWORD>(std::size(localAppData)));
    if (length == 0 || length >= std::size(localAppData)) return {};
    const fs::path directory = fs::path(localAppData) / L"TuringDesk" / L"WebView2" / L"Harness";
    std::error_code ec;
    fs::create_directories(directory, ec);
    return directory;
}

std::wstring HrText(HRESULT hr) {
    wchar_t text[64]{};
    swprintf_s(text, L"HRESULT 0x%08X", static_cast<unsigned>(hr));
    return text;
}

std::wstring HarnessLogHint() {
    const std::wstring logPath = turingdesk::HarnessProcessManager::LogPath();
    return logPath.empty() ? std::wstring{} : L"\r\n日志：" + logPath;
}

int RunHarnessSmokeTest() {
    turingdesk::HarnessProcessManager harness;
    if (!harness.Start()) return 6;
    const bool ready = harness.WaitUntilReady(kSmokeTimeoutMs);
    harness.Stop();
    return ready ? 0 : 7;
}

class HarnessHost {
public:
    explicit HarnessHost(HINSTANCE instance) : instance_(instance) {}

    bool Create() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance_;
        wc.lpfnWndProc = &HarnessHost::WndProc;
        wc.lpszClassName = kWindowClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        hwnd_ = CreateWindowExW(0, kWindowClass, L"TuringDesk · DeepSeek Harness",
                                WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                                CW_USEDEFAULT, CW_USEDEFAULT, 1180, 820,
                                nullptr, nullptr, instance_, this);
        if (!hwnd_) return false;

        // Use a borderless read-only EDIT instead of STATIC so every startup/error
        // message and the log path can be selected with the mouse and copied with
        // Ctrl+C. ES_NOHIDESEL keeps a useful selection visible when focus moves.
        status_ = CreateWindowExW(0, L"EDIT", L"正在启动 DeepSeek Harness…",
                                  WS_CHILD | WS_VISIBLE | WS_TABSTOP |
                                      ES_MULTILINE | ES_CENTER | ES_READONLY | ES_NOHIDESEL,
                                  24, 24, 1100, 120, hwnd_, nullptr, instance_, nullptr);
        HFONT font = CreateFontW(-20, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                                 DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                 CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
        if (font) {
            statusFont_ = font;
            SendMessageW(status_, WM_SETFONT, reinterpret_cast<WPARAM>(statusFont_), TRUE);
        }

        ShowWindow(hwnd_, SW_SHOWNORMAL);
        UpdateWindow(hwnd_);

        if (harness_.ServiceReady()) {
            SetStatus(L"正在连接 DeepSeek Harness…");
            InitializeWebView();
            return true;
        }

        if (!harness_.Start()) {
            SetStatus(L"DeepSeek Harness 启动失败：" + harness_.LastError());
            return true;
        }

        startedAt_ = GetTickCount64();
        nextStatusUpdate_ = startedAt_;
        UpdateStartingStatus(startedAt_);
        SetTimer(hwnd_, kReadyTimerId, kReadyPollMs, nullptr);
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

private:
    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        HarnessHost* self = reinterpret_cast<HarnessHost*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<HarnessHost*>(create->lpCreateParams);
            self->hwnd_ = hwnd;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        return self ? self->HandleMessage(message, wParam, lParam)
                    : DefWindowProcW(hwnd, message, wParam, lParam);
    }

    LRESULT HandleMessage(UINT message, WPARAM wParam, LPARAM lParam) {
        switch (message) {
        case WM_TIMER:
            if (wParam == kReadyTimerId) {
                PollHarness();
                return 0;
            }
            break;
        case WM_SIZE:
            ResizeWebView();
            ResizeStatus();
            return 0;
        case WM_SETFOCUS:
            if (webviewController_) webviewController_->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
            return 0;
        case WM_CLOSE:
            DestroyWindow(hwnd_);
            return 0;
        case WM_DESTROY:
            KillTimer(hwnd_, kReadyTimerId);
            webview_.Reset();
            if (webviewController_) webviewController_->Close();
            webviewController_.Reset();
            harness_.Stop();
            if (statusFont_) {
                DeleteObject(statusFont_);
                statusFont_ = nullptr;
            }
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd_, message, wParam, lParam);
    }

    void PollHarness() {
        if (harness_.ServiceReady()) {
            KillTimer(hwnd_, kReadyTimerId);
            SetStatus(L"Harness 已就绪，正在打开界面…");
            InitializeWebView();
            return;
        }

        if (!harness_.Running()) {
            KillTimer(hwnd_, kReadyTimerId);
            const DWORD exitCode = harness_.ExitCode();
            std::wstring text = L"DeepSeek 官方 Harness 在 Web UI 就绪前退出";
            if (exitCode != STILL_ACTIVE) text += L"，ExitCode=" + std::to_wstring(exitCode);
            text += L"。请查看下方日志中的 RuntimeBundle/DSH 原始错误。" + HarnessLogHint();
            SetStatus(text);
            return;
        }

        const ULONGLONG now = GetTickCount64();
        if (now >= nextStatusUpdate_) {
            // Do not replace the text while the user is selecting/copying it.
            // Harness readiness polling continues normally in the background.
            if (GetFocus() != status_) UpdateStartingStatus(now);
            nextStatusUpdate_ = now + 1000;
        }
    }

    void UpdateStartingStatus(ULONGLONG now) {
        const ULONGLONG elapsedSeconds = startedAt_ == 0 ? 0 : (now - startedAt_) / 1000;
        std::wstring text = L"正在启动仓库内固定版本的 DeepSeek Harness… 已等待 " + std::to_wstring(elapsedSeconds) + L" 秒。";
        text += L"\r\n不会执行 npm/npx 下载；Node 和 Harness 已随 TuringDesk RuntimeBundle 部署。关闭此窗口即可取消。";
        if (elapsedSeconds >= 45) text += L"\r\n启动时间异常偏长，请检查 RuntimeBundle 完整性和下方日志。";
        text += HarnessLogHint();
        SetStatus(text);
    }

    void InitializeWebView() {
        if (webviewInitializing_ || webview_) return;
        webviewInitializing_ = true;

        const fs::path userData = UserDataDirectory();
        const std::wstring userDataText = userData.empty() ? std::wstring{} : userData.wstring();
        const HRESULT start = CreateCoreWebView2EnvironmentWithOptions(
            nullptr,
            userDataText.empty() ? nullptr : userDataText.c_str(),
            nullptr,
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
                [this](HRESULT result, ICoreWebView2Environment* environment) -> HRESULT {
                    if (FAILED(result) || !environment || !IsWindow(hwnd_)) {
                        webviewInitializing_ = false;
                        SetStatus(L"WebView2 Runtime 初始化失败：" + HrText(result));
                        return S_OK;
                    }

                    return environment->CreateCoreWebView2Controller(
                        hwnd_,
                        Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                            [this](HRESULT controllerResult, ICoreWebView2Controller* controller) -> HRESULT {
                                webviewInitializing_ = false;
                                if (FAILED(controllerResult) || !controller || !IsWindow(hwnd_)) {
                                    SetStatus(L"WebView2 窗口创建失败：" + HrText(controllerResult));
                                    return S_OK;
                                }

                                webviewController_ = controller;
                                HRESULT hr = webviewController_->get_CoreWebView2(webview_.ReleaseAndGetAddressOf());
                                if (FAILED(hr) || !webview_) {
                                    SetStatus(L"WebView2 页面创建失败：" + HrText(hr));
                                    return S_OK;
                                }

                                webviewController_->put_IsVisible(TRUE);
                                ResizeWebView();
                                ShowWindow(status_, SW_HIDE);
                                const std::wstring url = turingdesk::HarnessProcessManager::DefaultUrl();
                                hr = webview_->Navigate(url.c_str());
                                if (FAILED(hr)) SetStatus(L"打开 Harness Web UI 失败：" + HrText(hr));
                                return S_OK;
                            }).Get());
                }).Get());

        if (FAILED(start)) {
            webviewInitializing_ = false;
            SetStatus(L"WebView2 Loader 启动失败：" + HrText(start));
        }
    }

    void ResizeWebView() {
        if (!webviewController_ || !hwnd_) return;
        RECT bounds{};
        if (GetClientRect(hwnd_, &bounds)) webviewController_->put_Bounds(bounds);
    }

    void ResizeStatus() {
        if (!status_ || !hwnd_) return;
        RECT bounds{};
        if (!GetClientRect(hwnd_, &bounds)) return;
        SetWindowPos(status_, nullptr, 24, 24, (bounds.right - bounds.left) - 48, 140,
                     SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void SetStatus(const std::wstring& text) {
        if (!status_ || !IsWindow(status_)) return;
        ShowWindow(status_, SW_SHOW);
        SetWindowTextW(status_, text.c_str());
        // Keep the read-only control ready for keyboard selection without
        // automatically highlighting the full status on every refresh.
        SendMessageW(status_, EM_SETSEL, 0, 0);
        UpdateWindow(status_);
    }

    HINSTANCE instance_{};
    HWND hwnd_{};
    HWND status_{};
    HFONT statusFont_{};
    ULONGLONG startedAt_{};
    ULONGLONG nextStatusUpdate_{};
    bool webviewInitializing_{};
    turingdesk::HarnessProcessManager harness_;
    ComPtr<ICoreWebView2Controller> webviewController_;
    ComPtr<ICoreWebView2> webview_;
};

void ActivateExistingHarnessWindow() {
    const HWND existing = FindWindowW(kWindowClass, nullptr);
    if (!existing) return;
    ShowWindow(existing, SW_SHOWNORMAL);
    SetForegroundWindow(existing);
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) return 3;

    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (args.find(L"--harness-smoke-test") != std::wstring_view::npos) {
        const int result = RunHarnessSmokeTest();
        if (SUCCEEDED(com)) CoUninitialize();
        return result;
    }
    if (args.find(L"--self-test") != std::wstring_view::npos) {
        const int result = turingdesk::HarnessProcessManager::SelfTest() ? 0 : 5;
        if (SUCCEEDED(com)) CoUninitialize();
        return result;
    }

    HANDLE mutex = CreateMutexW(nullptr, FALSE, kMutexName);
    if (!mutex) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 2;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        ActivateExistingHarnessWindow();
        CloseHandle(mutex);
        if (SUCCEEDED(com)) CoUninitialize();
        return 0;
    }

    HarnessHost host(instance);
    if (!host.Create()) {
        CloseHandle(mutex);
        if (SUCCEEDED(com)) CoUninitialize();
        return 4;
    }

    const int result = host.Run();
    CloseHandle(mutex);
    if (SUCCEEDED(com)) CoUninitialize();
    return result;
}
#include "turingdesk/SettingsCenterWindow.h"
#include "turingdesk/ModelSettingsWindow.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <filesystem>
#include <iterator>
#include <string>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr wchar_t kCenterClass[] = L"TuringDesk.Native.SettingsCenterWindow";
constexpr int kDesktopButtonId = 3101;
constexpr int kAiButtonId = 3102;
constexpr int kHarnessButtonId = 3103;
constexpr int kCloseButtonId = 3104;
constexpr int kStatusLabelId = 3105;
constexpr DWMWINDOWATTRIBUTE kDwmUseImmersiveDarkMode = static_cast<DWMWINDOWATTRIBUTE>(20);
constexpr DWMWINDOWATTRIBUTE kDwmWindowCornerPreference = static_cast<DWMWINDOWATTRIBUTE>(33);
constexpr DWMWINDOWATTRIBUTE kDwmBorderColor = static_cast<DWMWINDOWATTRIBUTE>(34);
constexpr DWMWINDOWATTRIBUTE kDwmSystemBackdropType = static_cast<DWMWINDOWATTRIBUTE>(38);

HWND gCenterWindow{};

struct CenterState {
    HINSTANCE instance{};
    L3Agent* agent{};
    HWND window{};
    HWND status{};
    HFONT titleFont{};
    HFONT sectionFont{};
    HFONT bodyFont{};
};

fs::path ModuleDirectory() {
    wchar_t modulePath[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath)));
    if (length == 0 || length >= std::size(modulePath)) return {};
    return fs::path(std::wstring(modulePath, length)).parent_path();
}

void SetStatus(CenterState& state, const std::wstring& text) {
    if (state.status) SetWindowTextW(state.status, text.c_str());
}

bool LaunchSibling(CenterState& state,
                   const wchar_t* executableName,
                   const wchar_t* arguments,
                   const wchar_t* displayName) {
    const fs::path directory = ModuleDirectory();
    if (directory.empty()) {
        SetStatus(state, L"无法读取 TuringDesk 安装目录。");
        return false;
    }

    const fs::path executable = directory / executableName;
    std::error_code ec;
    if (!fs::is_regular_file(executable, ec)) {
        SetStatus(state, std::wstring(displayName) + L" 未安装：" + executable.wstring());
        MessageBoxW(state.window,
                    (std::wstring(L"当前安装包缺少 ") + executableName + L"。请部署最新版本。").c_str(),
                    L"TuringDesk 设置中心", MB_OK | MB_ICONERROR);
        return false;
    }

    const auto result = reinterpret_cast<INT_PTR>(
        ShellExecuteW(state.window, L"open", executable.c_str(), arguments,
                      directory.c_str(), SW_SHOWNORMAL));
    if (result <= 32) {
        SetStatus(state, std::wstring(displayName) + L" 启动失败（ShellExecute=" +
                         std::to_wstring(result) + L"）。");
        return false;
    }

    SetStatus(state, std::wstring(displayName) + L" 已启动。设置中心仍可继续使用。");
    return true;
}

void ApplyWindows11Style(HWND hwnd) {
    const BOOL dark = FALSE;
    DwmSetWindowAttribute(hwnd, kDwmUseImmersiveDarkMode, &dark, sizeof(dark));
    const int corner = 2;
    DwmSetWindowAttribute(hwnd, kDwmWindowCornerPreference, &corner, sizeof(corner));
    const COLORREF border = RGB(210, 215, 222);
    DwmSetWindowAttribute(hwnd, kDwmBorderColor, &border, sizeof(border));
    const int backdrop = 2;
    DwmSetWindowAttribute(hwnd, kDwmSystemBackdropType, &backdrop, sizeof(backdrop));
}

void SetFont(HWND control, HFONT font) {
    if (control && font) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
}

LRESULT CALLBACK CenterProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    auto* state = reinterpret_cast<CenterState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        state = static_cast<CenterState*>(create->lpCreateParams);
        state->window = hwnd;
        gCenterWindow = hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
    }
    if (!state) return DefWindowProcW(hwnd, message, wParam, lParam);

    switch (message) {
    case WM_COMMAND:
        if (HIWORD(wParam) != BN_CLICKED) break;
        switch (LOWORD(wParam)) {
        case kDesktopButtonId:
            LaunchSibling(*state, L"TuringDeskWallpaper.exe", L"--settings", L"桌面与壁纸设置");
            return 0;
        case kAiButtonId: {
            if (state->agent->Busy()) state->agent->Stop();
            const bool saved = ShowModelSettingsWindow(state->instance, hwnd, *state->agent);
            if (saved) {
                SetStatus(*state, L"AI 模型配置已保存：" + state->agent->Config().model);
            } else {
                SetStatus(*state, L"AI 模型设置已关闭。");
            }
            return 0;
        }
        case kHarnessButtonId:
            LaunchSibling(*state, L"TuringDeskHarness.exe", nullptr, L"DeepSeek Harness (L4)");
            return 0;
        case kCloseButtonId:
            DestroyWindow(hwnd);
            return 0;
        }
        break;
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        gCenterWindow = nullptr;
        if (state->titleFont) DeleteObject(state->titleFont);
        if (state->sectionFont) DeleteObject(state->sectionFont);
        if (state->bodyFont) DeleteObject(state->bodyFont);
        delete state;
        return 0;
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

} // namespace

bool ShowSettingsCenterWindow(HINSTANCE instance, HWND owner, L3Agent& agent) {
    if (gCenterWindow && IsWindow(gCenterWindow)) {
        ShowWindow(gCenterWindow, SW_SHOWNORMAL);
        SetForegroundWindow(gCenterWindow);
        return true;
    }

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.hInstance = instance;
    wc.lpfnWndProc = &CenterProc;
    wc.lpszClassName = kCenterClass;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

    auto* state = new CenterState{};
    state->instance = instance;
    state->agent = &agent;
    state->titleFont = CreateFontW(-26, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                   OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                   DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Display");
    state->sectionFont = CreateFontW(-18, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                     OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                     DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    state->bodyFont = CreateFontW(-15, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");

    constexpr int width = 660;
    constexpr int height = 445;
    RECT ownerRect{};
    if (!owner || !GetWindowRect(owner, &ownerRect)) {
        ownerRect = {200, 120, 960, 540};
    }
    const int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    const int y = ownerRect.top + 70;

    HWND window = CreateWindowExW(WS_EX_TOOLWINDOW,
                                  kCenterClass, L"TuringDesk 设置中心",
                                  WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                                  x, y, width, height,
                                  nullptr, nullptr, instance, state);
    if (!window) {
        if (state->titleFont) DeleteObject(state->titleFont);
        if (state->sectionFont) DeleteObject(state->sectionFont);
        if (state->bodyFont) DeleteObject(state->bodyFont);
        delete state;
        return false;
    }

    ApplyWindows11Style(window);

    HWND title = CreateWindowExW(0, L"STATIC", L"设置中心", WS_CHILD | WS_VISIBLE,
                                 28, 22, 560, 36, window, nullptr, instance, nullptr);
    HWND intro = CreateWindowExW(0, L"STATIC", L"统一管理桌面、AI 模型和 DeepSeek Harness。",
                                 WS_CHILD | WS_VISIBLE,
                                 30, 58, 560, 24, window, nullptr, instance, nullptr);

    HWND desktopTitle = CreateWindowExW(0, L"STATIC", L"桌面与壁纸", WS_CHILD | WS_VISIBLE,
                                        30, 104, 360, 24, window, nullptr, instance, nullptr);
    HWND desktopDesc = CreateWindowExW(0, L"STATIC", L"动态 Scene、图片/视频壁纸、应用到桌面。",
                                       WS_CHILD | WS_VISIBLE,
                                       30, 132, 440, 22, window, nullptr, instance, nullptr);
    HWND desktopButton = CreateWindowExW(0, L"BUTTON", L"打开",
                                         WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                         510, 112, 105, 36, window,
                                         reinterpret_cast<HMENU>(static_cast<INT_PTR>(kDesktopButtonId)), instance, nullptr);

    HWND aiTitle = CreateWindowExW(0, L"STATIC", L"AI 与模型", WS_CHILD | WS_VISIBLE,
                                   30, 181, 360, 24, window, nullptr, instance, nullptr);
    std::wstring aiDescription = agent.HasApiKey()
        ? L"当前模型：" + agent.Config().model + L" · API Key 已配置"
        : L"配置 Provider / Base URL / API Key / Model。";
    HWND aiDesc = CreateWindowExW(0, L"STATIC", aiDescription.c_str(),
                                  WS_CHILD | WS_VISIBLE,
                                  30, 209, 440, 22, window, nullptr, instance, nullptr);
    HWND aiButton = CreateWindowExW(0, L"BUTTON", L"配置",
                                    WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                    510, 189, 105, 36, window,
                                    reinterpret_cast<HMENU>(static_cast<INT_PTR>(kAiButtonId)), instance, nullptr);

    HWND harnessTitle = CreateWindowExW(0, L"STATIC", L"DeepSeek Harness (L4)", WS_CHILD | WS_VISIBLE,
                                        30, 258, 390, 24, window, nullptr, instance, nullptr);
    HWND harnessDesc = CreateWindowExW(0, L"STATIC", L"Agent 工作台在独立 WebView2 窗口运行，不阻塞设置中心。",
                                       WS_CHILD | WS_VISIBLE,
                                       30, 286, 455, 22, window, nullptr, instance, nullptr);
    HWND harnessButton = CreateWindowExW(0, L"BUTTON", L"启动",
                                         WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                         510, 266, 105, 36, window,
                                         reinterpret_cast<HMENU>(static_cast<INT_PTR>(kHarnessButtonId)), instance, nullptr);

    state->status = CreateWindowExW(0, L"STATIC", L"DeepSeek Harness 可直接从这里进入。",
                                    WS_CHILD | WS_VISIBLE,
                                    30, 338, 470, 38, window,
                                    reinterpret_cast<HMENU>(static_cast<INT_PTR>(kStatusLabelId)), instance, nullptr);
    HWND closeButton = CreateWindowExW(0, L"BUTTON", L"关闭",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       510, 344, 105, 34, window,
                                       reinterpret_cast<HMENU>(static_cast<INT_PTR>(kCloseButtonId)), instance, nullptr);

    SetFont(title, state->titleFont);
    for (HWND control : {desktopTitle, aiTitle, harnessTitle}) SetFont(control, state->sectionFont);
    for (HWND control : {intro, desktopDesc, desktopButton, aiDesc, aiButton, harnessDesc,
                         harnessButton, state->status, closeButton}) {
        SetFont(control, state->bodyFont);
    }

    ShowWindow(window, SW_SHOWNORMAL);
    SetForegroundWindow(window);
    return true;
}

} // namespace turingdesk

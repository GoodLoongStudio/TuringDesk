#include "turingdesk/SettingsCenterWindow.h"
#include "turingdesk/ModelSettingsWindow.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <algorithm>
#include <filesystem>
#include <iterator>
#include <string>
#include <vector>

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

constexpr COLORREF kText = RGB(31, 31, 31);
constexpr COLORREF kSecondary = RGB(96, 96, 96);
constexpr COLORREF kSurface = RGB(243, 243, 243);
constexpr COLORREF kCard = RGB(255, 255, 255);
constexpr COLORREF kBorder = RGB(224, 224, 224);
constexpr COLORREF kAccent = RGB(0, 103, 192);
constexpr COLORREF kPressed = RGB(232, 232, 232);

HWND gCenterWindow{};

struct CenterState {
    HINSTANCE instance{};
    L3Agent* agent{};
    HWND window{};
    HWND status{};
    HFONT titleFont{};
    HFONT sectionFont{};
    HFONT bodyFont{};
    HFONT captionFont{};
    HFONT iconFont{};
    HBRUSH surfaceBrush{};
    HBRUSH cardBrush{};
    std::vector<HWND> transparentStatics;
};

fs::path ModuleDirectory() {
    wchar_t modulePath[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath)));
    if (length == 0 || length >= std::size(modulePath)) return {};
    return fs::path(std::wstring(modulePath, length)).parent_path();
}

void SetStatus(CenterState& state, const std::wstring& text) {
    if (state.status) {
        SetWindowTextW(state.status, text.c_str());
        InvalidateRect(state.status, nullptr, TRUE);
    }
}

void SetFont(HWND control, HFONT font) {
    if (control && font) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
}

void RoundControl(HWND control, int radius = 10) {
    if (!control) return;
    RECT rc{};
    GetClientRect(control, &rc);
    HRGN region = CreateRoundRectRgn(0, 0, rc.right + 1, rc.bottom + 1, radius, radius);
    if (region && !SetWindowRgn(control, region, TRUE)) DeleteObject(region);
}

void DrawButton(const DRAWITEMSTRUCT& item, HFONT font, bool accent) {
    const bool pressed = (item.itemState & ODS_SELECTED) != 0;
    COLORREF fillColor = accent ? kAccent : RGB(250, 250, 250);
    COLORREF textColor = accent ? RGB(255, 255, 255) : kText;
    if (pressed) fillColor = accent ? RGB(0, 82, 153) : kPressed;

    HBRUSH fill = CreateSolidBrush(fillColor);
    HPEN pen = CreatePen(PS_SOLID, 1, accent ? kAccent : RGB(210, 210, 210));
    HGDIOBJ oldBrush = SelectObject(item.hDC, fill);
    HGDIOBJ oldPen = SelectObject(item.hDC, pen);
    RoundRect(item.hDC, item.rcItem.left, item.rcItem.top, item.rcItem.right, item.rcItem.bottom, 10, 10);
    SelectObject(item.hDC, oldBrush);
    SelectObject(item.hDC, oldPen);
    DeleteObject(fill);
    DeleteObject(pen);

    wchar_t text[128]{};
    GetWindowTextW(item.hwndItem, text, static_cast<int>(std::size(text)));
    SetBkMode(item.hDC, TRANSPARENT);
    SetTextColor(item.hDC, textColor);
    HFONT oldFont = reinterpret_cast<HFONT>(SelectObject(item.hDC, font));
    RECT rc = item.rcItem;
    DrawTextW(item.hDC, text, -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
    SelectObject(item.hDC, oldFont);
}

void DrawCard(HDC dc, int top, int bottom) {
    HBRUSH fill = CreateSolidBrush(kCard);
    HPEN pen = CreatePen(PS_SOLID, 1, kBorder);
    HGDIOBJ oldBrush = SelectObject(dc, fill);
    HGDIOBJ oldPen = SelectObject(dc, pen);
    RoundRect(dc, 24, top, 690, bottom, 14, 14);
    SelectObject(dc, oldBrush);
    SelectObject(dc, oldPen);
    DeleteObject(fill);
    DeleteObject(pen);
}

bool LaunchSibling(CenterState& state, const wchar_t* executableName,
                   const wchar_t* arguments, const wchar_t* displayName) {
    const fs::path directory = ModuleDirectory();
    if (directory.empty()) { SetStatus(state, L"无法读取 TuringDesk 安装目录。"); return false; }
    const fs::path executable = directory / executableName;
    std::error_code ec;
    if (!fs::is_regular_file(executable, ec)) {
        SetStatus(state, std::wstring(displayName) + L" 未安装。");
        MessageBoxW(state.window, (std::wstring(L"当前安装包缺少 ") + executableName + L"。请部署最新版本。").c_str(),
                    L"TuringDesk 设置中心", MB_OK | MB_ICONERROR);
        return false;
    }
    const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(state.window, L"open", executable.c_str(), arguments,
                                                               directory.c_str(), SW_SHOWNORMAL));
    if (result <= 32) {
        SetStatus(state, std::wstring(displayName) + L" 启动失败（" + std::to_wstring(result) + L"）。");
        return false;
    }
    SetStatus(state, std::wstring(displayName) + L" 已启动。");
    return true;
}

void ApplyWindows11Style(HWND hwnd) {
    const BOOL dark = FALSE;
    DwmSetWindowAttribute(hwnd, kDwmUseImmersiveDarkMode, &dark, sizeof(dark));
    const int corner = 2;
    DwmSetWindowAttribute(hwnd, kDwmWindowCornerPreference, &corner, sizeof(corner));
    const COLORREF border = RGB(207, 207, 207);
    DwmSetWindowAttribute(hwnd, kDwmBorderColor, &border, sizeof(border));
    const int backdrop = 2;
    DwmSetWindowAttribute(hwnd, kDwmSystemBackdropType, &backdrop, sizeof(backdrop));
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
            SetStatus(*state, saved ? L"AI 模型配置已保存：" + state->agent->Config().model : L"AI 模型设置已关闭。");
            return 0;
        }
        case kHarnessButtonId:
            LaunchSibling(*state, L"TuringDeskHarness.exe", nullptr, L"DeepSeek Harness");
            return 0;
        case kCloseButtonId:
            DestroyWindow(hwnd); return 0;
        }
        break;
    case WM_DRAWITEM: {
        const auto* item = reinterpret_cast<DRAWITEMSTRUCT*>(lParam);
        if (!item) break;
        if (item->CtlID == kDesktopButtonId || item->CtlID == kAiButtonId || item->CtlID == kHarnessButtonId || item->CtlID == kCloseButtonId) {
            DrawButton(*item, state->bodyFont, item->CtlID == kHarnessButtonId);
            return TRUE;
        }
        break;
    }
    case WM_CTLCOLORSTATIC: {
        const HDC dc = reinterpret_cast<HDC>(wParam);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, kText);
        HWND control = reinterpret_cast<HWND>(lParam);
        if (control == state->status) SetTextColor(dc, kSecondary);
        return reinterpret_cast<LRESULT>(GetStockObject(NULL_BRUSH));
    }
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT: {
        PAINTSTRUCT ps{};
        HDC dc = BeginPaint(hwnd, &ps);
        RECT rc{}; GetClientRect(hwnd, &rc);
        FillRect(dc, &rc, state->surfaceBrush);
        DrawCard(dc, 112, 207);
        DrawCard(dc, 219, 314);
        DrawCard(dc, 326, 421);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_CLOSE:
        DestroyWindow(hwnd); return 0;
    case WM_DESTROY:
        gCenterWindow = nullptr;
        if (state->titleFont) DeleteObject(state->titleFont);
        if (state->sectionFont) DeleteObject(state->sectionFont);
        if (state->bodyFont) DeleteObject(state->bodyFont);
        if (state->captionFont) DeleteObject(state->captionFont);
        if (state->iconFont) DeleteObject(state->iconFont);
        if (state->surfaceBrush) DeleteObject(state->surfaceBrush);
        if (state->cardBrush) DeleteObject(state->cardBrush);
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
    wc.hbrBackground = nullptr;
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

    auto* state = new CenterState{};
    state->instance = instance;
    state->agent = &agent;
    state->titleFont = CreateFontW(-30, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                   OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                   DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Display");
    state->sectionFont = CreateFontW(-18, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                     OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                     DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    state->bodyFont = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    state->captionFont = CreateFontW(-14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                     OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                     DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    state->iconFont = CreateFontW(-24, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH | FF_DONTCARE, L"Segoe Fluent Icons");
    state->surfaceBrush = CreateSolidBrush(kSurface);
    state->cardBrush = CreateSolidBrush(kCard);

    constexpr int width = 730;
    constexpr int height = 520;
    RECT ownerRect{};
    if (!owner || !GetWindowRect(owner, &ownerRect)) ownerRect = {200, 120, 1020, 620};
    const int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    const int y = ownerRect.top + 54;

    HWND window = CreateWindowExW(WS_EX_TOOLWINDOW, kCenterClass, L"TuringDesk 设置",
                                  WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                                  x, y, width, height, nullptr, nullptr, instance, state);
    if (!window) { delete state; return false; }
    ApplyWindows11Style(window);

    auto makeStatic = [&](const wchar_t* text, int x0, int y0, int w, int h, HFONT font) {
        HWND control = CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                       x0, y0, w, h, window, nullptr, instance, nullptr);
        SetFont(control, font);
        state->transparentStatics.push_back(control);
        return control;
    };

    makeStatic(L"设置", 28, 24, 500, 40, state->titleFont);
    makeStatic(L"管理 TuringDesk 的桌面体验、AI 模型与 Agent 工作台", 30, 68, 620, 24, state->bodyFont);

    HWND desktopIcon = makeStatic(L"\xE7F4", 42, 137, 40, 32, state->iconFont);
    (void)desktopIcon;
    makeStatic(L"桌面与壁纸", 94, 130, 390, 26, state->sectionFont);
    makeStatic(L"动态 Scene、图片与视频壁纸、桌面应用方式", 94, 160, 430, 22, state->captionFont);
    HWND desktopButton = CreateWindowExW(0, L"BUTTON", L"打开", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                         570, 141, 90, 36, window,
                                         reinterpret_cast<HMENU>(static_cast<INT_PTR>(kDesktopButtonId)), instance, nullptr);

    makeStatic(L"✦", 44, 246, 40, 32, state->sectionFont);
    makeStatic(L"AI 与模型", 94, 237, 390, 26, state->sectionFont);
    const std::wstring aiDescription = agent.HasApiKey()
        ? L"当前模型：" + agent.Config().model + L" · API Key 已安全保存"
        : L"配置 Provider、Base URL、API Key 与模型";
    makeStatic(aiDescription.c_str(), 94, 267, 430, 22, state->captionFont);
    HWND aiButton = CreateWindowExW(0, L"BUTTON", L"配置", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                    570, 248, 90, 36, window,
                                    reinterpret_cast<HMENU>(static_cast<INT_PTR>(kAiButtonId)), instance, nullptr);

    makeStatic(L"\xE756", 42, 350, 40, 32, state->iconFont);
    makeStatic(L"DeepSeek Harness", 94, 344, 390, 26, state->sectionFont);
    makeStatic(L"L4 Agent 工作台 · 独立 WebView2 窗口，不阻塞设置中心", 94, 374, 455, 22, state->captionFont);
    HWND harnessButton = CreateWindowExW(0, L"BUTTON", L"启动", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                         570, 355, 90, 36, window,
                                         reinterpret_cast<HMENU>(static_cast<INT_PTR>(kHarnessButtonId)), instance, nullptr);

    state->status = makeStatic(L"提示：Harness 入口已独立，不需要先进入 AI 设置。", 30, 448, 520, 24, state->captionFont);
    HWND closeButton = CreateWindowExW(0, L"BUTTON", L"关闭", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                       570, 442, 90, 34, window,
                                       reinterpret_cast<HMENU>(static_cast<INT_PTR>(kCloseButtonId)), instance, nullptr);

    for (HWND button : {desktopButton, aiButton, harnessButton, closeButton}) {
        SetFont(button, state->bodyFont);
        RoundControl(button);
    }

    ShowWindow(window, SW_SHOWNORMAL);
    SetForegroundWindow(window);
    return true;
}

} // namespace turingdesk

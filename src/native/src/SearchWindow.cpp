#include "turingdesk/SearchWindow.h"
#include "turingdesk/L3CliWindow.h"
#include "turingdesk/ModelSettingsWindow.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <algorithm>
#include <filesystem>
#include <iterator>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr int kHotkeyId = 1;
constexpr int kSearchEditId = 100;
constexpr int kSettingsButtonId = 101;
constexpr int kWindowWidth = 760;
constexpr int kCollapsedHeight = 112;
constexpr int kExpandedHeight = 420;
constexpr UINT kTrayMessage = WM_APP + 91;
constexpr UINT kTrayShow = 5101;
constexpr UINT kTrayWallpaper = 5102;
constexpr UINT kTrayExit = 5103;

constexpr DWM_WINDOW_ATTRIBUTE kDwmUseImmersiveDarkMode = static_cast<DWM_WINDOW_ATTRIBUTE>(20);
constexpr DWM_WINDOW_ATTRIBUTE kDwmWindowCornerPreference = static_cast<DWM_WINDOW_ATTRIBUTE>(33);
constexpr DWM_WINDOW_ATTRIBUTE kDwmBorderColor = static_cast<DWM_WINDOW_ATTRIBUTE>(34);
constexpr DWM_WINDOW_ATTRIBUTE kDwmSystemBackdropType = static_cast<DWM_WINDOW_ATTRIBUTE>(38);
constexpr int kDwmCornerRound = 2;
constexpr int kDwmBackdropTransient = 3;

struct TdAccentPolicy {
    int state{};
    int flags{};
    DWORD gradientColor{};
    int animationId{};
};

struct TdCompositionData {
    int attribute{};
    PVOID data{};
    SIZE_T size{};
};

using SetWindowCompositionAttributeFn = BOOL(WINAPI*)(HWND, TdCompositionData*);

bool IsLaunchable(ResultKind kind) {
    return kind == ResultKind::App || kind == ResultKind::File || kind == ResultKind::Folder;
}

const wchar_t* KindLabel(ResultKind kind) {
    switch (kind) {
    case ResultKind::App: return L"L1 应用";
    case ResultKind::File: return L"L2 文件";
    case ResultKind::Folder: return L"L2 文件夹";
    case ResultKind::Answer: return L"L3 AI";
    case ResultKind::Status: return L"状态";
    }
    return L"";
}

std::wstring ReadText(HWND control) {
    const int len = GetWindowTextLengthW(control);
    std::wstring value(static_cast<std::size_t>(len) + 1, L'\0');
    if (len > 0) GetWindowTextW(control, value.data(), len + 1);
    value.resize(static_cast<std::size_t>(len));
    return value;
}

void RoundControl(HWND control, int radius = 14) {
    if (!control || !IsWindow(control)) return;
    RECT rc{};
    if (!GetClientRect(control, &rc)) return;
    HRGN region = CreateRoundRectRgn(0, 0, std::max(1L, rc.right - rc.left) + 1,
                                     std::max(1L, rc.bottom - rc.top) + 1, radius, radius);
    if (!region) return;
    if (!SetWindowRgn(control, region, TRUE)) DeleteObject(region);
}

} // namespace

SearchWindow::SearchWindow(HINSTANCE instance) : instance_(instance) {}

SearchWindow::~SearchWindow() {
    l3_.Stop();
    files_.Shutdown();
    RemoveTray();
    if (hwnd_) UnregisterHotKey(hwnd_, kHotkeyId);
    if (editBrush_) DeleteObject(editBrush_);
    if (buttonBrush_) DeleteObject(buttonBrush_);
    if (uiFont_) DeleteObject(uiFont_);
}

bool SearchWindow::Create() {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.hInstance = instance_;
    wc.lpfnWndProc = &SearchWindow::WndProc;
    wc.lpszClassName = L"TuringDesk.Native.SearchWindow";
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = nullptr;
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory_.GetAddressOf()))) return false;
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                   reinterpret_cast<IUnknown**>(writeFactory_.GetAddressOf())))) return false;

    writeFactory_->CreateTextFormat(L"Segoe UI Variable Text", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
                                    DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                    17.0f, L"zh-CN", titleFormat_.GetAddressOf());
    if (!titleFormat_) {
        writeFactory_->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
                                        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                        17.0f, L"zh-CN", titleFormat_.GetAddressOf());
    }
    writeFactory_->CreateTextFormat(L"Segoe UI Variable Text", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                    DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                    12.5f, L"zh-CN", subtitleFormat_.GetAddressOf());
    if (!subtitleFormat_) {
        writeFactory_->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                                        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                                        12.5f, L"zh-CN", subtitleFormat_.GetAddressOf());
    }
    if (titleFormat_) titleFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    if (subtitleFormat_) subtitleFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);

    hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, L"TuringDesk Search", WS_POPUP,
                            CW_USEDEFAULT, CW_USEDEFAULT, kWindowWidth, kCollapsedHeight,
                            nullptr, nullptr, instance_, this);
    if (!hwnd_) return false;

    uiFont_ = CreateFontW(-19, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                          OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                          DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    if (!uiFont_) {
        uiFont_ = CreateFontW(-19, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                              OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                              DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    }
    editBrush_ = CreateSolidBrush(RGB(28, 30, 37));
    buttonBrush_ = CreateSolidBrush(RGB(42, 45, 54));

    edit_ = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                            20, 16, kWindowWidth - 150, 42, hwnd_, reinterpret_cast<HMENU>(kSearchEditId), instance_, nullptr);
    if (!edit_) return false;
    SetWindowLongPtrW(edit_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    oldEditProc_ = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(edit_, GWLP_WNDPROC,
                                                               reinterpret_cast<LONG_PTR>(&SearchWindow::EditProc)));
    SendMessageW(edit_, WM_SETFONT, reinterpret_cast<WPARAM>(uiFont_), TRUE);
    SendMessageW(edit_, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, MAKELPARAM(14, 14));
    SendMessageW(edit_, EM_SETCUEBANNER, TRUE,
                 reinterpret_cast<LPARAM>(L"搜索应用、文件或直接问 AI…"));

    settingsButton_ = CreateWindowExW(0, L"BUTTON", L"AI 设置",
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                      kWindowWidth - 118, 16, 98, 42, hwnd_,
                                      reinterpret_cast<HMENU>(kSettingsButtonId), instance_, nullptr);
    if (!settingsButton_) return false;
    SendMessageW(settingsButton_, WM_SETFONT, reinterpret_cast<WPARAM>(uiFont_), TRUE);
    RoundControl(edit_);
    RoundControl(settingsButton_);

    ApplyWindows11Style();
    ChangeWindowMessageFilterEx(hwnd_, WM_COPYDATA, MSGFLT_ALLOW, nullptr);

    if (!RegisterHotKey(hwnd_, kHotkeyId, MOD_ALT | MOD_NOREPEAT, VK_SPACE)) return false;

    taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
    AddTray();
    apps_.BuildIndex();
    fileSearchAvailable_ = files_.Available();
    expanded_ = false;
    PositionWindow();
    ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
    UpdateWindow(hwnd_);
    return true;
}

bool SearchWindow::SelfTest() {
    if (apps_.Count() == 0) apps_.BuildIndex();
    std::wstring reply;
    bool secret = false;
    const bool local = l3_.TryHandleLocal(L"/time", reply, secret);
    return apps_.Count() >= 5 && files_.SelfTest() && local && !reply.empty() && !secret;
}

void SearchWindow::ApplyWindows11Style() {
    if (!hwnd_) return;

    const BOOL dark = TRUE;
    DwmSetWindowAttribute(hwnd_, kDwmUseImmersiveDarkMode, &dark, sizeof(dark));
    const int corner = kDwmCornerRound;
    DwmSetWindowAttribute(hwnd_, kDwmWindowCornerPreference, &corner, sizeof(corner));
    const COLORREF border = RGB(72, 76, 88);
    DwmSetWindowAttribute(hwnd_, kDwmBorderColor, &border, sizeof(border));
    const int backdrop = kDwmBackdropTransient;
    DwmSetWindowAttribute(hwnd_, kDwmSystemBackdropType, &backdrop, sizeof(backdrop));

    MARGINS margins{-1, -1, -1, -1};
    DwmExtendFrameIntoClientArea(hwnd_, &margins);

    const HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (user32) {
        const auto setComposition = reinterpret_cast<SetWindowCompositionAttributeFn>(
            GetProcAddress(user32, "SetWindowCompositionAttribute"));
        if (setComposition) {
            // WCA_ACCENT_POLICY=19, ACCENT_ENABLE_ACRYLICBLURBEHIND=4.
            TdAccentPolicy policy{};
            policy.state = 4;
            policy.gradientColor = 0xD425211F;
            TdCompositionData data{};
            data.attribute = 19;
            data.data = &policy;
            data.size = sizeof(policy);
            setComposition(hwnd_, &data);
        }
    }
}

void SearchWindow::ShowAndFocus() {
    PositionWindow();
    if (!currentQuery_.empty() || !results_.empty()) SetExpanded(true);
    ShowWindow(hwnd_, SW_SHOWNORMAL);
    SetWindowPos(hwnd_, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    SetForegroundWindow(hwnd_);
    SetFocus(edit_);
    SendMessageW(edit_, EM_SETSEL, 0, -1);
    InvalidateRect(hwnd_, nullptr, FALSE);
}

int SearchWindow::RunMessageLoop() {
    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return static_cast<int>(msg.wParam);
}

void SearchWindow::SetExpanded(bool expanded) {
    if (!hwnd_ || expanded_ == expanded) return;
    expanded_ = expanded;
    RECT rect{};
    GetWindowRect(hwnd_, &rect);
    const int height = expanded_ ? kExpandedHeight : kCollapsedHeight;
    SetWindowPos(hwnd_, nullptr, rect.left, rect.top, kWindowWidth, height,
                 SWP_NOZORDER | SWP_NOACTIVATE);
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void SearchWindow::PositionWindow() {
    POINT point{};
    GetCursorPos(&point);
    HMONITOR monitor = MonitorFromPoint(point, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO info{sizeof(info)};
    if (!GetMonitorInfoW(monitor, &info)) return;
    const int x = info.rcWork.left + (info.rcWork.right - info.rcWork.left - kWindowWidth) / 2;
    const int y = info.rcWork.top + 24;
    const int height = expanded_ ? kExpandedHeight : kCollapsedHeight;
    SetWindowPos(hwnd_, nullptr, x, y, kWindowWidth, height, SWP_NOZORDER | SWP_NOACTIVATE);
}

void SearchWindow::AddTray() {
    if (!hwnd_ || trayAdded_) return;
    tray_ = {};
    tray_.cbSize = sizeof(tray_);
    tray_.hWnd = hwnd_;
    tray_.uID = 1;
    tray_.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    tray_.uCallbackMessage = kTrayMessage;
    tray_.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    wcscpy_s(tray_.szTip, L"TuringDesk");
    trayAdded_ = Shell_NotifyIconW(NIM_ADD, &tray_) != FALSE;
}

void SearchWindow::RemoveTray() {
    if (!trayAdded_) return;
    Shell_NotifyIconW(NIM_DELETE, &tray_);
    trayAdded_ = false;
}

void SearchWindow::HandleTray(UINT mouseMessage) {
    if (mouseMessage == WM_LBUTTONUP || mouseMessage == WM_LBUTTONDBLCLK) {
        ShowAndFocus();
        return;
    }
    if (mouseMessage != WM_RBUTTONUP && mouseMessage != WM_CONTEXTMENU) return;

    HMENU menu = CreatePopupMenu();
    if (!menu) return;
    AppendMenuW(menu, MF_STRING, kTrayShow, L"显示搜索");
    AppendMenuW(menu, MF_STRING, kTrayWallpaper, L"壁纸设置");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, kTrayExit, L"退出 TuringDesk");

    POINT point{};
    GetCursorPos(&point);
    SetForegroundWindow(hwnd_);
    const int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                                       point.x, point.y, 0, hwnd_, nullptr);
    DestroyMenu(menu);
    if (command == kTrayShow) ShowAndFocus();
    else if (command == kTrayWallpaper) OpenWallpaperSettings();
    else if (command == kTrayExit) ExitApplication();
}

void SearchWindow::OpenWallpaperSettings() {
    wchar_t modulePath[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath)));
    if (length == 0 || length >= std::size(modulePath)) {
        SetStatus(L"壁纸设置启动失败", L"无法读取 TuringDesk 安装路径。");
        return;
    }
    const fs::path wallpaper = fs::path(modulePath).parent_path() / L"TuringDeskWallpaper.exe";
    if (!fs::exists(wallpaper)) {
        SetStatus(L"壁纸引擎未找到", wallpaper.wstring());
        return;
    }
    const auto code = reinterpret_cast<INT_PTR>(ShellExecuteW(hwnd_, L"open", wallpaper.c_str(), L"--settings", nullptr, SW_SHOWNORMAL));
    if (code <= 32) SetStatus(L"壁纸设置启动失败", wallpaper.wstring());
}

void SearchWindow::ExitApplication() {
    if (exiting_) return;
    exiting_ = true;
    if (const HWND wallpaper = FindWindowW(L"TuringDesk.Native.WallpaperControl", nullptr)) {
        PostMessageW(wallpaper, WM_CLOSE, 0, 0);
    }
    if (hwnd_ && IsWindow(hwnd_)) DestroyWindow(hwnd_);
}

LRESULT CALLBACK SearchWindow::WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    SearchWindow* self = reinterpret_cast<SearchWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        self = static_cast<SearchWindow*>(create->lpCreateParams);
        self->hwnd_ = hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    return self ? self->HandleMessage(message, wParam, lParam) : DefWindowProcW(hwnd, message, wParam, lParam);
}

LRESULT CALLBACK SearchWindow::EditProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    auto* self = reinterpret_cast<SearchWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (!self) return DefWindowProcW(hwnd, message, wParam, lParam);
    if (message == WM_KEYDOWN) {
        if (wParam == VK_DOWN && !self->results_.empty()) {
            self->SetExpanded(true);
            self->selected_ = std::min<int>(self->selected_ + 1, static_cast<int>(self->results_.size()) - 1);
            InvalidateRect(self->hwnd_, nullptr, FALSE);
            return 0;
        }
        if (wParam == VK_UP && !self->results_.empty()) {
            self->SetExpanded(true);
            self->selected_ = std::max(0, self->selected_ - 1);
            InvalidateRect(self->hwnd_, nullptr, FALSE);
            return 0;
        }
        if (wParam == VK_RETURN) {
            self->ExecuteSelected((GetKeyState(VK_CONTROL) & 0x8000) != 0);
            return 0;
        }
        if (wParam == VK_ESCAPE) {
            self->SetExpanded(false);
            return 0;
        }
    }
    return CallWindowProcW(self->oldEditProc_, hwnd, message, wParam, lParam);
}

LRESULT SearchWindow::HandleMessage(UINT message, WPARAM wParam, LPARAM lParam) {
    if (taskbarCreated_ != 0 && message == taskbarCreated_) {
        trayAdded_ = false;
        AddTray();
        return 0;
    }

    switch (message) {
    case WM_HOTKEY:
        if (wParam == kHotkeyId) { ShowAndFocus(); return 0; }
        break;
    case WM_ACTIVATE:
        if (LOWORD(wParam) == WA_INACTIVE && expanded_) SetExpanded(false);
        return 0;
    case WM_COMMAND:
        if (LOWORD(wParam) == kSearchEditId && HIWORD(wParam) == EN_CHANGE) { OnQueryChanged(); return 0; }
        if (LOWORD(wParam) == kSettingsButtonId && HIWORD(wParam) == BN_CLICKED) { OpenModelSettings(); return 0; }
        break;
    case WM_DRAWITEM: {
        const auto* item = reinterpret_cast<DRAWITEMSTRUCT*>(lParam);
        if (!item || item->CtlID != kSettingsButtonId) break;
        HBRUSH selectedBrush = nullptr;
        HBRUSH fill = buttonBrush_;
        if ((item->itemState & ODS_SELECTED) != 0) {
            selectedBrush = CreateSolidBrush(RGB(54, 58, 69));
            fill = selectedBrush;
        }
        FillRect(item->hDC, &item->rcItem, fill);
        SetBkMode(item->hDC, TRANSPARENT);
        SetTextColor(item->hDC, RGB(242, 244, 248));
        const HFONT previous = reinterpret_cast<HFONT>(SelectObject(item->hDC, uiFont_));
        RECT textRect = item->rcItem;
        DrawTextW(item->hDC, L"AI 设置", -1, &textRect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        SelectObject(item->hDC, previous);
        if (selectedBrush) DeleteObject(selectedBrush);
        return TRUE;
    }
    case WM_CTLCOLOREDIT: {
        const HDC dc = reinterpret_cast<HDC>(wParam);
        SetTextColor(dc, RGB(244, 246, 250));
        SetBkColor(dc, RGB(28, 30, 37));
        return reinterpret_cast<LRESULT>(editBrush_);
    }
    case kTrayMessage:
        HandleTray(static_cast<UINT>(lParam));
        return 0;
    case WM_COPYDATA: {
        std::vector<SearchResult> received;
        if (files_.HandleCopyData(reinterpret_cast<COPYDATASTRUCT*>(lParam), received)) {
            fileSearchAvailable_ = true;
            fileSearchQueryFailed_ = false;
            fileResults_ = std::move(received);
            MergeResults();
            return TRUE;
        }
        break;
    }
    case WM_DISPLAYCHANGE:
        PositionWindow();
        return 0;
    case WM_SIZE: {
        const int width = static_cast<int>(LOWORD(lParam));
        ResizeRenderTarget(LOWORD(lParam), HIWORD(lParam));
        if (edit_) {
            MoveWindow(edit_, 20, 16, std::max(100, width - 150), 42, TRUE);
            RoundControl(edit_);
        }
        if (settingsButton_) {
            MoveWindow(settingsButton_, std::max(120, width - 118), 16, 98, 42, TRUE);
            RoundControl(settingsButton_);
        }
        return 0;
    }
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
        if (exiting_) DestroyWindow(hwnd_);
        else SetExpanded(false);
        return 0;
    case WM_QUERYENDSESSION:
        return TRUE;
    case WM_ENDSESSION:
        if (wParam) {
            exiting_ = true;
            DestroyWindow(hwnd_);
        }
        return 0;
    case WM_DESTROY:
        l3_.Stop();
        files_.Shutdown();
        RemoveTray();
        UnregisterHotKey(hwnd_, kHotkeyId);
        hwnd_ = nullptr;
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd_, message, wParam, lParam);
}

void SearchWindow::OnQueryChanged() {
    const auto query = ReadText(edit_);
    currentQuery_ = query;
    appResults_.clear();
    fileResults_.clear();
    results_.clear();
    selected_ = -1;
    fileSearchQueryFailed_ = false;

    if (query.empty()) {
        fileSearchAvailable_ = files_.Available();
        SetExpanded(false);
        InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }

    SetExpanded(true);
    if (query.front() == L'/') {
        results_.push_back({ResultKind::Status, L"L3 命令", L"Enter 打开 L3 CLI 并执行；/help 查看命令", L"", 0});
        InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }

    appResults_ = apps_.Query(query, 5);
    fileSearchAvailable_ = files_.Available();
    MergeResults();

    if (fileSearchAvailable_ && !files_.Query(hwnd_, query, 8)) {
        fileSearchQueryFailed_ = true;
        MergeResults();
    }
}

void SearchWindow::MergeResults() {
    results_.clear();
    for (const auto& result : appResults_) results_.push_back(result);
    for (const auto& result : fileResults_) {
        if (results_.size() >= 9) break;
        results_.push_back(result);
    }

    if (!fileSearchAvailable_) {
        results_.push_back({ResultKind::Status,
                            L"L2 文件搜索暂不可用",
                            L"内置 Everything 尚未启动或索引服务不可用。",
                            L"", -1000});
    } else if (fileSearchQueryFailed_) {
        results_.push_back({ResultKind::Status,
                            L"L2 文件查询失败",
                            L"Everything 已连接，但本次 IPC 查询没有成功。",
                            L"", -1000});
    }

    selected_ = -1;
    for (std::size_t i = 0; i < results_.size(); ++i) {
        if (IsLaunchable(results_[i].kind)) {
            selected_ = static_cast<int>(i);
            break;
        }
    }
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void SearchWindow::ExecuteSelected(bool forceL3) {
    const auto query = ReadText(edit_);
    if (query.empty()) return;

    if (!forceL3 && selected_ >= 0 && selected_ < static_cast<int>(results_.size())) {
        const auto& result = results_[selected_];
        if (IsLaunchable(result.kind) && !result.target.empty()) {
            const auto code = reinterpret_cast<INT_PTR>(ShellExecuteW(hwnd_, L"open", result.target.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
            if (code <= 32) SetStatus(L"打开失败", result.target);
            else {
                SetWindowTextW(edit_, L"");
                SetExpanded(false);
            }
            return;
        }
    }
    StartL3(query);
}

void SearchWindow::StartL3(const std::wstring& prompt) {
    if (l3_.Busy()) l3_.Stop();
    SetExpanded(false);
    if (!ShowL3CliWindow(instance_, hwnd_, l3_, prompt)) {
        SetStatus(L"L3 CLI 启动失败", L"请重试，或打开 AI 设置检查模型配置。");
        return;
    }
    SetWindowTextW(edit_, L"");
    SetExpanded(false);
    ShowAndFocus();
}

void SearchWindow::OpenModelSettings() {
    if (l3_.Busy()) l3_.Stop();

    const bool saved = ShowModelSettingsWindow(instance_, hwnd_, l3_);
    if (saved) {
        SetStatus(L"L3 模型配置已保存",
                  l3_.Config().model + (l3_.HasApiKey() ? L" · API Key 已配置" : L" · API Key 未配置"));
    } else {
        InvalidateRect(hwnd_, nullptr, FALSE);
    }
    SetFocus(edit_);
}

void SearchWindow::SetStatus(std::wstring title, std::wstring subtitle) {
    results_.clear();
    results_.push_back({ResultKind::Status, std::move(title), std::move(subtitle), L"", 0});
    selected_ = -1;
    SetExpanded(true);
    InvalidateRect(hwnd_, nullptr, FALSE);
}

void SearchWindow::ResizeRenderTarget(UINT width, UINT height) {
    if (renderTarget_) renderTarget_->Resize(D2D1::SizeU(width, height));
}

void SearchWindow::Draw() {
    if (!renderTarget_) {
        RECT rc{};
        GetClientRect(hwnd_, &rc);
        const auto props = D2D1::RenderTargetProperties();
        const auto hwndProps = D2D1::HwndRenderTargetProperties(hwnd_, D2D1::SizeU(rc.right - rc.left, rc.bottom - rc.top));
        if (FAILED(d2dFactory_->CreateHwndRenderTarget(props, hwndProps, renderTarget_.GetAddressOf()))) return;
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(0xf5f7fa), textBrush_.GetAddressOf());
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(0xb2b7c2), secondaryBrush_.GetAddressOf());
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(0x363b47, 0.88f), selectionBrush_.GetAddressOf());
    }

    renderTarget_->BeginDraw();
    renderTarget_->Clear(D2D1::ColorF(0x171a21, 0.82f));

    float y = 72.0f;
    const float width = renderTarget_->GetSize().width;

    std::wstring persistentStatus = L"L1 应用 · L2 ";
    persistentStatus += fileSearchAvailable_ ? L"已连接" : L"启动中";
    persistentStatus += L" · L3 ";
    persistentStatus += l3_.HasApiKey() ? l3_.Config().model : L"未配置（AI 设置）";

    if (!expanded_) {
        renderTarget_->DrawText(persistentStatus.c_str(), static_cast<UINT32>(persistentStatus.size()), subtitleFormat_.Get(),
                                D2D1::RectF(24, y, width - 24, y + 28), secondaryBrush_.Get(),
                                D2D1_DRAW_TEXT_OPTIONS_CLIP);
    } else if (results_.empty()) {
        const std::wstring hint = currentQuery_.empty()
            ? persistentStatus
            : L"没有本地结果 · Enter 进入 L3 CLI，Ctrl+Enter 可随时强制 L3";
        renderTarget_->DrawText(hint.c_str(), static_cast<UINT32>(hint.size()), subtitleFormat_.Get(),
                                D2D1::RectF(24, y, width - 24, y + 30), secondaryBrush_.Get(),
                                D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }

    if (expanded_) {
        for (std::size_t i = 0; i < results_.size(); ++i) {
            const auto& result = results_[i];
            const float rowHeight = (result.kind == ResultKind::Status) ? 64.0f : 50.0f;
            if (static_cast<int>(i) == selected_) {
                renderTarget_->FillRoundedRectangle(
                    D2D1::RoundedRect(D2D1::RectF(14, y - 4, width - 14, y + rowHeight - 4), 10, 10),
                    selectionBrush_.Get());
            }

            std::wstring title = result.title;
            if (title.size() > 100) title.resize(100);
            renderTarget_->DrawText(title.c_str(), static_cast<UINT32>(title.size()), titleFormat_.Get(),
                                    D2D1::RectF(24, y, width - 64, y + 26), textBrush_.Get(),
                                    D2D1_DRAW_TEXT_OPTIONS_CLIP);

            std::wstring subtitle = std::wstring(KindLabel(result.kind));
            if (!result.subtitle.empty()) subtitle += L" · " + result.subtitle;
            if (subtitle.size() > 150) subtitle.resize(150);
            renderTarget_->DrawText(subtitle.c_str(), static_cast<UINT32>(subtitle.size()), subtitleFormat_.Get(),
                                    D2D1::RectF(24, y + 30, width - 64, y + rowHeight), secondaryBrush_.Get(),
                                    D2D1_DRAW_TEXT_OPTIONS_CLIP);

            y += rowHeight;
            if (y > renderTarget_->GetSize().height - 30) break;
        }
    }

    const HRESULT hr = renderTarget_->EndDraw();
    if (hr == D2DERR_RECREATE_TARGET) {
        renderTarget_.Reset();
        textBrush_.Reset();
        secondaryBrush_.Reset();
        selectionBrush_.Reset();
    }
}

} // namespace turingdesk

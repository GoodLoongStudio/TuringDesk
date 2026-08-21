#include "turingdesk/SearchWindow.h"
#include "turingdesk/L3CliWindow.h"
#include "turingdesk/ModelSettingsWindow.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <algorithm>

namespace turingdesk {
namespace {

constexpr int kHotkeyId = 1;
constexpr int kSearchEditId = 100;
constexpr int kSettingsButtonId = 101;
constexpr int kCloseButtonId = 102;
constexpr int kWindowWidth = 760;
constexpr int kWindowHeight = 420;

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

} // namespace

SearchWindow::SearchWindow(HINSTANCE instance) : instance_(instance) {}

SearchWindow::~SearchWindow() {
    l3_.Stop();
    files_.Shutdown();
    if (hwnd_) UnregisterHotKey(hwnd_, kHotkeyId);
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

    writeFactory_->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
                                    DWRITE_FONT_STRETCH_NORMAL, 17.0f, L"zh-CN", titleFormat_.GetAddressOf());
    writeFactory_->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                                    DWRITE_FONT_STRETCH_NORMAL, 12.5f, L"zh-CN", subtitleFormat_.GetAddressOf());
    if (titleFormat_) titleFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    if (subtitleFormat_) subtitleFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);

    hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, L"TuringDesk Search", WS_POPUP,
                            CW_USEDEFAULT, CW_USEDEFAULT, kWindowWidth, kWindowHeight,
                            nullptr, nullptr, instance_, this);
    if (!hwnd_) return false;

    edit_ = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                            20, 16, kWindowWidth - 150, 38, hwnd_, reinterpret_cast<HMENU>(kSearchEditId), instance_, nullptr);
    if (!edit_) return false;
    SetWindowLongPtrW(edit_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    oldEditProc_ = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(edit_, GWLP_WNDPROC,
                                                               reinterpret_cast<LONG_PTR>(&SearchWindow::EditProc)));
    SendMessageW(edit_, EM_SETCUEBANNER, TRUE,
                 reinterpret_cast<LPARAM>(L"搜索应用、文件；Enter 进入 L3，Ctrl+Enter 强制 L3"));

    settingsButton_ = CreateWindowExW(0, L"BUTTON", L"AI 设置",
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                      kWindowWidth - 118, 16, 98, 38, hwnd_,
                                      reinterpret_cast<HMENU>(kSettingsButtonId), instance_, nullptr);
    if (!settingsButton_) return false;

    closeButton_ = CreateWindowExW(0, L"BUTTON", L"×",
                                   WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                   kWindowWidth - 52, kWindowHeight - 48, 32, 30, hwnd_,
                                   reinterpret_cast<HMENU>(kCloseButtonId), instance_, nullptr);
    if (!closeButton_) return false;

    const auto font = reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT));
    SendMessageW(settingsButton_, WM_SETFONT, font, TRUE);
    SendMessageW(closeButton_, WM_SETFONT, font, TRUE);

    const DWM_WINDOW_CORNER_PREFERENCE corner = DWMWCP_ROUND;
    DwmSetWindowAttribute(hwnd_, DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(corner));
    ChangeWindowMessageFilterEx(hwnd_, WM_COPYDATA, MSGFLT_ALLOW, nullptr);

    if (!RegisterHotKey(hwnd_, kHotkeyId, MOD_ALT | MOD_NOREPEAT, VK_SPACE)) return false;

    apps_.BuildIndex();
    fileSearchAvailable_ = files_.Available();
    PositionWindow();
    ShowAndFocus();
    return true;
}

bool SearchWindow::SelfTest() {
    if (apps_.Count() == 0) apps_.BuildIndex();
    std::wstring reply;
    bool secret = false;
    const bool local = l3_.TryHandleLocal(L"/time", reply, secret);
    return apps_.Count() >= 5 && files_.SelfTest() && local && !reply.empty() && !secret;
}

void SearchWindow::ShowAndFocus() {
    PositionWindow();
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

void SearchWindow::PositionWindow() {
    POINT point{};
    GetCursorPos(&point);
    HMONITOR monitor = MonitorFromPoint(point, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO info{sizeof(info)};
    if (!GetMonitorInfoW(monitor, &info)) return;
    const int x = info.rcWork.left + (info.rcWork.right - info.rcWork.left - kWindowWidth) / 2;
    const int y = info.rcWork.top + 24;
    SetWindowPos(hwnd_, nullptr, x, y, kWindowWidth, kWindowHeight, SWP_NOZORDER | SWP_NOACTIVATE);
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
            self->selected_ = std::min<int>(self->selected_ + 1, static_cast<int>(self->results_.size()) - 1);
            InvalidateRect(self->hwnd_, nullptr, FALSE);
            return 0;
        }
        if (wParam == VK_UP && !self->results_.empty()) {
            self->selected_ = std::max(0, self->selected_ - 1);
            InvalidateRect(self->hwnd_, nullptr, FALSE);
            return 0;
        }
        if (wParam == VK_RETURN) {
            self->ExecuteSelected((GetKeyState(VK_CONTROL) & 0x8000) != 0);
            return 0;
        }
        if (wParam == VK_ESCAPE) {
            ShowWindow(self->hwnd_, SW_HIDE);
            return 0;
        }
    }
    return CallWindowProcW(self->oldEditProc_, hwnd, message, wParam, lParam);
}

LRESULT SearchWindow::HandleMessage(UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_HOTKEY:
        if (wParam == kHotkeyId) { ShowAndFocus(); return 0; }
        break;
    case WM_COMMAND:
        if (LOWORD(wParam) == kSearchEditId && HIWORD(wParam) == EN_CHANGE) { OnQueryChanged(); return 0; }
        if (LOWORD(wParam) == kSettingsButtonId && HIWORD(wParam) == BN_CLICKED) { OpenModelSettings(); return 0; }
        if (LOWORD(wParam) == kCloseButtonId && HIWORD(wParam) == BN_CLICKED) { DestroyWindow(hwnd_); return 0; }
        break;
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
    case WM_SIZE: {
        const int width = static_cast<int>(LOWORD(lParam));
        const int height = static_cast<int>(HIWORD(lParam));
        ResizeRenderTarget(LOWORD(lParam), HIWORD(lParam));
        if (edit_) MoveWindow(edit_, 20, 16, std::max(100, width - 150), 38, TRUE);
        if (settingsButton_) MoveWindow(settingsButton_, std::max(120, width - 118), 16, 98, 38, TRUE);
        if (closeButton_) MoveWindow(closeButton_, std::max(20, width - 52), std::max(70, height - 48), 32, 30, TRUE);
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
    case WM_DESTROY:
        l3_.Stop();
        files_.Shutdown();
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
        InvalidateRect(hwnd_, nullptr, FALSE);
        return;
    }
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
            else ShowWindow(hwnd_, SW_HIDE);
            return;
        }
    }
    StartL3(query);
}

void SearchWindow::StartL3(const std::wstring& prompt) {
    if (l3_.Busy()) l3_.Stop();
    if (!ShowL3CliWindow(instance_, hwnd_, l3_, prompt)) {
        SetStatus(L"L3 CLI 启动失败", L"请重试，或打开 AI 设置检查模型配置。");
        return;
    }
    SetWindowTextW(edit_, L"");
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
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(0xf5f5f5), textBrush_.GetAddressOf());
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(0xa8a8a8), secondaryBrush_.GetAddressOf());
        renderTarget_->CreateSolidColorBrush(D2D1::ColorF(0x30343d), selectionBrush_.GetAddressOf());
    }

    renderTarget_->BeginDraw();
    renderTarget_->Clear(D2D1::ColorF(0x17191f));

    float y = 68.0f;
    const float width = renderTarget_->GetSize().width;
    if (results_.empty()) {
        std::wstring hint;
        if (currentQuery_.empty()) {
            hint = L"L1 应用 · L2 ";
            hint += fileSearchAvailable_ ? L"已连接" : L"启动中";
            hint += L" · L3 ";
            hint += l3_.HasApiKey() ? l3_.Config().model : L"未配置（AI 设置）";
        } else {
            hint = L"没有本地结果 · Enter 进入 L3 CLI，Ctrl+Enter 可随时强制 L3";
        }
        renderTarget_->DrawText(hint.c_str(), static_cast<UINT32>(hint.size()), subtitleFormat_.Get(),
                                D2D1::RectF(24, y, width - 24, y + 30), secondaryBrush_.Get());
    }

    for (std::size_t i = 0; i < results_.size(); ++i) {
        const auto& result = results_[i];
        const float rowHeight = (result.kind == ResultKind::Status) ? 64.0f : 50.0f;
        if (static_cast<int>(i) == selected_) {
            renderTarget_->FillRoundedRectangle(
                D2D1::RoundedRect(D2D1::RectF(14, y - 4, width - 14, y + rowHeight - 4), 8, 8),
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
        if (y > renderTarget_->GetSize().height - 56) break;
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

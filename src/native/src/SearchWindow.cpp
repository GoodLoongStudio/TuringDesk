#include "turingdesk/SearchWindow.h"
#include "turingdesk/ModelSettingsWindow.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <memory>

namespace turingdesk {
namespace {

constexpr int kHotkeyId = 1;
constexpr int kSearchEditId = 100;
constexpr int kSettingsButtonId = 101;
constexpr UINT kL3DeltaMessage = WM_APP + 10;
constexpr UINT kL3DoneMessage = WM_APP + 11;
constexpr int kWindowWidth = 760;
constexpr int kWindowHeight = 420;

struct L3UiMessage {
    std::uint64_t generation{};
    std::wstring text;
};

std::atomic_uint64_t gL3Generation{0};

void PostL3Message(HWND hwnd, UINT message, std::uint64_t generation, std::wstring text) {
    auto payload = std::make_unique<L3UiMessage>();
    payload->generation = generation;
    payload->text = std::move(text);
    if (PostMessageW(hwnd, message, 0, reinterpret_cast<LPARAM>(payload.get()))) {
        payload.release();
    }
}

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

} // namespace

SearchWindow::SearchWindow(HINSTANCE instance) : instance_(instance) {}

SearchWindow::~SearchWindow() {
    gL3Generation.fetch_add(1, std::memory_order_relaxed);
    l3_.Stop();
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
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(writeFactory_.GetAddressOf())))) return false;

    writeFactory_->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
                                    DWRITE_FONT_STRETCH_NORMAL, 17.0f, L"zh-CN", titleFormat_.GetAddressOf());
    writeFactory_->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                                    DWRITE_FONT_STRETCH_NORMAL, 12.5f, L"zh-CN", subtitleFormat_.GetAddressOf());

    hwnd_ = CreateWindowExW(WS_EX_TOOLWINDOW, wc.lpszClassName, L"TuringDesk Search", WS_POPUP,
                            CW_USEDEFAULT, CW_USEDEFAULT, kWindowWidth, kWindowHeight,
                            nullptr, nullptr, instance_, this);
    if (!hwnd_) return false;

    edit_ = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                            20, 16, kWindowWidth - 150, 38, hwnd_, reinterpret_cast<HMENU>(kSearchEditId), instance_, nullptr);
    if (!edit_) return false;
    SetWindowLongPtrW(edit_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    oldEditProc_ = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(edit_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&SearchWindow::EditProc)));
    SendMessageW(edit_, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"搜索应用、文件，或直接问 L3…  Ctrl+Enter 强制 AI"));

    settingsButton_ = CreateWindowExW(0, L"BUTTON", L"AI 设置",
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                      kWindowWidth - 118, 16, 98, 38, hwnd_,
                                      reinterpret_cast<HMENU>(kSettingsButtonId), instance_, nullptr);
    if (!settingsButton_) return false;
    SendMessageW(settingsButton_, WM_SETFONT,
                 reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);

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
            if (self->l3_.Busy()) {
                gL3Generation.fetch_add(1, std::memory_order_relaxed);
                self->l3_.Stop();
                self->streamingText_.clear();
                self->SetStatus(L"已取消 L3 请求", L"再次按 Esc 可隐藏搜索框");
            } else {
                ShowWindow(self->hwnd_, SW_HIDE);
            }
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
    case kL3DeltaMessage: {
        std::unique_ptr<L3UiMessage> delta(reinterpret_cast<L3UiMessage*>(lParam));
        if (!delta || delta->generation != gL3Generation.load(std::memory_order_relaxed)) return 0;
        streamingText_ += delta->text;
        results_.clear();
        results_.push_back({ResultKind::Answer, streamingText_.empty() ? L"…" : streamingText_, L"L3 · WinHTTP streaming", L"", 0});
        selected_ = -1;
        InvalidateRect(hwnd_, nullptr, FALSE);
        return 0;
    }
    case kL3DoneMessage: {
        std::unique_ptr<L3UiMessage> done(reinterpret_cast<L3UiMessage*>(lParam));
        if (!done || done->generation != gL3Generation.load(std::memory_order_relaxed)) return 0;
        const bool failed = !done->text.empty();
        if (failed) {
            if (streamingText_.empty()) streamingText_ = done->text;
            else streamingText_ += L"\n" + done->text;
        }
        if (!streamingText_.empty()) {
            results_.clear();
            results_.push_back({ResultKind::Answer,
                                streamingText_,
                                failed ? L"L3 · 失败 · 输入 /retry 重试" : L"L3 · 完成",
                                L"", 0});
        }
        InvalidateRect(hwnd_, nullptr, FALSE);
        return 0;
    }
    case WM_SIZE: {
        const int width = static_cast<int>(LOWORD(lParam));
        ResizeRenderTarget(LOWORD(lParam), HIWORD(lParam));
        if (edit_) MoveWindow(edit_, 20, 16, std::max(100, width - 150), 38, TRUE);
        if (settingsButton_) MoveWindow(settingsButton_, std::max(120, width - 118), 16, 98, 38, TRUE);
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
        gL3Generation.fetch_add(1, std::memory_order_relaxed);
        l3_.Stop();
        UnregisterHotKey(hwnd_, kHotkeyId);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd_, message, wParam, lParam);
}

void SearchWindow::OnQueryChanged() {
    const int len = GetWindowTextLengthW(edit_);
    std::wstring query(static_cast<std::size_t>(len) + 1, L'\0');
    if (len > 0) GetWindowTextW(edit_, query.data(), len + 1);
    query.resize(static_cast<std::size_t>(len));

    if (l3_.Busy()) {
        gL3Generation.fetch_add(1, std::memory_order_relaxed);
        l3_.Stop();
    }

    currentQuery_ = query;
    streamingText_.clear();
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
        results_.push_back({ResultKind::Status, L"L3 命令", L"Enter 执行；/help 查看命令", L"", 0});
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
                            L"L2 文件搜索未连接",
                            L"未检测到 Everything。启动 Everything 后，文件和文件夹结果会自动出现。",
                            L"", -1000});
    } else if (fileSearchQueryFailed_) {
        results_.push_back({ResultKind::Status,
                            L"L2 文件查询失败",
                            L"Everything 已检测到，但 IPC 查询没有成功。",
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
    const int len = GetWindowTextLengthW(edit_);
    std::wstring query(static_cast<std::size_t>(len) + 1, L'\0');
    if (len > 0) GetWindowTextW(edit_, query.data(), len + 1);
    query.resize(static_cast<std::size_t>(len));
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
    const auto generation = gL3Generation.fetch_add(1, std::memory_order_relaxed) + 1;
    if (l3_.Busy()) l3_.Stop();

    std::wstring actualPrompt = prompt;
    if (prompt == L"/retry") {
        if (lastL3Prompt_.empty()) {
            SetStatus(L"没有可重试的 L3 请求", L"先提交一次模型请求后才能使用 /retry。");
            return;
        }
        actualPrompt = lastL3Prompt_;
    }

    std::wstring localReply;
    bool consumedSecret = false;
    if (l3_.TryHandleLocal(actualPrompt, localReply, consumedSecret)) {
        if (consumedSecret) SetWindowTextW(edit_, L"");
        SetStatus(localReply, L"L3 · Native Tool Router");
        return;
    }

    if (!l3_.HasApiKey()) {
        SetStatus(L"L3 模型尚未配置",
                  L"点击右上角“AI 设置”填写 Base URL、Model 和 API Key；本地 L3 命令仍可使用。");
        return;
    }

    lastL3Prompt_ = actualPrompt;
    streamingText_.clear();
    SetStatus(prompt == L"/retry" ? L"正在重试模型请求…" : L"正在连接模型…",
              l3_.Config().model + L" · WinHTTP · 不经过 Harness");
    l3_.AskAsync(actualPrompt,
        [hwnd = hwnd_, generation](std::wstring delta) {
            PostL3Message(hwnd, kL3DeltaMessage, generation, std::move(delta));
        },
        [hwnd = hwnd_, generation](std::wstring done) {
            PostL3Message(hwnd, kL3DoneMessage, generation, std::move(done));
        });
}

void SearchWindow::OpenModelSettings() {
    if (l3_.Busy()) {
        gL3Generation.fetch_add(1, std::memory_order_relaxed);
        l3_.Stop();
    }

    const bool saved = ShowModelSettingsWindow(instance_, hwnd_, l3_);
    if (saved) {
        SetStatus(L"L3 模型配置已保存",
                  l3_.Config().model + L" · " + l3_.Config().baseUrl +
                  (l3_.HasApiKey() ? L" · API Key 已配置" : L" · API Key 未配置"));
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
            hint += fileSearchAvailable_ ? L"已连接" : L"需要 Everything";
            hint += L" · L3 ";
            hint += l3_.HasApiKey() ? l3_.Config().model : L"未配置（右上角 AI 设置）";
        } else {
            hint = L"没有本地结果 · 按 Enter 交给 L3，Ctrl+Enter 可随时强制 L3";
        }
        renderTarget_->DrawText(hint.c_str(), static_cast<UINT32>(hint.size()), subtitleFormat_.Get(),
                                D2D1::RectF(24, y, width - 24, y + 36), secondaryBrush_.Get());
    }

    for (std::size_t i = 0; i < results_.size(); ++i) {
        const auto& result = results_[i];
        const float rowHeight = (result.kind == ResultKind::Answer || result.kind == ResultKind::Status) ? 86.0f : 50.0f;
        if (static_cast<int>(i) == selected_) {
            renderTarget_->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(14, y - 4, width - 14, y + rowHeight - 4), 8, 8), selectionBrush_.Get());
        }
        std::wstring title = result.title;
        if (title.size() > 280) title.resize(280);
        renderTarget_->DrawText(title.c_str(), static_cast<UINT32>(title.size()), titleFormat_.Get(),
                                D2D1::RectF(24, y, width - 24, y + rowHeight - 26), textBrush_.Get());
        const std::wstring subtitle = std::wstring(KindLabel(result.kind)) + (result.subtitle.empty() ? L"" : L" · " + result.subtitle);
        renderTarget_->DrawText(subtitle.c_str(), static_cast<UINT32>(subtitle.size()), subtitleFormat_.Get(),
                                D2D1::RectF(24, y + rowHeight - 28, width - 24, y + rowHeight), secondaryBrush_.Get());
        y += rowHeight;
        if (y > renderTarget_->GetSize().height - 20) break;
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

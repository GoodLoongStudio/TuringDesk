#include "turingdesk/WallpaperApplicationRulesWindow.h"
#include "turingdesk/WallpaperApplicationRules.h"

#include <commdlg.h>

#include <algorithm>
#include <cwchar>
#include <filesystem>
#include <string>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

constexpr wchar_t kWindowClass[] = L"TuringDesk.Native.WallpaperApplicationRules";
constexpr UINT_PTR kRefreshTimer = 1;
constexpr int kListId = 7001;
constexpr int kExeId = 7002;
constexpr int kBrowseId = 7003;
constexpr int kCaptureId = 7004;
constexpr int kTriggerId = 7005;
constexpr int kActionId = 7006;
constexpr int kEnabledId = 7007;
constexpr int kPriorityId = 7008;
constexpr int kSaveId = 7009;
constexpr int kNewId = 7010;
constexpr int kDeleteId = 7011;
constexpr int kCloseId = 7012;
constexpr int kStatusId = 7013;

HMENU ControlId(int id) {
    return reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
}

std::wstring WindowText(HWND hwnd) {
    if (!hwnd) return {};
    const int length = GetWindowTextLengthW(hwnd);
    if (length <= 0) return {};
    std::wstring value(static_cast<std::size_t>(length) + 1, L'\0');
    GetWindowTextW(hwnd, value.data(), length + 1);
    value.resize(static_cast<std::size_t>(length));
    return value;
}

std::wstring ExecutableForWindow(HWND hwnd) {
    if (!hwnd) return {};
    DWORD processId = 0;
    GetWindowThreadProcessId(hwnd, &processId);
    if (processId == 0 || processId == GetCurrentProcessId()) return {};
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (!process) return {};
    std::vector<wchar_t> buffer(32768);
    DWORD size = static_cast<DWORD>(buffer.size());
    std::wstring result;
    if (QueryFullProcessImageNameW(process, 0, buffer.data(), &size) && size > 0)
        result.assign(buffer.data(), size);
    CloseHandle(process);
    return result;
}

std::wstring DisplayNameForPath(const std::wstring& path) {
    if (path.empty()) return {};
    return fs::path(path).stem().wstring();
}

} // namespace

struct WallpaperApplicationRulesWindow::Impl {
    HINSTANCE instance{};
    HWND window{};
    HWND list{};
    HWND exeEdit{};
    HWND triggerCombo{};
    HWND actionCombo{};
    HWND enabledCheck{};
    HWND priorityEdit{};
    HWND status{};
    HWND lastExternalForeground{};
    WallpaperApplicationRules rules;
    std::vector<std::wstring> visibleRuleIds;
    std::wstring selectedRuleId;

    ~Impl() {
        if (window && IsWindow(window)) DestroyWindow(window);
    }

    void SetStatus(const std::wstring& text) const {
        if (status) SetWindowTextW(status, text.c_str());
    }

    int SelectedIndex(HWND combo) const {
        if (!combo) return -1;
        const LRESULT selected = SendMessageW(combo, CB_GETCURSEL, 0, 0);
        return selected == CB_ERR ? -1 : static_cast<int>(selected);
    }

    void RebuildList() {
        if (!list) return;
        const std::wstring previous = selectedRuleId;
        SendMessageW(list, LB_RESETCONTENT, 0, 0);
        visibleRuleIds.clear();
        int restore = -1;
        for (const auto& rule : rules.Items()) {
            std::wstring label = rule.enabled ? L"● " : L"○ ";
            label += rule.displayName.empty() ? rule.executable : rule.displayName;
            label += L" · ";
            label += WallpaperApplicationRules::TriggerDisplayName(rule.trigger);
            label += L" → ";
            label += PerformanceActionDisplayName(rule.action);
            label += L" · P" + std::to_wstring(rule.priority);
            SendMessageW(list, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
            if (!previous.empty() && _wcsicmp(previous.c_str(), rule.id.c_str()) == 0)
                restore = static_cast<int>(visibleRuleIds.size());
            visibleRuleIds.push_back(rule.id);
        }
        if (restore >= 0) SendMessageW(list, LB_SETCURSEL, restore, 0);
        else if (!visibleRuleIds.empty()) SendMessageW(list, LB_SETCURSEL, 0, 0);
        LoadSelected();
    }

    void LoadSelected() {
        if (!list) return;
        const LRESULT selected = SendMessageW(list, LB_GETCURSEL, 0, 0);
        if (selected == LB_ERR || selected < 0 || static_cast<std::size_t>(selected) >= visibleRuleIds.size()) {
            selectedRuleId.clear();
            SetWindowTextW(exeEdit, L"");
            SendMessageW(enabledCheck, BM_SETCHECK, BST_CHECKED, 0);
            SendMessageW(triggerCombo, CB_SETCURSEL, 0, 0);
            SendMessageW(actionCombo, CB_SETCURSEL, 2, 0);
            SetWindowTextW(priorityEdit, L"100");
            return;
        }
        selectedRuleId = visibleRuleIds[static_cast<std::size_t>(selected)];
        const auto rule = rules.Find(selectedRuleId);
        if (!rule) return;
        SetWindowTextW(exeEdit, rule->executable.c_str());
        SendMessageW(enabledCheck, BM_SETCHECK, rule->enabled ? BST_CHECKED : BST_UNCHECKED, 0);
        SendMessageW(triggerCombo, CB_SETCURSEL,
                     rule->trigger == ApplicationRuleTrigger::Running ? 1 :
                     rule->trigger == ApplicationRuleTrigger::Fullscreen ? 2 :
                     rule->trigger == ApplicationRuleTrigger::Maximized ? 3 : 0, 0);
        SendMessageW(actionCombo, CB_SETCURSEL, static_cast<int>(rule->action), 0);
        SetWindowTextW(priorityEdit, std::to_wstring(rule->priority).c_str());
    }

    void NewRule() {
        selectedRuleId.clear();
        SendMessageW(list, LB_SETCURSEL, static_cast<WPARAM>(-1), 0);
        SetWindowTextW(exeEdit, L"");
        SendMessageW(enabledCheck, BM_SETCHECK, BST_CHECKED, 0);
        SendMessageW(triggerCombo, CB_SETCURSEL, 0, 0);
        SendMessageW(actionCombo, CB_SETCURSEL, 2, 0);
        SetWindowTextW(priorityEdit, L"100");
        SetFocus(exeEdit);
        SetStatus(L"新规则：填写 EXE，或使用“选择 EXE / 抓取最近前台”。");
    }

    void BrowseExe() {
        wchar_t path[32768]{};
        OPENFILENAMEW dialog{};
        dialog.lStructSize = sizeof(dialog);
        dialog.hwndOwner = window;
        dialog.lpstrFilter = L"Windows 应用程序 (*.exe)\0*.exe\0所有文件 (*.*)\0*.*\0\0";
        dialog.lpstrFile = path;
        dialog.nMaxFile = static_cast<DWORD>(std::size(path));
        dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
        if (!GetOpenFileNameW(&dialog)) return;
        SetWindowTextW(exeEdit, WallpaperApplicationRules::NormalizeExecutable(path).c_str());
        SetStatus(L"已选择：" + std::wstring(path));
    }

    void CaptureRecentForeground() {
        std::wstring path = ExecutableForWindow(lastExternalForeground);
        if (path.empty()) {
            SetStatus(L"暂时没有可抓取的外部前台应用。先切到目标应用，再切回这里。 ");
            return;
        }
        SetWindowTextW(exeEdit, WallpaperApplicationRules::NormalizeExecutable(path).c_str());
        SetStatus(L"已抓取最近前台：" + path);
    }

    void SaveRule() {
        WallpaperApplicationRule rule;
        if (!selectedRuleId.empty()) {
            if (const auto existing = rules.Find(selectedRuleId)) rule = *existing;
        }
        if (rule.id.empty()) rule.id = WallpaperApplicationRules::MakeId();
        rule.executable = WallpaperApplicationRules::NormalizeExecutable(WindowText(exeEdit));
        if (rule.executable.empty()) {
            SetStatus(L"请填写或选择一个 EXE。 ");
            return;
        }
        rule.displayName = DisplayNameForPath(rule.executable);
        rule.enabled = SendMessageW(enabledCheck, BM_GETCHECK, 0, 0) == BST_CHECKED;
        const int trigger = SelectedIndex(triggerCombo);
        rule.trigger = trigger == 1 ? ApplicationRuleTrigger::Running :
                       trigger == 2 ? ApplicationRuleTrigger::Fullscreen :
                       trigger == 3 ? ApplicationRuleTrigger::Maximized : ApplicationRuleTrigger::Foreground;
        const int action = std::clamp(SelectedIndex(actionCombo), 0, 3);
        rule.action = static_cast<PerformanceAction>(action);
        const std::wstring priorityText = WindowText(priorityEdit);
        wchar_t* end = nullptr;
        long priority = std::wcstol(priorityText.c_str(), &end, 10);
        if (end == priorityText.c_str()) priority = 100;
        rule.priority = std::clamp(static_cast<int>(priority), 0, 10000);

        std::wstring error;
        if (!rules.Upsert(rule, &error)) {
            SetStatus(error.empty() ? L"规则保存失败。" : error);
            return;
        }
        selectedRuleId = rule.id;
        RebuildList();
        SetStatus(L"规则已保存，最迟 1 秒内生效：" + rule.executable);
    }

    void DeleteRule() {
        if (selectedRuleId.empty()) return;
        std::wstring error;
        if (!rules.Remove(selectedRuleId, &error)) {
            SetStatus(error.empty() ? L"删除规则失败。" : error);
            return;
        }
        selectedRuleId.clear();
        RebuildList();
        SetStatus(L"规则已删除。 ");
    }

    void RefreshDiagnostics() {
        const HWND foreground = GetForegroundWindow();
        if (foreground && foreground != window && GetAncestor(foreground, GA_ROOT) != GetAncestor(window, GA_ROOT))
            lastExternalForeground = foreground;
        const auto match = rules.Evaluate(nullptr, window);
        if (match.matched) {
            std::wstring text = L"当前命中：";
            text += match.displayName.empty() ? match.executable : match.displayName;
            text += L" · ";
            text += WallpaperApplicationRules::TriggerDisplayName(match.trigger);
            text += L" → ";
            text += PerformanceActionDisplayName(match.action);
            if (!match.windowTitle.empty()) text += L" · " + match.windowTitle;
            SetStatus(text);
        }
    }

    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<Impl*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<Impl*>(create->lpCreateParams);
            if (self) {
                self->window = hwnd;
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
            }
        }
        if (!self) return DefWindowProcW(hwnd, message, wParam, lParam);

        if (message == WM_COMMAND) {
            const int id = LOWORD(wParam);
            const int notification = HIWORD(wParam);
            if (id == kListId && notification == LBN_SELCHANGE) self->LoadSelected();
            else if (id == kBrowseId && notification == BN_CLICKED) self->BrowseExe();
            else if (id == kCaptureId && notification == BN_CLICKED) self->CaptureRecentForeground();
            else if (id == kSaveId && notification == BN_CLICKED) self->SaveRule();
            else if (id == kNewId && notification == BN_CLICKED) self->NewRule();
            else if (id == kDeleteId && notification == BN_CLICKED) self->DeleteRule();
            else if (id == kCloseId && notification == BN_CLICKED) ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        if (message == WM_TIMER && wParam == kRefreshTimer) {
            self->RefreshDiagnostics();
            return 0;
        }
        if (message == WM_CLOSE) {
            ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        if (message == WM_DESTROY) {
            KillTimer(hwnd, kRefreshTimer);
            self->window = nullptr;
            self->list = nullptr;
            self->exeEdit = nullptr;
            self->triggerCombo = nullptr;
            self->actionCombo = nullptr;
            self->enabledCheck = nullptr;
            self->priorityEdit = nullptr;
            self->status = nullptr;
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    bool CreateUi() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance;
        wc.lpfnWndProc = &Impl::WndProc;
        wc.lpszClassName = kWindowClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        window = CreateWindowExW(WS_EX_TOOLWINDOW, kWindowClass, L"TuringDesk 应用程序壁纸规则",
                                 WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                                 CW_USEDEFAULT, CW_USEDEFAULT, 820, 610,
                                 nullptr, nullptr, instance, this);
        if (!window) return false;

        const HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto setFont = [&](HWND control) {
            if (control) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };
        auto label = [&](const wchar_t* text, int x, int y, int w, int h) {
            return setFont(CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                            x, y, w, h, window, nullptr, instance, nullptr));
        };
        auto button = [&](const wchar_t* text, int id, int x, int y, int w, int h) {
            return setFont(CreateWindowExW(0, L"BUTTON", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                            x, y, w, h, window, ControlId(id), instance, nullptr));
        };

        label(L"应用程序规则", 20, 18, 220, 26);
        label(L"显式规则会覆盖通用“全屏/最大化”默认行为；锁屏、远程桌面、节能等系统保护仍优先。", 20, 48, 760, 36);

        list = setFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | LBS_NOTIFY | LBS_NOINTEGRALHEIGHT,
            20, 92, 760, 190, window, ControlId(kListId), instance, nullptr));

        label(L"EXE", 20, 310, 50, 22);
        exeEdit = setFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            76, 306, 360, 28, window, ControlId(kExeId), instance, nullptr));
        button(L"选择 EXE…", kBrowseId, 446, 304, 100, 32);
        button(L"抓取最近前台", kCaptureId, 556, 304, 120, 32);

        label(L"触发", 20, 354, 50, 22);
        triggerCombo = setFont(CreateWindowExW(0, L"COMBOBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
            76, 350, 190, 150, window, ControlId(kTriggerId), instance, nullptr));
        for (const wchar_t* text : {L"该应用在前台时", L"应用运行时", L"该应用全屏时", L"该应用最大化时"})
            SendMessageW(triggerCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(text));
        SendMessageW(triggerCombo, CB_SETCURSEL, 0, 0);

        label(L"动作", 286, 354, 50, 22);
        actionCombo = setFont(CreateWindowExW(0, L"COMBOBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
            342, 350, 150, 140, window, ControlId(kActionId), instance, nullptr));
        for (const wchar_t* text : {L"继续运行", L"降频", L"暂停", L"停止/隐藏"})
            SendMessageW(actionCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(text));
        SendMessageW(actionCombo, CB_SETCURSEL, 2, 0);

        enabledCheck = setFont(CreateWindowExW(0, L"BUTTON", L"启用", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                                                512, 350, 82, 26, window, ControlId(kEnabledId), instance, nullptr));
        SendMessageW(enabledCheck, BM_SETCHECK, BST_CHECKED, 0);
        label(L"优先级", 610, 354, 58, 22);
        priorityEdit = setFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"100",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_NUMBER | ES_AUTOHSCROLL,
            672, 350, 76, 28, window, ControlId(kPriorityId), instance, nullptr));

        button(L"新建", kNewId, 20, 404, 90, 32);
        button(L"保存规则", kSaveId, 120, 404, 100, 32);
        button(L"删除", kDeleteId, 230, 404, 90, 32);

        status = setFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | WS_VSCROLL,
            20, 458, 760, 72, window, ControlId(kStatusId), instance, nullptr));
        button(L"关闭", kCloseId, 690, 540, 90, 32);

        SetTimer(window, kRefreshTimer, 1000, nullptr);
        return true;
    }
};

WallpaperApplicationRulesWindow::WallpaperApplicationRulesWindow() : impl_(std::make_unique<Impl>()) {}
WallpaperApplicationRulesWindow::~WallpaperApplicationRulesWindow() = default;

bool WallpaperApplicationRulesWindow::Show(HINSTANCE instance) {
    if (!impl_) return false;
    impl_->instance = instance;
    std::wstring error;
    if (!impl_->rules.Load(&error)) return false;
    if (!impl_->window || !IsWindow(impl_->window)) {
        if (!impl_->CreateUi()) return false;
    }
    impl_->RebuildList();
    if (!error.empty()) impl_->SetStatus(error);
    ShowWindow(impl_->window, SW_RESTORE);
    SetForegroundWindow(impl_->window);
    return true;
}

void WallpaperApplicationRulesWindow::Close() {
    if (impl_ && impl_->window && IsWindow(impl_->window)) DestroyWindow(impl_->window);
}

void WallpaperApplicationRulesWindow::Refresh() {
    if (!impl_) return;
    std::wstring error;
    impl_->rules.Load(&error);
    if (impl_->window && IsWindow(impl_->window)) impl_->RebuildList();
    if (!error.empty()) impl_->SetStatus(error);
}

bool WallpaperApplicationRulesWindow::Visible() const noexcept {
    return impl_ && impl_->window && IsWindow(impl_->window) && IsWindowVisible(impl_->window);
}

HWND WallpaperApplicationRulesWindow::Window() const noexcept {
    return impl_ ? impl_->window : nullptr;
}

} // namespace turingdesk::wallpaper

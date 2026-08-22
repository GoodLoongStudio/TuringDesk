#include "turingdesk/ModelSettingsWindow.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <algorithm>
#include <cwctype>
#include <filesystem>
#include <iterator>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr wchar_t kSettingsClass[] = L"TuringDesk.Native.ModelSettingsWindow";
constexpr wchar_t kSavedKeyMask[] = L"********";
constexpr int kApiUrlEditId = 2101;
constexpr int kApiKeyEditId = 2102;
constexpr int kModelComboId = 2103;
constexpr int kProbeButtonId = 2104;
constexpr int kClearKeyButtonId = 2105;
constexpr int kStatusLabelId = 2106;
constexpr int kHarnessButtonId = 2107;

constexpr DWMWINDOWATTRIBUTE kDwmUseImmersiveDarkMode = static_cast<DWMWINDOWATTRIBUTE>(20);
constexpr DWMWINDOWATTRIBUTE kDwmWindowCornerPreference = static_cast<DWMWINDOWATTRIBUTE>(33);
constexpr DWMWINDOWATTRIBUTE kDwmBorderColor = static_cast<DWMWINDOWATTRIBUTE>(34);
constexpr DWMWINDOWATTRIBUTE kDwmSystemBackdropType = static_cast<DWMWINDOWATTRIBUTE>(38);

constexpr COLORREF kText = RGB(31, 31, 31);
constexpr COLORREF kSecondary = RGB(96, 96, 96);
constexpr COLORREF kSurface = RGB(243, 243, 243);
constexpr COLORREF kInput = RGB(255, 255, 255);
constexpr COLORREF kBorder = RGB(218, 218, 218);
constexpr COLORREF kAccent = RGB(0, 103, 192);
constexpr COLORREF kPressed = RGB(232, 232, 232);

struct SettingsState {
    L3Agent* agent{};
    HWND window{};
    HWND apiUrlEdit{};
    HWND apiKeyEdit{};
    HWND modelCombo{};
    HWND probeButton{};
    HWND statusLabel{};
    bool hadExistingKey{};
    bool hasProbe{};
    bool saved{};
    std::wstring probedApiUrl;
    ModelProbeResult probe;
    HFONT titleFont{};
    HFONT labelFont{};
    HFONT bodyFont{};
    HFONT captionFont{};
    HBRUSH surfaceBrush{};
    HBRUSH inputBrush{};
};

std::wstring ReadText(HWND control) {
    const int length = GetWindowTextLengthW(control);
    std::wstring value(static_cast<std::size_t>(length) + 1, L'\0');
    if (length > 0) GetWindowTextW(control, value.data(), length + 1);
    value.resize(static_cast<std::size_t>(length));
    return value;
}

std::wstring Trim(std::wstring value) {
    const auto notSpace = [](wchar_t ch) { return !std::iswspace(ch); };
    value.erase(value.begin(), std::find_if(value.begin(), value.end(), notSpace));
    value.erase(std::find_if(value.rbegin(), value.rend(), notSpace).base(), value.end());
    return value;
}

void SetControlFont(HWND control, HFONT font) {
    if (control && font) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
}

void RoundControl(HWND control, int radius = 10) {
    if (!control) return;
    RECT rc{};
    GetClientRect(control, &rc);
    HRGN region = CreateRoundRectRgn(0, 0, rc.right + 1, rc.bottom + 1, radius, radius);
    if (region && !SetWindowRgn(control, region, TRUE)) DeleteObject(region);
}

void SetStatus(SettingsState& state, const std::wstring& text) {
    SetWindowTextW(state.statusLabel, text.c_str());
    InvalidateRect(state.statusLabel, nullptr, TRUE);
    UpdateWindow(state.statusLabel);
}

void RefreshKeyField(SettingsState& state) {
    state.hadExistingKey = state.agent->HasStoredApiKey();
    SetWindowTextW(state.apiKeyEdit, state.hadExistingKey ? kSavedKeyMask : L"");
}

bool UsingStoredKey(const SettingsState& state, const std::wstring& fieldText) {
    return state.hadExistingKey && (fieldText.empty() || fieldText == kSavedKeyMask);
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

void DrawButton(const DRAWITEMSTRUCT& item, HFONT font, bool accent) {
    const bool pressed = (item.itemState & ODS_SELECTED) != 0;
    COLORREF fillColor = accent ? kAccent : RGB(250, 250, 250);
    if (pressed) fillColor = accent ? RGB(0, 82, 153) : kPressed;
    const COLORREF textColor = accent ? RGB(255, 255, 255) : kText;

    HBRUSH fill = CreateSolidBrush(fillColor);
    HPEN pen = CreatePen(PS_SOLID, 1, accent ? kAccent : RGB(210, 210, 210));
    HGDIOBJ oldBrush = SelectObject(item.hDC, fill);
    HGDIOBJ oldPen = SelectObject(item.hDC, pen);
    RoundRect(item.hDC, item.rcItem.left, item.rcItem.top, item.rcItem.right, item.rcItem.bottom, 10, 10);
    SelectObject(item.hDC, oldBrush);
    SelectObject(item.hDC, oldPen);
    DeleteObject(fill);
    DeleteObject(pen);

    wchar_t text[160]{};
    GetWindowTextW(item.hwndItem, text, static_cast<int>(std::size(text)));
    SetBkMode(item.hDC, TRANSPARENT);
    SetTextColor(item.hDC, textColor);
    HFONT oldFont = reinterpret_cast<HFONT>(SelectObject(item.hDC, font));
    RECT rc = item.rcItem;
    DrawTextW(item.hDC, text, -1, &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
    SelectObject(item.hDC, oldFont);
}

bool OpenHarness(SettingsState& state) {
    wchar_t modulePath[32768]{};
    const DWORD length = GetModuleFileNameW(nullptr, modulePath, static_cast<DWORD>(std::size(modulePath)));
    if (length == 0 || length >= std::size(modulePath)) {
        MessageBoxW(state.window, L"无法定位 TuringDesk 安装目录。", L"DeepSeek Harness", MB_OK | MB_ICONERROR);
        return false;
    }
    const fs::path executable = fs::path(std::wstring(modulePath, length)).parent_path() / L"TuringDeskHarness.exe";
    std::error_code ec;
    if (!fs::is_regular_file(executable, ec)) {
        MessageBoxW(state.window, L"当前安装包缺少 TuringDeskHarness.exe。请部署最新版本。",
                    L"DeepSeek Harness", MB_OK | MB_ICONERROR);
        return false;
    }
    const auto launchResult = reinterpret_cast<INT_PTR>(ShellExecuteW(state.window, L"open", executable.c_str(), nullptr,
                                                                      executable.parent_path().c_str(), SW_SHOWNORMAL));
    if (launchResult <= 32) {
        MessageBoxW(state.window, (L"无法启动 DeepSeek Harness（" + std::to_wstring(launchResult) + L"）。").c_str(),
                    L"DeepSeek Harness", MB_OK | MB_ICONERROR);
        return false;
    }
    SetStatus(state, L"DeepSeek Harness 已在独立窗口启动。");
    return true;
}

void PopulateModels(SettingsState& state, const ModelProbeResult& probe) {
    const auto previous = Trim(ReadText(state.modelCombo));
    SendMessageW(state.modelCombo, CB_RESETCONTENT, 0, 0);
    if (probe.models.empty()) {
        const auto fallback = !previous.empty() ? previous : state.agent->Config().model;
        SetWindowTextW(state.modelCombo, fallback.c_str());
        return;
    }
    for (const auto& model : probe.models)
        SendMessageW(state.modelCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(model.c_str()));
    std::wstring selected = probe.recommendedModel;
    if (!previous.empty() && std::find(probe.models.begin(), probe.models.end(), previous) != probe.models.end()) selected = previous;
    if (selected.empty()) selected = probe.models.front();
    const auto index = SendMessageW(state.modelCombo, CB_FINDSTRINGEXACT, static_cast<WPARAM>(-1), reinterpret_cast<LPARAM>(selected.c_str()));
    if (index != CB_ERR) SendMessageW(state.modelCombo, CB_SETCURSEL, static_cast<WPARAM>(index), 0);
    else SetWindowTextW(state.modelCombo, selected.c_str());
}

ModelProbeResult RunProbe(SettingsState& state) {
    const auto apiUrl = Trim(ReadText(state.apiUrlEdit));
    const auto keyField = Trim(ReadText(state.apiKeyEdit));
    const bool useStored = UsingStoredKey(state, keyField);
    const std::wstring keyOverride = useStored ? L"" : keyField;
    SetStatus(state, L"正在识别 API 协议并读取模型列表…");
    EnableWindow(state.probeButton, FALSE);
    HCURSOR oldCursor = SetCursor(LoadCursorW(nullptr, IDC_WAIT));
    auto probe = state.agent->ProbeModels(apiUrl, keyOverride, useStored);
    SetCursor(oldCursor);
    EnableWindow(state.probeButton, TRUE);
    state.probe = probe;
    state.hasProbe = !probe.baseUrl.empty() && !probe.endpoint.empty();
    state.probedApiUrl = apiUrl;
    PopulateModels(state, probe);
    if (probe.ok) {
        SetStatus(state, L"✓ " + probe.protocolLabel + L" · 找到 " + std::to_wstring(probe.models.size()) + L" 个模型");
    } else if (!probe.protocolLabel.empty()) {
        SetStatus(state, L"⚠ " + probe.protocolLabel + L" · " + probe.message);
    } else {
        SetStatus(state, L"无法连接 · " + probe.message);
    }
    return probe;
}

bool SaveSettings(SettingsState& state) {
    const auto apiUrl = Trim(ReadText(state.apiUrlEdit));
    auto model = Trim(ReadText(state.modelCombo));
    const auto keyField = Trim(ReadText(state.apiKeyEdit));
    if (apiUrl.empty()) {
        MessageBoxW(state.window, L"请填写 API 地址。", L"TuringDesk AI 设置", MB_OK | MB_ICONWARNING);
        return false;
    }
    if (!state.hasProbe || Trim(state.probedApiUrl) != apiUrl) {
        RunProbe(state);
        model = Trim(ReadText(state.modelCombo));
    }
    if (!state.hasProbe) {
        MessageBoxW(state.window, L"无法识别这个 API 地址，请检查后重试。", L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
        return false;
    }
    if (state.probe.statusCode == 401 || state.probe.statusCode == 403) {
        MessageBoxW(state.window, state.probe.message.c_str(), L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
        return false;
    }
    if (model.empty()) {
        MessageBoxW(state.window, L"没有获取到模型。可以在模型框中手动输入模型 ID。",
                    L"TuringDesk AI 设置", MB_OK | MB_ICONWARNING);
        return false;
    }
    const bool preserveExistingKey = UsingStoredKey(state, keyField);
    const std::wstring keyOverride = preserveExistingKey ? L"" : keyField;
    std::wstring reply;
    if (!state.agent->ApplyModelConfig(state.probe, model, keyOverride, preserveExistingKey, reply)) {
        MessageBoxW(state.window, reply.empty() ? L"保存失败。" : reply.c_str(), L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
        return false;
    }
    state.saved = true;
    return true;
}

LRESULT CALLBACK SettingsProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    auto* state = reinterpret_cast<SettingsState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
        state = static_cast<SettingsState*>(create->lpCreateParams);
        state->window = hwnd;
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
    }
    if (!state) return DefWindowProcW(hwnd, message, wParam, lParam);

    switch (message) {
    case WM_COMMAND:
        if (LOWORD(wParam) == kApiKeyEditId && HIWORD(wParam) == EN_SETFOCUS && state->hadExistingKey &&
            ReadText(state->apiKeyEdit) == kSavedKeyMask) {
            SendMessageW(state->apiKeyEdit, EM_SETSEL, 0, -1);
            return 0;
        }
        switch (LOWORD(wParam)) {
        case kProbeButtonId:
            if (HIWORD(wParam) == BN_CLICKED) RunProbe(*state);
            return 0;
        case kHarnessButtonId:
            if (HIWORD(wParam) == BN_CLICKED) OpenHarness(*state);
            return 0;
        case IDOK:
            if (SaveSettings(*state)) DestroyWindow(hwnd);
            return 0;
        case IDCANCEL:
            DestroyWindow(hwnd); return 0;
        case kClearKeyButtonId: {
            std::wstring reply;
            bool consumedSecret = false;
            state->agent->TryHandleLocal(L"/clear-key", reply, consumedSecret);
            RefreshKeyField(*state);
            state->hasProbe = false;
            SetStatus(*state, L"API Key 已清除。填写新 Key 后重新检测模型。");
            return 0;
        }
        }
        break;
    case WM_DRAWITEM: {
        const auto* item = reinterpret_cast<DRAWITEMSTRUCT*>(lParam);
        if (!item) break;
        const UINT id = item->CtlID;
        if (id == kProbeButtonId || id == kClearKeyButtonId || id == kHarnessButtonId || id == IDOK || id == IDCANCEL) {
            DrawButton(*item, state->bodyFont, id == IDOK || id == kProbeButtonId);
            return TRUE;
        }
        break;
    }
    case WM_CTLCOLOREDIT: {
        const HDC dc = reinterpret_cast<HDC>(wParam);
        SetTextColor(dc, kText);
        SetBkColor(dc, kInput);
        return reinterpret_cast<LRESULT>(state->inputBrush);
    }
    case WM_CTLCOLORSTATIC: {
        const HDC dc = reinterpret_cast<HDC>(wParam);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, reinterpret_cast<HWND>(lParam) == state->statusLabel ? kSecondary : kText);
        return reinterpret_cast<LRESULT>(GetStockObject(NULL_BRUSH));
    }
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT: {
        PAINTSTRUCT ps{};
        HDC dc = BeginPaint(hwnd, &ps);
        RECT rc{}; GetClientRect(hwnd, &rc);
        FillRect(dc, &rc, state->surfaceBrush);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_CLOSE:
        DestroyWindow(hwnd); return 0;
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

} // namespace

bool ShowModelSettingsWindow(HINSTANCE instance, HWND owner, L3Agent& agent) {
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.hInstance = instance;
    wc.lpfnWndProc = &SettingsProc;
    wc.lpszClassName = kSettingsClass;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = nullptr;
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

    SettingsState state{};
    state.agent = &agent;
    state.titleFont = CreateFontW(-27, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Display");
    state.labelFont = CreateFontW(-16, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    state.bodyFont = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                 OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                 DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    state.captionFont = CreateFontW(-14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                    DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI Variable Text");
    state.surfaceBrush = CreateSolidBrush(kSurface);
    state.inputBrush = CreateSolidBrush(kInput);

    constexpr int width = 690;
    constexpr int height = 480;
    RECT ownerRect{};
    if (!owner || !GetWindowRect(owner, &ownerRect)) ownerRect = {200, 120, 960, 600};
    const int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    const int y = ownerRect.top + 42;

    HWND window = CreateWindowExW(WS_EX_DLGMODALFRAME | WS_EX_TOOLWINDOW,
                                  kSettingsClass, L"AI 与模型",
                                  WS_CAPTION | WS_SYSMENU | WS_POPUP,
                                  x, y, width, height, owner, nullptr, instance, &state);
    if (!window) return false;
    ApplyWindows11Style(window);

    auto makeStatic = [&](const wchar_t* text, int x0, int y0, int w, int h, HFONT font) {
        HWND control = CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                       x0, y0, w, h, window, nullptr, instance, nullptr);
        SetControlFont(control, font);
        return control;
    };

    makeStatic(L"AI 与模型", 26, 18, 560, 36, state.titleFont);
    makeStatic(L"兼容 OpenAI 风格接口；协议与模型列表可自动识别", 28, 56, 610, 22, state.captionFont);

    makeStatic(L"API 地址", 28, 92, 610, 22, state.labelFont);
    const auto apiUrl = agent.CurrentApiUrl();
    state.apiUrlEdit = CreateWindowExW(0, L"EDIT", apiUrl.c_str(), WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                       28, 118, 624, 36, window,
                                       reinterpret_cast<HMENU>(static_cast<INT_PTR>(kApiUrlEditId)), instance, nullptr);
    SetControlFont(state.apiUrlEdit, state.bodyFont);
    SendMessageW(state.apiUrlEdit, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, MAKELPARAM(12, 12));
    RoundControl(state.apiUrlEdit);

    makeStatic(L"API Key", 28, 170, 180, 22, state.labelFont);
    makeStatic(L"已保存的 Key 只显示为 ********，不会回填明文", 116, 172, 480, 20, state.captionFont);
    state.apiKeyEdit = CreateWindowExW(0, L"EDIT", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL | ES_PASSWORD,
                                       28, 196, 466, 36, window,
                                       reinterpret_cast<HMENU>(static_cast<INT_PTR>(kApiKeyEditId)), instance, nullptr);
    SetControlFont(state.apiKeyEdit, state.bodyFont);
    SendMessageW(state.apiKeyEdit, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, MAKELPARAM(12, 12));
    SendMessageW(state.apiKeyEdit, EM_SETPASSWORDCHAR, static_cast<WPARAM>(L'•'), 0);
    RoundControl(state.apiKeyEdit);
    state.probeButton = CreateWindowExW(0, L"BUTTON", L"检测模型", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                        506, 196, 146, 36, window,
                                        reinterpret_cast<HMENU>(static_cast<INT_PTR>(kProbeButtonId)), instance, nullptr);

    makeStatic(L"模型", 28, 250, 610, 22, state.labelFont);
    state.modelCombo = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL |
                                       CBS_DROPDOWN | CBS_AUTOHSCROLL,
                                       28, 276, 624, 190, window,
                                       reinterpret_cast<HMENU>(static_cast<INT_PTR>(kModelComboId)), instance, nullptr);
    SetControlFont(state.modelCombo, state.bodyFont);
    if (!agent.Config().model.empty()) {
        SendMessageW(state.modelCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(agent.Config().model.c_str()));
        SendMessageW(state.modelCombo, CB_SETCURSEL, 0, 0);
    }

    state.statusLabel = makeStatic(L"填写地址和 Key 后点击“检测模型”。", 28, 320, 620, 42, state.captionFont);

    HWND harnessButton = CreateWindowExW(0, L"BUTTON", L"DeepSeek Harness", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                         28, 380, 160, 36, window,
                                         reinterpret_cast<HMENU>(static_cast<INT_PTR>(kHarnessButtonId)), instance, nullptr);
    HWND clearButton = CreateWindowExW(0, L"BUTTON", L"清除 Key", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                       302, 380, 100, 36, window,
                                       reinterpret_cast<HMENU>(static_cast<INT_PTR>(kClearKeyButtonId)), instance, nullptr);
    HWND cancelButton = CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                        414, 380, 100, 36, window,
                                        reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDCANCEL)), instance, nullptr);
    HWND saveButton = CreateWindowExW(0, L"BUTTON", L"保存", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                      526, 380, 126, 36, window,
                                      reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDOK)), instance, nullptr);

    for (HWND button : {state.probeButton, harnessButton, clearButton, cancelButton, saveButton}) {
        SetControlFont(button, state.bodyFont);
        RoundControl(button);
    }

    RefreshKeyField(state);
    if (!agent.Config().providerId.empty() && agent.Config().providerId != L"unconfigured" && !agent.Config().model.empty())
        SetStatus(state, L"当前：" + agent.Config().providerId + L" · " + agent.Config().model);

    EnableWindow(owner, FALSE);
    ShowWindow(window, SW_SHOWNORMAL);
    SetForegroundWindow(window);
    SetFocus(state.apiUrlEdit);

    MSG msg{};
    bool sawQuit = false;
    while (IsWindow(window)) {
        const BOOL result = GetMessageW(&msg, nullptr, 0, 0);
        if (result <= 0) { if (result == 0) sawQuit = true; break; }
        if (!IsDialogMessageW(window, &msg)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    }

    EnableWindow(owner, TRUE);
    SetForegroundWindow(owner);
    if (state.titleFont) DeleteObject(state.titleFont);
    if (state.labelFont) DeleteObject(state.labelFont);
    if (state.bodyFont) DeleteObject(state.bodyFont);
    if (state.captionFont) DeleteObject(state.captionFont);
    if (state.surfaceBrush) DeleteObject(state.surfaceBrush);
    if (state.inputBrush) DeleteObject(state.inputBrush);
    if (sawQuit) PostQuitMessage(static_cast<int>(msg.wParam));
    return state.saved;
}

} // namespace turingdesk

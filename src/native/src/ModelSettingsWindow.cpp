#include "turingdesk/ModelSettingsWindow.h"
#include <algorithm>
#include <cwctype>
#include <string>

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
    SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
}

void SetStatus(SettingsState& state, const std::wstring& text) {
    SetWindowTextW(state.statusLabel, text.c_str());
    UpdateWindow(state.statusLabel);
}

void RefreshKeyField(SettingsState& state) {
    state.hadExistingKey = state.agent->HasStoredApiKey();
    SetWindowTextW(state.apiKeyEdit, state.hadExistingKey ? kSavedKeyMask : L"");
}

bool UsingStoredKey(const SettingsState& state, const std::wstring& fieldText) {
    return state.hadExistingKey && (fieldText.empty() || fieldText == kSavedKeyMask);
}

void PopulateModels(SettingsState& state, const ModelProbeResult& probe) {
    const auto previous = Trim(ReadText(state.modelCombo));
    SendMessageW(state.modelCombo, CB_RESETCONTENT, 0, 0);

    if (probe.models.empty()) {
        const auto fallback = !previous.empty() ? previous : state.agent->Config().model;
        SetWindowTextW(state.modelCombo, fallback.c_str());
        return;
    }

    for (const auto& model : probe.models) {
        SendMessageW(state.modelCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(model.c_str()));
    }

    std::wstring selected = probe.recommendedModel;
    if (!previous.empty() && std::find(probe.models.begin(), probe.models.end(), previous) != probe.models.end()) {
        selected = previous;
    }
    if (selected.empty()) selected = probe.models.front();

    const auto index = SendMessageW(state.modelCombo, CB_FINDSTRINGEXACT, static_cast<WPARAM>(-1),
                                    reinterpret_cast<LPARAM>(selected.c_str()));
    if (index != CB_ERR) SendMessageW(state.modelCombo, CB_SETCURSEL, static_cast<WPARAM>(index), 0);
    else SetWindowTextW(state.modelCombo, selected.c_str());
}

ModelProbeResult RunProbe(SettingsState& state) {
    const auto apiUrl = Trim(ReadText(state.apiUrlEdit));
    const auto keyField = Trim(ReadText(state.apiKeyEdit));
    const bool useStored = UsingStoredKey(state, keyField);
    const std::wstring keyOverride = useStored ? L"" : keyField;

    SetStatus(state, L"正在自动识别协议并读取模型列表…");
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
        SetStatus(state, L"✓ " + probe.protocolLabel + L" · 已发现 " +
                         std::to_wstring(probe.models.size()) + L" 个模型 · 已自动推荐");
    } else if (!probe.protocolLabel.empty()) {
        SetStatus(state, L"⚠ " + probe.protocolLabel + L" · " + probe.message);
    } else {
        SetStatus(state, L"✕ " + probe.message);
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
        MessageBoxW(state.window, L"无法识别这个 API 地址，请检查地址后重试。",
                    L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
        return false;
    }
    if (state.probe.statusCode == 401 || state.probe.statusCode == 403) {
        MessageBoxW(state.window, state.probe.message.c_str(), L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
        return false;
    }
    if (model.empty()) {
        MessageBoxW(state.window,
                    L"没有自动获取到模型。可以在“模型”框中手动输入模型 ID 后保存。",
                    L"TuringDesk AI 设置", MB_OK | MB_ICONWARNING);
        return false;
    }

    const bool preserveExistingKey = UsingStoredKey(state, keyField);
    const std::wstring keyOverride = preserveExistingKey ? L"" : keyField;
    std::wstring reply;
    if (!state.agent->ApplyModelConfig(state.probe, model, keyOverride, preserveExistingKey, reply)) {
        MessageBoxW(state.window, reply.empty() ? L"保存失败。" : reply.c_str(),
                    L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
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
        case IDOK:
            if (SaveSettings(*state)) DestroyWindow(hwnd);
            return 0;
        case IDCANCEL:
            DestroyWindow(hwnd);
            return 0;
        case kClearKeyButtonId: {
            std::wstring reply;
            bool consumedSecret = false;
            state->agent->TryHandleLocal(L"/clear-key", reply, consumedSecret);
            RefreshKeyField(*state);
            state->hasProbe = false;
            SetStatus(*state, L"API Key 已清除。重新填写 Key 后点击“检测模型”。");
            return 0;
        }
        }
        break;
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
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
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

    SettingsState state{};
    state.agent = &agent;

    constexpr int width = 640;
    constexpr int height = 350;
    RECT ownerRect{};
    GetWindowRect(owner, &ownerRect);
    const int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    const int y = ownerRect.top + 45;

    HWND window = CreateWindowExW(WS_EX_DLGMODALFRAME | WS_EX_TOOLWINDOW,
                                  kSettingsClass, L"TuringDesk AI 设置",
                                  WS_CAPTION | WS_SYSMENU | WS_POPUP,
                                  x, y, width, height, owner, nullptr, instance, &state);
    if (!window) return false;

    HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));

    HWND labelApi = CreateWindowExW(0, L"STATIC", L"API 地址", WS_CHILD | WS_VISIBLE,
                                    24, 18, 572, 20, window, nullptr, instance, nullptr);
    const auto apiUrl = agent.CurrentApiUrl();
    state.apiUrlEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", apiUrl.c_str(),
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                       24, 40, 572, 28, window, reinterpret_cast<HMENU>(kApiUrlEditId), instance, nullptr);

    HWND labelKey = CreateWindowExW(0, L"STATIC", L"API Key（已有 Key 显示为 ********）",
                                    WS_CHILD | WS_VISIBLE,
                                    24, 80, 572, 20, window, nullptr, instance, nullptr);
    state.apiKeyEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL | ES_PASSWORD,
                                       24, 102, 420, 28, window, reinterpret_cast<HMENU>(kApiKeyEditId), instance, nullptr);
    state.probeButton = CreateWindowExW(0, L"BUTTON", L"检测模型",
                                        WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                        456, 101, 140, 30, window, reinterpret_cast<HMENU>(kProbeButtonId), instance, nullptr);

    HWND labelModel = CreateWindowExW(0, L"STATIC", L"模型（自动获取并推荐，可手动切换）",
                                      WS_CHILD | WS_VISIBLE,
                                      24, 144, 572, 20, window, nullptr, instance, nullptr);
    state.modelCombo = CreateWindowExW(WS_EX_CLIENTEDGE, L"COMBOBOX", L"",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL |
                                       CBS_DROPDOWN | CBS_AUTOHSCROLL,
                                       24, 166, 572, 180, window, reinterpret_cast<HMENU>(kModelComboId), instance, nullptr);
    if (!agent.Config().model.empty()) {
        SendMessageW(state.modelCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(agent.Config().model.c_str()));
        SendMessageW(state.modelCombo, CB_SETCURSEL, 0, 0);
    }

    state.statusLabel = CreateWindowExW(0, L"STATIC", L"协议自动识别，无需选择。填写地址和 Key 后点击“检测模型”。",
                                        WS_CHILD | WS_VISIBLE,
                                        24, 208, 572, 42, window, reinterpret_cast<HMENU>(kStatusLabelId), instance, nullptr);

    HWND autoHint = CreateWindowExW(0, L"STATIC", L"检测只读取模型列表，不发送聊天请求。",
                                    WS_CHILD | WS_VISIBLE,
                                    24, 250, 300, 20, window, nullptr, instance, nullptr);

    HWND saveButton = CreateWindowExW(0, L"BUTTON", L"保存", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                      364, 274, 72, 28, window, reinterpret_cast<HMENU>(IDOK), instance, nullptr);
    HWND clearButton = CreateWindowExW(0, L"BUTTON", L"清除 Key", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       444, 274, 72, 28, window, reinterpret_cast<HMENU>(kClearKeyButtonId), instance, nullptr);
    HWND cancelButton = CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                        524, 274, 72, 28, window, reinterpret_cast<HMENU>(IDCANCEL), instance, nullptr);

    for (HWND control : {labelApi, state.apiUrlEdit, labelKey, state.apiKeyEdit, state.probeButton,
                         labelModel, state.modelCombo, state.statusLabel, autoHint,
                         saveButton, clearButton, cancelButton}) {
        SetControlFont(control, font);
    }
    SendMessageW(state.apiKeyEdit, EM_SETPASSWORDCHAR, static_cast<WPARAM>(L'*'), 0);
    RefreshKeyField(state);

    if (!agent.Config().providerId.empty() && agent.Config().providerId != L"unconfigured" && !agent.Config().model.empty()) {
        SetStatus(state, L"已保存：" + agent.Config().providerId + L" · " + agent.Config().model + L"。可点击“检测模型”刷新。");
    }

    EnableWindow(owner, FALSE);
    ShowWindow(window, SW_SHOWNORMAL);
    SetForegroundWindow(window);
    SetFocus(state.apiUrlEdit);

    MSG msg{};
    bool sawQuit = false;
    while (IsWindow(window)) {
        const BOOL result = GetMessageW(&msg, nullptr, 0, 0);
        if (result <= 0) {
            if (result == 0) sawQuit = true;
            break;
        }
        if (!IsDialogMessageW(window, &msg)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    EnableWindow(owner, TRUE);
    SetForegroundWindow(owner);
    if (sawQuit) PostQuitMessage(static_cast<int>(msg.wParam));
    return state.saved;
}

} // namespace turingdesk

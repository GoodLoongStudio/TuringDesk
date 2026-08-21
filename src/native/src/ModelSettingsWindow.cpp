#include "turingdesk/ModelSettingsWindow.h"
#include <algorithm>
#include <cwctype>
#include <string>

namespace turingdesk {
namespace {

constexpr wchar_t kSettingsClass[] = L"TuringDesk.Native.ModelSettingsWindow";
constexpr wchar_t kSavedKeyMask[] = L"********";
constexpr int kApiUrlEditId = 2101;
constexpr int kModelEditId = 2102;
constexpr int kApiKeyEditId = 2103;
constexpr int kClearKeyButtonId = 2104;
constexpr int kStatusLabelId = 2105;

struct SettingsState {
    L3Agent* agent{};
    HWND window{};
    HWND apiUrlEdit{};
    HWND modelEdit{};
    HWND apiKeyEdit{};
    HWND statusLabel{};
    bool hadExistingKey{};
    bool saved{};
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

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

void SetControlFont(HWND control, HFONT font) {
    SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
}

std::wstring CurrentApiUrl(const ModelConfig& config) {
    std::wstring endpoint = Trim(config.endpoint);

    // Repair the old settings UI bug where a complete URL entered in Endpoint
    // was stored as /https://host/... . Do not make the user understand this
    // migration detail; simply present a valid full API URL next time.
    if (endpoint.starts_with(L"/https://") || endpoint.starts_with(L"/http://")) {
        endpoint.erase(endpoint.begin());
        return endpoint;
    }
    if (endpoint.starts_with(L"https://") || endpoint.starts_with(L"http://")) {
        return endpoint;
    }

    std::wstring base = Trim(config.baseUrl);
    while (base.size() > 1 && base.back() == L'/') base.pop_back();
    if (endpoint.empty() || endpoint == L"-") return base;
    if (endpoint.front() != L'/') endpoint.insert(endpoint.begin(), L'/');
    return base + endpoint;
}

bool SplitApiUrl(std::wstring apiUrl, std::wstring& baseUrl, std::wstring& endpoint) {
    apiUrl = Trim(std::move(apiUrl));
    if (!apiUrl.starts_with(L"https://") && !apiUrl.starts_with(L"http://")) return false;
    if (apiUrl.find_first_of(L" \t\r\n") != std::wstring::npos) return false;

    const auto schemeEnd = apiUrl.find(L"://");
    if (schemeEnd == std::wstring::npos) return false;
    const auto pathStart = apiUrl.find(L'/', schemeEnd + 3);

    if (pathStart == std::wstring::npos) {
        baseUrl = apiUrl;
        const auto lower = Lower(baseUrl);
        endpoint = lower.find(L"deepseek") != std::wstring::npos
            ? L"/chat/completions"
            : L"/v1/chat/completions";
        return true;
    }

    baseUrl = apiUrl.substr(0, pathStart);
    endpoint = apiUrl.substr(pathStart);
    if (baseUrl.empty() || endpoint.empty()) return false;
    return true;
}

void RefreshKeyStatus(SettingsState& state) {
    state.hadExistingKey = state.agent->HasApiKey();
    const std::wstring text = state.hadExistingKey
        ? L"API Key：已安全保存到 Windows Credential Manager"
        : L"API Key：未配置";
    SetWindowTextW(state.statusLabel, text.c_str());
    SetWindowTextW(state.apiKeyEdit, state.hadExistingKey ? kSavedKeyMask : L"");
}

bool SaveSettings(SettingsState& state) {
    const auto apiUrl = Trim(ReadText(state.apiUrlEdit));
    const auto model = Trim(ReadText(state.modelEdit));
    const auto apiKey = Trim(ReadText(state.apiKeyEdit));

    std::wstring baseUrl;
    std::wstring endpoint;
    if (!SplitApiUrl(apiUrl, baseUrl, endpoint) || model.empty()) {
        MessageBoxW(state.window,
                    L"请填写完整 API URL 和模型 ID。\n例如：https://api.example.com/v1/chat/completions",
                    L"TuringDesk AI 设置", MB_OK | MB_ICONWARNING);
        return false;
    }

    std::wstring reply;
    bool consumedSecret = false;
    const std::wstring providerCommand = L"/provider " + baseUrl + L" " + model;
    if (!state.agent->TryHandleLocal(providerCommand, reply, consumedSecret) ||
        reply.find(L"失败") != std::wstring::npos || reply.find(L"用法") != std::wstring::npos) {
        MessageBoxW(state.window, reply.empty() ? L"模型配置保存失败。" : reply.c_str(),
                    L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
        return false;
    }

    reply.clear();
    consumedSecret = false;
    if (!state.agent->TryHandleLocal(L"/endpoint " + endpoint, reply, consumedSecret) ||
        reply.find(L"失败") != std::wstring::npos) {
        MessageBoxW(state.window, reply.empty() ? L"API URL 保存失败。" : reply.c_str(),
                    L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
        return false;
    }

    const bool isSavedMask = state.hadExistingKey && apiKey == kSavedKeyMask;
    if (!apiKey.empty() && !isSavedMask) {
        reply.clear();
        consumedSecret = false;
        if (!state.agent->TryHandleLocal(L"/key " + apiKey, reply, consumedSecret) ||
            reply.find(L"失败") != std::wstring::npos) {
            RefreshKeyStatus(state);
            MessageBoxW(state.window, reply.empty() ? L"API Key 保存失败。" : reply.c_str(),
                        L"TuringDesk AI 设置", MB_OK | MB_ICONERROR);
            return false;
        }
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
            RefreshKeyStatus(*state);
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

    constexpr int width = 560;
    constexpr int height = 310;
    RECT ownerRect{};
    GetWindowRect(owner, &ownerRect);
    const int x = ownerRect.left + ((ownerRect.right - ownerRect.left) - width) / 2;
    const int y = ownerRect.top + 60;

    HWND window = CreateWindowExW(WS_EX_DLGMODALFRAME | WS_EX_TOOLWINDOW,
                                  kSettingsClass, L"TuringDesk L3 设置",
                                  WS_CAPTION | WS_SYSMENU | WS_POPUP,
                                  x, y, width, height, owner, nullptr, instance, &state);
    if (!window) return false;

    HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));

    const auto apiUrl = CurrentApiUrl(agent.Config());
    HWND labelApi = CreateWindowExW(0, L"STATIC", L"API URL", WS_CHILD | WS_VISIBLE,
                                    24, 20, 500, 20, window, nullptr, instance, nullptr);
    state.apiUrlEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", apiUrl.c_str(),
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                       24, 42, 500, 28, window, reinterpret_cast<HMENU>(kApiUrlEditId), instance, nullptr);

    HWND labelModel = CreateWindowExW(0, L"STATIC", L"Model", WS_CHILD | WS_VISIBLE,
                                      24, 80, 500, 20, window, nullptr, instance, nullptr);
    state.modelEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", agent.Config().model.c_str(),
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                      24, 102, 500, 28, window, reinterpret_cast<HMENU>(kModelEditId), instance, nullptr);

    HWND labelKey = CreateWindowExW(0, L"STATIC", L"API Key（已有 Key 显示为 ********；输入新值即可覆盖）",
                                    WS_CHILD | WS_VISIBLE,
                                    24, 140, 500, 20, window, nullptr, instance, nullptr);
    state.apiKeyEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL | ES_PASSWORD,
                                       24, 162, 500, 28, window, reinterpret_cast<HMENU>(kApiKeyEditId), instance, nullptr);

    state.statusLabel = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE,
                                        24, 198, 500, 20, window, reinterpret_cast<HMENU>(kStatusLabelId), instance, nullptr);

    HWND saveButton = CreateWindowExW(0, L"BUTTON", L"保存", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                      292, 234, 72, 28, window, reinterpret_cast<HMENU>(IDOK), instance, nullptr);
    HWND clearButton = CreateWindowExW(0, L"BUTTON", L"清除 Key", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       372, 234, 72, 28, window, reinterpret_cast<HMENU>(kClearKeyButtonId), instance, nullptr);
    HWND cancelButton = CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                        452, 234, 72, 28, window, reinterpret_cast<HMENU>(IDCANCEL), instance, nullptr);

    for (HWND control : {labelApi, state.apiUrlEdit, labelModel, state.modelEdit, labelKey,
                         state.apiKeyEdit, state.statusLabel, saveButton, clearButton, cancelButton}) {
        SetControlFont(control, font);
    }
    SendMessageW(state.apiKeyEdit, EM_SETPASSWORDCHAR, static_cast<WPARAM>(L'*'), 0);
    RefreshKeyStatus(state);

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

#include "turingdesk/ModelSettingsWindow.h"
#include <algorithm>
#include <cwctype>
#include <string>

namespace turingdesk {
namespace {

constexpr wchar_t kSettingsClass[] = L"TuringDesk.Native.ModelSettingsWindow";
constexpr wchar_t kSavedKeyMask[] = L"********";
constexpr int kBaseUrlEditId = 2101;
constexpr int kModelEditId = 2102;
constexpr int kApiKeyEditId = 2103;
constexpr int kClearKeyButtonId = 2104;
constexpr int kStatusLabelId = 2105;
constexpr int kEndpointEditId = 2106;

struct SettingsState {
    L3Agent* agent{};
    HWND window{};
    HWND baseUrlEdit{};
    HWND modelEdit{};
    HWND endpointEdit{};
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

void SetControlFont(HWND control, HFONT font) {
    SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
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
    const auto baseUrl = Trim(ReadText(state.baseUrlEdit));
    const auto model = Trim(ReadText(state.modelEdit));
    const auto endpoint = Trim(ReadText(state.endpointEdit));
    const auto apiKey = Trim(ReadText(state.apiKeyEdit));

    if ((!baseUrl.starts_with(L"https://") && !baseUrl.starts_with(L"http://")) || model.empty()) {
        MessageBoxW(state.window,
                    L"请填写有效的 Base URL（http:// 或 https://）和模型 ID。",
                    L"TuringDesk AI 设置", MB_OK | MB_ICONWARNING);
        return false;
    }

    if (!endpoint.empty() && endpoint.find_first_of(L" \t\r\n") != std::wstring::npos) {
        MessageBoxW(state.window, L"Endpoint 不能包含空格。示例：/v1/chat/completions",
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
    const std::wstring endpointCommand = endpoint.empty() ? L"/endpoint -" : L"/endpoint " + endpoint;
    if (!state.agent->TryHandleLocal(endpointCommand, reply, consumedSecret) ||
        reply.find(L"失败") != std::wstring::npos) {
        MessageBoxW(state.window, reply.empty() ? L"Endpoint 保存失败。" : reply.c_str(),
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
    constexpr int height = 370;
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

    HWND labelBase = CreateWindowExW(0, L"STATIC", L"Base URL", WS_CHILD | WS_VISIBLE,
                                     24, 20, 500, 20, window, nullptr, instance, nullptr);
    state.baseUrlEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", agent.Config().baseUrl.c_str(),
                                        WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                        24, 42, 500, 28, window, reinterpret_cast<HMENU>(kBaseUrlEditId), instance, nullptr);

    HWND labelModel = CreateWindowExW(0, L"STATIC", L"Model", WS_CHILD | WS_VISIBLE,
                                      24, 80, 500, 20, window, nullptr, instance, nullptr);
    state.modelEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", agent.Config().model.c_str(),
                                      WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                      24, 102, 500, 28, window, reinterpret_cast<HMENU>(kModelEditId), instance, nullptr);

    HWND labelEndpoint = CreateWindowExW(0, L"STATIC", L"Endpoint（例如 /v1/chat/completions；留空则直接使用 Base URL 的路径）",
                                         WS_CHILD | WS_VISIBLE,
                                         24, 140, 500, 20, window, nullptr, instance, nullptr);
    state.endpointEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", agent.Config().endpoint.c_str(),
                                         WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                         24, 162, 500, 28, window, reinterpret_cast<HMENU>(kEndpointEditId), instance, nullptr);

    HWND labelKey = CreateWindowExW(0, L"STATIC", L"API Key（已有 Key 显示为 ********；输入新值即可覆盖）",
                                    WS_CHILD | WS_VISIBLE,
                                    24, 200, 500, 20, window, nullptr, instance, nullptr);
    state.apiKeyEdit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
                                       WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL | ES_PASSWORD,
                                       24, 222, 500, 28, window, reinterpret_cast<HMENU>(kApiKeyEditId), instance, nullptr);

    state.statusLabel = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE,
                                        24, 258, 500, 20, window, reinterpret_cast<HMENU>(kStatusLabelId), instance, nullptr);

    HWND saveButton = CreateWindowExW(0, L"BUTTON", L"保存", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                                      292, 294, 72, 28, window, reinterpret_cast<HMENU>(IDOK), instance, nullptr);
    HWND clearButton = CreateWindowExW(0, L"BUTTON", L"清除 Key", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                       372, 294, 72, 28, window, reinterpret_cast<HMENU>(kClearKeyButtonId), instance, nullptr);
    HWND cancelButton = CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                        452, 294, 72, 28, window, reinterpret_cast<HMENU>(IDCANCEL), instance, nullptr);

    for (HWND control : {labelBase, state.baseUrlEdit, labelModel, state.modelEdit, labelEndpoint,
                         state.endpointEdit, labelKey, state.apiKeyEdit, state.statusLabel,
                         saveButton, clearButton, cancelButton}) {
        SetControlFont(control, font);
    }
    SendMessageW(state.apiKeyEdit, EM_SETPASSWORDCHAR, static_cast<WPARAM>(L'*'), 0);
    RefreshKeyStatus(state);

    EnableWindow(owner, FALSE);
    ShowWindow(window, SW_SHOWNORMAL);
    SetForegroundWindow(window);
    SetFocus(state.baseUrlEdit);

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

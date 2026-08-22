#include "turingdesk/AppSearch.h"
#include "turingdesk/DirectToolRuntime.h"
#include "turingdesk/EverythingSearch.h"
#include "turingdesk/HarnessProcessManager.h"
#include "turingdesk/L3Agent.h"
#include "turingdesk/SearchWindow.h"
#include <windows.h>
#include <string_view>

namespace turingdesk {
bool RunL3PersistenceSelfTest();
}

namespace {

constexpr wchar_t kSearchWindowClass[] = L"TuringDesk.Native.SearchWindow";

bool RunNativeSelfTest() {
    turingdesk::AppSearch apps;
    apps.BuildIndex();
    const auto appResults = apps.Query(L"Notepad", 5);
    if (apps.Count() < 5 || appResults.empty()) return false;

    turingdesk::EverythingSearch files;
    if (!files.SelfTest()) return false;

    if (!turingdesk::DirectToolRuntime::SelfTest()) return false;
    if (!turingdesk::HarnessProcessManager::SelfTest()) return false;
    if (!turingdesk::RunL3PersistenceSelfTest()) return false;

    turingdesk::L3Agent l3;
    std::wstring reply;
    bool consumedSecret = false;
    if (!l3.TryHandleLocal(L"/time", reply, consumedSecret) || reply.empty() || consumedSecret) return false;

    reply.clear();
    consumedSecret = false;
    if (!l3.TryHandleLocal(L"/status", reply, consumedSecret) || reply.empty() || consumedSecret) return false;
    if (reply.find(L"Harness=未参与") == std::wstring::npos) return false;
    if (reply.find(L"4317") != std::wstring::npos || reply.find(L"4318") != std::wstring::npos || reply.find(L"MCP") != std::wstring::npos) return false;
    // L3 is a TuringDesk-owned lightweight layer. Its normal status surface must
    // not regress into advertising or depending on another heavy agent runtime.
    if (reply.find(L"Codex") != std::wstring::npos || reply.find(L"Node") != std::wstring::npos ||
        reply.find(L"WebView") != std::wstring::npos) return false;

    reply.clear();
    consumedSecret = false;
    if (!l3.TryHandleLocal(L"/help", reply, consumedSecret) || reply.empty() || consumedSecret) return false;
    if (reply.find(L"/status") == std::wstring::npos || reply.find(L"/time") == std::wstring::npos ||
        reply.find(L"/new") == std::wstring::npos || reply.find(L"Ctrl+Enter") == std::wstring::npos) return false;
    if (reply.find(L"4317") != std::wstring::npos || reply.find(L"4318") != std::wstring::npos || reply.find(L"MCP") != std::wstring::npos) return false;
    if (reply.find(L"Codex") != std::wstring::npos || reply.find(L"Node") != std::wstring::npos ||
        reply.find(L"WebView") != std::wstring::npos) return false;

    for (const wchar_t* command : {L"/new", L"/new-chat", L"新对话"}) {
        reply.clear();
        consumedSecret = false;
        if (!l3.TryHandleLocal(command, reply, consumedSecret) || reply.empty() || consumedSecret) return false;
        if (reply.find(L"L3") == std::wstring::npos) return false;
    }

    return true;
}

void ActivateExistingSearchWindow() {
    const HWND existing = FindWindowW(kSearchWindowClass, nullptr);
    if (!existing) return;

    ShowWindow(existing, SW_SHOWNORMAL);
    SetForegroundWindow(existing);
    const HWND edit = FindWindowExW(existing, nullptr, L"EDIT", nullptr);
    if (edit) SetFocus(edit);
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) return 3;

    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (args.find(L"--self-test") != std::wstring_view::npos) {
        const int result = RunNativeSelfTest() ? 0 : 5;
        if (SUCCEEDED(com)) CoUninitialize();
        return result;
    }

    HANDLE mutex = CreateMutexW(nullptr, FALSE, L"Local\\TuringDesk.Native.Search.Singleton");
    if (!mutex) {
        if (SUCCEEDED(com)) CoUninitialize();
        return 2;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        ActivateExistingSearchWindow();
        CloseHandle(mutex);
        if (SUCCEEDED(com)) CoUninitialize();
        return 0;
    }

    turingdesk::SearchWindow window(instance);
    if (!window.Create()) {
        if (SUCCEEDED(com)) CoUninitialize();
        CloseHandle(mutex);
        return 4;
    }

    const int result = window.RunMessageLoop();
    if (SUCCEEDED(com)) CoUninitialize();
    CloseHandle(mutex);
    return result;
}

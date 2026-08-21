#include "turingdesk/AppSearch.h"
#include "turingdesk/EverythingSearch.h"
#include "turingdesk/L3Agent.h"
#include "turingdesk/SearchWindow.h"
#include <windows.h>
#include <string_view>

namespace {

bool RunNativeSelfTest() {
    turingdesk::AppSearch apps;
    apps.BuildIndex();
    const auto appResults = apps.Query(L"Notepad", 5);
    if (apps.Count() < 5 || appResults.empty()) return false;

    turingdesk::EverythingSearch files;
    if (!files.SelfTest()) return false;

    turingdesk::L3Agent l3;
    std::wstring reply;
    bool consumedSecret = false;
    if (!l3.TryHandleLocal(L"/time", reply, consumedSecret) || reply.empty() || consumedSecret) return false;

    reply.clear();
    consumedSecret = false;
    if (!l3.TryHandleLocal(L"/new", reply, consumedSecret) || reply.empty() || consumedSecret) return false;

    return true;
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR commandLine, int) {
    HANDLE mutex = CreateMutexW(nullptr, FALSE, L"Local\\TuringDesk.Native.Search.Singleton");
    if (!mutex) return 2;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(mutex);
        return 0;
    }

    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) {
        CloseHandle(mutex);
        return 3;
    }

    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (args.find(L"--self-test") != std::wstring_view::npos) {
        const int result = RunNativeSelfTest() ? 0 : 5;
        if (SUCCEEDED(com)) CoUninitialize();
        CloseHandle(mutex);
        return result;
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

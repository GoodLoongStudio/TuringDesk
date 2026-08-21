#include "turingdesk/SearchWindow.h"
#include <windows.h>
#include <string_view>

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

    turingdesk::SearchWindow window(instance);
    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (args.find(L"--self-test") != std::wstring_view::npos) {
        const int result = window.SelfTest() ? 0 : 5;
        if (SUCCEEDED(com)) CoUninitialize();
        CloseHandle(mutex);
        return result;
    }

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

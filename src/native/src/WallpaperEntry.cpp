#include <windows.h>

#include <string_view>

#include "turingdesk/WebWallpaperHost.h"

// WallpaperEngine.cpp is compiled with its historical WinMain symbol renamed to
// TuringDeskWallpaperMain. This small entry layer must remain the only exported
// GUI entry point so isolated WebView2 wallpaper children can bypass the main
// wallpaper singleton before the legacy engine creates its mutex.
int WINAPI TuringDeskWallpaperMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand);

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand) {
    const int webResult = turingdesk::wallpaper::TryRunWebWallpaperChild(instance);
    if (webResult >= 0) return webResult;

    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (args.find(L"--self-test") != std::wstring_view::npos &&
        !turingdesk::wallpaper::WebWallpaperProcessSet::SelfTest()) {
        return 37;
    }

    return TuringDeskWallpaperMain(instance, previous, commandLine, showCommand);
}

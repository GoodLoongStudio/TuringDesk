#include <windows.h>

#include <string_view>

#include "turingdesk/WebWallpaperHost.h"

// WallpaperEngine.cpp is compiled with its historical WinMain symbol renamed to
// TuringDeskWallpaperMain. The Windows headers declare wWinMain with C linkage,
// so the macro-renamed legacy entry keeps that linkage as well.
extern "C" int WINAPI TuringDeskWallpaperMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand);

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

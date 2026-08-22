#include <windows.h>

#include <filesystem>
#include <string_view>
#include <system_error>

#include "turingdesk/WallpaperLibrary.h"
#include "turingdesk/WebWallpaperHost.h"

namespace fs = std::filesystem;

// WallpaperEngine.cpp is compiled with its historical WinMain symbol renamed to
// TuringDeskWallpaperMain. The Windows headers declare wWinMain with C linkage,
// so the macro-renamed legacy entry keeps that linkage as well.
extern "C" int WINAPI TuringDeskWallpaperMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand);

namespace {

bool WebLibrarySelfTest() {
    std::error_code ec;
    const fs::path root = fs::temp_directory_path() /
        (L"TuringDesk-WebLibrary-SelfTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(root, ec);
    if (ec) return false;

    turingdesk::wallpaper::WallpaperLibrary library(root / L"Library");
    std::wstring error;
    bool ok = library.Load(&error);
    ok = ok && turingdesk::wallpaper::WallpaperLibrary::IsTrustedWebUrl(L"https://example.com/wallpaper");
    ok = ok && !turingdesk::wallpaper::WallpaperLibrary::IsTrustedWebUrl(L"http://example.com/wallpaper");
    ok = ok && !turingdesk::wallpaper::WallpaperLibrary::IsTrustedWebUrl(L"https://user:pass@example.com/wallpaper");

    const auto imported = library.ImportWebUrl(L"https://example.com/wallpaper", L"Web Self Test", &error);
    ok = ok && imported.has_value() && imported->kind == turingdesk::wallpaper::LibraryWallpaperKind::Web;

    turingdesk::wallpaper::WallpaperLibrary reloaded(root / L"Library");
    ok = ok && reloaded.Load(&error);
    if (imported) {
        const auto persisted = reloaded.Find(imported->id);
        ok = ok && persisted.has_value() && persisted->source.wstring() == L"https://example.com/wallpaper";
    }

    fs::remove_all(root, ec);
    return ok;
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand) {
    const int webResult = turingdesk::wallpaper::TryRunWebWallpaperChild(instance);
    if (webResult >= 0) return webResult;

    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    if (args.find(L"--self-test") != std::wstring_view::npos) {
        if (!turingdesk::wallpaper::WebWallpaperProcessSet::SelfTest()) return 37;
        if (!WebLibrarySelfTest()) return 38;
    }

    return TuringDeskWallpaperMain(instance, previous, commandLine, showCommand);
}

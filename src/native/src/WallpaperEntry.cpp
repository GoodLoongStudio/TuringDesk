#include <windows.h>

#include <filesystem>
#include <iterator>
#include <string>
#include <string_view>
#include <system_error>

#include "turingdesk/WallpaperLibrary.h"
#include "turingdesk/WallpaperWebRuntimeCoordinator.h"
#include "turingdesk/WebWallpaperHost.h"

namespace fs = std::filesystem;

// WallpaperEngine.cpp is compiled with its historical WinMain symbol renamed to
// TuringDeskWallpaperMain. The Windows headers declare wWinMain with C linkage,
// so the macro-renamed legacy entry keeps that linkage as well.
extern "C" int WINAPI TuringDeskWallpaperMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand);

namespace {

constexpr wchar_t kWallpaperControlClass[] = L"TuringDesk.Native.WallpaperControl";

std::wstring ReadProfileValue(const fs::path& path, const wchar_t* key) {
    wchar_t buffer[32768]{};
    GetPrivateProfileStringW(L"Wallpaper", key, L"", buffer, static_cast<DWORD>(std::size(buffer)), path.c_str());
    return buffer;
}

bool WebLibrarySelfTest() {
    std::error_code ec;
    const fs::path root = fs::temp_directory_path() /
        (L"TuringDesk-WebLibrary-SelfTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(root, ec);
    if (ec) return false;

    wchar_t previousLocalAppData[32768]{};
    const DWORD previousLocalAppDataLength = GetEnvironmentVariableW(
        L"LOCALAPPDATA", previousLocalAppData, static_cast<DWORD>(std::size(previousLocalAppData)));
    const bool hadLocalAppData = previousLocalAppDataLength > 0 && previousLocalAppDataLength < std::size(previousLocalAppData);
    const fs::path isolatedLocalAppData = root / L"LocalAppData";
    fs::create_directories(isolatedLocalAppData, ec);
    bool ok = !ec && SetEnvironmentVariableW(L"LOCALAPPDATA", isolatedLocalAppData.c_str()) != FALSE;

    turingdesk::wallpaper::WallpaperLibrary library(root / L"Library");
    std::wstring error;
    ok = ok && library.Load(&error);
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
        ok = ok && turingdesk::wallpaper::ActivateWebWallpaperItem(*imported, L"", &error);

        const fs::path wallpaperConfig = isolatedLocalAppData / L"TuringDesk" / L"wallpaper.ini";
        ok = ok && ReadProfileValue(wallpaperConfig, L"Enabled") == L"1";
        ok = ok && _wcsicmp(ReadProfileValue(wallpaperConfig, L"Scene").c_str(), L"web") == 0;
        ok = ok && ReadProfileValue(wallpaperConfig, L"Image") == L"https://example.com/wallpaper";
        ok = ok && ReadProfileValue(wallpaperConfig, L"Video").empty();
    }

    if (hadLocalAppData)
        SetEnvironmentVariableW(L"LOCALAPPDATA", previousLocalAppData);
    else
        SetEnvironmentVariableW(L"LOCALAPPDATA", nullptr);

    fs::remove_all(root, ec);
    return ok;
}

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand) {
    const int webResult = turingdesk::wallpaper::TryRunWebWallpaperChild(instance);
    if (webResult >= 0) return webResult;

    const std::wstring_view args = commandLine ? std::wstring_view(commandLine) : std::wstring_view{};
    const bool selfTest = args.find(L"--self-test") != std::wstring_view::npos;
    if (selfTest) {
        if (!turingdesk::wallpaper::WebWallpaperProcessSet::SelfTest()) return 37;
        if (!WebLibrarySelfTest()) return 38;
        if (!turingdesk::wallpaper::WallpaperWebRuntimeCoordinator::SelfTest()) return 39;
        return TuringDeskWallpaperMain(instance, previous, commandLine, showCommand);
    }

    // A second TuringDeskWallpaper invocation is only a command sender for the
    // already-running singleton. Do not let that short-lived process spawn a
    // duplicate set of Web wallpaper children.
    if (FindWindowW(kWallpaperControlClass, nullptr))
        return TuringDeskWallpaperMain(instance, previous, commandLine, showCommand);

    turingdesk::wallpaper::WallpaperWebRuntimeCoordinator webCoordinator;
    if (!webCoordinator.Start()) return 40;
    const int result = TuringDeskWallpaperMain(instance, previous, commandLine, showCommand);
    webCoordinator.Stop();
    return result;
}

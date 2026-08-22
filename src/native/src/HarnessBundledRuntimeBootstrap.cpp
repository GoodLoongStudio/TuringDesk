#include <windows.h>

#include <filesystem>
#include <iterator>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace {

fs::path ExecutableDirectory() {
    std::vector<wchar_t> buffer(32768);
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    return fs::path(std::wstring(buffer.data(), length)).parent_path();
}

bool DirectoryExists(const fs::path& path) {
    std::error_code ec;
    return fs::is_directory(path, ec);
}

std::wstring CurrentPath() {
    const DWORD needed = GetEnvironmentVariableW(L"PATH", nullptr, 0);
    if (needed == 0) return {};
    std::wstring value(static_cast<std::size_t>(needed), L'\0');
    const DWORD length = GetEnvironmentVariableW(L"PATH", value.data(), needed);
    if (length == 0 || length >= needed) return {};
    value.resize(length);
    return value;
}

void PrependBundledHarnessRuntimeToPath() {
    const fs::path appDir = ExecutableDirectory();
    if (appDir.empty()) return;

    const fs::path nodeDir = appDir / L"Runtime" / L"Node";
    if (!DirectoryExists(nodeDir)) return;

    std::wstring prefix = nodeDir.wstring();
    const fs::path binDir = nodeDir / L"node_modules" / L".bin";
    if (DirectoryExists(binDir)) prefix += L";" + binDir.wstring();

    const std::wstring oldPath = CurrentPath();
    if (!oldPath.empty()) prefix += L";" + oldPath;
    SetEnvironmentVariableW(L"PATH", prefix.c_str());

    // Keep npm/DeepSeek transient caches inside TuringDesk-owned state when the
    // upstream package needs them. This does not install or mutate system Node.
    wchar_t local[32768]{};
    const DWORD localLength = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    if (localLength > 0 && localLength < std::size(local)) {
        const fs::path stateRoot = fs::path(std::wstring(local, localLength)) / L"TuringDesk" / L"HarnessState";
        std::error_code ec;
        fs::create_directories(stateRoot, ec);
        if (!ec) {
            const std::wstring dshHome = (stateRoot / L"dsh-home").wstring();
            const std::wstring npmCache = (stateRoot / L"npm-cache").wstring();
            SetEnvironmentVariableW(L"DSH_HOME", dshHome.c_str());
            SetEnvironmentVariableW(L"npm_config_cache", npmCache.c_str());
        }
    }
}

struct BundledRuntimeBootstrap final {
    BundledRuntimeBootstrap() { PrependBundledHarnessRuntimeToPath(); }
};

// This translation unit is linked only into TuringDeskHarness. Static
// initialization intentionally runs before wWinMain/HarnessProcessManager.
BundledRuntimeBootstrap g_bundledRuntimeBootstrap;

} // namespace

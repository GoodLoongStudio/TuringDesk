#pragma once
#include <filesystem>
#include <string>
#include <string_view>

namespace turingdesk::wallpaper {

enum class WallpaperPackageType {
    Image,
    Video,
    Web,
    Scene,
    Unknown,
};

struct WallpaperPackageManifest {
    int schema{1};
    WallpaperPackageType type{WallpaperPackageType::Unknown};
    std::wstring title;
    std::wstring author;
    std::filesystem::path entry;
    std::wstring provenance;
    int fpsCap{30};
    bool audio{};
};

class WallpaperPackage {
public:
    static bool CreateWeb(
        const std::filesystem::path& packageDirectory,
        std::wstring title,
        std::string_view htmlUtf8,
        std::wstring provenance = L"user-authored",
        std::wstring author = L"TuringDesk",
        std::wstring* error = nullptr);

    static bool Validate(
        const std::filesystem::path& packageDirectory,
        WallpaperPackageManifest* manifest = nullptr,
        std::wstring* error = nullptr);

    static const wchar_t* TypeKey(WallpaperPackageType type) noexcept;
    static WallpaperPackageType ParseType(std::wstring_view value) noexcept;
    static bool SelfTest();
};

} // namespace turingdesk::wallpaper

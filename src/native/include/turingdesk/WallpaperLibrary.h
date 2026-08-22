#pragma once

#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace turingdesk::wallpaper {

enum class LibraryWallpaperKind {
    Image,
    Video,
    Web,
    Scene,
    Unknown,
};

struct WallpaperLibraryItem {
    std::wstring id;
    LibraryWallpaperKind kind{LibraryWallpaperKind::Unknown};
    std::wstring title;
    std::filesystem::path source;
    std::filesystem::path thumbnail;
    bool favorite{};
    bool managedCopy{};
    unsigned long long importedUnixSeconds{};
    unsigned long long lastUsedUnixSeconds{};
};

struct WallpaperImportOptions {
    bool managedCopy{};
    std::wstring title;
};

class WallpaperLibrary {
public:
    WallpaperLibrary();
    explicit WallpaperLibrary(std::filesystem::path root);

    bool Load(std::wstring* error = nullptr);
    const std::vector<WallpaperLibraryItem>& Items() const noexcept;

    std::optional<WallpaperLibraryItem> ImportFile(
        const std::filesystem::path& source,
        const WallpaperImportOptions& options = {},
        std::wstring* error = nullptr);
    std::optional<WallpaperLibraryItem> ImportWebUrl(
        std::wstring url,
        std::wstring title = {},
        std::wstring* error = nullptr);

    bool UpsertScene(std::wstring id, std::wstring title, std::wstring* error = nullptr);
    bool Remove(std::wstring_view id, bool deleteManagedCopy, std::wstring* error = nullptr);
    bool SetFavorite(std::wstring_view id, bool favorite, std::wstring* error = nullptr);
    bool MarkUsed(std::wstring_view id, std::wstring* error = nullptr);

    std::optional<WallpaperLibraryItem> Find(std::wstring_view id) const;
    std::vector<WallpaperLibraryItem> Search(std::wstring_view query) const;
    std::vector<WallpaperLibraryItem> RecentlyUsed(std::size_t limit = 12) const;
    std::vector<WallpaperLibraryItem> Favorites() const;

    const std::filesystem::path& Root() const noexcept;
    std::filesystem::path ManifestPath() const;
    std::filesystem::path MediaDirectory() const;
    std::filesystem::path PackageDirectory() const;
    std::filesystem::path ThumbnailDirectory() const;

    static LibraryWallpaperKind InferKind(const std::filesystem::path& path) noexcept;
    static const wchar_t* KindKey(LibraryWallpaperKind kind) noexcept;
    static LibraryWallpaperKind ParseKind(std::wstring_view value) noexcept;
    static bool IsTrustedWebUrl(std::wstring_view value) noexcept;
    static bool SelfTest();

private:
    bool DiscoverPackages(std::wstring* error);
    bool SaveItem(const WallpaperLibraryItem& item, std::wstring* error);
    bool GenerateThumbnail(WallpaperLibraryItem& item);
    std::optional<std::size_t> FindIndex(std::wstring_view id) const;
    std::optional<std::size_t> FindSourceIndex(const std::filesystem::path& source) const;

    std::filesystem::path root_;
    std::vector<WallpaperLibraryItem> items_;
};

} // namespace turingdesk::wallpaper

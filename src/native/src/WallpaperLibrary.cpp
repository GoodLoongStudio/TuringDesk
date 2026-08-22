#include "turingdesk/WallpaperLibrary.h"

#include <windows.h>
#include <shobjidl.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cwchar>
#include <cwctype>
#include <fstream>
#include <iterator>
#include <system_error>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

constexpr wchar_t kItemPrefix[] = L"Item.";
constexpr UINT kThumbnailWidth = 320;
constexpr UINT kThumbnailHeight = 180;

void SetError(std::wstring* error, std::wstring value) {
    if (error) *error = std::move(value);
}

fs::path DefaultLibraryRoot() {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path base = (length > 0 && length < std::size(local))
        ? fs::path(local)
        : fs::temp_directory_path();
    return base / L"TuringDesk" / L"WallpaperLibrary";
}

unsigned long long NowUnixSeconds() {
    return static_cast<unsigned long long>(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());
}

std::wstring SanitizeText(std::wstring value) {
    for (auto& ch : value) {
        if (ch == L'\r' || ch == L'\n' || ch == L'\t') ch = L' ';
    }
    return value;
}

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

fs::path NormalizedAbsolute(const fs::path& value) {
    std::error_code ec;
    fs::path absolute = fs::absolute(value, ec);
    if (ec) absolute = value;
    absolute = absolute.lexically_normal();
    fs::path canonical = fs::weakly_canonical(absolute, ec);
    return ec ? absolute : canonical;
}

bool SamePath(const fs::path& a, const fs::path& b) {
    const std::wstring left = NormalizedAbsolute(a).wstring();
    const std::wstring right = NormalizedAbsolute(b).wstring();
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}

bool PathIsInside(const fs::path& candidate, const fs::path& root) {
    const std::wstring value = Lower(NormalizedAbsolute(candidate).wstring());
    std::wstring base = Lower(NormalizedAbsolute(root).wstring());
    if (!base.empty() && base.back() != L'\\' && base.back() != L'/') base.push_back(fs::path::preferred_separator);
    return value.size() >= base.size() && value.compare(0, base.size(), base) == 0;
}

std::wstring MakeId() {
    GUID guid{};
    if (FAILED(CoCreateGuid(&guid))) {
        return L"wallpaper-" + std::to_wstring(GetTickCount64()) + L"-" + std::to_wstring(GetCurrentProcessId());
    }
    wchar_t text[64]{};
    StringFromGUID2(guid, text, static_cast<int>(std::size(text)));
    std::wstring result;
    result.reserve(36);
    for (const wchar_t ch : std::wstring_view(text)) {
        if (ch != L'{' && ch != L'}' && ch != L'-') result.push_back(ch);
    }
    return Lower(result);
}

std::wstring SectionName(std::wstring_view id) {
    return std::wstring(kItemPrefix) + std::wstring(id);
}

std::wstring ReadProfileText(const fs::path& manifest, const std::wstring& section,
                             const wchar_t* key, const wchar_t* fallback = L"") {
    std::vector<wchar_t> buffer(32768);
    GetPrivateProfileStringW(section.c_str(), key, fallback, buffer.data(),
                             static_cast<DWORD>(buffer.size()), manifest.c_str());
    return buffer.data();
}

unsigned long long ReadProfileU64(const fs::path& manifest, const std::wstring& section,
                                  const wchar_t* key, unsigned long long fallback = 0) {
    const auto value = ReadProfileText(manifest, section, key, L"");
    if (value.empty()) return fallback;
    wchar_t* end = nullptr;
    const unsigned long long parsed = _wcstoui64(value.c_str(), &end, 10);
    return end == value.c_str() ? fallback : parsed;
}

bool WriteProfileText(const fs::path& manifest, const std::wstring& section,
                      const wchar_t* key, const std::wstring& value) {
    return WritePrivateProfileStringW(section.c_str(), key, value.c_str(), manifest.c_str()) != FALSE;
}

std::vector<std::wstring> EnumerateSections(const fs::path& manifest) {
    std::vector<std::wstring> sections;
    DWORD capacity = 65536;
    for (; capacity <= 1024 * 1024; capacity *= 2) {
        std::vector<wchar_t> buffer(capacity);
        const DWORD written = GetPrivateProfileSectionNamesW(buffer.data(), capacity, manifest.c_str());
        if (written == 0) return sections;
        if (written >= capacity - 2) continue;
        for (const wchar_t* cursor = buffer.data(); *cursor; cursor += std::wcslen(cursor) + 1)
            sections.emplace_back(cursor);
        return sections;
    }
    return sections;
}

std::wstring DefaultTitle(const fs::path& path) {
    std::wstring title = path.stem().wstring();
    if (title.empty()) title = path.filename().wstring();
    return title.empty() ? L"未命名壁纸" : title;
}

bool SaveHBitmapAsPng(HBITMAP bitmap, const fs::path& path) {
    if (!bitmap) return false;
    std::error_code ec;
    fs::create_directories(path.parent_path(), ec);

    ComPtr<IWICImagingFactory> factory;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(factory.GetAddressOf())))) return false;

    ComPtr<IWICBitmap> source;
    if (FAILED(factory->CreateBitmapFromHBITMAP(bitmap, nullptr, WICBitmapUsePremultipliedAlpha,
                                                source.GetAddressOf()))) return false;

    ComPtr<IWICStream> stream;
    if (FAILED(factory->CreateStream(stream.GetAddressOf()))) return false;
    if (FAILED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE))) return false;

    ComPtr<IWICBitmapEncoder> encoder;
    if (FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, encoder.GetAddressOf()))) return false;
    if (FAILED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache))) return false;

    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> properties;
    if (FAILED(encoder->CreateNewFrame(frame.GetAddressOf(), properties.GetAddressOf()))) return false;
    if (FAILED(frame->Initialize(properties.Get()))) return false;

    UINT width = 0;
    UINT height = 0;
    if (FAILED(source->GetSize(&width, &height)) || width == 0 || height == 0) return false;
    if (FAILED(frame->SetSize(width, height))) return false;
    WICPixelFormatGUID pixelFormat = GUID_WICPixelFormat32bppBGRA;
    if (FAILED(frame->SetPixelFormat(&pixelFormat))) return false;
    if (FAILED(frame->WriteSource(source.Get(), nullptr))) return false;
    if (FAILED(frame->Commit())) return false;
    return SUCCEEDED(encoder->Commit());
}

bool GenerateShellThumbnail(const fs::path& source, const fs::path& destination) {
    ComPtr<IShellItem> item;
    if (FAILED(SHCreateItemFromParsingName(source.c_str(), nullptr, IID_PPV_ARGS(item.GetAddressOf())))) return false;

    ComPtr<IShellItemImageFactory> imageFactory;
    if (FAILED(item.As(&imageFactory))) return false;

    HBITMAP bitmap = nullptr;
    const SIZE requested{static_cast<LONG>(kThumbnailWidth), static_cast<LONG>(kThumbnailHeight)};
    const HRESULT hr = imageFactory->GetImage(
        requested,
        static_cast<SIIGBF>(SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT),
        &bitmap);
    if (FAILED(hr) || !bitmap) return false;

    const bool saved = SaveHBitmapAsPng(bitmap, destination);
    DeleteObject(bitmap);
    return saved;
}

} // namespace

WallpaperLibrary::WallpaperLibrary() : root_(DefaultLibraryRoot()) {}
WallpaperLibrary::WallpaperLibrary(fs::path root) : root_(std::move(root)) {}

bool WallpaperLibrary::Load(std::wstring* error) {
    SetError(error, L"");
    items_.clear();
    std::error_code ec;
    fs::create_directories(root_, ec);
    fs::create_directories(MediaDirectory(), ec);
    fs::create_directories(ThumbnailDirectory(), ec);
    if (ec) {
        SetError(error, L"无法创建壁纸库目录：" + root_.wstring());
        return false;
    }

    const fs::path manifest = ManifestPath();
    if (!fs::exists(manifest, ec)) return true;

    for (const auto& section : EnumerateSections(manifest)) {
        if (section.rfind(kItemPrefix, 0) != 0) continue;
        WallpaperLibraryItem item;
        item.id = section.substr(std::size(kItemPrefix) - 1);
        item.kind = ParseKind(ReadProfileText(manifest, section, L"Kind", L"unknown"));
        item.title = ReadProfileText(manifest, section, L"Title", L"");
        item.source = ReadProfileText(manifest, section, L"Source", L"");
        item.thumbnail = ReadProfileText(manifest, section, L"Thumbnail", L"");
        item.favorite = ReadProfileText(manifest, section, L"Favorite", L"0") == L"1";
        item.managedCopy = ReadProfileText(manifest, section, L"ManagedCopy", L"0") == L"1";
        item.importedUnixSeconds = ReadProfileU64(manifest, section, L"Imported", 0);
        item.lastUsedUnixSeconds = ReadProfileU64(manifest, section, L"LastUsed", 0);
        if (item.title.empty()) item.title = item.source.empty() ? L"未命名壁纸" : DefaultTitle(item.source);
        if (!item.id.empty() && item.kind != LibraryWallpaperKind::Unknown) items_.push_back(std::move(item));
    }

    std::sort(items_.begin(), items_.end(), [](const WallpaperLibraryItem& a, const WallpaperLibraryItem& b) {
        if (a.lastUsedUnixSeconds != b.lastUsedUnixSeconds) return a.lastUsedUnixSeconds > b.lastUsedUnixSeconds;
        return a.importedUnixSeconds > b.importedUnixSeconds;
    });
    return true;
}

const std::vector<WallpaperLibraryItem>& WallpaperLibrary::Items() const noexcept {
    return items_;
}

std::optional<WallpaperLibraryItem> WallpaperLibrary::ImportFile(
    const fs::path& sourcePath, const WallpaperImportOptions& options, std::wstring* error) {
    SetError(error, L"");
    std::error_code ec;
    if (!fs::exists(sourcePath, ec) || !fs::is_regular_file(sourcePath, ec)) {
        SetError(error, L"壁纸源文件不存在：" + sourcePath.wstring());
        return std::nullopt;
    }

    const LibraryWallpaperKind kind = InferKind(sourcePath);
    if (kind == LibraryWallpaperKind::Unknown) {
        SetError(error, L"不支持的壁纸文件类型：" + sourcePath.extension().wstring());
        return std::nullopt;
    }

    const fs::path normalizedSource = NormalizedAbsolute(sourcePath);
    if (const auto existing = FindSourceIndex(normalizedSource)) {
        auto& item = items_[*existing];
        if (!options.title.empty()) item.title = SanitizeText(options.title);
        item.lastUsedUnixSeconds = NowUnixSeconds();
        if (!SaveItem(item, error)) return std::nullopt;
        return item;
    }

    WallpaperLibraryItem item;
    item.id = MakeId();
    item.kind = kind;
    item.title = SanitizeText(options.title.empty() ? DefaultTitle(normalizedSource) : options.title);
    item.favorite = false;
    item.managedCopy = options.managedCopy;
    item.importedUnixSeconds = NowUnixSeconds();
    item.lastUsedUnixSeconds = item.importedUnixSeconds;

    if (options.managedCopy) {
        fs::create_directories(MediaDirectory(), ec);
        const fs::path destination = MediaDirectory() / (item.id + normalizedSource.extension().wstring());
        fs::copy_file(normalizedSource, destination, fs::copy_options::overwrite_existing, ec);
        if (ec) {
            SetError(error, L"复制壁纸到托管库失败：" + ec.message().c_str());
            return std::nullopt;
        }
        item.source = destination;
    } else {
        item.source = normalizedSource;
    }

    GenerateThumbnail(item);
    if (!SaveItem(item, error)) {
        if (item.managedCopy) fs::remove(item.source, ec);
        if (!item.thumbnail.empty()) fs::remove(item.thumbnail, ec);
        return std::nullopt;
    }
    items_.insert(items_.begin(), item);
    return item;
}

bool WallpaperLibrary::UpsertScene(std::wstring id, std::wstring title, std::wstring* error) {
    SetError(error, L"");
    if (id.empty()) {
        SetError(error, L"Scene ID 不能为空");
        return false;
    }
    id = SanitizeText(std::move(id));
    title = SanitizeText(std::move(title));
    if (const auto index = FindIndex(id)) {
        auto& item = items_[*index];
        item.kind = LibraryWallpaperKind::Scene;
        item.title = title.empty() ? id : title;
        return SaveItem(item, error);
    }

    WallpaperLibraryItem item;
    item.id = std::move(id);
    item.kind = LibraryWallpaperKind::Scene;
    item.title = title.empty() ? item.id : std::move(title);
    item.importedUnixSeconds = NowUnixSeconds();
    if (!SaveItem(item, error)) return false;
    items_.push_back(std::move(item));
    return true;
}

bool WallpaperLibrary::Remove(std::wstring_view id, bool deleteManagedCopy, std::wstring* error) {
    SetError(error, L"");
    const auto index = FindIndex(id);
    if (!index) {
        SetError(error, L"壁纸库中不存在该项目");
        return false;
    }

    const WallpaperLibraryItem item = items_[*index];
    const std::wstring section = SectionName(item.id);
    if (!WritePrivateProfileStringW(section.c_str(), nullptr, nullptr, ManifestPath().c_str())) {
        SetError(error, L"删除壁纸库记录失败");
        return false;
    }

    std::error_code ec;
    if (!item.thumbnail.empty() && PathIsInside(item.thumbnail, ThumbnailDirectory())) fs::remove(item.thumbnail, ec);
    if (deleteManagedCopy && item.managedCopy && !item.source.empty() && PathIsInside(item.source, MediaDirectory()))
        fs::remove(item.source, ec);
    items_.erase(items_.begin() + static_cast<std::ptrdiff_t>(*index));
    return true;
}

bool WallpaperLibrary::SetFavorite(std::wstring_view id, bool favorite, std::wstring* error) {
    const auto index = FindIndex(id);
    if (!index) {
        SetError(error, L"壁纸库中不存在该项目");
        return false;
    }
    items_[*index].favorite = favorite;
    return SaveItem(items_[*index], error);
}

bool WallpaperLibrary::MarkUsed(std::wstring_view id, std::wstring* error) {
    const auto index = FindIndex(id);
    if (!index) {
        SetError(error, L"壁纸库中不存在该项目");
        return false;
    }
    items_[*index].lastUsedUnixSeconds = NowUnixSeconds();
    if (!SaveItem(items_[*index], error)) return false;
    const WallpaperLibraryItem used = items_[*index];
    items_.erase(items_.begin() + static_cast<std::ptrdiff_t>(*index));
    items_.insert(items_.begin(), used);
    return true;
}

std::optional<WallpaperLibraryItem> WallpaperLibrary::Find(std::wstring_view id) const {
    if (const auto index = FindIndex(id)) return items_[*index];
    return std::nullopt;
}

std::vector<WallpaperLibraryItem> WallpaperLibrary::Search(std::wstring_view query) const {
    const std::wstring needle = Lower(std::wstring(query));
    if (needle.empty()) return items_;
    std::vector<WallpaperLibraryItem> result;
    for (const auto& item : items_) {
        const std::wstring haystack = Lower(item.title + L"\n" + item.source.wstring() + L"\n" + KindKey(item.kind));
        if (haystack.find(needle) != std::wstring::npos) result.push_back(item);
    }
    return result;
}

std::vector<WallpaperLibraryItem> WallpaperLibrary::RecentlyUsed(std::size_t limit) const {
    std::vector<WallpaperLibraryItem> result;
    for (const auto& item : items_) {
        if (item.lastUsedUnixSeconds > 0) result.push_back(item);
    }
    std::sort(result.begin(), result.end(), [](const auto& a, const auto& b) {
        return a.lastUsedUnixSeconds > b.lastUsedUnixSeconds;
    });
    if (result.size() > limit) result.resize(limit);
    return result;
}

std::vector<WallpaperLibraryItem> WallpaperLibrary::Favorites() const {
    std::vector<WallpaperLibraryItem> result;
    std::copy_if(items_.begin(), items_.end(), std::back_inserter(result), [](const auto& item) { return item.favorite; });
    return result;
}

const fs::path& WallpaperLibrary::Root() const noexcept {
    return root_;
}

fs::path WallpaperLibrary::ManifestPath() const {
    return root_ / L"library.ini";
}

fs::path WallpaperLibrary::MediaDirectory() const {
    return root_ / L"Media";
}

fs::path WallpaperLibrary::ThumbnailDirectory() const {
    return root_ / L"Thumbnails";
}

LibraryWallpaperKind WallpaperLibrary::InferKind(const fs::path& path) noexcept {
    const std::wstring extension = Lower(path.extension().wstring());
    if (extension == L".jpg" || extension == L".jpeg" || extension == L".png" || extension == L".bmp" ||
        extension == L".gif" || extension == L".webp" || extension == L".tif" || extension == L".tiff")
        return LibraryWallpaperKind::Image;
    if (extension == L".mp4" || extension == L".mov" || extension == L".wmv" || extension == L".m4v" ||
        extension == L".avi" || extension == L".mkv" || extension == L".webm")
        return LibraryWallpaperKind::Video;
    if (extension == L".html" || extension == L".htm") return LibraryWallpaperKind::Web;
    return LibraryWallpaperKind::Unknown;
}

const wchar_t* WallpaperLibrary::KindKey(LibraryWallpaperKind kind) noexcept {
    switch (kind) {
    case LibraryWallpaperKind::Image: return L"image";
    case LibraryWallpaperKind::Video: return L"video";
    case LibraryWallpaperKind::Web: return L"web";
    case LibraryWallpaperKind::Scene: return L"scene";
    case LibraryWallpaperKind::Unknown: break;
    }
    return L"unknown";
}

LibraryWallpaperKind WallpaperLibrary::ParseKind(std::wstring_view value) noexcept {
    if (_wcsicmp(std::wstring(value).c_str(), L"image") == 0) return LibraryWallpaperKind::Image;
    if (_wcsicmp(std::wstring(value).c_str(), L"video") == 0) return LibraryWallpaperKind::Video;
    if (_wcsicmp(std::wstring(value).c_str(), L"web") == 0) return LibraryWallpaperKind::Web;
    if (_wcsicmp(std::wstring(value).c_str(), L"scene") == 0) return LibraryWallpaperKind::Scene;
    return LibraryWallpaperKind::Unknown;
}

bool WallpaperLibrary::SaveItem(const WallpaperLibraryItem& item, std::wstring* error) {
    std::error_code ec;
    fs::create_directories(root_, ec);
    if (ec) {
        SetError(error, L"无法创建壁纸库目录");
        return false;
    }
    const fs::path manifest = ManifestPath();
    const std::wstring section = SectionName(item.id);
    bool ok = true;
    ok = WriteProfileText(manifest, section, L"Kind", KindKey(item.kind)) && ok;
    ok = WriteProfileText(manifest, section, L"Title", SanitizeText(item.title)) && ok;
    ok = WriteProfileText(manifest, section, L"Source", item.source.wstring()) && ok;
    ok = WriteProfileText(manifest, section, L"Thumbnail", item.thumbnail.wstring()) && ok;
    ok = WriteProfileText(manifest, section, L"Favorite", item.favorite ? L"1" : L"0") && ok;
    ok = WriteProfileText(manifest, section, L"ManagedCopy", item.managedCopy ? L"1" : L"0") && ok;
    ok = WriteProfileText(manifest, section, L"Imported", std::to_wstring(item.importedUnixSeconds)) && ok;
    ok = WriteProfileText(manifest, section, L"LastUsed", std::to_wstring(item.lastUsedUnixSeconds)) && ok;
    WritePrivateProfileStringW(nullptr, nullptr, nullptr, manifest.c_str());
    if (!ok) SetError(error, L"写入壁纸库清单失败");
    return ok;
}

bool WallpaperLibrary::GenerateThumbnail(WallpaperLibraryItem& item) {
    if (item.source.empty() || item.kind == LibraryWallpaperKind::Scene || item.kind == LibraryWallpaperKind::Web) return false;
    const fs::path destination = ThumbnailDirectory() / (item.id + L".png");
    if (!GenerateShellThumbnail(item.source, destination)) return false;
    item.thumbnail = destination;
    return true;
}

std::optional<std::size_t> WallpaperLibrary::FindIndex(std::wstring_view id) const {
    for (std::size_t i = 0; i < items_.size(); ++i) {
        if (_wcsicmp(items_[i].id.c_str(), std::wstring(id).c_str()) == 0) return i;
    }
    return std::nullopt;
}

std::optional<std::size_t> WallpaperLibrary::FindSourceIndex(const fs::path& source) const {
    for (std::size_t i = 0; i < items_.size(); ++i) {
        if (!items_[i].source.empty() && SamePath(items_[i].source, source)) return i;
    }
    return std::nullopt;
}

bool WallpaperLibrary::SelfTest() {
    std::error_code ec;
    const fs::path root = fs::temp_directory_path() /
        (L"TuringDesk-WallpaperLibrary-SelfTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(root, ec);
    if (ec) return false;

    const fs::path sample = root / L"sample.mp4";
    {
        std::ofstream output(sample, std::ios::binary);
        output << "TuringDesk wallpaper library self-test";
    }

    WallpaperLibrary library(root / L"Library");
    std::wstring error;
    bool ok = library.Load(&error);
    auto imported = library.ImportFile(sample, {}, &error);
    ok = ok && imported.has_value();
    if (imported) {
        ok = ok && library.SetFavorite(imported->id, true, &error);
        ok = ok && library.MarkUsed(imported->id, &error);
        ok = ok && !library.Search(L"sample").empty();
        ok = ok && library.Favorites().size() == 1;
        ok = ok && !library.RecentlyUsed(1).empty();
    }
    ok = ok && library.UpsertScene(L"scene-aurora", L"Aurora Flow", &error);

    WallpaperLibrary reloaded(root / L"Library");
    ok = ok && reloaded.Load(&error);
    ok = ok && reloaded.Find(L"scene-aurora").has_value();
    if (imported) ok = ok && reloaded.Find(imported->id).has_value();

    fs::remove_all(root, ec);
    return ok;
}

} // namespace turingdesk::wallpaper

#include "turingdesk/WallpaperIndependentLayout.h"

#include <windows.h>

#include <algorithm>
#include <fstream>
#include <system_error>

namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

ResolvedMonitorWallpaper MakeFallback(const MonitorInfo& monitor, const RECT& region,
                                      const GlobalWallpaperDescriptor& fallback,
                                      std::wstring reason) {
    ResolvedMonitorWallpaper result;
    result.monitorId = StableMonitorKey(monitor);
    result.monitorName = monitor.friendlyName.empty() ? monitor.deviceName : monitor.friendlyName;
    result.region = region;
    result.kind = fallback.kind;
    result.sceneKey = fallback.sceneKey;
    result.source = fallback.source;
    result.fallback = true;
    result.fallbackReason = std::move(reason);
    return result;
}

std::wstring SceneKeyForLibraryId(std::wstring_view id) {
    const std::wstring value(id);
    if (_wcsicmp(value.c_str(), L"scene-neon") == 0) return L"neon";
    if (_wcsicmp(value.c_str(), L"scene-grid") == 0) return L"grid";
    return L"aurora";
}

} // namespace

std::vector<ResolvedMonitorWallpaper> ResolveIndependentWallpapers(
    const MonitorTopology& topology,
    const WallpaperMonitorAssignments& assignments,
    const WallpaperLibrary& library,
    const GlobalWallpaperDescriptor& globalFallback) {
    std::vector<ResolvedMonitorWallpaper> result;
    if (!topology.Valid()) return result;

    const auto regions = DrawRegionsInHost(topology, LayoutMode::Independent);
    result.reserve(topology.monitors.size());
    for (std::size_t i = 0; i < topology.monitors.size(); ++i) {
        const auto& monitor = topology.monitors[i];
        const RECT region = i < regions.size() ? regions[i] : RECT{};
        const auto assignedId = assignments.WallpaperIdFor(monitor);
        if (!assignedId) {
            result.push_back(MakeFallback(monitor, region, globalFallback, L"该显示器尚未分配独立壁纸"));
            continue;
        }

        const auto item = library.Find(*assignedId);
        if (!item) {
            result.push_back(MakeFallback(monitor, region, globalFallback, L"保存的壁纸库项目已不存在"));
            result.back().wallpaperId = *assignedId;
            continue;
        }

        if (item->kind == LibraryWallpaperKind::Web) {
            result.push_back(MakeFallback(monitor, region, globalFallback, L"Web 壁纸后端尚未启用"));
            result.back().wallpaperId = item->id;
            continue;
        }
        if (item->kind == LibraryWallpaperKind::Unknown) {
            result.push_back(MakeFallback(monitor, region, globalFallback, L"壁纸类型无法识别"));
            result.back().wallpaperId = item->id;
            continue;
        }
        if (item->kind != LibraryWallpaperKind::Scene) {
            std::error_code ec;
            if (item->source.empty() || !fs::exists(item->source, ec) || !fs::is_regular_file(item->source, ec)) {
                result.push_back(MakeFallback(monitor, region, globalFallback, L"壁纸源文件已离线或被移动"));
                result.back().wallpaperId = item->id;
                continue;
            }
        }

        ResolvedMonitorWallpaper resolved;
        resolved.monitorId = StableMonitorKey(monitor);
        resolved.monitorName = monitor.friendlyName.empty() ? monitor.deviceName : monitor.friendlyName;
        resolved.region = region;
        resolved.wallpaperId = item->id;
        resolved.source = item->source;
        switch (item->kind) {
        case LibraryWallpaperKind::Scene:
            resolved.kind = ResolvedWallpaperKind::Scene;
            resolved.sceneKey = SceneKeyForLibraryId(item->id);
            break;
        case LibraryWallpaperKind::Image:
            resolved.kind = ResolvedWallpaperKind::Image;
            break;
        case LibraryWallpaperKind::Video:
            resolved.kind = ResolvedWallpaperKind::Video;
            break;
        case LibraryWallpaperKind::Web:
        case LibraryWallpaperKind::Unknown:
            break;
        }
        result.push_back(std::move(resolved));
    }
    return result;
}

bool IndependentLayoutHasVideo(const std::vector<ResolvedMonitorWallpaper>& resolved) noexcept {
    return std::any_of(resolved.begin(), resolved.end(), [](const auto& item) {
        return item.kind == ResolvedWallpaperKind::Video;
    });
}

bool SelfTestIndependentWallpaperResolution() {
    std::error_code ec;
    const fs::path root = fs::temp_directory_path() /
        (L"TuringDesk-IndependentLayout-SelfTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(root, ec);
    if (ec) return false;

    WallpaperLibrary library(root / L"Library");
    WallpaperMonitorAssignments assignments(root / L"assignments.ini");
    std::wstring error;
    bool ok = library.Load(&error) && assignments.Load(&error);
    ok = ok && library.UpsertScene(L"scene-aurora", L"Aurora", &error);
    ok = ok && library.UpsertScene(L"scene-neon", L"Neon", &error);

    const fs::path web = root / L"test.html";
    {
        std::ofstream out(web, std::ios::binary);
        out << "<html></html>";
    }
    const auto webItem = library.ImportFile(web, {}, &error);

    MonitorTopology topology;
    topology.virtualBounds = {0, 0, 5760, 1080};
    topology.primaryBounds = {0, 0, 1920, 1080};
    topology.monitors = {
        {nullptr, {0, 0, 1920, 1080}, true, 96, 96, L"A", L"monitor-a", L"Panel A"},
        {nullptr, {1920, 0, 3840, 1080}, false, 96, 96, L"B", L"monitor-b", L"Panel B"},
        {nullptr, {3840, 0, 5760, 1080}, false, 96, 96, L"C", L"monitor-c", L"Panel C"},
    };

    ok = ok && assignments.Assign(topology.monitors[0], L"scene-neon", &error);
    ok = ok && assignments.Assign(topology.monitors[1], L"missing-item", &error);
    if (webItem) ok = ok && assignments.Assign(topology.monitors[2], webItem->id, &error);

    GlobalWallpaperDescriptor fallback;
    fallback.kind = ResolvedWallpaperKind::Scene;
    fallback.sceneKey = L"aurora";
    const auto resolved = ResolveIndependentWallpapers(topology, assignments, library, fallback);
    ok = ok && resolved.size() == 3;
    if (resolved.size() == 3) {
        ok = ok && !resolved[0].fallback && resolved[0].kind == ResolvedWallpaperKind::Scene && resolved[0].sceneKey == L"neon";
        ok = ok && resolved[1].fallback && resolved[1].sceneKey == L"aurora" && !resolved[1].fallbackReason.empty();
        ok = ok && resolved[2].fallback && !resolved[2].fallbackReason.empty();
    }
    ok = ok && !IndependentLayoutHasVideo(resolved);

    fs::remove_all(root, ec);
    return ok;
}

} // namespace turingdesk::wallpaper

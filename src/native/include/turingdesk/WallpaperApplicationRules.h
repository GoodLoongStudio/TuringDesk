#pragma once

#include <windows.h>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "turingdesk/WallpaperPerformancePolicy.h"

namespace turingdesk::wallpaper {

enum class ApplicationRuleTrigger {
    Foreground,
    Running,
    Fullscreen,
    Maximized,
};

struct WallpaperApplicationRule {
    std::wstring id;
    std::wstring executable;
    std::wstring displayName;
    bool enabled{true};
    ApplicationRuleTrigger trigger{ApplicationRuleTrigger::Foreground};
    PerformanceAction action{PerformanceAction::Pause};
    int priority{100};
};

struct ApplicationRuleMatch {
    bool matched{};
    std::wstring ruleId;
    std::wstring executable;
    std::wstring displayName;
    std::wstring windowTitle;
    ApplicationRuleTrigger trigger{ApplicationRuleTrigger::Foreground};
    PerformanceAction action{PerformanceAction::Normal};
    int priority{};
    HWND window{};
};

class WallpaperApplicationRules {
public:
    WallpaperApplicationRules();
    explicit WallpaperApplicationRules(std::filesystem::path storagePath);

    bool Load(std::wstring* error = nullptr);
    bool Save(std::wstring* error = nullptr) const;

    bool Upsert(WallpaperApplicationRule rule, std::wstring* error = nullptr);
    bool Remove(std::wstring_view id, std::wstring* error = nullptr);
    void Clear();

    const std::vector<WallpaperApplicationRule>& Items() const noexcept;
    std::optional<WallpaperApplicationRule> Find(std::wstring_view id) const;
    const std::filesystem::path& StoragePath() const noexcept;

    ApplicationRuleMatch Evaluate(HWND wallpaperWindow = nullptr, HWND settingsWindow = nullptr) const;

    static std::wstring NormalizeExecutable(std::wstring value);
    static std::wstring MakeId(std::wstring_view prefix = L"app-rule");
    static const wchar_t* TriggerKey(ApplicationRuleTrigger trigger) noexcept;
    static const wchar_t* TriggerDisplayName(ApplicationRuleTrigger trigger) noexcept;
    static ApplicationRuleTrigger ParseTrigger(std::wstring_view value) noexcept;
    static bool SelfTest();

private:
    std::optional<std::size_t> FindIndex(std::wstring_view id) const noexcept;

    std::filesystem::path storagePath_;
    std::vector<WallpaperApplicationRule> items_;
};

// Shared process-local cache used by the performance policy. The backing INI is
// checked at most once per second, so the render loop never reparses it per frame.
ApplicationRuleMatch EvaluateCachedApplicationRules(HWND wallpaperWindow, HWND settingsWindow);
void InvalidateApplicationRuleCache() noexcept;

} // namespace turingdesk::wallpaper

#include "turingdesk/WallpaperAutomationWindow.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cwchar>
#include <iterator>
#include <string>
#include <utility>
#include <vector>

namespace turingdesk::wallpaper {
namespace {

constexpr wchar_t kWindowClass[] = L"TuringDesk.Native.WallpaperAutomation";
constexpr int kEnableId = 6001;
constexpr int kProfileComboId = 6002;
constexpr int kProfileNameId = 6003;
constexpr int kProfileSaveId = 6004;
constexpr int kProfileApplyId = 6005;
constexpr int kProfileDeleteId = 6006;
constexpr int kPlaylistComboId = 6010;
constexpr int kPlaylistNameId = 6011;
constexpr int kPlaylistIntervalId = 6012;
constexpr int kPlaylistOrderId = 6013;
constexpr int kPlaylistEntriesId = 6014;
constexpr int kLibraryComboId = 6015;
constexpr int kPlaylistAddId = 6016;
constexpr int kPlaylistRemoveId = 6017;
constexpr int kPlaylistSaveId = 6018;
constexpr int kPlaylistActivateId = 6019;
constexpr int kPlaylistNextId = 6020;
constexpr int kPlaylistDeleteId = 6021;
constexpr int kScheduleComboId = 6030;
constexpr int kScheduleNameId = 6031;
constexpr int kScheduleEnabledId = 6032;
constexpr int kScheduleStartId = 6033;
constexpr int kScheduleEndId = 6034;
constexpr int kScheduleTargetKindId = 6035;
constexpr int kScheduleTargetId = 6036;
constexpr int kScheduleSaveId = 6037;
constexpr int kScheduleDeleteId = 6038;
constexpr int kDayBaseId = 6040;
constexpr int kCloseId = 6050;
constexpr int kStatusId = 6051;

HMENU ControlId(int id) {
    return reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
}

std::wstring WindowText(HWND hwnd) {
    if (!hwnd) return {};
    const int length = GetWindowTextLengthW(hwnd);
    if (length <= 0) return {};
    std::wstring result(static_cast<std::size_t>(length) + 1, L'\0');
    GetWindowTextW(hwnd, result.data(), length + 1);
    result.resize(static_cast<std::size_t>(length));
    return result;
}

unsigned long long NowUnixSeconds() {
    return static_cast<unsigned long long>(
        std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());
}

const wchar_t* KindLabel(LibraryWallpaperKind kind) {
    switch (kind) {
    case LibraryWallpaperKind::Scene: return L"Scene";
    case LibraryWallpaperKind::Image: return L"图片";
    case LibraryWallpaperKind::Video: return L"视频";
    case LibraryWallpaperKind::Web: return L"Web";
    case LibraryWallpaperKind::Unknown: break;
    }
    return L"未知";
}

std::wstring TimeLabel(int minute) {
    if (minute >= 1440) return L"24:00";
    const int hour = std::clamp(minute / 60, 0, 23);
    const int minutes = std::clamp(minute % 60, 0, 59);
    wchar_t text[16]{};
    swprintf_s(text, L"%02d:%02d", hour, minutes);
    return text;
}

} // namespace

struct WallpaperAutomationWindow::Impl {
    HINSTANCE instance{};
    HWND window{};
    HWND enabledCheck{};
    HWND profileCombo{};
    HWND profileName{};
    HWND playlistCombo{};
    HWND playlistName{};
    HWND intervalCombo{};
    HWND orderCombo{};
    HWND entriesList{};
    HWND libraryCombo{};
    HWND scheduleCombo{};
    HWND scheduleName{};
    HWND scheduleEnabled{};
    HWND startCombo{};
    HWND endCombo{};
    HWND targetKindCombo{};
    HWND targetCombo{};
    std::array<HWND, 7> dayChecks{};
    HWND status{};

    WallpaperAutomationStore* automation{};
    WallpaperLibrary* library{};
    CaptureProfileCallback captureProfile;
    DecisionCallback applyDecision;

    std::vector<std::wstring> profileIds;
    std::vector<std::wstring> playlistIds;
    std::vector<std::wstring> libraryIds;
    std::vector<std::wstring> scheduleIds;
    std::vector<std::wstring> targetIds;
    std::vector<std::wstring> playlistEntryIds;
    std::vector<unsigned> intervalValues;
    std::vector<int> startMinutes;
    std::vector<int> endMinutes;

    ~Impl() {
        if (window && IsWindow(window)) DestroyWindow(window);
    }

    void SetStatus(const std::wstring& text) const {
        if (status) SetWindowTextW(status, text.c_str());
    }

    static int SelectedIndex(HWND combo) {
        if (!combo) return -1;
        const LRESULT selected = SendMessageW(combo, CB_GETCURSEL, 0, 0);
        return selected == CB_ERR ? -1 : static_cast<int>(selected);
    }

    static std::optional<std::wstring> SelectedId(HWND combo, const std::vector<std::wstring>& ids) {
        const int selected = SelectedIndex(combo);
        if (selected < 0 || static_cast<std::size_t>(selected) >= ids.size()) return std::nullopt;
        return ids[static_cast<std::size_t>(selected)];
    }

    void SelectId(HWND combo, const std::vector<std::wstring>& ids, std::wstring_view id) {
        if (!combo || id.empty()) return;
        const std::wstring wanted(id);
        for (std::size_t i = 0; i < ids.size(); ++i) {
            if (_wcsicmp(ids[i].c_str(), wanted.c_str()) == 0) {
                SendMessageW(combo, CB_SETCURSEL, static_cast<WPARAM>(i), 0);
                return;
            }
        }
    }

    void RebuildProfiles() {
        if (!automation || !profileCombo) return;
        const auto previous = SelectedId(profileCombo, profileIds);
        SendMessageW(profileCombo, CB_RESETCONTENT, 0, 0);
        profileIds.clear();
        for (const auto& profile : automation->Profiles()) {
            SendMessageW(profileCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(profile.name.c_str()));
            profileIds.push_back(profile.id);
        }
        if (previous) SelectId(profileCombo, profileIds, *previous);
        if (SelectedIndex(profileCombo) < 0 && !profileIds.empty()) SendMessageW(profileCombo, CB_SETCURSEL, 0, 0);
        LoadSelectedProfile();
    }

    void LoadSelectedProfile() {
        if (!automation || !profileName) return;
        const auto id = SelectedId(profileCombo, profileIds);
        const auto profile = id ? automation->FindProfile(*id) : std::nullopt;
        SetWindowTextW(profileName, profile ? profile->name.c_str() : L"");
    }

    void SaveCurrentProfile() {
        if (!automation || !captureProfile) return;
        std::wstring name = WindowText(profileName);
        if (name.empty()) name = L"桌面方案";
        auto profile = captureProfile(name);
        if (!profile) {
            SetStatus(L"当前壁纸无法保存为 Profile；请先应用一个壁纸库项目。");
            return;
        }
        const auto selected = SelectedId(profileCombo, profileIds);
        if (selected) profile->id = *selected;
        if (profile->id.empty()) profile->id = WallpaperAutomationStore::MakeId(L"profile");
        profile->name = name;
        std::wstring error;
        if (!automation->UpsertProfile(*profile, &error)) {
            SetStatus(error.empty() ? L"Profile 保存失败。" : error);
            return;
        }
        RebuildProfiles();
        SelectId(profileCombo, profileIds, profile->id);
        LoadSelectedProfile();
        RebuildTargets();
        SetStatus(L"已保存 Profile：" + name);
    }

    void ApplySelectedProfile() {
        const auto id = SelectedId(profileCombo, profileIds);
        if (!id || !applyDecision) return;
        AutomationDecision decision;
        decision.kind = AutomationDecisionKind::ApplyProfile;
        decision.targetId = *id;
        decision.sourceId = L"manual-profile";
        decision.reason = L"手动应用 Profile";
        applyDecision(decision);
        SetStatus(L"已请求应用 Profile。");
    }

    void DeleteSelectedProfile() {
        if (!automation) return;
        const auto id = SelectedId(profileCombo, profileIds);
        if (!id) return;
        std::wstring error;
        if (!automation->RemoveProfile(*id, &error)) {
            SetStatus(error.empty() ? L"Profile 删除失败。" : error);
            return;
        }
        RebuildProfiles();
        RebuildSchedules();
        SetStatus(L"Profile 已删除；引用它的 Schedule 也已清理。");
    }

    void RebuildLibraryChoices() {
        if (!library || !libraryCombo) return;
        const auto previous = SelectedId(libraryCombo, libraryIds);
        SendMessageW(libraryCombo, CB_RESETCONTENT, 0, 0);
        libraryIds.clear();
        for (const auto& item : library->Items()) {
            if (item.kind == LibraryWallpaperKind::Web || item.kind == LibraryWallpaperKind::Unknown) continue;
            std::wstring label = L"[" + std::wstring(KindLabel(item.kind)) + L"] " + item.title;
            SendMessageW(libraryCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
            libraryIds.push_back(item.id);
        }
        if (previous) SelectId(libraryCombo, libraryIds, *previous);
        if (SelectedIndex(libraryCombo) < 0 && !libraryIds.empty()) SendMessageW(libraryCombo, CB_SETCURSEL, 0, 0);
    }

    void RebuildPlaylistEntries() {
        if (!entriesList) return;
        SendMessageW(entriesList, LB_RESETCONTENT, 0, 0);
        for (const auto& id : playlistEntryIds) {
            const auto item = library ? library->Find(id) : std::nullopt;
            const std::wstring label = item ? (L"[" + std::wstring(KindLabel(item->kind)) + L"] " + item->title)
                                             : (L"[缺失] " + id);
            SendMessageW(entriesList, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
        }
        if (!playlistEntryIds.empty()) SendMessageW(entriesList, LB_SETCURSEL, 0, 0);
    }

    void RebuildPlaylists() {
        if (!automation || !playlistCombo) return;
        const auto previous = SelectedId(playlistCombo, playlistIds);
        SendMessageW(playlistCombo, CB_RESETCONTENT, 0, 0);
        playlistIds.clear();
        for (const auto& playlist : automation->Playlists()) {
            std::wstring label = playlist.name;
            if (_wcsicmp(playlist.id.c_str(), automation->ActivePlaylistId().c_str()) == 0) label += L" · 当前";
            SendMessageW(playlistCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
            playlistIds.push_back(playlist.id);
        }
        if (previous) SelectId(playlistCombo, playlistIds, *previous);
        if (SelectedIndex(playlistCombo) < 0 && !playlistIds.empty()) SendMessageW(playlistCombo, CB_SETCURSEL, 0, 0);
        LoadSelectedPlaylist();
        RebuildTargets();
    }

    void LoadSelectedPlaylist() {
        if (!automation) return;
        const auto id = SelectedId(playlistCombo, playlistIds);
        const auto playlist = id ? automation->FindPlaylist(*id) : std::nullopt;
        SetWindowTextW(playlistName, playlist ? playlist->name.c_str() : L"");
        playlistEntryIds = playlist ? playlist->wallpaperIds : std::vector<std::wstring>{};
        RebuildPlaylistEntries();

        int intervalIndex = 2;
        if (playlist) {
            unsigned bestDistance = ~0U;
            for (std::size_t i = 0; i < intervalValues.size(); ++i) {
                const unsigned value = intervalValues[i];
                const unsigned distance = value > playlist->intervalSeconds ? value - playlist->intervalSeconds : playlist->intervalSeconds - value;
                if (distance < bestDistance) {
                    bestDistance = distance;
                    intervalIndex = static_cast<int>(i);
                }
            }
        }
        if (intervalCombo_) SendMessageW(intervalCombo, CB_SETCURSEL, intervalIndex, 0);
        if (orderCombo_) SendMessageW(orderCombo, CB_SETCURSEL,
                                      playlist && playlist->order == PlaylistOrder::Random ? 1 : 0, 0);
    }

    void AddPlaylistEntry() {
        const auto id = SelectedId(libraryCombo, libraryIds);
        if (!id) return;
        playlistEntryIds.push_back(*id);
        RebuildPlaylistEntries();
        SendMessageW(entriesList, LB_SETCURSEL, static_cast<WPARAM>(playlistEntryIds.size() - 1), 0);
    }

    void RemovePlaylistEntry() {
        if (!entriesList) return;
        const LRESULT selected = SendMessageW(entriesList, LB_GETCURSEL, 0, 0);
        if (selected == LB_ERR || selected < 0 || static_cast<std::size_t>(selected) >= playlistEntryIds.size()) return;
        playlistEntryIds.erase(playlistEntryIds.begin() + static_cast<std::ptrdiff_t>(selected));
        RebuildPlaylistEntries();
    }

    void SavePlaylist() {
        if (!automation) return;
        WallpaperPlaylist playlist;
        if (const auto id = SelectedId(playlistCombo, playlistIds)) {
            if (const auto existing = automation->FindPlaylist(*id)) playlist = *existing;
            playlist.id = *id;
        } else {
            playlist.id = WallpaperAutomationStore::MakeId(L"playlist");
        }
        playlist.name = WindowText(playlistName);
        if (playlist.name.empty()) playlist.name = L"播放列表";
        playlist.wallpaperIds = playlistEntryIds;
        const int intervalIndex = SelectedIndex(intervalCombo);
        if (intervalIndex >= 0 && static_cast<std::size_t>(intervalIndex) < intervalValues.size())
            playlist.intervalSeconds = intervalValues[static_cast<std::size_t>(intervalIndex)];
        playlist.order = SelectedIndex(orderCombo) == 1 ? PlaylistOrder::Random : PlaylistOrder::Sequential;
        playlist.enabled = true;
        std::wstring error;
        if (!automation->UpsertPlaylist(playlist, &error)) {
            SetStatus(error.empty() ? L"Playlist 保存失败。" : error);
            return;
        }
        RebuildPlaylists();
        SelectId(playlistCombo, playlistIds, playlist.id);
        LoadSelectedPlaylist();
        SetStatus(L"已保存 Playlist：" + playlist.name);
    }

    void ActivatePlaylist() {
        if (!automation) return;
        const auto id = SelectedId(playlistCombo, playlistIds);
        if (!id) return;
        std::wstring error;
        if (!automation->SetActivePlaylist(*id, &error)) {
            SetStatus(error.empty() ? L"激活 Playlist 失败。" : error);
            return;
        }
        RebuildPlaylists();
        SelectId(playlistCombo, playlistIds, *id);
        SetStatus(L"已设为默认自动轮换 Playlist。");
    }

    void NextPlaylist() {
        if (!automation || !applyDecision) return;
        const auto id = SelectedId(playlistCombo, playlistIds);
        if (!id) return;
        const auto decision = automation->ForceNextPlaylist(*id, NowUnixSeconds());
        if (decision.kind == AutomationDecisionKind::None) {
            SetStatus(L"Playlist 没有可轮换的项目。");
            return;
        }
        applyDecision(decision);
        Refresh();
        SetStatus(L"已切换到 Playlist 下一项。");
    }

    void DeletePlaylist() {
        if (!automation) return;
        const auto id = SelectedId(playlistCombo, playlistIds);
        if (!id) return;
        std::wstring error;
        if (!automation->RemovePlaylist(*id, &error)) {
            SetStatus(error.empty() ? L"Playlist 删除失败。" : error);
            return;
        }
        RebuildPlaylists();
        RebuildSchedules();
        SetStatus(L"Playlist 已删除；引用它的 Schedule 也已清理。");
    }

    void RebuildTargets() {
        if (!automation || !targetCombo) return;
        const auto previous = SelectedId(targetCombo, targetIds);
        SendMessageW(targetCombo, CB_RESETCONTENT, 0, 0);
        targetIds.clear();
        const bool playlistTarget = SelectedIndex(targetKindCombo) == 1;
        if (playlistTarget) {
            for (const auto& playlist : automation->Playlists()) {
                SendMessageW(targetCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(playlist.name.c_str()));
                targetIds.push_back(playlist.id);
            }
        } else {
            for (const auto& profile : automation->Profiles()) {
                SendMessageW(targetCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(profile.name.c_str()));
                targetIds.push_back(profile.id);
            }
        }
        if (previous) SelectId(targetCombo, targetIds, *previous);
        if (SelectedIndex(targetCombo) < 0 && !targetIds.empty()) SendMessageW(targetCombo, CB_SETCURSEL, 0, 0);
    }

    void RebuildSchedules() {
        if (!automation || !scheduleCombo) return;
        const auto previous = SelectedId(scheduleCombo, scheduleIds);
        SendMessageW(scheduleCombo, CB_RESETCONTENT, 0, 0);
        scheduleIds.clear();
        for (const auto& schedule : automation->Schedules()) {
            std::wstring label = schedule.enabled ? L"● " : L"○ ";
            label += schedule.name;
            SendMessageW(scheduleCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
            scheduleIds.push_back(schedule.id);
        }
        if (previous) SelectId(scheduleCombo, scheduleIds, *previous);
        if (SelectedIndex(scheduleCombo) < 0 && !scheduleIds.empty()) SendMessageW(scheduleCombo, CB_SETCURSEL, 0, 0);
        LoadSelectedSchedule();
    }

    void LoadSelectedSchedule() {
        if (!automation) return;
        const auto id = SelectedId(scheduleCombo, scheduleIds);
        const auto schedule = id ? automation->FindSchedule(*id) : std::nullopt;
        SetWindowTextW(scheduleName, schedule ? schedule->name.c_str() : L"");
        if (scheduleEnabled) SendMessageW(scheduleEnabled, BM_SETCHECK,
                                           !schedule || schedule->enabled ? BST_CHECKED : BST_UNCHECKED, 0);
        for (std::size_t day = 0; day < dayChecks.size(); ++day) {
            const bool checked = !schedule || (schedule->dayMask & (1U << day)) != 0;
            SendMessageW(dayChecks[day], BM_SETCHECK, checked ? BST_CHECKED : BST_UNCHECKED, 0);
        }
        if (startCombo) SendMessageW(startCombo, CB_SETCURSEL,
                                     schedule ? std::clamp(schedule->startMinute / 30, 0, 47) : 16, 0);
        int endIndex = 36;
        if (schedule) endIndex = schedule->endMinute >= 1440 ? 48 : std::clamp(schedule->endMinute / 30, 0, 47);
        if (endCombo) SendMessageW(endCombo, CB_SETCURSEL, endIndex, 0);
        if (targetKindCombo) SendMessageW(targetKindCombo, CB_SETCURSEL,
            schedule && schedule->targetKind == AutomationTargetKind::Playlist ? 1 : 0, 0);
        RebuildTargets();
        if (schedule) SelectId(targetCombo, targetIds, schedule->targetId);
    }

    void SaveSchedule() {
        if (!automation) return;
        WallpaperSchedule schedule;
        if (const auto id = SelectedId(scheduleCombo, scheduleIds)) {
            if (const auto existing = automation->FindSchedule(*id)) schedule = *existing;
            schedule.id = *id;
        } else {
            schedule.id = WallpaperAutomationStore::MakeId(L"schedule");
        }
        schedule.name = WindowText(scheduleName);
        if (schedule.name.empty()) schedule.name = L"定时规则";
        schedule.enabled = !scheduleEnabled || SendMessageW(scheduleEnabled, BM_GETCHECK, 0, 0) == BST_CHECKED;
        schedule.dayMask = 0;
        for (std::size_t day = 0; day < dayChecks.size(); ++day)
            if (SendMessageW(dayChecks[day], BM_GETCHECK, 0, 0) == BST_CHECKED) schedule.dayMask |= 1U << day;
        const int startIndex = SelectedIndex(startCombo);
        const int endIndex = SelectedIndex(endCombo);
        if (startIndex >= 0 && static_cast<std::size_t>(startIndex) < startMinutes.size()) schedule.startMinute = startMinutes[static_cast<std::size_t>(startIndex)];
        if (endIndex >= 0 && static_cast<std::size_t>(endIndex) < endMinutes.size()) schedule.endMinute = endMinutes[static_cast<std::size_t>(endIndex)];
        schedule.targetKind = SelectedIndex(targetKindCombo) == 1 ? AutomationTargetKind::Playlist : AutomationTargetKind::Profile;
        const auto targetId = SelectedId(targetCombo, targetIds);
        schedule.targetId = targetId.value_or(L"");
        std::wstring error;
        if (!automation->UpsertSchedule(schedule, &error)) {
            SetStatus(error.empty() ? L"Schedule 保存失败。" : error);
            return;
        }
        RebuildSchedules();
        SelectId(scheduleCombo, scheduleIds, schedule.id);
        LoadSelectedSchedule();
        SetStatus(L"已保存 Schedule：" + schedule.name);
    }

    void DeleteSchedule() {
        if (!automation) return;
        const auto id = SelectedId(scheduleCombo, scheduleIds);
        if (!id) return;
        std::wstring error;
        if (!automation->RemoveSchedule(*id, &error)) {
            SetStatus(error.empty() ? L"Schedule 删除失败。" : error);
            return;
        }
        RebuildSchedules();
        SetStatus(L"Schedule 已删除。");
    }

    void ToggleAutomation() {
        if (!automation || !enabledCheck) return;
        const bool enabled = SendMessageW(enabledCheck, BM_GETCHECK, 0, 0) == BST_CHECKED;
        std::wstring error;
        if (!automation->SetEnabled(enabled, &error)) {
            SetStatus(error.empty() ? L"自动化状态保存失败。" : error);
            return;
        }
        SetStatus(enabled ? L"自动化已启用。" : L"自动化已暂停。当前壁纸不会被计划任务切换。");
    }

    void RefreshAll() {
        if (!automation || !library) return;
        if (enabledCheck) SendMessageW(enabledCheck, BM_SETCHECK, automation->Enabled() ? BST_CHECKED : BST_UNCHECKED, 0);
        RebuildLibraryChoices();
        RebuildProfiles();
        RebuildPlaylists();
        RebuildSchedules();
        std::wstring text = L"Profile " + std::to_wstring(automation->Profiles().size()) +
                            L" · Playlist " + std::to_wstring(automation->Playlists().size()) +
                            L" · Schedule " + std::to_wstring(automation->Schedules().size());
        if (!automation->LastMatchedScheduleId().empty()) text += L" · 当前 Schedule 活跃";
        SetStatus(text);
    }

    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<Impl*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<Impl*>(create->lpCreateParams);
            if (self) {
                self->window = hwnd;
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
            }
        }
        if (!self) return DefWindowProcW(hwnd, message, wParam, lParam);

        if (message == WM_COMMAND) {
            const int id = LOWORD(wParam);
            const int notification = HIWORD(wParam);
            if (id == kEnableId && notification == BN_CLICKED) self->ToggleAutomation();
            else if (id == kProfileComboId && notification == CBN_SELCHANGE) self->LoadSelectedProfile();
            else if (id == kProfileSaveId && notification == BN_CLICKED) self->SaveCurrentProfile();
            else if (id == kProfileApplyId && notification == BN_CLICKED) self->ApplySelectedProfile();
            else if (id == kProfileDeleteId && notification == BN_CLICKED) self->DeleteSelectedProfile();
            else if (id == kPlaylistComboId && notification == CBN_SELCHANGE) self->LoadSelectedPlaylist();
            else if (id == kPlaylistAddId && notification == BN_CLICKED) self->AddPlaylistEntry();
            else if (id == kPlaylistRemoveId && notification == BN_CLICKED) self->RemovePlaylistEntry();
            else if (id == kPlaylistSaveId && notification == BN_CLICKED) self->SavePlaylist();
            else if (id == kPlaylistActivateId && notification == BN_CLICKED) self->ActivatePlaylist();
            else if (id == kPlaylistNextId && notification == BN_CLICKED) self->NextPlaylist();
            else if (id == kPlaylistDeleteId && notification == BN_CLICKED) self->DeletePlaylist();
            else if (id == kScheduleComboId && notification == CBN_SELCHANGE) self->LoadSelectedSchedule();
            else if (id == kScheduleTargetKindId && notification == CBN_SELCHANGE) self->RebuildTargets();
            else if (id == kScheduleSaveId && notification == BN_CLICKED) self->SaveSchedule();
            else if (id == kScheduleDeleteId && notification == BN_CLICKED) self->DeleteSchedule();
            else if (id == kCloseId && notification == BN_CLICKED) ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        if (message == WM_CLOSE) {
            ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        if (message == WM_DESTROY) {
            self->window = nullptr;
            self->enabledCheck = nullptr;
            self->profileCombo = nullptr;
            self->profileName = nullptr;
            self->playlistCombo = nullptr;
            self->playlistName = nullptr;
            self->intervalCombo = nullptr;
            self->orderCombo = nullptr;
            self->entriesList = nullptr;
            self->libraryCombo = nullptr;
            self->scheduleCombo = nullptr;
            self->scheduleName = nullptr;
            self->scheduleEnabled = nullptr;
            self->startCombo = nullptr;
            self->endCombo = nullptr;
            self->targetKindCombo = nullptr;
            self->targetCombo = nullptr;
            self->dayChecks.fill(nullptr);
            self->status = nullptr;
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    bool CreateUi() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance;
        wc.lpfnWndProc = &Impl::WndProc;
        wc.lpszClassName = kWindowClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        window = CreateWindowExW(WS_EX_TOOLWINDOW, kWindowClass, L"TuringDesk 壁纸自动化",
                                 WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                                 CW_USEDEFAULT, CW_USEDEFAULT, 960, 790,
                                 nullptr, nullptr, instance, this);
        if (!window) return false;

        const HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto controlFont = [&](HWND control) {
            if (control) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };
        auto label = [&](const wchar_t* text, int x, int y, int w, int h) {
            return controlFont(CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                                               x, y, w, h, window, nullptr, instance, nullptr));
        };
        auto button = [&](const wchar_t* text, int id, int x, int y, int w, int h) {
            return controlFont(CreateWindowExW(0, L"BUTTON", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                               x, y, w, h, window, ControlId(id), instance, nullptr));
        };
        auto combo = [&](int id, int x, int y, int w, int h) {
            return controlFont(CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
                                               x, y, w, h, window, ControlId(id), instance, nullptr));
        };
        auto edit = [&](int id, int x, int y, int w, int h) {
            return controlFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                               x, y, w, h, window, ControlId(id), instance, nullptr));
        };

        label(L"壁纸自动化", 20, 15, 180, 28);
        enabledCheck = controlFont(CreateWindowExW(0, L"BUTTON", L"启用 Playlist / Schedule 自动切换",
                                                   WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                                                   220, 15, 300, 26, window, ControlId(kEnableId), instance, nullptr));

        label(L"Profile · 保存完整桌面配置", 20, 58, 280, 24);
        profileCombo = combo(kProfileComboId, 20, 86, 250, 150);
        profileName = edit(kProfileNameId, 280, 86, 220, 28);
        button(L"保存当前", kProfileSaveId, 510, 84, 100, 30);
        button(L"应用", kProfileApplyId, 620, 84, 80, 30);
        button(L"删除", kProfileDeleteId, 710, 84, 80, 30);
        label(L"Profile 保存壁纸、布局、缩放、帧率、性能规则和视频设置。", 20, 120, 780, 22);

        label(L"Playlist · 自动轮换", 20, 158, 220, 24);
        playlistCombo = combo(kPlaylistComboId, 20, 186, 250, 150);
        playlistName = edit(kPlaylistNameId, 280, 186, 190, 28);
        intervalCombo = combo(kPlaylistIntervalId, 480, 186, 130, 180);
        intervalValues = {30, 60, 300, 900, 1800, 3600, 21600, 86400};
        for (const wchar_t* text : {L"30 秒", L"1 分钟", L"5 分钟", L"15 分钟", L"30 分钟", L"1 小时", L"6 小时", L"24 小时"})
            SendMessageW(intervalCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(text));
        orderCombo = combo(kPlaylistOrderId, 620, 186, 120, 100);
        SendMessageW(orderCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"顺序"));
        SendMessageW(orderCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"随机"));
        button(L"保存", kPlaylistSaveId, 750, 184, 80, 30);
        button(L"删除", kPlaylistDeleteId, 840, 184, 70, 30);

        entriesList = controlFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | LBS_NOTIFY | LBS_NOINTEGRALHEIGHT,
            20, 224, 590, 135, window, ControlId(kPlaylistEntriesId), instance, nullptr));
        libraryCombo = combo(kLibraryComboId, 620, 224, 290, 180);
        button(L"加入 →", kPlaylistAddId, 620, 260, 90, 30);
        button(L"移除", kPlaylistRemoveId, 720, 260, 80, 30);
        button(L"设为默认", kPlaylistActivateId, 620, 306, 100, 32);
        button(L"下一张", kPlaylistNextId, 730, 306, 90, 32);

        label(L"Schedule · 星期 + 时间段", 20, 382, 250, 24);
        scheduleCombo = combo(kScheduleComboId, 20, 410, 250, 160);
        scheduleName = edit(kScheduleNameId, 280, 410, 220, 28);
        scheduleEnabled = controlFont(CreateWindowExW(0, L"BUTTON", L"启用规则", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                                                       510, 410, 100, 26, window, ControlId(kScheduleEnabledId), instance, nullptr));
        SendMessageW(scheduleEnabled, BM_SETCHECK, BST_CHECKED, 0);

        const std::array<const wchar_t*, 7> dayNames = {L"日", L"一", L"二", L"三", L"四", L"五", L"六"};
        for (std::size_t day = 0; day < dayNames.size(); ++day) {
            dayChecks[day] = controlFont(CreateWindowExW(0, L"BUTTON", dayNames[day],
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                20 + static_cast<int>(day) * 52, 450, 48, 24, window,
                ControlId(kDayBaseId + static_cast<int>(day)), instance, nullptr));
            SendMessageW(dayChecks[day], BM_SETCHECK, BST_CHECKED, 0);
        }
        label(L"开始", 405, 452, 45, 22);
        startCombo = combo(kScheduleStartId, 452, 448, 105, 220);
        for (int minute = 0; minute < 1440; minute += 30) {
            const std::wstring text = TimeLabel(minute);
            SendMessageW(startCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(text.c_str()));
            startMinutes.push_back(minute);
        }
        label(L"结束", 570, 452, 45, 22);
        endCombo = combo(kScheduleEndId, 617, 448, 105, 220);
        for (int minute = 0; minute <= 1440; minute += 30) {
            const std::wstring text = TimeLabel(minute);
            SendMessageW(endCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(text.c_str()));
            endMinutes.push_back(minute);
        }
        SendMessageW(startCombo, CB_SETCURSEL, 16, 0);
        SendMessageW(endCombo, CB_SETCURSEL, 36, 0);

        label(L"目标", 20, 496, 45, 22);
        targetKindCombo = combo(kScheduleTargetKindId, 70, 492, 120, 100);
        SendMessageW(targetKindCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Profile"));
        SendMessageW(targetKindCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Playlist"));
        SendMessageW(targetKindCombo, CB_SETCURSEL, 0, 0);
        targetCombo = combo(kScheduleTargetId, 200, 492, 300, 180);
        button(L"保存规则", kScheduleSaveId, 510, 490, 100, 30);
        button(L"删除规则", kScheduleDeleteId, 620, 490, 100, 30);
        label(L"跨午夜示例：22:00 → 02:00；凌晨部分自动继承前一天的星期规则。", 20, 532, 760, 24);

        status = controlFont(CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE,
                                              20, 585, 890, 70, window, ControlId(kStatusId), instance, nullptr));
        button(L"关闭", kCloseId, 820, 680, 90, 34);
        return true;
    }
};

WallpaperAutomationWindow::WallpaperAutomationWindow() : impl_(std::make_unique<Impl>()) {}
WallpaperAutomationWindow::~WallpaperAutomationWindow() = default;

bool WallpaperAutomationWindow::Show(HINSTANCE instance,
                                     WallpaperAutomationStore* automation,
                                     WallpaperLibrary* library,
                                     CaptureProfileCallback captureProfile,
                                     DecisionCallback applyDecision) {
    if (!impl_ || !automation || !library) return false;
    impl_->instance = instance;
    impl_->automation = automation;
    impl_->library = library;
    impl_->captureProfile = std::move(captureProfile);
    impl_->applyDecision = std::move(applyDecision);
    if (!impl_->window || !IsWindow(impl_->window)) {
        if (!impl_->CreateUi()) return false;
    }
    impl_->RefreshAll();
    ShowWindow(impl_->window, SW_RESTORE);
    SetForegroundWindow(impl_->window);
    return true;
}

void WallpaperAutomationWindow::Close() {
    if (impl_ && impl_->window && IsWindow(impl_->window)) DestroyWindow(impl_->window);
}

void WallpaperAutomationWindow::Refresh() {
    if (impl_ && impl_->window && IsWindow(impl_->window)) impl_->RefreshAll();
}

bool WallpaperAutomationWindow::Visible() const noexcept {
    return impl_ && impl_->window && IsWindow(impl_->window) && IsWindowVisible(impl_->window);
}

HWND WallpaperAutomationWindow::Window() const noexcept {
    return impl_ ? impl_->window : nullptr;
}

} // namespace turingdesk::wallpaper

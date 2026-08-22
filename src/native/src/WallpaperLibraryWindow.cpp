#include "turingdesk/WallpaperLibraryWindow.h"
#include "turingdesk/WallpaperWebRuntimeCoordinator.h"
#include "turingdesk/WebWallpaperHost.h"

#include <commctrl.h>
#include <commdlg.h>

#include <filesystem>
#include <iterator>
#include <optional>
#include <string>
#include <system_error>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

constexpr wchar_t kWindowClass[] = L"TuringDesk.Native.WallpaperLibrary";
constexpr int kSearchId = 5101;
constexpr int kListId = 5102;
constexpr int kAllId = 5103;
constexpr int kFavoritesId = 5104;
constexpr int kRecentId = 5105;
constexpr int kImportId = 5106;
constexpr int kManagedCopyId = 5107;
constexpr int kFavoriteId = 5108;
constexpr int kRemoveId = 5109;
constexpr int kApplyId = 5110;
constexpr int kCloseId = 5111;
constexpr int kStatusId = 5112;
constexpr int kTargetComboId = 5113;
constexpr int kWebUrlId = 5114;
constexpr int kImportWebUrlId = 5115;

enum class FilterMode {
    All,
    Favorites,
    Recent,
};

HMENU ControlId(int id) {
    return reinterpret_cast<HMENU>(static_cast<INT_PTR>(id));
}

const wchar_t* KindLabel(LibraryWallpaperKind kind) {
    switch (kind) {
    case LibraryWallpaperKind::Image: return L"图片";
    case LibraryWallpaperKind::Video: return L"视频";
    case LibraryWallpaperKind::Web: return L"Web";
    case LibraryWallpaperKind::Scene: return L"Scene";
    case LibraryWallpaperKind::Unknown: break;
    }
    return L"未知";
}

std::wstring WindowText(HWND hwnd) {
    if (!hwnd) return {};
    const int length = GetWindowTextLengthW(hwnd);
    if (length <= 0) return {};
    std::wstring value(static_cast<std::size_t>(length) + 1, L'\0');
    GetWindowTextW(hwnd, value.data(), length + 1);
    value.resize(static_cast<std::size_t>(length));
    return value;
}

} // namespace

struct WallpaperLibraryWindow::Impl {
    HINSTANCE instance{};
    HWND window{};
    HWND search{};
    HWND list{};
    HWND targetCombo{};
    HWND managedCopy{};
    HWND webUrl{};
    HWND favoriteButton{};
    HWND status{};
    WallpaperLibrary* library{};
    ApplyCallback applyCallback;
    FilterMode filter{FilterMode::All};
    std::vector<std::wstring> visibleIds;
    std::vector<WallpaperLibraryTarget> targets;
    std::vector<std::wstring> targetIds;

    ~Impl() {
        if (window && IsWindow(window)) DestroyWindow(window);
    }

    void SetStatus(const std::wstring& text) const {
        if (status) SetWindowTextW(status, text.c_str());
    }

    std::optional<WallpaperLibraryItem> Selected() const {
        if (!library || !list) return std::nullopt;
        const LRESULT selected = SendMessageW(list, LB_GETCURSEL, 0, 0);
        if (selected == LB_ERR || selected < 0 || static_cast<std::size_t>(selected) >= visibleIds.size())
            return std::nullopt;
        return library->Find(visibleIds[static_cast<std::size_t>(selected)]);
    }

    std::wstring SelectedTargetId() const {
        if (!targetCombo) return {};
        const LRESULT selected = SendMessageW(targetCombo, CB_GETCURSEL, 0, 0);
        if (selected == CB_ERR || selected < 0 || static_cast<std::size_t>(selected) >= targetIds.size()) return {};
        return targetIds[static_cast<std::size_t>(selected)];
    }

    std::wstring TargetDisplayName(std::wstring_view id) const {
        if (id.empty()) return L"全局壁纸";
        for (const auto& target : targets) {
            if (_wcsicmp(target.monitorId.c_str(), std::wstring(id).c_str()) == 0)
                return target.displayName.empty() ? L"显示器" : target.displayName;
        }
        return L"显示器";
    }

    bool SourceMissing(const WallpaperLibraryItem& item) const {
        if (item.kind == LibraryWallpaperKind::Scene) return false;
        if (item.kind == LibraryWallpaperKind::Web)
            return !WebWallpaperProcessSet::IsSupportedSource(item.source.wstring());
        if (item.source.empty()) return true;
        std::error_code ec;
        return !fs::exists(item.source, ec) || !fs::is_regular_file(item.source, ec);
    }

    void RebuildTargets() {
        if (!targetCombo) return;
        const std::wstring previous = SelectedTargetId();
        SendMessageW(targetCombo, CB_RESETCONTENT, 0, 0);
        targetIds.clear();
        SendMessageW(targetCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"全局 / 当前布局"));
        targetIds.emplace_back();
        int selectedIndex = 0;
        for (const auto& target : targets) {
            std::wstring label = target.primary ? L"主屏 · " : L"显示器 · ";
            label += target.displayName.empty() ? L"未命名显示器" : target.displayName;
            SendMessageW(targetCombo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
            targetIds.push_back(target.monitorId);
            if (!previous.empty() && _wcsicmp(previous.c_str(), target.monitorId.c_str()) == 0)
                selectedIndex = static_cast<int>(targetIds.size() - 1);
        }
        SendMessageW(targetCombo, CB_SETCURSEL, selectedIndex, 0);
    }

    void SelectVisibleId(std::wstring_view id) {
        if (!list || id.empty()) return;
        const std::wstring wanted(id);
        for (std::size_t i = 0; i < visibleIds.size(); ++i) {
            if (_wcsicmp(visibleIds[i].c_str(), wanted.c_str()) == 0) {
                SendMessageW(list, LB_SETCURSEL, static_cast<WPARAM>(i), 0);
                UpdateSelectionActions();
                return;
            }
        }
    }

    void RebuildList() {
        if (!library || !list) return;
        const auto oldSelected = Selected();
        const std::wstring previous = oldSelected ? oldSelected->id : L"";
        SendMessageW(list, LB_RESETCONTENT, 0, 0);
        visibleIds.clear();

        const std::wstring query = WindowText(search);
        std::vector<WallpaperLibraryItem> items;
        if (filter == FilterMode::Favorites) items = library->Favorites();
        else if (filter == FilterMode::Recent) items = library->RecentlyUsed(50);
        else items = library->Search(query);

        if (filter != FilterMode::All && !query.empty()) {
            const auto searched = library->Search(query);
            std::vector<WallpaperLibraryItem> filtered;
            for (const auto& item : items) {
                for (const auto& match : searched) {
                    if (_wcsicmp(item.id.c_str(), match.id.c_str()) == 0) {
                        filtered.push_back(item);
                        break;
                    }
                }
            }
            items = std::move(filtered);
        }

        int restoreIndex = -1;
        for (const auto& item : items) {
            std::wstring label = item.favorite ? L"★ " : L"☆ ";
            label += L"[" + std::wstring(KindLabel(item.kind)) + L"] " + item.title;
            if (item.managedCopy) label += L" · 托管副本";
            if (SourceMissing(item)) label += item.kind == LibraryWallpaperKind::Web ? L" · Web 源无效" : L" · 源文件缺失";
            SendMessageW(list, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label.c_str()));
            if (!previous.empty() && _wcsicmp(previous.c_str(), item.id.c_str()) == 0)
                restoreIndex = static_cast<int>(visibleIds.size());
            visibleIds.push_back(item.id);
        }
        if (restoreIndex >= 0) SendMessageW(list, LB_SETCURSEL, restoreIndex, 0);
        else if (!visibleIds.empty()) SendMessageW(list, LB_SETCURSEL, 0, 0);

        std::wstring text = L"壁纸库：" + std::to_wstring(library->Items().size()) + L" 项";
        if (filter == FilterMode::Favorites) text += L" · 收藏";
        else if (filter == FilterMode::Recent) text += L" · 最近使用";
        else text += L" · 全部";
        SetStatus(text);
        UpdateSelectionActions();
    }

    void UpdateSelectionActions() {
        const auto selected = Selected();
        if (favoriteButton)
            SetWindowTextW(favoriteButton, selected && selected->favorite ? L"取消收藏" : L"收藏");
    }

    void FinishImport(const WallpaperLibraryItem& imported, const std::wstring& message) {
        filter = FilterMode::All;
        if (search) SetWindowTextW(search, L"");
        RebuildList();
        SelectVisibleId(imported.id);
        SetStatus(message);
    }

    void ImportFile() {
        if (!library) return;
        wchar_t path[32768]{};
        OPENFILENAMEW dialog{};
        dialog.lStructSize = sizeof(dialog);
        dialog.hwndOwner = window;
        dialog.lpstrFile = path;
        dialog.nMaxFile = static_cast<DWORD>(std::size(path));
        dialog.lpstrFilter =
            L"壁纸文件\0*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.mp4;*.mov;*.wmv;*.m4v;*.avi;*.mkv;*.webm;*.html;*.htm\0"
            L"图片\0*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff\0"
            L"视频\0*.mp4;*.mov;*.wmv;*.m4v;*.avi;*.mkv;*.webm\0"
            L"本地 Web\0*.html;*.htm\0所有文件\0*.*\0";
        dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
        if (!GetOpenFileNameW(&dialog)) return;

        WallpaperImportOptions options;
        options.managedCopy = managedCopy && SendMessageW(managedCopy, BM_GETCHECK, 0, 0) == BST_CHECKED;
        std::wstring error;
        const auto imported = library->ImportFile(path, options, &error);
        if (!imported) {
            SetStatus(error.empty() ? L"导入失败。" : error);
            return;
        }
        FinishImport(*imported,
                     L"已导入：" + imported->title +
                     (imported->thumbnail.empty() ? L" · 未生成缩略图" : L" · 缩略图已生成"));
    }

    void ImportWebUrl() {
        if (!library || !webUrl) return;
        const std::wstring url = WindowText(webUrl);
        if (url.empty()) {
            SetStatus(L"请粘贴 HTTPS Web 壁纸地址。");
            return;
        }
        std::wstring error;
        const auto imported = library->ImportWebUrl(url, {}, &error);
        if (!imported) {
            SetStatus(error.empty() ? L"Web URL 导入失败。" : error);
            return;
        }
        SetWindowTextW(webUrl, L"");
        FinishImport(*imported, L"已导入 HTTPS Web 壁纸：" + imported->title);
    }

    void ToggleFavorite() {
        if (!library) return;
        const auto selected = Selected();
        if (!selected) return;
        std::wstring error;
        if (!library->SetFavorite(selected->id, !selected->favorite, &error)) {
            SetStatus(error.empty() ? L"收藏状态保存失败。" : error);
            return;
        }
        RebuildList();
    }

    void RemoveSelected() {
        if (!library) return;
        const auto selected = Selected();
        if (!selected) return;
        if (selected->kind == LibraryWallpaperKind::Scene) {
            SetStatus(L"内置 Scene 属于 TuringDesk 基础壁纸，不能从库中删除。");
            return;
        }
        std::wstring error;
        if (!library->Remove(selected->id, false, &error)) {
            SetStatus(error.empty() ? L"删除库记录失败。" : error);
            return;
        }
        RebuildList();
        SetStatus(L"已从壁纸库移除记录；原文件未删除。");
    }

    void ApplySelected() {
        if (!library) return;
        const auto selected = Selected();
        if (!selected) {
            SetStatus(L"请先选择一个壁纸。");
            return;
        }
        if (selected->kind == LibraryWallpaperKind::Unknown) {
            SetStatus(L"未知壁纸类型，不能应用。");
            return;
        }
        if (SourceMissing(*selected)) {
            SetStatus(selected->kind == LibraryWallpaperKind::Web
                ? L"Web 壁纸源不可用：" + selected->source.wstring()
                : L"源文件已经不存在：" + selected->source.wstring());
            return;
        }

        const std::wstring targetId = SelectedTargetId();
        std::wstring error;
        if (selected->kind == LibraryWallpaperKind::Web) {
            if (!ActivateWebWallpaperItem(*selected, targetId, &error)) {
                SetStatus(error.empty() ? L"Web 壁纸应用失败。" : error);
                return;
            }
        } else {
            if (!applyCallback) {
                SetStatus(L"壁纸运行时没有提供应用回调。");
                return;
            }
            applyCallback(*selected, targetId);
        }

        std::wstring markError;
        library->MarkUsed(selected->id, &markError);
        RebuildList();
        const std::wstring targetName = TargetDisplayName(targetId);
        SetStatus(markError.empty()
            ? L"已应用到 " + targetName + L"：" + selected->title
            : L"壁纸已应用，但最近使用记录保存失败：" + markError);
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
            if (id == kSearchId && notification == EN_CHANGE) self->RebuildList();
            else if (id == kListId && notification == LBN_SELCHANGE) self->UpdateSelectionActions();
            else if (id == kListId && notification == LBN_DBLCLK) self->ApplySelected();
            else if (id == kAllId && notification == BN_CLICKED) { self->filter = FilterMode::All; self->RebuildList(); }
            else if (id == kFavoritesId && notification == BN_CLICKED) { self->filter = FilterMode::Favorites; self->RebuildList(); }
            else if (id == kRecentId && notification == BN_CLICKED) { self->filter = FilterMode::Recent; self->RebuildList(); }
            else if (id == kImportId && notification == BN_CLICKED) self->ImportFile();
            else if (id == kImportWebUrlId && notification == BN_CLICKED) self->ImportWebUrl();
            else if (id == kFavoriteId && notification == BN_CLICKED) self->ToggleFavorite();
            else if (id == kRemoveId && notification == BN_CLICKED) self->RemoveSelected();
            else if (id == kApplyId && notification == BN_CLICKED) self->ApplySelected();
            else if (id == kCloseId && notification == BN_CLICKED) ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        if (message == WM_CLOSE) {
            ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        if (message == WM_DESTROY) {
            self->window = nullptr;
            self->search = nullptr;
            self->list = nullptr;
            self->targetCombo = nullptr;
            self->managedCopy = nullptr;
            self->webUrl = nullptr;
            self->favoriteButton = nullptr;
            self->status = nullptr;
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    bool CreateWindowUi() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance;
        wc.lpfnWndProc = &Impl::WndProc;
        wc.lpszClassName = kWindowClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return false;

        window = CreateWindowExW(WS_EX_TOOLWINDOW, kWindowClass, L"TuringDesk 壁纸库",
                                 WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                                 CW_USEDEFAULT, CW_USEDEFAULT, 900, 720,
                                 nullptr, nullptr, instance, this);
        if (!window) return false;

        const HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto setFont = [&](HWND control) {
            if (control) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };
        auto button = [&](const wchar_t* text, int id, int x, int y, int w, int h) {
            return setFont(CreateWindowExW(0, L"BUTTON", text,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                x, y, w, h, window, ControlId(id), instance, nullptr));
        };
        auto label = [&](const wchar_t* text, int x, int y, int w, int h) {
            return setFont(CreateWindowExW(0, L"STATIC", text, WS_CHILD | WS_VISIBLE,
                x, y, w, h, window, nullptr, instance, nullptr));
        };

        label(L"壁纸库", 20, 18, 160, 28);
        search = setFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            20, 54, 390, 30, window, ControlId(kSearchId), instance, nullptr));
        SendMessageW(search, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"搜索标题、路径或类型"));
        button(L"全部", kAllId, 424, 54, 72, 30);
        button(L"收藏", kFavoritesId, 504, 54, 72, 30);
        button(L"最近", kRecentId, 584, 54, 72, 30);
        button(L"导入文件…", kImportId, 676, 54, 164, 30);

        label(L"目标", 20, 100, 48, 24);
        targetCombo = setFont(CreateWindowExW(0, L"COMBOBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
            72, 94, 420, 180, window, ControlId(kTargetComboId), instance, nullptr));
        managedCopy = setFont(CreateWindowExW(0, L"BUTTON", L"导入文件时复制到托管库",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
            512, 96, 250, 26, window, ControlId(kManagedCopyId), instance, nullptr));

        list = setFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | LBS_NOTIFY | LBS_NOINTEGRALHEIGHT,
            20, 136, 820, 330, window, ControlId(kListId), instance, nullptr));

        favoriteButton = button(L"收藏", kFavoriteId, 20, 482, 100, 32);
        button(L"移出库", kRemoveId, 130, 482, 100, 32);
        button(L"应用到桌面", kApplyId, 590, 482, 130, 32);
        button(L"关闭", kCloseId, 730, 482, 110, 32);

        label(L"HTTPS Web", 20, 536, 80, 24);
        webUrl = setFont(CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            104, 530, 566, 30, window, ControlId(kWebUrlId), instance, nullptr));
        SendMessageW(webUrl, EM_SETCUEBANNER, TRUE,
                     reinterpret_cast<LPARAM>(L"https://example.com/wallpaper"));
        button(L"导入 Web URL", kImportWebUrlId, 680, 530, 160, 30);
        label(L"远程 Web 壁纸只接受 HTTPS；本地 Web 请用“导入文件…”选择 .html/.htm。",
              104, 566, 736, 24);

        status = setFont(CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE,
            20, 604, 820, 54, window, ControlId(kStatusId), instance, nullptr));
        RebuildTargets();
        return true;
    }
};

WallpaperLibraryWindow::WallpaperLibraryWindow() : impl_(std::make_unique<Impl>()) {}
WallpaperLibraryWindow::~WallpaperLibraryWindow() = default;

bool WallpaperLibraryWindow::Show(HINSTANCE instance, WallpaperLibrary* library,
                                  const std::vector<WallpaperLibraryTarget>& targets,
                                  ApplyCallback applyCallback) {
    if (!impl_ || !library) return false;
    impl_->instance = instance;
    impl_->library = library;
    impl_->targets = targets;
    impl_->applyCallback = std::move(applyCallback);
    if (!impl_->window || !IsWindow(impl_->window)) {
        if (!impl_->CreateWindowUi()) return false;
    } else {
        impl_->RebuildTargets();
    }
    impl_->RebuildList();
    ShowWindow(impl_->window, SW_SHOWNORMAL);
    SetForegroundWindow(impl_->window);
    return true;
}

void WallpaperLibraryWindow::SetTargets(const std::vector<WallpaperLibraryTarget>& targets) {
    if (!impl_) return;
    impl_->targets = targets;
    if (impl_->window && IsWindow(impl_->window)) impl_->RebuildTargets();
}

void WallpaperLibraryWindow::Close() {
    if (impl_ && impl_->window && IsWindow(impl_->window)) ShowWindow(impl_->window, SW_HIDE);
}

void WallpaperLibraryWindow::Refresh() {
    if (impl_ && impl_->window && IsWindow(impl_->window)) impl_->RebuildList();
}

bool WallpaperLibraryWindow::Visible() const noexcept {
    return impl_ && impl_->window && IsWindowVisible(impl_->window) != FALSE;
}

HWND WallpaperLibraryWindow::Window() const noexcept {
    return impl_ ? impl_->window : nullptr;
}

} // namespace turingdesk::wallpaper

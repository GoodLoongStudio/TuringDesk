#include "turingdesk/WallpaperLibraryWindow.h"

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
    HWND managedCopy{};
    HWND favoriteButton{};
    HWND status{};
    WallpaperLibrary* library{};
    ApplyCallback applyCallback;
    FilterMode filter{FilterMode::All};
    std::vector<std::wstring> visibleIds;

    ~Impl() {
        if (window && IsWindow(window)) DestroyWindow(window);
    }

    void SetStatus(const std::wstring& text) const {
        if (status) SetWindowTextW(status, text.c_str());
    }

    std::optional<WallpaperLibraryItem> Selected() const {
        if (!library || !list) return std::nullopt;
        const LRESULT selected = SendMessageW(list, LB_GETCURSEL, 0, 0);
        if (selected == LB_ERR || selected < 0 || static_cast<std::size_t>(selected) >= visibleIds.size()) return std::nullopt;
        return library->Find(visibleIds[static_cast<std::size_t>(selected)]);
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
            bool missing = false;
            if (item.kind != LibraryWallpaperKind::Scene && !item.source.empty()) {
                std::error_code ec;
                missing = !fs::exists(item.source, ec);
            }
            std::wstring label = item.favorite ? L"★ " : L"☆ ";
            label += L"[" + std::wstring(KindLabel(item.kind)) + L"] " + item.title;
            if (item.managedCopy) label += L" · 托管副本";
            if (missing) label += L" · 源文件缺失";
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
        if (favoriteButton) SetWindowTextW(favoriteButton, selected && selected->favorite ? L"取消收藏" : L"收藏");
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
            L"Web\0*.html;*.htm\0所有文件\0*.*\0";
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
        filter = FilterMode::All;
        if (search) SetWindowTextW(search, L"");
        RebuildList();
        for (std::size_t i = 0; i < visibleIds.size(); ++i) {
            if (_wcsicmp(visibleIds[i].c_str(), imported->id.c_str()) == 0) {
                SendMessageW(list, LB_SETCURSEL, static_cast<WPARAM>(i), 0);
                break;
            }
        }
        SetStatus(L"已导入：" + imported->title + (imported->thumbnail.empty() ? L" · 未生成缩略图" : L" · 缩略图已生成"));
        UpdateSelectionActions();
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
            SetStatus(L"内置 Scene 属于 TuringDesk 基础壁纸，不能从库中删除。" );
            return;
        }
        std::wstring error;
        if (!library->Remove(selected->id, false, &error)) {
            SetStatus(error.empty() ? L"删除库记录失败。" : error);
            return;
        }
        RebuildList();
        SetStatus(L"已从壁纸库移除记录；原文件未删除。" );
    }

    void ApplySelected() {
        if (!library || !applyCallback) return;
        const auto selected = Selected();
        if (!selected) {
            SetStatus(L"请先选择一个壁纸。" );
            return;
        }
        if (selected->kind == LibraryWallpaperKind::Web) {
            SetStatus(L"Web 壁纸已可入库；WebView2 运行后端将在路线第 9 项接入。" );
            return;
        }
        if (selected->kind == LibraryWallpaperKind::Unknown) {
            SetStatus(L"未知壁纸类型，不能应用。" );
            return;
        }
        if (selected->kind != LibraryWallpaperKind::Scene && !selected->source.empty()) {
            std::error_code ec;
            if (!fs::exists(selected->source, ec)) {
                SetStatus(L"源文件已经不存在：" + selected->source.wstring());
                return;
            }
        }
        applyCallback(*selected);
        std::wstring error;
        library->MarkUsed(selected->id, &error);
        RebuildList();
        SetStatus(error.empty() ? L"已应用：" + selected->title : L"壁纸已应用，但最近使用记录保存失败：" + error);
    }

    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<Impl*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<Impl*>(create->lpCreateParams);
            self->window = hwnd;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
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
            self->managedCopy = nullptr;
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
                                 CW_USEDEFAULT, CW_USEDEFAULT, 820, 650,
                                 nullptr, nullptr, instance, this);
        if (!window) return false;

        const HFONT font = reinterpret_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        auto button = [&](const wchar_t* text, int id, int x, int y, int w, int h) {
            HWND control = CreateWindowExW(0, L"BUTTON", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                           x, y, w, h, window, ControlId(id), instance, nullptr);
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return control;
        };

        HWND title = CreateWindowExW(0, L"STATIC", L"壁纸库", WS_CHILD | WS_VISIBLE,
                                     20, 18, 160, 28, window, nullptr, instance, nullptr);
        SendMessageW(title, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        search = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
                                 20, 54, 360, 30, window, ControlId(kSearchId), instance, nullptr);
        SendMessageW(search, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        SendMessageW(search, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"搜索标题、路径或类型"));

        button(L"全部", kAllId, 394, 54, 72, 30);
        button(L"收藏", kFavoritesId, 474, 54, 72, 30);
        button(L"最近", kRecentId, 554, 54, 72, 30);
        button(L"导入…", kImportId, 650, 54, 120, 30);

        managedCopy = CreateWindowExW(0, L"BUTTON", L"导入时复制到托管库", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
                                      20, 94, 220, 26, window, ControlId(kManagedCopyId), instance, nullptr);
        SendMessageW(managedCopy, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);

        list = CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", L"",
                               WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | LBS_NOTIFY | LBS_NOINTEGRALHEIGHT,
                               20, 128, 750, 350, window, ControlId(kListId), instance, nullptr);
        SendMessageW(list, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);

        favoriteButton = button(L"收藏", kFavoriteId, 20, 494, 100, 32);
        button(L"移出库", kRemoveId, 130, 494, 100, 32);
        button(L"应用到桌面", kApplyId, 520, 494, 130, 32);
        button(L"关闭", kCloseId, 660, 494, 110, 32);

        status = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE,
                                 20, 542, 750, 46, window, ControlId(kStatusId), instance, nullptr);
        SendMessageW(status, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
        return true;
    }
};

WallpaperLibraryWindow::WallpaperLibraryWindow() : impl_(std::make_unique<Impl>()) {}
WallpaperLibraryWindow::~WallpaperLibraryWindow() = default;

bool WallpaperLibraryWindow::Show(HINSTANCE instance, WallpaperLibrary* library, ApplyCallback applyCallback) {
    if (!impl_ || !library) return false;
    impl_->instance = instance;
    impl_->library = library;
    impl_->applyCallback = std::move(applyCallback);
    if (!impl_->window || !IsWindow(impl_->window)) {
        if (!impl_->CreateWindowUi()) return false;
    }
    impl_->RebuildList();
    ShowWindow(impl_->window, SW_SHOWNORMAL);
    SetForegroundWindow(impl_->window);
    return true;
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

#include "turingdesk/WebWallpaperHost.h"

#include <windows.h>
#include <shellapi.h>
#include <objbase.h>
#include <WebView2.h>
#include <wrl.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <iterator>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

namespace turingdesk::wallpaper {
namespace {

constexpr wchar_t kWebHostClass[] = L"TuringDesk.Native.WebWallpaperHost";
constexpr wchar_t kLocalVirtualHost[] = L"turingdesk-wallpaper.local";
constexpr UINT kPauseMessage = WM_APP + 901;
constexpr UINT kResumeMessage = WM_APP + 902;
constexpr UINT kShutdownMessage = WM_APP + 903;
constexpr ULONGLONG kRestartCooldownMs = 3000;
constexpr ULONGLONG kStableResetMs = 30000;
constexpr unsigned kMaxRecoveryAttempts = 3;

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

std::wstring Trim(std::wstring value) {
    while (!value.empty() && std::iswspace(value.front())) value.erase(value.begin());
    while (!value.empty() && std::iswspace(value.back())) value.pop_back();
    return value;
}

bool StartsWithInsensitive(std::wstring_view value, std::wstring_view prefix) {
    if (value.size() < prefix.size()) return false;
    for (std::size_t i = 0; i < prefix.size(); ++i) {
        if (std::towlower(value[i]) != std::towlower(prefix[i])) return false;
    }
    return true;
}

bool IsHtmlPath(const fs::path& source) {
    const std::wstring extension = Lower(source.extension().wstring());
    return extension == L".html" || extension == L".htm";
}

std::wstring SafeToken(std::wstring value) {
    if (value.empty()) value = L"web";
    for (auto& ch : value) {
        if (!(std::iswalnum(ch) || ch == L'-' || ch == L'_')) ch = L'_';
    }
    if (value.size() > 80) value.resize(80);
    return value;
}

fs::path WebUserDataDirectory(std::wstring_view token) {
    wchar_t local[32768]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
    fs::path base = (length > 0 && length < std::size(local)) ? fs::path(local) : fs::temp_directory_path();
    fs::path directory = base / L"TuringDesk" / L"WebView2" / L"Wallpaper" / SafeToken(std::wstring(token));
    std::error_code ec;
    fs::create_directories(directory, ec);
    return directory;
}

std::wstring QuoteArg(std::wstring_view value) {
    std::wstring result = L"\"";
    unsigned backslashes = 0;
    for (wchar_t ch : value) {
        if (ch == L'\\') {
            ++backslashes;
            continue;
        }
        if (ch == L'\"') {
            result.append(backslashes * 2 + 1, L'\\');
            result.push_back(L'\"');
            backslashes = 0;
            continue;
        }
        result.append(backslashes, L'\\');
        backslashes = 0;
        result.push_back(ch);
    }
    result.append(backslashes * 2, L'\\');
    result.push_back(L'\"');
    return result;
}

std::wstring ExecutablePath() {
    std::vector<wchar_t> buffer(32768);
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    return std::wstring(buffer.data(), length);
}

std::wstring OriginOfHttpsUrl(std::wstring_view input) {
    std::wstring value = Trim(std::wstring(input));
    if (!StartsWithInsensitive(value, L"https://")) return {};
    const std::size_t authorityStart = 8;
    const std::size_t end = value.find_first_of(L"/?#", authorityStart);
    std::wstring origin = end == std::wstring::npos ? value : value.substr(0, end);
    if (origin.size() <= authorityStart) return {};
    return Lower(std::move(origin));
}

std::wstring UrlEncodeSegment(std::wstring_view input) {
    if (input.empty()) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                                            input.data(), static_cast<int>(input.size()),
                                            nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string utf8(static_cast<std::size_t>(needed), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                            input.data(), static_cast<int>(input.size()),
                            utf8.data(), needed, nullptr, nullptr) <= 0) return {};
    static constexpr char hex[] = "0123456789ABCDEF";
    std::wstring result;
    result.reserve(utf8.size() * 3);
    for (unsigned char ch : utf8) {
        const bool safe = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                          (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.' || ch == '~';
        if (safe) {
            result.push_back(static_cast<wchar_t>(ch));
        } else {
            result.push_back(L'%');
            result.push_back(static_cast<wchar_t>(hex[(ch >> 4) & 0x0F]));
            result.push_back(static_cast<wchar_t>(hex[ch & 0x0F]));
        }
    }
    return result;
}

std::wstring HrText(HRESULT hr) {
    wchar_t text[64]{};
    swprintf_s(text, L"HRESULT 0x%08X", static_cast<unsigned>(hr));
    return text;
}

std::optional<std::wstring> ArgValue(const std::vector<std::wstring>& args, std::wstring_view name) {
    for (std::size_t i = 0; i + 1 < args.size(); ++i) {
        if (_wcsicmp(args[i].c_str(), std::wstring(name).c_str()) == 0) return args[i + 1];
    }
    return std::nullopt;
}

bool HasArg(const std::vector<std::wstring>& args, std::wstring_view name) {
    for (const auto& arg : args) if (_wcsicmp(arg.c_str(), std::wstring(name).c_str()) == 0) return true;
    return false;
}

std::vector<std::wstring> ProcessArguments() {
    int count = 0;
    LPWSTR* raw = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!raw || count <= 0) return {};
    std::vector<std::wstring> result;
    result.reserve(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) result.emplace_back(raw[i]);
    LocalFree(raw);
    return result;
}

struct ChildOptions {
    HWND parent{};
    RECT region{};
    std::wstring source;
    std::wstring token;
    std::wstring itemId;
    bool muted{true};
};

bool ParseIntArg(const std::vector<std::wstring>& args, std::wstring_view name, LONG& value) {
    const auto text = ArgValue(args, name);
    if (!text) return false;
    wchar_t* end = nullptr;
    const long parsed = std::wcstol(text->c_str(), &end, 10);
    if (end == text->c_str()) return false;
    value = static_cast<LONG>(parsed);
    return true;
}

std::optional<ChildOptions> ParseChildOptions(const std::vector<std::wstring>& args) {
    if (!HasArg(args, L"--web-wallpaper-host")) return std::nullopt;
    ChildOptions options;
    const auto parentText = ArgValue(args, L"--parent-hwnd");
    const auto source = ArgValue(args, L"--source");
    const auto token = ArgValue(args, L"--token");
    const auto itemId = ArgValue(args, L"--item-id");
    if (!parentText || !source || !token) return ChildOptions{};
    wchar_t* end = nullptr;
    const unsigned long long parentValue = _wcstoui64(parentText->c_str(), &end, 10);
    if (end == parentText->c_str() || parentValue == 0) return ChildOptions{};
    options.parent = reinterpret_cast<HWND>(static_cast<uintptr_t>(parentValue));
    if (!ParseIntArg(args, L"--left", options.region.left) ||
        !ParseIntArg(args, L"--top", options.region.top) ||
        !ParseIntArg(args, L"--right", options.region.right) ||
        !ParseIntArg(args, L"--bottom", options.region.bottom)) return ChildOptions{};
    options.source = *source;
    options.token = *token;
    options.itemId = itemId.value_or(L"web");
    const auto muted = ArgValue(args, L"--muted");
    options.muted = !muted || *muted != L"0";
    return options;
}

class WebWallpaperChildHost {
public:
    WebWallpaperChildHost(HINSTANCE instance, ChildOptions options)
        : instance_(instance), options_(std::move(options)) {}

    bool Create() {
        if (!options_.parent || !IsWindow(options_.parent) ||
            options_.region.right <= options_.region.left || options_.region.bottom <= options_.region.top ||
            !WebWallpaperProcessSet::IsSupportedSource(options_.source)) {
            exitCode_ = 21;
            return false;
        }

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = instance_;
        wc.lpfnWndProc = &WebWallpaperChildHost::WndProc;
        wc.lpszClassName = kWebHostClass;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
            exitCode_ = 22;
            return false;
        }

        const LONG width = options_.region.right - options_.region.left;
        const LONG height = options_.region.bottom - options_.region.top;
        hwnd_ = CreateWindowExW(
            WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
            kWebHostClass,
            options_.token.c_str(),
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            options_.region.left, options_.region.top, width, height,
            options_.parent, nullptr, instance_, this);
        if (!hwnd_) {
            exitCode_ = 23;
            return false;
        }
        ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
        InitializeWebView();
        return true;
    }

    int Run() {
        MSG msg{};
        while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        return exitCode_ == 0 ? static_cast<int>(msg.wParam) : exitCode_;
    }

private:
    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
        auto* self = reinterpret_cast<WebWallpaperChildHost*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<WebWallpaperChildHost*>(create->lpCreateParams);
            if (self) {
                self->hwnd_ = hwnd;
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
            }
        }
        return self ? self->HandleMessage(message, wParam, lParam)
                    : DefWindowProcW(hwnd, message, wParam, lParam);
    }

    LRESULT HandleMessage(UINT message, WPARAM, LPARAM lParam) {
        switch (message) {
        case WM_NCHITTEST:
            return HTTRANSPARENT;
        case WM_ERASEBKGND:
            return 1;
        case WM_SIZE:
            ResizeWebView();
            return 0;
        case kPauseMessage:
            Suspend();
            return 0;
        case kResumeMessage:
            Resume();
            return 0;
        case kShutdownMessage:
            DestroyWindow(hwnd_);
            return 0;
        case WM_DESTROY:
            webview_.Reset();
            if (controller_) controller_->Close();
            controller_.Reset();
            PostQuitMessage(exitCode_);
            return 0;
        }
        return DefWindowProcW(hwnd_, message, 0, lParam);
    }

    void Fail(int code) {
        if (exitCode_ == 0) exitCode_ = code;
        if (hwnd_ && IsWindow(hwnd_)) PostMessageW(hwnd_, kShutdownMessage, 0, 0);
    }

    bool AllowedNavigation(std::wstring_view uri) const {
        if (uri.empty() || StartsWithInsensitive(uri, L"about:blank")) return true;
        if (allowedOrigin_.empty()) return false;
        if (!StartsWithInsensitive(uri, allowedOrigin_)) return false;
        if (uri.size() == allowedOrigin_.size()) return true;
        const wchar_t next = uri[allowedOrigin_.size()];
        return next == L'/' || next == L'?' || next == L'#';
    }

    std::wstring NavigationTarget(ComPtr<ICoreWebView2_3>& webview3) {
        if (WebWallpaperProcessSet::IsRemoteHttpsSource(options_.source)) {
            allowedOrigin_ = OriginOfHttpsUrl(options_.source);
            return options_.source;
        }

        std::error_code ec;
        fs::path source = fs::absolute(fs::path(options_.source), ec);
        if (ec) source = fs::path(options_.source);
        source = source.lexically_normal();
        if (!fs::exists(source, ec) || !fs::is_regular_file(source, ec) || !IsHtmlPath(source)) return {};
        if (!webview3) return {};
        const fs::path folder = source.parent_path();
        if (FAILED(webview3->SetVirtualHostNameToFolderMapping(
                kLocalVirtualHost, folder.c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS))) return {};
        allowedOrigin_ = L"https://" + std::wstring(kLocalVirtualHost);
        const std::wstring encoded = UrlEncodeSegment(source.filename().wstring());
        if (encoded.empty()) return {};
        return allowedOrigin_ + L"/" + encoded;
    }

    void ConfigureSecurity() {
        if (!webview_) return;
        ComPtr<ICoreWebView2Settings> settings;
        if (SUCCEEDED(webview_->get_Settings(settings.GetAddressOf())) && settings) {
            settings->put_AreDevToolsEnabled(FALSE);
            settings->put_AreDefaultContextMenusEnabled(FALSE);
            settings->put_IsStatusBarEnabled(FALSE);
            settings->put_IsZoomControlEnabled(FALSE);
        }

        EventRegistrationToken navigationToken{};
        webview_->add_NavigationStarting(
            Callback<ICoreWebView2NavigationStartingEventHandler>(
                [this](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
                    LPWSTR raw = nullptr;
                    if (!args || FAILED(args->get_Uri(&raw)) || !raw) return S_OK;
                    const std::wstring uri(raw);
                    CoTaskMemFree(raw);
                    if (!AllowedNavigation(uri)) args->put_Cancel(TRUE);
                    return S_OK;
                }).Get(), &navigationToken);

        EventRegistrationToken newWindowToken{};
        webview_->add_NewWindowRequested(
            Callback<ICoreWebView2NewWindowRequestedEventHandler>(
                [](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
                    if (args) args->put_Handled(TRUE);
                    return S_OK;
                }).Get(), &newWindowToken);

        EventRegistrationToken permissionToken{};
        webview_->add_PermissionRequested(
            Callback<ICoreWebView2PermissionRequestedEventHandler>(
                [](ICoreWebView2*, ICoreWebView2PermissionRequestedEventArgs* args) -> HRESULT {
                    if (args) args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY);
                    return S_OK;
                }).Get(), &permissionToken);

        ComPtr<ICoreWebView2_4> webview4;
        if (SUCCEEDED(webview_.As(&webview4)) && webview4) {
            EventRegistrationToken downloadToken{};
            webview4->add_DownloadStarting(
                Callback<ICoreWebView2DownloadStartingEventHandler>(
                    [](ICoreWebView2*, ICoreWebView2DownloadStartingEventArgs* args) -> HRESULT {
                        if (args) {
                            args->put_Cancel(TRUE);
                            args->put_Handled(TRUE);
                        }
                        return S_OK;
                    }).Get(), &downloadToken);
        }

        ComPtr<ICoreWebView2_8> webview8;
        if (SUCCEEDED(webview_.As(&webview8)) && webview8) webview8->put_IsMuted(options_.muted ? TRUE : FALSE);
    }

    void InitializeWebView() {
        const fs::path userData = WebUserDataDirectory(options_.token);
        const std::wstring userDataText = userData.wstring();
        const HRESULT start = CreateCoreWebView2EnvironmentWithOptions(
            nullptr, userDataText.c_str(), nullptr,
            Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
                [this](HRESULT result, ICoreWebView2Environment* environment) -> HRESULT {
                    if (FAILED(result) || !environment || !hwnd_ || !IsWindow(hwnd_)) {
                        Fail(24);
                        return S_OK;
                    }
                    return environment->CreateCoreWebView2Controller(
                        hwnd_,
                        Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                            [this](HRESULT controllerResult, ICoreWebView2Controller* controller) -> HRESULT {
                                if (FAILED(controllerResult) || !controller || !hwnd_ || !IsWindow(hwnd_)) {
                                    Fail(25);
                                    return S_OK;
                                }
                                controller_ = controller;
                                const HRESULT hr = controller_->get_CoreWebView2(webview_.ReleaseAndGetAddressOf());
                                if (FAILED(hr) || !webview_) {
                                    Fail(26);
                                    return S_OK;
                                }
                                ComPtr<ICoreWebView2_3> webview3;
                                webview_.As(&webview3);
                                const std::wstring target = NavigationTarget(webview3);
                                if (target.empty()) {
                                    Fail(27);
                                    return S_OK;
                                }
                                ConfigureSecurity();
                                controller_->put_IsVisible(TRUE);
                                ResizeWebView();
                                if (FAILED(webview_->Navigate(target.c_str()))) {
                                    Fail(28);
                                    return S_OK;
                                }
                                ready_ = true;
                                return S_OK;
                            }).Get());
                }).Get());
        if (FAILED(start)) Fail(29);
    }

    void ResizeWebView() {
        if (!controller_ || !hwnd_) return;
        RECT bounds{};
        if (GetClientRect(hwnd_, &bounds)) controller_->put_Bounds(bounds);
    }

    void Suspend() {
        if (!webview_) {
            if (controller_) controller_->put_IsVisible(FALSE);
            return;
        }
        ComPtr<ICoreWebView2_3> webview3;
        if (SUCCEEDED(webview_.As(&webview3)) && webview3) {
            webview3->TrySuspend(
                Callback<ICoreWebView2TrySuspendCompletedHandler>(
                    [](HRESULT, BOOL) -> HRESULT { return S_OK; }).Get());
        }
        if (controller_) controller_->put_IsVisible(FALSE);
    }

    void Resume() {
        if (webview_) {
            ComPtr<ICoreWebView2_3> webview3;
            if (SUCCEEDED(webview_.As(&webview3)) && webview3) webview3->Resume();
        }
        if (controller_) controller_->put_IsVisible(TRUE);
    }

    HINSTANCE instance_{};
    ChildOptions options_;
    HWND hwnd_{};
    int exitCode_{};
    bool ready_{};
    std::wstring allowedOrigin_;
    ComPtr<ICoreWebView2Controller> controller_;
    ComPtr<ICoreWebView2> webview_;
};

std::wstring SlotToken(const WebWallpaperRequest& request, std::size_t index) {
    std::wstring token = request.itemId.empty() ? L"web" : request.itemId;
    token += L"-" + std::to_wstring(index) + L"-" + std::to_wstring(request.region.left) + L"-" +
             std::to_wstring(request.region.top) + L"-" + std::to_wstring(request.region.right) + L"-" +
             std::to_wstring(request.region.bottom);
    return SafeToken(std::move(token));
}

} // namespace

struct WebWallpaperProcessSet::Slot {
    WebWallpaperRequest request;
    std::wstring token;
    PROCESS_INFORMATION process{};
    HWND window{};
    unsigned recoveryAttempts{};
    ULONGLONG lastStartTick{};
    ULONGLONG healthySinceTick{};
    DWORD lastExitCode{STILL_ACTIVE};
};

WebWallpaperProcessSet::WebWallpaperProcessSet() = default;
WebWallpaperProcessSet::~WebWallpaperProcessSet() { Stop(); }

bool WebWallpaperProcessSet::IsRemoteHttpsSource(const std::wstring& source) noexcept {
    return !OriginOfHttpsUrl(source).empty();
}

bool WebWallpaperProcessSet::IsSupportedSource(const std::wstring& source) noexcept {
    if (IsRemoteHttpsSource(source)) return true;
    if (source.empty()) return false;
    std::error_code ec;
    const fs::path path(source);
    return IsHtmlPath(path) && fs::exists(path, ec) && fs::is_regular_file(path, ec);
}

bool WebWallpaperProcessSet::SelfTest() noexcept {
    if (!IsRemoteHttpsSource(L"https://example.com/wallpaper")) return false;
    if (IsRemoteHttpsSource(L"http://example.com")) return false;
    if (IsRemoteHttpsSource(L"javascript:alert(1)")) return false;
    if (OriginOfHttpsUrl(L"HTTPS://Example.COM/path") != L"https://example.com") return false;
    if (!StartsWithInsensitive(L"https://EXAMPLE.com/a", L"https://example.COM")) return false;
    return !SafeToken(L"a:b/c").empty() && UrlEncodeSegment(L"壁纸 test.html").find(L"%") != std::wstring::npos;
}

bool WebWallpaperProcessSet::Start(HWND parentWindow, const std::vector<WebWallpaperRequest>& requests) {
    Stop();
    lastError_.clear();
    if (!parentWindow || !IsWindow(parentWindow) || requests.empty()) {
        lastError_ = L"Web 壁纸参数无效";
        return false;
    }
    for (const auto& request : requests) {
        if (request.region.right <= request.region.left || request.region.bottom <= request.region.top ||
            !IsSupportedSource(request.source)) {
            lastError_ = L"不支持的 Web 壁纸源：" + request.source;
            return false;
        }
    }

    parent_ = parentWindow;
    job_ = CreateJobObjectW(nullptr, nullptr);
    if (job_) {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job_, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) {
            CloseHandle(job_);
            job_ = nullptr;
        }
    }

    slots_.resize(requests.size());
    for (std::size_t i = 0; i < requests.size(); ++i) {
        slots_[i].request = requests[i];
        slots_[i].token = SlotToken(requests[i], i);
        if (!StartSlot(slots_[i], false)) {
            Stop();
            return false;
        }
    }
    return true;
}

bool WebWallpaperProcessSet::StartSlot(Slot& slot, bool recovery) {
    const std::wstring executable = ExecutablePath();
    if (executable.empty()) {
        lastError_ = L"无法定位 TuringDeskWallpaper.exe";
        return false;
    }

    if (recovery) ++slot.recoveryAttempts;
    slot.window = nullptr;
    slot.lastExitCode = STILL_ACTIVE;
    slot.lastStartTick = GetTickCount64();
    slot.healthySinceTick = slot.lastStartTick;

    std::wstring command = QuoteArg(executable);
    command += L" --web-wallpaper-host";
    command += L" --parent-hwnd " + std::to_wstring(reinterpret_cast<uintptr_t>(parent_));
    command += L" --left " + std::to_wstring(slot.request.region.left);
    command += L" --top " + std::to_wstring(slot.request.region.top);
    command += L" --right " + std::to_wstring(slot.request.region.right);
    command += L" --bottom " + std::to_wstring(slot.request.region.bottom);
    command += L" --source " + QuoteArg(slot.request.source);
    command += L" --token " + QuoteArg(slot.token);
    command += L" --item-id " + QuoteArg(slot.request.itemId);
    command += L" --muted " + std::wstring(slot.request.muted ? L"1" : L"0");

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    std::vector<wchar_t> mutableCommand(command.begin(), command.end());
    mutableCommand.push_back(L'\0');
    if (!CreateProcessW(executable.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE,
                        CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) {
        lastError_ = L"启动 Web 壁纸隔离进程失败，Win32=" + std::to_wstring(GetLastError());
        return false;
    }
    slot.process = process;
    if (job_) AssignProcessToJobObject(job_, slot.process.hProcess);
    return true;
}

HWND WebWallpaperProcessSet::FindSlotWindow(const Slot& slot) const {
    if (!parent_ || !IsWindow(parent_)) return nullptr;
    return FindWindowExW(parent_, nullptr, kWebHostClass, slot.token.c_str());
}

void WebWallpaperProcessSet::StopSlot(Slot& slot) {
    HWND window = slot.window && IsWindow(slot.window) ? slot.window : FindSlotWindow(slot);
    if (window) PostMessageW(window, kShutdownMessage, 0, 0);
    if (slot.process.hProcess) {
        if (WaitForSingleObject(slot.process.hProcess, 700) == WAIT_TIMEOUT)
            TerminateProcess(slot.process.hProcess, 0);
        CloseHandle(slot.process.hProcess);
    }
    if (slot.process.hThread) CloseHandle(slot.process.hThread);
    slot.process = {};
    slot.window = nullptr;
}

void WebWallpaperProcessSet::Stop() {
    for (auto& slot : slots_) StopSlot(slot);
    slots_.clear();
    if (job_) {
        CloseHandle(job_);
        job_ = nullptr;
    }
    parent_ = nullptr;
    paused_ = false;
}

void WebWallpaperProcessSet::SetPaused(bool paused) {
    paused_ = paused;
    for (auto& slot : slots_) {
        if (!slot.window || !IsWindow(slot.window)) slot.window = FindSlotWindow(slot);
        if (slot.window) PostMessageW(slot.window, paused ? kPauseMessage : kResumeMessage, 0, 0);
    }
}

void WebWallpaperProcessSet::Tick() {
    if (slots_.empty()) return;
    const ULONGLONG now = GetTickCount64();
    for (auto& slot : slots_) {
        if (!slot.process.hProcess) continue;
        if (!slot.window || !IsWindow(slot.window)) slot.window = FindSlotWindow(slot);
        DWORD exitCode = STILL_ACTIVE;
        if (!GetExitCodeProcess(slot.process.hProcess, &exitCode)) exitCode = 30;
        if (exitCode == STILL_ACTIVE) {
            if (slot.recoveryAttempts > 0 && now - slot.healthySinceTick >= kStableResetMs) slot.recoveryAttempts = 0;
            if (slot.window && paused_) PostMessageW(slot.window, kPauseMessage, 0, 0);
            continue;
        }

        slot.lastExitCode = exitCode;
        if (slot.process.hProcess) CloseHandle(slot.process.hProcess);
        if (slot.process.hThread) CloseHandle(slot.process.hThread);
        slot.process = {};
        slot.window = nullptr;
        if (slot.recoveryAttempts >= kMaxRecoveryAttempts) {
            lastError_ = L"Web 壁纸连续崩溃，已停止自动恢复；ExitCode=" + std::to_wstring(exitCode);
            continue;
        }
        if (now - slot.lastStartTick < kRestartCooldownMs) continue;
        if (!StartSlot(slot, true)) continue;
        if (paused_) SetPaused(true);
    }
}

bool WebWallpaperProcessSet::Active() const noexcept {
    if (slots_.empty()) return false;
    for (const auto& slot : slots_) {
        if (!slot.process.hProcess) return false;
        DWORD exitCode = 0;
        if (!GetExitCodeProcess(slot.process.hProcess, &exitCode) || exitCode != STILL_ACTIVE) return false;
    }
    return true;
}

std::wstring WebWallpaperProcessSet::LastErrorText() const { return lastError_; }

std::wstring WebWallpaperProcessSet::DiagnosticsText() const {
    std::wostringstream text;
    text << slots_.size() << L" 个 WebView2 隔离 Surface";
    std::size_t ready = 0;
    unsigned recoveries = 0;
    for (const auto& slot : slots_) {
        if ((slot.window && IsWindow(slot.window)) || FindSlotWindow(slot)) ++ready;
        recoveries += slot.recoveryAttempts;
    }
    text << L" · HWND " << ready << L"/" << slots_.size();
    if (recoveries > 0) text << L" · 恢复 " << recoveries << L" 次";
    if (!lastError_.empty()) text << L" · " << lastError_;
    return text.str();
}

int TryRunWebWallpaperChild(HINSTANCE instance) {
    const auto args = ProcessArguments();
    const auto parsed = ParseChildOptions(args);
    if (!parsed) return -1;
    if (!parsed->parent || parsed->source.empty() || parsed->token.empty()) return 20;

    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    WebWallpaperChildHost host(instance, *parsed);
    if (!host.Create()) return 21;
    return host.Run();
}

} // namespace turingdesk::wallpaper

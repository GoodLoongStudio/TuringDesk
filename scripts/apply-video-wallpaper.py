from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
engine_path = ROOT / "src/native/src/WallpaperEngine.cpp"
cmake_path = ROOT / "src/native/CMakeLists.txt"
header_path = ROOT / "src/native/include/turingdesk/VideoWallpaperPlayer.h"
player_path = ROOT / "src/native/src/VideoWallpaperPlayer.cpp"

s = engine_path.read_text(encoding="utf-8")

def rep(old: str, new: str):
    global s
    if old not in s:
        raise RuntimeError("missing patch anchor:\n" + old[:180])
    s = s.replace(old, new, 1)

rep('#include <wrl/client.h>\n#include <algorithm>', '#include <wrl/client.h>\n#include "turingdesk/VideoWallpaperPlayer.h"\n#include <algorithm>')
rep('constexpr int kConfigVersion = 3;', 'constexpr int kConfigVersion = 4;')
rep('    std::wstring scene{L"aurora"};\n    std::wstring image;\n};', '    std::wstring scene{L"aurora"};\n    std::wstring image;\n    std::wstring video;\n};')
rep('return scene == L"aurora" || scene == L"neon" || scene == L"grid" || scene == L"image";', 'return scene == L"aurora" || scene == L"neon" || scene == L"grid" || scene == L"image" || scene == L"video";')
rep('    WritePrivateProfileStringW(L"Wallpaper", L"Image", config.image.c_str(), path.c_str());\n}', '    WritePrivateProfileStringW(L"Wallpaper", L"Image", config.image.c_str(), path.c_str());\n    WritePrivateProfileStringW(L"Wallpaper", L"Video", config.video.c_str(), path.c_str());\n}')
rep('    GetPrivateProfileStringW(L"Wallpaper", L"Image", L"", text, static_cast<DWORD>(std::size(text)), path.c_str());\n    config.image = text;\n\n    if (!ValidScene(config.scene)) config.scene = L"aurora";\n    if (config.scene == L"image" && config.image.empty()) config.scene = L"aurora";\n\n    if (version < kConfigVersion) {\n        config.enabled = true;\n        config.scene = L"aurora";\n        config.image.clear();\n        SaveConfig(config);\n    }', '    GetPrivateProfileStringW(L"Wallpaper", L"Image", L"", text, static_cast<DWORD>(std::size(text)), path.c_str());\n    config.image = text;\n    GetPrivateProfileStringW(L"Wallpaper", L"Video", L"", text, static_cast<DWORD>(std::size(text)), path.c_str());\n    config.video = text;\n\n    if (!ValidScene(config.scene)) config.scene = L"aurora";\n    if (config.scene == L"image" && config.image.empty()) config.scene = L"aurora";\n    if (config.scene == L"video" && config.video.empty()) config.scene = L"aurora";\n\n    // V4 adds video state; preserve an existing V3 scene instead of resetting the user wallpaper.\n    if (version < kConfigVersion) SaveConfig(config);')
rep('    ~WallpaperApp() {\n        RemoveTray();', '    ~WallpaperApp() {\n        videoPlayer_.Stop();\n        RemoveTray();')
rep('        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"图片壁纸"));\n\n        imageButton_ = CreateWindowExW(0, L"BUTTON", L"选择图片…",', '        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"图片壁纸"));\n        SendMessageW(sceneCombo_, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"视频壁纸 · Media Foundation"));\n\n        imageButton_ = CreateWindowExW(0, L"BUTTON", L"选择图片 / 视频…",')
rep('        if (enabled) {\n            AttachToDesktop();\n            ShowWindow(host_, SW_SHOWNOACTIVATE);\n            InvalidateRect(host_, nullptr, FALSE);\n            UpdateWindow(host_);\n        } else {\n            ShowWindow(host_, SW_HIDE);\n        }', '        if (enabled) {\n            AttachToDesktop();\n            ShowWindow(host_, SW_SHOWNOACTIVATE);\n            if (config_.scene == L"video") {\n                if (!videoPlayer_.Active()) StartVideo();\n                videoPlayer_.SetPaused(false);\n            } else {\n                InvalidateRect(host_, nullptr, FALSE);\n                UpdateWindow(host_);\n            }\n        } else {\n            videoPlayer_.SetPaused(true);\n            ShowWindow(host_, SW_HIDE);\n        }')
rep('                if (config_.enabled && config_.image.empty()) {\n                    const bool paused = config_.pauseFullscreen && ForegroundIsFullscreen(host_, settings_);\n                    if (!paused) {\n                        time_ += 0.033f;\n                        InvalidateRect(host_, nullptr, FALSE);\n                    }\n                }', '                if (config_.enabled) {\n                    const bool paused = config_.pauseFullscreen && ForegroundIsFullscreen(host_, settings_);\n                    if (config_.scene == L"video") {\n                        videoPlayer_.Tick();\n                        videoPlayer_.SetPaused(paused);\n                        const auto mediaError = videoPlayer_.LastErrorText();\n                        if (mediaError != lastMediaError_) {\n                            lastMediaError_ = mediaError;\n                            RefreshSettings();\n                        }\n                    } else if (config_.image.empty() && !paused) {\n                        time_ += 0.033f;\n                        InvalidateRect(host_, nullptr, FALSE);\n                    }\n                }')
rep('    void Draw() {\n        EnsureRenderTarget();\n        if (!renderTarget_ || !brush_ || !config_.enabled) return;\n\n        renderTarget_->BeginDraw();', '    void Draw() {\n        EnsureRenderTarget();\n        if (!renderTarget_ || !brush_ || !config_.enabled) return;\n        if (config_.scene == L"video" && videoPlayer_.Active()) return;\n\n        renderTarget_->BeginDraw();')
rep('        if (!config_.image.empty() && imageBitmap_) DrawImage();\n        else if (config_.scene == L"neon") DrawNeon();', '        if (config_.scene == L"video") renderTarget_->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f));\n        else if (!config_.image.empty() && imageBitmap_) DrawImage();\n        else if (config_.scene == L"neon") DrawNeon();')
rep('    bool ApplyConfig(const Config& next, bool persist = true) {\n        config_ = next;\n        if (persist) SaveConfig(config_);\n        pendingImage_.clear();\n        imageBitmap_.Reset();\n        if (renderTarget_) LoadImage();\n        const bool mounted = AttachToDesktop();\n        if (config_.enabled && mounted) {\n            ShowWindow(host_, SW_SHOWNOACTIVATE);\n            InvalidateRect(host_, nullptr, FALSE);\n            UpdateWindow(host_);\n        } else if (!config_.enabled) {\n            ShowWindow(host_, SW_HIDE);\n        }\n        RefreshSettings();\n        return mounted;\n    }', '    bool ApplyConfig(const Config& next, bool persist = true) {\n        videoPlayer_.Stop();\n        lastMediaError_.clear();\n        config_ = next;\n        if (persist) SaveConfig(config_);\n        pendingImage_.clear();\n        pendingVideo_.clear();\n        imageBitmap_.Reset();\n        if (renderTarget_) LoadImage();\n        const bool mounted = AttachToDesktop();\n        if (config_.enabled && mounted) {\n            ShowWindow(host_, SW_SHOWNOACTIVATE);\n            InvalidateRect(host_, nullptr, FALSE);\n            UpdateWindow(host_);\n            if (config_.scene == L"video") StartVideo();\n        } else if (!config_.enabled) {\n            ShowWindow(host_, SW_HIDE);\n        }\n        RefreshSettings();\n        return mounted;\n    }\n\n    bool StartVideo() {\n        lastMediaError_.clear();\n        if (config_.video.empty() || !fs::exists(config_.video)) {\n            lastMediaError_ = L"视频文件不存在";\n            return false;\n        }\n        if (!videoPlayer_.Start(host_, config_.video)) {\n            lastMediaError_ = videoPlayer_.LastErrorText();\n            if (lastMediaError_.empty()) lastMediaError_ = L"Media Foundation 无法启动该视频";\n            return false;\n        }\n        videoPlayer_.SetPaused(false);\n        return true;\n    }')
rep('        dialog.lpstrFilter = L"图片\\0*.jpg;*.jpeg;*.png;*.bmp\\0所有文件\\0*.*\\0";\n        dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;\n        if (!GetOpenFileNameW(&dialog)) return;\n        pendingImage_ = path;\n        SendMessageW(sceneCombo_, CB_SETCURSEL, 3, 0);\n        if (status_) SetWindowTextW(status_, pendingImage_.c_str());', '        dialog.lpstrFilter = L"图片和视频\\0*.jpg;*.jpeg;*.png;*.bmp;*.mp4;*.mov;*.wmv;*.m4v\\0视频\\0*.mp4;*.mov;*.wmv;*.m4v\\0图片\\0*.jpg;*.jpeg;*.png;*.bmp\\0所有文件\\0*.*\\0";\n        dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;\n        if (!GetOpenFileNameW(&dialog)) return;\n        const std::wstring selectedPath = path;\n        const auto extension = fs::path(selectedPath).extension().wstring();\n        const bool isVideo = _wcsicmp(extension.c_str(), L".mp4") == 0 ||\n                             _wcsicmp(extension.c_str(), L".mov") == 0 ||\n                             _wcsicmp(extension.c_str(), L".wmv") == 0 ||\n                             _wcsicmp(extension.c_str(), L".m4v") == 0;\n        if (isVideo) {\n            pendingVideo_ = selectedPath;\n            SendMessageW(sceneCombo_, CB_SETCURSEL, 4, 0);\n        } else {\n            pendingImage_ = selectedPath;\n            SendMessageW(sceneCombo_, CB_SETCURSEL, 3, 0);\n        }\n        if (status_) SetWindowTextW(status_, selectedPath.c_str());')
rep('        next.pauseFullscreen = SendMessageW(pauseCheck_, BM_GETCHECK, 0, 0) == BST_CHECKED;\n        next.image.clear();\n\n        if (selected == 1) next.scene = L"neon";', '        next.pauseFullscreen = SendMessageW(pauseCheck_, BM_GETCHECK, 0, 0) == BST_CHECKED;\n        next.image.clear();\n        next.video.clear();\n\n        if (selected == 1) next.scene = L"neon";')
rep('        } else {\n            next.scene = L"aurora";\n        }\n        ApplyConfig(next);', '        } else if (selected == 4) {\n            next.scene = L"video";\n            next.video = pendingVideo_.empty() ? config_.video : pendingVideo_;\n            if (next.video.empty()) {\n                SetWindowTextW(status_, L"请先选择一个 MP4 / MOV / WMV / M4V 视频。");\n                return;\n            }\n        } else {\n            next.scene = L"aurora";\n        }\n        ApplyConfig(next);')
rep('        int selected = 0;\n        if (!config_.image.empty() || config_.scene == L"image") selected = 3;\n        else if (config_.scene == L"neon") selected = 1;\n        else if (config_.scene == L"grid") selected = 2;', '        int selected = 0;\n        if (!config_.video.empty() || config_.scene == L"video") selected = 4;\n        else if (!config_.image.empty() || config_.scene == L"image") selected = 3;\n        else if (config_.scene == L"neon") selected = 1;\n        else if (config_.scene == L"grid") selected = 2;')
rep('            const wchar_t* scene = selected == 0 ? L"Aurora Flow" : selected == 1 ? L"Neon Flow" : selected == 2 ? L"Quiet Grid" : L"图片壁纸";\n            status = std::wstring(scene) + L" 已应用 · 桌面层：" + MountModeText(mountMode_);\n            if (selected != 3) status += L" · Direct2D 约 30 FPS";', '            const wchar_t* scene = selected == 0 ? L"Aurora Flow" : selected == 1 ? L"Neon Flow" : selected == 2 ? L"Quiet Grid" : selected == 3 ? L"图片壁纸" : L"视频壁纸";\n            status = std::wstring(scene) + L" 已应用 · 桌面层：" + MountModeText(mountMode_);\n            if (selected <= 2) status += L" · Direct2D 约 30 FPS";\n            else if (selected == 4) {\n                if (!lastMediaError_.empty()) status = L"视频壁纸错误：" + lastMediaError_;\n                else status += L" · Media Foundation · 静音循环";\n            }')
rep('    Config config_;\n    std::wstring pendingImage_;\n    float time_{};', '    Config config_;\n    std::wstring pendingImage_;\n    std::wstring pendingVideo_;\n    std::wstring lastMediaError_;\n    turingdesk::VideoWallpaperPlayer videoPlayer_;\n    float time_{};')
rep('    const bool pathOk = !ConfigPath().empty();\n    wic.Reset();', '    const bool pathOk = !ConfigPath().empty();\n    const bool mediaFoundationOk = turingdesk::VideoWallpaperPlayer::MediaFoundationAvailable();\n    wic.Reset();')
rep('    if (!layered) return 26;\n    return pathOk ? 0 : 27;', '    if (!layered) return 26;\n    if (!pathOk) return 27;\n    return mediaFoundationOk ? 0 : 28;')

engine_path.write_text(s, encoding="utf-8")

header_path.write_text(r'''#pragma once
#include <windows.h>
#include <memory>
#include <string>

namespace turingdesk {

class VideoWallpaperPlayer {
public:
    VideoWallpaperPlayer();
    ~VideoWallpaperPlayer();

    VideoWallpaperPlayer(const VideoWallpaperPlayer&) = delete;
    VideoWallpaperPlayer& operator=(const VideoWallpaperPlayer&) = delete;

    bool Start(HWND targetWindow, const std::wstring& path);
    void Stop();
    void Tick();
    void SetPaused(bool paused);
    bool Active() const;
    HRESULT LastError() const;
    std::wstring LastErrorText() const;

    static bool MediaFoundationAvailable();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk
''', encoding="utf-8")

player_path.write_text(r'''#include "turingdesk/VideoWallpaperPlayer.h"
#include <mfapi.h>
#include <mfplay.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdio>
#include <new>

using Microsoft::WRL::ComPtr;

namespace turingdesk {
namespace {

class PlayerCallback final : public IMFPMediaPlayerCallback {
public:
    PlayerCallback(std::atomic_bool& ended, std::atomic_long& error)
        : ended_(ended), error_(error) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFPMediaPlayerCallback)) {
            *object = static_cast<IMFPMediaPlayerCallback*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override {
        return refs_.fetch_add(1, std::memory_order_relaxed) + 1;
    }

    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG value = refs_.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (value == 0) delete this;
        return value;
    }

    void STDMETHODCALLTYPE OnMediaPlayerEvent(MFP_EVENT_HEADER* eventHeader) override {
        if (!eventHeader) return;
        if (FAILED(eventHeader->hrEvent)) error_.store(eventHeader->hrEvent, std::memory_order_relaxed);
        if (eventHeader->eEventType == MFP_EVENT_TYPE_PLAYBACK_ENDED)
            ended_.store(true, std::memory_order_release);
    }

private:
    std::atomic_ulong refs_{1};
    std::atomic_bool& ended_;
    std::atomic_long& error_;
};

} // namespace

struct VideoWallpaperPlayer::Impl {
    bool mfStarted{};
    bool paused{};
    HRESULT lastError{S_OK};
    std::atomic_bool ended{false};
    std::atomic_long asyncError{S_OK};
    ComPtr<IMFPMediaPlayerCallback> callback;
    ComPtr<IMFPMediaPlayer> player;

    bool EnsureMediaFoundation() {
        if (mfStarted) return true;
        lastError = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        mfStarted = SUCCEEDED(lastError);
        return mfStarted;
    }

    void ResetPlayer() {
        if (player) player->Shutdown();
        player.Reset();
        callback.Reset();
        ended.store(false, std::memory_order_relaxed);
        asyncError.store(S_OK, std::memory_order_relaxed);
        paused = false;
    }
};

VideoWallpaperPlayer::VideoWallpaperPlayer() : impl_(std::make_unique<Impl>()) {}

VideoWallpaperPlayer::~VideoWallpaperPlayer() {
    Stop();
    if (impl_->mfStarted) {
        MFShutdown();
        impl_->mfStarted = false;
    }
}

bool VideoWallpaperPlayer::Start(HWND targetWindow, const std::wstring& path) {
    if (!targetWindow || path.empty()) {
        impl_->lastError = E_INVALIDARG;
        return false;
    }
    if (!impl_->EnsureMediaFoundation()) return false;

    impl_->ResetPlayer();
    auto* callback = new (std::nothrow) PlayerCallback(impl_->ended, impl_->asyncError);
    if (!callback) {
        impl_->lastError = E_OUTOFMEMORY;
        return false;
    }
    impl_->callback.Attach(callback);

    IMFPMediaPlayer* rawPlayer = nullptr;
    impl_->lastError = MFPCreateMediaPlayer(path.c_str(), TRUE, MFP_OPTION_NONE,
                                            impl_->callback.Get(), targetWindow, &rawPlayer);
    if (FAILED(impl_->lastError) || !rawPlayer) {
        if (rawPlayer) rawPlayer->Release();
        impl_->callback.Reset();
        return false;
    }
    impl_->player.Attach(rawPlayer);
    const HRESULT volumeResult = impl_->player->SetVolume(0.0f);
    if (FAILED(volumeResult)) impl_->lastError = volumeResult;
    else impl_->lastError = S_OK;
    impl_->paused = false;
    return true;
}

void VideoWallpaperPlayer::Stop() {
    if (!impl_) return;
    impl_->ResetPlayer();
}

void VideoWallpaperPlayer::Tick() {
    if (!impl_ || !impl_->player) return;

    const HRESULT asyncError = static_cast<HRESULT>(impl_->asyncError.exchange(S_OK, std::memory_order_acq_rel));
    if (FAILED(asyncError)) impl_->lastError = asyncError;

    if (!impl_->ended.exchange(false, std::memory_order_acq_rel)) return;

    PROPVARIANT position{};
    PropVariantInit(&position);
    position.vt = VT_I8;
    position.hVal.QuadPart = 0;
    HRESULT hr = impl_->player->SetPosition(MFP_POSITIONTYPE_100NS, &position);
    if (SUCCEEDED(hr)) hr = impl_->player->Play();
    if (FAILED(hr)) impl_->lastError = hr;
    else impl_->lastError = S_OK;
}

void VideoWallpaperPlayer::SetPaused(bool paused) {
    if (!impl_ || !impl_->player || impl_->paused == paused) return;
    const HRESULT hr = paused ? impl_->player->Pause() : impl_->player->Play();
    if (FAILED(hr)) {
        impl_->lastError = hr;
        return;
    }
    impl_->paused = paused;
}

bool VideoWallpaperPlayer::Active() const {
    return impl_ && impl_->player != nullptr;
}

HRESULT VideoWallpaperPlayer::LastError() const {
    return impl_ ? impl_->lastError : E_UNEXPECTED;
}

std::wstring VideoWallpaperPlayer::LastErrorText() const {
    const HRESULT hr = LastError();
    if (SUCCEEDED(hr)) return {};
    wchar_t text[48]{};
    swprintf_s(text, L"HRESULT 0x%08X", static_cast<unsigned>(hr));
    return text;
}

bool VideoWallpaperPlayer::MediaFoundationAvailable() {
    const HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (SUCCEEDED(hr)) MFShutdown();
    return SUCCEEDED(hr);
}

} // namespace turingdesk
''', encoding="utf-8")

cm = cmake_path.read_text(encoding="utf-8")
old = 'add_executable(TuringDeskWallpaper WIN32\n    src/WallpaperEngine.cpp\n)'
new = 'add_executable(TuringDeskWallpaper WIN32\n    src/WallpaperEngine.cpp\n    src/VideoWallpaperPlayer.cpp\n)'
if old not in cm:
    raise RuntimeError('missing wallpaper target')
cm = cm.replace(old, new, 1)
old = '    windowscodecs\n    comdlg32\n)'
new = '    windowscodecs\n    comdlg32\n    mf\n    mfplat\n    mfplay\n    mfuuid\n)'
if old not in cm:
    raise RuntimeError('missing wallpaper libraries')
cm = cm.replace(old, new, 1)
cmake_path.write_text(cm, encoding="utf-8")

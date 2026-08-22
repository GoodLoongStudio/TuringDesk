#include "turingdesk/VideoWallpaperPlayer.h"
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
    PlayerCallback(std::atomic_bool& ended, std::atomic_bool& mediaReady, std::atomic_long& error)
        : ended_(ended), mediaReady_(mediaReady), error_(error) {}

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
        if (eventHeader->eEventType == MFP_EVENT_TYPE_MEDIAITEM_SET)
            mediaReady_.store(true, std::memory_order_release);
        if (eventHeader->eEventType == MFP_EVENT_TYPE_PLAYBACK_ENDED)
            ended_.store(true, std::memory_order_release);
    }

private:
    std::atomic_ulong refs_{1};
    std::atomic_bool& ended_;
    std::atomic_bool& mediaReady_;
    std::atomic_long& error_;
};

RECT ToRect(const wallpaper::RectF& value) {
    return RECT{
        static_cast<LONG>(value.left + 0.5f),
        static_cast<LONG>(value.top + 0.5f),
        static_cast<LONG>(value.right + 0.5f),
        static_cast<LONG>(value.bottom + 0.5f),
    };
}

MFVideoNormalizedRect NormalizeSource(const wallpaper::RectF& source, float width, float height) {
    MFVideoNormalizedRect result{};
    result.left = source.left / width;
    result.top = source.top / height;
    result.right = source.right / width;
    result.bottom = source.bottom / height;
    return result;
}

} // namespace

struct VideoWallpaperPlayer::Impl {
    bool mfStarted{};
    bool paused{};
    bool placementDirty{true};
    HRESULT lastError{S_OK};
    HWND targetWindow{};
    SIZE lastClientSize{};
    SIZE nativeSize{};
    wallpaper::ScaleMode scaleMode{wallpaper::ScaleMode::Cover};
    float focalX{0.5f};
    float focalY{0.5f};
    std::atomic_bool ended{false};
    std::atomic_bool mediaReady{false};
    std::atomic_long asyncError{S_OK};
    ComPtr<IMFPMediaPlayerCallback> callback;
    ComPtr<IMFPMediaPlayer> player;

    bool EnsureMediaFoundation() {
        if (mfStarted) return true;
        lastError = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        mfStarted = SUCCEEDED(lastError);
        return mfStarted;
    }

    void CaptureClientSize() {
        lastClientSize = {};
        if (!targetWindow) return;
        RECT rect{};
        if (!GetClientRect(targetWindow, &rect)) return;
        lastClientSize.cx = rect.right - rect.left;
        lastClientSize.cy = rect.bottom - rect.top;
    }

    bool ClientSizeChanged() {
        if (!targetWindow) return false;
        RECT rect{};
        if (!GetClientRect(targetWindow, &rect)) return false;
        const SIZE current{rect.right - rect.left, rect.bottom - rect.top};
        if (current.cx == lastClientSize.cx && current.cy == lastClientSize.cy) return false;
        lastClientSize = current;
        placementDirty = true;
        return true;
    }

    void RefreshNativeSize() {
        nativeSize = {};
        if (!player) return;
        SIZE video{};
        SIZE aspect{};
        if (SUCCEEDED(player->GetNativeVideoSize(&video, &aspect))) {
            nativeSize = (aspect.cx > 0 && aspect.cy > 0) ? aspect : video;
        }
    }

    bool ApplyPlacement() {
        if (!player || !targetWindow) return false;
        if (nativeSize.cx <= 0 || nativeSize.cy <= 0) RefreshNativeSize();
        if (nativeSize.cx <= 0 || nativeSize.cy <= 0 || lastClientSize.cx <= 0 || lastClientSize.cy <= 0) return false;

        const wallpaper::ScaleMode effective = scaleMode == wallpaper::ScaleMode::Tile
            ? wallpaper::ScaleMode::Center
            : scaleMode;
        const auto placement = wallpaper::ComputePlacement(
            static_cast<float>(nativeSize.cx), static_cast<float>(nativeSize.cy),
            static_cast<float>(lastClientSize.cx), static_cast<float>(lastClientSize.cy),
            effective, focalX, focalY);

        const MFVideoNormalizedRect source = NormalizeSource(
            placement.source, static_cast<float>(nativeSize.cx), static_cast<float>(nativeSize.cy));
        const RECT destination = ToRect(placement.destination);

        HRESULT hr = player->SetAspectRatioMode(MFVideoARMode_None);
        if (SUCCEEDED(hr)) hr = player->SetVideoPosition(&source, &destination);
        if (FAILED(hr)) {
            lastError = hr;
            return false;
        }
        placementDirty = false;
        lastError = S_OK;
        return true;
    }

    void ResetPlayer() {
        if (player) player->Shutdown();
        player.Reset();
        callback.Reset();
        targetWindow = nullptr;
        lastClientSize = {};
        nativeSize = {};
        ended.store(false, std::memory_order_relaxed);
        mediaReady.store(false, std::memory_order_relaxed);
        asyncError.store(S_OK, std::memory_order_relaxed);
        paused = false;
        placementDirty = true;
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
    auto* callback = new (std::nothrow) PlayerCallback(impl_->ended, impl_->mediaReady, impl_->asyncError);
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
    impl_->targetWindow = targetWindow;
    impl_->CaptureClientSize();
    const HRESULT volumeResult = impl_->player->SetVolume(0.0f);
    if (FAILED(volumeResult)) impl_->lastError = volumeResult;
    else impl_->lastError = S_OK;
    impl_->paused = false;
    impl_->placementDirty = true;
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

    if (impl_->mediaReady.exchange(false, std::memory_order_acq_rel)) {
        impl_->RefreshNativeSize();
        impl_->placementDirty = true;
    }

    const bool resized = impl_->ClientSizeChanged();
    if ((impl_->placementDirty || resized) && impl_->nativeSize.cx > 0 && impl_->nativeSize.cy > 0) {
        impl_->ApplyPlacement();
        UpdateVideo();
    } else if (resized) {
        UpdateVideo();
    }

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

void VideoWallpaperPlayer::UpdateVideo() {
    if (!impl_ || !impl_->player) return;
    const HRESULT hr = impl_->player->UpdateVideo();
    if (FAILED(hr)) impl_->lastError = hr;
}

void VideoWallpaperPlayer::SetPaused(bool paused) {
    if (!impl_ || !impl_->player || impl_->paused == paused) return;
    const HRESULT hr = paused ? impl_->player->Pause() : impl_->player->Play();
    if (FAILED(hr)) {
        impl_->lastError = hr;
        return;
    }
    impl_->paused = paused;
    if (!paused) UpdateVideo();
}

void VideoWallpaperPlayer::SetScaling(wallpaper::ScaleMode mode, float focalX, float focalY) {
    if (!impl_) return;
    impl_->scaleMode = mode;
    impl_->focalX = wallpaper::ClampFocal(focalX);
    impl_->focalY = wallpaper::ClampFocal(focalY);
    impl_->placementDirty = true;
    if (impl_->player && impl_->nativeSize.cx > 0 && impl_->nativeSize.cy > 0) {
        impl_->ApplyPlacement();
        UpdateVideo();
    }
}

SIZE VideoWallpaperPlayer::NativeVideoSize() const {
    return impl_ ? impl_->nativeSize : SIZE{};
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

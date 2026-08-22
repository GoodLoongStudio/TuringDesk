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

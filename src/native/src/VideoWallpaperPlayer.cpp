#include "turingdesk/VideoWallpaperPlayer.h"
#include <mfapi.h>
#include <mfplay.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <new>
#include <sstream>

using Microsoft::WRL::ComPtr;

namespace turingdesk {
namespace {

constexpr double kHundredNsPerSecond = 10000000.0;

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

MFVideoNormalizedRect FullSource() {
    MFVideoNormalizedRect result{};
    result.left = 0.0f;
    result.top = 0.0f;
    result.right = 1.0f;
    result.bottom = 1.0f;
    return result;
}

MFVideoNormalizedRect NormalizeSource(const wallpaper::RectF& source, float width, float height) {
    MFVideoNormalizedRect result{};
    result.left = source.left / width;
    result.top = source.top / height;
    result.right = source.right / width;
    result.bottom = source.bottom / height;
    return result;
}

float ClampVolume(float value) noexcept {
    return std::clamp(value, 0.0f, 1.0f);
}

float ClampRate(float value) noexcept {
    return std::clamp(value, 0.25f, 4.0f);
}

double ReadPlayerTimeSeconds(IMFPMediaPlayer* player, bool duration) {
    if (!player) return -1.0;
    PROPVARIANT value{};
    PropVariantInit(&value);
    const HRESULT hr = duration
        ? player->GetDuration(MFP_POSITIONTYPE_100NS, &value)
        : player->GetPosition(MFP_POSITIONTYPE_100NS, &value);
    double seconds = -1.0;
    if (SUCCEEDED(hr)) {
        if (value.vt == VT_I8) seconds = static_cast<double>(value.hVal.QuadPart) / kHundredNsPerSecond;
        else if (value.vt == VT_UI8) seconds = static_cast<double>(value.uhVal.QuadPart) / kHundredNsPerSecond;
    }
    PropVariantClear(&value);
    return seconds;
}

std::wstring FormatTimeline(double position, double duration) {
    if (position < 0.0) return {};
    const auto pos = static_cast<unsigned long long>(position + 0.5);
    const auto posMin = pos / 60ULL;
    const auto posSec = pos % 60ULL;
    wchar_t text[96]{};
    if (duration > 0.0) {
        const auto dur = static_cast<unsigned long long>(duration + 0.5);
        swprintf_s(text, L"%llu:%02llu / %llu:%02llu", posMin, posSec, dur / 60ULL, dur % 60ULL);
    } else {
        swprintf_s(text, L"%llu:%02llu", posMin, posSec);
    }
    return text;
}

} // namespace

struct VideoWallpaperPlayer::Impl {
    bool mfStarted{};
    bool paused{};
    bool placementDirty{true};
    bool controlsDirty{true};
    bool looping{true};
    bool muted{true};
    float volume{0.0f};
    float playbackRate{1.0f};
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

        MFVideoNormalizedRect source = FullSource();
        DWORD aspectMode = MFVideoARMode_None;

        if (scaleMode == wallpaper::ScaleMode::Cover) {
            const auto placement = wallpaper::ComputePlacement(
                static_cast<float>(nativeSize.cx), static_cast<float>(nativeSize.cy),
                static_cast<float>(lastClientSize.cx), static_cast<float>(lastClientSize.cy),
                wallpaper::ScaleMode::Cover, focalX, focalY);
            source = NormalizeSource(
                placement.source, static_cast<float>(nativeSize.cx), static_cast<float>(nativeSize.cy));
        } else if (scaleMode == wallpaper::ScaleMode::Contain) {
            aspectMode = MFVideoARMode_PreservePicture;
        }

        HRESULT hr = player->SetVideoSourceRect(&source);
        if (SUCCEEDED(hr)) hr = player->SetAspectRatioMode(aspectMode);
        if (FAILED(hr)) {
            lastError = hr;
            return false;
        }
        placementDirty = false;
        lastError = S_OK;
        return true;
    }

    bool ApplyControls() {
        if (!player) return false;
        HRESULT hr = player->SetMute(muted ? TRUE : FALSE);
        if (SUCCEEDED(hr)) hr = player->SetVolume(ClampVolume(volume));
        if (SUCCEEDED(hr)) hr = player->SetRate(ClampRate(playbackRate));
        if (FAILED(hr)) {
            lastError = hr;
            return false;
        }
        controlsDirty = false;
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
        controlsDirty = true;
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
    impl_->paused = false;
    impl_->placementDirty = true;
    impl_->controlsDirty = true;
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
        impl_->controlsDirty = true;
    }

    const bool resized = impl_->ClientSizeChanged();
    if ((impl_->placementDirty || resized) && impl_->nativeSize.cx > 0 && impl_->nativeSize.cy > 0) {
        impl_->ApplyPlacement();
        UpdateVideo();
    } else if (resized) {
        UpdateVideo();
    }

    if (impl_->controlsDirty) impl_->ApplyControls();

    if (!impl_->ended.exchange(false, std::memory_order_acq_rel)) return;
    if (!impl_->looping) {
        impl_->paused = true;
        return;
    }
    Restart();
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

void VideoWallpaperPlayer::SetLooping(bool looping) {
    if (!impl_) return;
    impl_->looping = looping;
}

void VideoWallpaperPlayer::SetMuted(bool muted) {
    if (!impl_) return;
    impl_->muted = muted;
    impl_->controlsDirty = true;
    if (impl_->player) impl_->ApplyControls();
}

void VideoWallpaperPlayer::SetVolume(float volume) {
    if (!impl_) return;
    impl_->volume = ClampVolume(volume);
    impl_->controlsDirty = true;
    if (impl_->player) impl_->ApplyControls();
}

void VideoWallpaperPlayer::SetPlaybackRate(float rate) {
    if (!impl_) return;
    impl_->playbackRate = ClampRate(rate);
    impl_->controlsDirty = true;
    if (impl_->player) impl_->ApplyControls();
}

bool VideoWallpaperPlayer::Restart() {
    if (!impl_ || !impl_->player) return false;
    PROPVARIANT position{};
    PropVariantInit(&position);
    position.vt = VT_I8;
    position.hVal.QuadPart = 0;
    HRESULT hr = impl_->player->SetPosition(MFP_POSITIONTYPE_100NS, &position);
    if (SUCCEEDED(hr) && !impl_->paused) hr = impl_->player->Play();
    if (FAILED(hr)) {
        impl_->lastError = hr;
        return false;
    }
    impl_->ended.store(false, std::memory_order_release);
    impl_->lastError = S_OK;
    UpdateVideo();
    return true;
}

bool VideoWallpaperPlayer::SeekRelativeSeconds(double seconds) {
    if (!impl_ || !impl_->player) return false;
    const double current = PositionSeconds();
    if (current < 0.0) return false;
    double target = std::max(0.0, current + seconds);
    const double duration = DurationSeconds();
    if (duration > 0.0) target = std::min(target, duration);

    PROPVARIANT position{};
    PropVariantInit(&position);
    position.vt = VT_I8;
    position.hVal.QuadPart = static_cast<LONGLONG>(target * kHundredNsPerSecond);
    const HRESULT hr = impl_->player->SetPosition(MFP_POSITIONTYPE_100NS, &position);
    if (FAILED(hr)) {
        impl_->lastError = hr;
        return false;
    }
    impl_->ended.store(false, std::memory_order_release);
    impl_->lastError = S_OK;
    UpdateVideo();
    return true;
}

SIZE VideoWallpaperPlayer::NativeVideoSize() const {
    return impl_ ? impl_->nativeSize : SIZE{};
}

bool VideoWallpaperPlayer::Active() const {
    return impl_ && impl_->player != nullptr;
}

bool VideoWallpaperPlayer::Looping() const {
    return impl_ && impl_->looping;
}

bool VideoWallpaperPlayer::Muted() const {
    return !impl_ || impl_->muted;
}

float VideoWallpaperPlayer::Volume() const {
    return impl_ ? impl_->volume : 0.0f;
}

float VideoWallpaperPlayer::PlaybackRate() const {
    return impl_ ? impl_->playbackRate : 1.0f;
}

double VideoWallpaperPlayer::PositionSeconds() const {
    return impl_ ? ReadPlayerTimeSeconds(impl_->player.Get(), false) : -1.0;
}

double VideoWallpaperPlayer::DurationSeconds() const {
    return impl_ ? ReadPlayerTimeSeconds(impl_->player.Get(), true) : -1.0;
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

std::wstring VideoWallpaperPlayer::DiagnosticsText() const {
    if (!impl_) return L"Media Foundation player unavailable";
    std::wostringstream text;
    text << L"Media Foundation MFPlay / EVR";
    if (impl_->nativeSize.cx > 0 && impl_->nativeSize.cy > 0)
        text << L" · " << impl_->nativeSize.cx << L"×" << impl_->nativeSize.cy;
    const std::wstring audio = impl_->muted
        ? std::wstring(L"静音")
        : std::wstring(L"音量 ") + std::to_wstring(static_cast<int>(impl_->volume * 100.0f)) + L"%";
    text << L" · " << audio
         << L" · " << impl_->playbackRate << L"×"
         << L" · " << (impl_->looping ? L"循环" : L"单次");
    const auto timeline = FormatTimeline(PositionSeconds(), DurationSeconds());
    if (!timeline.empty()) text << L" · " << timeline;
    if (impl_->player) {
        float slowest = 0.0f;
        float fastest = 0.0f;
        if (SUCCEEDED(impl_->player->GetSupportedRates(TRUE, &slowest, &fastest)))
            text << L" · 支持速率 " << slowest << L"–" << fastest << L"×";
    }
    text << L" · 硬件解码由 Windows Media Foundation/EVR 自动协商";
    if (FAILED(impl_->lastError)) text << L" · " << LastErrorText();
    return text.str();
}

bool VideoWallpaperPlayer::MediaFoundationAvailable() {
    const HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (SUCCEEDED(hr)) MFShutdown();
    return SUCCEEDED(hr);
}

} // namespace turingdesk

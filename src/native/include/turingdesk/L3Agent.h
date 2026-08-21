#pragma once
#include <atomic>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <windows.h>
#include <winhttp.h>

namespace turingdesk {

struct ModelConfig {
    std::wstring providerId{L"deepseek"};
    std::wstring baseUrl{L"https://api.deepseek.com"};
    std::wstring model{L"deepseek-v4-flash"};
};

class L3Agent {
public:
    using DeltaCallback = std::function<void(std::wstring)>;
    using DoneCallback = std::function<void(std::wstring)>;

    L3Agent();
    ~L3Agent();

    bool TryHandleLocal(const std::wstring& input, std::wstring& reply, bool& consumedSecret);
    void AskAsync(std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone);
    void Stop();
    bool Busy() const noexcept { return busy_.load(); }
    const ModelConfig& Config() const noexcept { return config_; }
    bool HasApiKey() const;

private:
    struct ChatTurn {
        std::wstring user;
        std::wstring assistant;
    };

    ModelConfig LoadConfig() const;
    bool SaveConfig(const ModelConfig& config) const;
    std::wstring LoadApiKey() const;
    bool SaveApiKey(const std::wstring& key) const;
    void RunRequest(std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone, std::stop_token stopToken);
    void ClearConversation();

    ModelConfig config_;
    std::jthread worker_;
    std::atomic_bool busy_{false};
    std::atomic<HINTERNET> activeRequest_{nullptr};
    std::mutex conversationMutex_;
    std::vector<ChatTurn> conversation_;
};

} // namespace turingdesk

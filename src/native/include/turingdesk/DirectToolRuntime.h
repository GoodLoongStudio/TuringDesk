#pragma once
#include "turingdesk/L3Agent.h"
#include <atomic>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <windows.h>
#include <winhttp.h>

namespace turingdesk {

class DirectToolRuntime {
public:
    using DeltaCallback = L3Agent::DeltaCallback;
    using DoneCallback = L3Agent::DoneCallback;

    DirectToolRuntime() = default;
    ~DirectToolRuntime();

    bool CanHandle(const L3Agent& agent) const;
    std::wstring StatusText(const L3Agent& agent) const;
    void AskAsync(const L3Agent& agent, std::wstring prompt, DeltaCallback onDelta, DoneCallback onDone);
    void Stop();
    void ResetSession();

private:
    struct ChatTurn {
        std::wstring user;
        std::wstring assistant;
    };

    void RunRequest(const L3Agent& agent,
                    std::wstring prompt,
                    DeltaCallback onDelta,
                    DoneCallback onDone,
                    std::stop_token stopToken);

    std::jthread worker_;
    std::atomic_bool busy_{false};
    std::atomic<HINTERNET> activeRequest_{nullptr};
    std::mutex conversationMutex_;
    std::vector<ChatTurn> conversation_;
};

} // namespace turingdesk

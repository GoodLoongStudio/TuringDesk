#pragma once
#include <windows.h>
#include <memory>
#include <string>

namespace turingdesk {

class HarnessProcessManager {
public:
    HarnessProcessManager();
    ~HarnessProcessManager();

    HarnessProcessManager(const HarnessProcessManager&) = delete;
    HarnessProcessManager& operator=(const HarnessProcessManager&) = delete;

    bool Start();
    void Stop();
    bool Running() const;
    bool WaitUntilReady(DWORD timeoutMs);
    const std::wstring& LastError() const;

    static std::wstring DefaultUrl();
    static std::wstring BuildLaunchCommand();
    static bool SelfTest();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace turingdesk

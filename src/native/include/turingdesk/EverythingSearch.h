#pragma once
#include "turingdesk/SearchTypes.h"
#include <windows.h>
#include <atomic>
#include <memory>
#include <string>
#include <vector>

namespace turingdesk {

// Compatibility name kept temporarily so the L2 call sites can move to goz
// without coupling the Search UI to the backend migration. No Everything
// executable, IPC protocol, or service is used by this implementation.
class EverythingSearch {
public:
    EverythingSearch();

    bool Available() const;
    bool Query(HWND replyWindow, const std::wstring& query, DWORD maxResults = 12) const;
    bool HandleCopyData(const COPYDATASTRUCT* copyData, std::vector<SearchResult>& results) const;
    bool SelfTest() const;
    void Shutdown() const;

    static constexpr DWORD kReplyId = 0x5444475A; // TDGZ

private:
    struct SharedState {
        std::atomic_uint64_t generation{0};
    };

    static std::wstring FindClientBinary();
    static bool PipeAvailable();

    std::shared_ptr<SharedState> state_;
};

} // namespace turingdesk

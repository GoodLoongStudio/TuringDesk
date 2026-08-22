#pragma once
#include "turingdesk/SearchTypes.h"
#include <windows.h>
#include <atomic>
#include <memory>
#include <string>
#include <vector>

namespace turingdesk {

// L2 file-search adapter. TuringDesk owns UI/ranking while the pinned goz
// runtime owns the NTFS MFT + USN index and authenticated named-pipe protocol.
class GozSearch {
public:
    GozSearch();

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

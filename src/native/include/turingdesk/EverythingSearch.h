#pragma once
#include "turingdesk/SearchTypes.h"
#include <windows.h>
#include <string>
#include <vector>

namespace turingdesk {

class EverythingSearch {
public:
    bool Available() const;
    bool Query(HWND replyWindow, const std::wstring& query, DWORD maxResults = 12) const;
    bool HandleCopyData(const COPYDATASTRUCT* copyData, std::vector<SearchResult>& results) const;
    bool SelfTest() const;

    static constexpr DWORD kReplyId = 0x54444631; // TDF1

private:
    static HWND FindEverythingWindow();
};

} // namespace turingdesk

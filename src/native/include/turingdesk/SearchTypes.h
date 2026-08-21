#pragma once
#include <string>

namespace turingdesk {

enum class ResultKind { App, File, Folder, Answer, Status };

struct SearchResult {
    ResultKind kind{};
    std::wstring title;
    std::wstring subtitle;
    std::wstring target;
    double score{};
};

} // namespace turingdesk

#pragma once
#include <string>
#include <string_view>

namespace turingdesk {

struct NativeToolResult {
    bool success{};
    std::wstring message;
};

// Canonical Codex app-server dynamicTools payload. The same logical registry
// is also used by Direct provider adapters so L3 capabilities do not fork by runtime.
std::string NativeToolDefinitionsJson();
NativeToolResult ExecuteNativeTool(std::string_view toolName, std::string_view argumentsJson);

} // namespace turingdesk

#include "turingdesk/L3Agent.h"
#include <windows.h>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <system_error>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

constexpr std::uint32_t kSessionMagic = 0x334c4454;
constexpr std::uint32_t kSessionVersion = 1;

std::wstring Lower(std::wstring value) {
    for (auto& ch : value) ch = static_cast<wchar_t>(std::towlower(ch));
    return value;
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) return {};
    std::string out(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count, nullptr, nullptr);
    return out;
}

std::uint64_t SessionHash(const ModelConfig& config) {
    const std::wstring key = Lower(config.providerId) + L"\n" + Lower(config.baseUrl) + L"\n" +
                             config.endpoint + L"\n" + config.model;
    std::uint64_t hash = 1469598103934665603ull;
    for (wchar_t ch : key) {
        hash ^= static_cast<std::uint16_t>(ch);
        hash *= 1099511628211ull;
    }
    return hash;
}

fs::path SessionPath(const fs::path& localAppData, const ModelConfig& config) {
    wchar_t name[32]{};
    swprintf_s(name, L"%016llx.bin", static_cast<unsigned long long>(SessionHash(config)));
    return localAppData / L"TuringDesk" / L"l3-sessions" / name;
}

void WriteU32(std::ostream& stream, std::uint32_t value) {
    stream.write(reinterpret_cast<const char*>(&value), sizeof(value));
}

bool WriteField(std::ostream& stream, const std::wstring& value) {
    const auto utf8 = WideToUtf8(value);
    WriteU32(stream, static_cast<std::uint32_t>(utf8.size()));
    if (!utf8.empty()) stream.write(utf8.data(), static_cast<std::streamsize>(utf8.size()));
    return static_cast<bool>(stream);
}

bool SeedSession(const fs::path& path, const std::wstring& user, const std::wstring& assistant) {
    std::error_code ec;
    fs::create_directories(path.parent_path(), ec);
    if (ec) return false;
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) return false;
    WriteU32(stream, kSessionMagic);
    WriteU32(stream, kSessionVersion);
    WriteU32(stream, 1);
    return WriteField(stream, user) && WriteField(stream, assistant);
}

class LocalAppDataScope {
public:
    LocalAppDataScope() {
        wchar_t current[32768]{};
        const DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", current, static_cast<DWORD>(std::size(current)));
        hadOriginal_ = count > 0 && count < std::size(current);
        if (hadOriginal_) original_.assign(current, count);

        wchar_t temp[MAX_PATH]{};
        const DWORD tempCount = GetTempPathW(static_cast<DWORD>(std::size(temp)), temp);
        if (!tempCount || tempCount >= std::size(temp)) return;
        root_ = fs::path(temp) / (L"TuringDesk-L3SelfTest-" + std::to_wstring(GetCurrentProcessId()));
        std::error_code ec;
        fs::remove_all(root_, ec);
        fs::create_directories(root_, ec);
        if (ec) return;
        active_ = SetEnvironmentVariableW(L"LOCALAPPDATA", root_.c_str()) != FALSE;
    }

    ~LocalAppDataScope() {
        if (active_) SetEnvironmentVariableW(L"LOCALAPPDATA", hadOriginal_ ? original_.c_str() : nullptr);
        std::error_code ec;
        if (!root_.empty()) fs::remove_all(root_, ec);
    }

    bool Active() const noexcept { return active_; }
    const fs::path& Root() const noexcept { return root_; }

private:
    bool active_{};
    bool hadOriginal_{};
    std::wstring original_;
    fs::path root_;
};

bool SetProvider(L3Agent& agent, const wchar_t* model) {
    std::wstring reply;
    bool consumedSecret = false;
    const std::wstring command = std::wstring(L"/provider http://127.0.0.1:11434 ") + model;
    return agent.TryHandleLocal(command, reply, consumedSecret) && !reply.empty() && !consumedSecret;
}

} // namespace

bool RunL3PersistenceSelfTest() {
    LocalAppDataScope local;
    if (!local.Active()) return false;

    L3Agent agent;
    if (!SetProvider(agent, L"turingdesk-selftest-a")) return false;
    const ModelConfig configA = agent.Config();
    if (!SetProvider(agent, L"turingdesk-selftest-b")) return false;
    const ModelConfig configB = agent.Config();

    const auto pathA = SessionPath(local.Root(), configA);
    const auto pathB = SessionPath(local.Root(), configB);
    if (pathA == pathB) return false;
    if (!SeedSession(pathA, L"user-a", L"assistant-a") || !SeedSession(pathB, L"user-b", L"assistant-b")) return false;

    if (!SetProvider(agent, L"turingdesk-selftest-a") || agent.ConversationTurnCountForSelfTest() != 1) return false;
    if (!SetProvider(agent, L"turingdesk-selftest-b") || agent.ConversationTurnCountForSelfTest() != 1) return false;

    std::wstring reply;
    bool consumedSecret = false;
    if (!agent.TryHandleLocal(L"/new", reply, consumedSecret) || consumedSecret) return false;
    if (agent.ConversationTurnCountForSelfTest() != 0 || fs::exists(pathB) || !fs::exists(pathA)) return false;

    if (!SetProvider(agent, L"turingdesk-selftest-a") || agent.ConversationTurnCountForSelfTest() != 1) return false;
    return true;
}

} // namespace turingdesk

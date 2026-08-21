$ErrorActionPreference = "Stop"

function Read-Utf8([string]$Path) {
    [System.IO.File]::ReadAllText((Resolve-Path $Path), [System.Text.Encoding]::UTF8)
}
function Write-Utf8([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Resolve-Path $Path), $Text, [System.Text.UTF8Encoding]::new($false))
}
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $Text = Read-Utf8 $Path
    if (-not $Text.Contains($Old)) { throw "Expected block not found in $Path" }
    Write-Utf8 $Path ($Text.Replace($Old, $New))
}

$Native = "src/native/src/NativeTools.cpp"
$Codex = "src/native/src/CodexRuntime.cpp"

# NativeTools compile/lifetime hardening.
Replace-Exact $Native `
'#include <cctype>
#include <filesystem>
#include <fstream>
#include <sstream>' `
'#include <cctype>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <sstream>'

$DispatchNeedle = @'
class DispatchPtr {
'@
$Apartment = @'
class ComApartment {
public:
    ComApartment() : hr_(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)), uninitialize_(SUCCEEDED(hr_)) {}
    ~ComApartment() { if (uninitialize_) CoUninitialize(); }
    HRESULT Result() const { return hr_; }
    bool Ready() const { return SUCCEEDED(hr_) || hr_ == RPC_E_CHANGED_MODE; }
private:
    HRESULT hr_{};
    bool uninitialize_{};
};

class DispatchPtr {
'@
Replace-Exact $Native $DispatchNeedle $Apartment

$OldInit = @'
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool uninitialize = SUCCEEDED(init);
    if (FAILED(init) && init != RPC_E_CHANGED_MODE) return {false, L"初始化 Office COM 失败：" + HResultText(init)};

    CLSID clsid{};
'@
$NewInit = @'
    ComApartment apartment;
    if (!apartment.Ready()) return {false, L"初始化 Office COM 失败：" + HResultText(apartment.Result())};

    CLSID clsid{};
'@
Replace-Exact $Native $OldInit $NewInit

$Text = Read-Utf8 $Native
$Text = $Text.Replace('        if (uninitialize) CoUninitialize();`n', '')
$Text = $Text.Replace('    if (uninitialize) CoUninitialize();`n', '')
# PowerShell single-quoted strings above contain literal backtick-n, also handle actual line forms.
$Text = $Text.Replace("        if (uninitialize) CoUninitialize();`n", '')
$Text = $Text.Replace("    if (uninitialize) CoUninitialize();`n", '')
Write-Utf8 $Native $Text

$OldOpen = @'
    const auto name = SanitizeFileName(Utf8ToWide(ExtractJsonString(arguments, "file_name")), L"");
    if (name.empty()) return {false, L"缺少 file_name。"};
'@
$NewOpen = @'
    const auto rawName = Utf8ToWide(ExtractJsonString(arguments, "file_name"));
    if (rawName.empty()) return {false, L"缺少 file_name。"};
    const auto name = SanitizeFileName(rawName, L"");
'@
Replace-Exact $Native $OldOpen $NewOpen

# Codex runtime: register dynamic tools and service item/tool/call requests.
Replace-Exact $Codex `
'#include "turingdesk/CodexRuntime.h"' `
'#include "turingdesk/CodexRuntime.h"
#include "turingdesk/NativeTools.h"'

$HasIdEnd = @'
    return any && value == id;
}

std::wstring TomlEscape
'@
$HasIdReplacement = @'
    return any && value == id;
}

bool TryReadRequestId(std::string_view json, long long& id) {
    auto pos = json.find("\"id\"");
    if (pos == std::string_view::npos) return false;
    pos = json.find(':', pos + 4);
    if (pos == std::string_view::npos) return false;
    ++pos;
    while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t')) ++pos;
    bool negative = false;
    if (pos < json.size() && json[pos] == '-') { negative = true; ++pos; }
    long long value = 0;
    bool any = false;
    while (pos < json.size() && json[pos] >= '0' && json[pos] <= '9') {
        any = true;
        value = value * 10 + (json[pos] - '0');
        ++pos;
    }
    if (!any) return false;
    id = negative ? -value : value;
    return true;
}

std::wstring TomlEscape
'@
Replace-Exact $Codex $HasIdEnd $HasIdReplacement

Replace-Exact $Codex `
'\"capabilities\":{\"experimentalApi\":false}}}";' `
'\"capabilities\":{\"experimentalApi\":true}}}";'

$OldThread = @'
    const std::string threadStart =
        "{\"id\":" + std::to_string(threadIdRequest) +
        ",\"method\":\"thread/start\",\"params\":{\"model\":\"" + EscapeJson(setup.model) +
        "\",\"modelProvider\":\"turingdesk\",\"cwd\":\"" + EscapeJson(DesktopDirectory()) +
        "\",\"ephemeral\":true}}";
'@
$NewThread = @'
    const std::wstring developerInstructions =
        L"You are TuringDesk Native Agent. Use the provided native dynamic tools when the user asks to create, open, or inspect supported desktop artifacts. "
        L"Do not claim you cannot access the desktop when a matching tool exists. Never report an action as successful until its tool result reports success. "
        L"Prefer native tools over shell commands for supported tasks.";
    const std::string threadStart =
        "{\"id\":" + std::to_string(threadIdRequest) +
        ",\"method\":\"thread/start\",\"params\":{\"model\":\"" + EscapeJson(setup.model) +
        "\",\"modelProvider\":\"turingdesk\",\"cwd\":\"" + EscapeJson(DesktopDirectory()) +
        "\",\"developerInstructions\":\"" + EscapeJson(developerInstructions) +
        "\",\"dynamicTools\":" + NativeToolDefinitionsJson() +
        ",\"ephemeral\":true}}";
'@
Replace-Exact $Codex $OldThread $NewThread

$EventNeedle = @'
        const auto method = ExtractJsonString(line, "\"method\"");
        if (method == "item/agentMessage/delta") {
'@
$EventReplacement = @'
        const auto method = ExtractJsonString(line, "\"method\"");
        if (method == "item/tool/call") {
            long long serverRequestId = 0;
            const auto tool = ExtractJsonString(line, "\"tool\"");
            NativeToolResult result;
            if (!TryReadRequestId(line, serverRequestId)) {
                result = {false, L"TuringDesk 无法解析 Codex tool request id。"};
            } else if (tool.empty()) {
                result = {false, L"Codex tool request 缺少 tool 名称。"};
            } else {
                result = ExecuteNativeTool(tool, line);
            }
            if (serverRequestId != 0) {
                const std::string reply =
                    "{\"id\":" + std::to_string(serverRequestId) +
                    ",\"result\":{\"contentItems\":[{\"type\":\"inputText\",\"text\":\"" + EscapeJson(result.message) +
                    "\"}],\"success\":" + (result.success ? "true" : "false") + "}}";
                if (!WriteLine(reply)) {
                    if (onDone) onDone(L"Codex Native Tool 结果回传失败");
                    CleanupProcess();
                    return;
                }
            }
            continue;
        }
        if (method == "item/agentMessage/delta") {
'@
Replace-Exact $Codex $EventNeedle $EventReplacement

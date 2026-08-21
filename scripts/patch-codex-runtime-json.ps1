$ErrorActionPreference = "Stop"
$Path = "src/native/src/CodexRuntime.cpp"
$Text = [System.IO.File]::ReadAllText((Resolve-Path $Path), [System.Text.Encoding]::UTF8)

$OldError = 'if (line.find("\"error\"") != std::string::npos) {'
$NewError = 'if (line.find("\"result\"") == std::string::npos && line.find("\"error\"") != std::string::npos) {'
$Count = ([regex]::Matches($Text, [regex]::Escape($OldError))).Count
if ($Count -ne 2) { throw "Expected 2 JSON-RPC error checks, found $Count" }
$Text = $Text.Replace($OldError, $NewError)

$OldEvents = @'
        if (line.find("\"method\":\"item/agentMessage/delta\"") != std::string::npos) {
            const auto delta = ExtractJsonString(line, "\"delta\"");
            if (!delta.empty() && onDelta) onDelta(Utf8ToWide(delta));
            continue;
        }
        if (line.find("\"method\":\"turn/completed\"") != std::string::npos) {
            std::wstring done;
            if (line.find("\"status\":\"failed\"") != std::string::npos) {
                const auto message = ExtractJsonString(line, "\"message\"");
                done = message.empty() ? L"Codex turn 失败" : Utf8ToWide(message);
            } else if (line.find("\"status\":\"interrupted\"") != std::string::npos) {
                done = L"Codex turn 已中断";
            }
            if (onDone) onDone(std::move(done));
            return;
        }
'@
$NewEvents = @'
        const auto method = ExtractJsonString(line, "\"method\"");
        if (method == "item/agentMessage/delta") {
            const auto delta = ExtractJsonString(line, "\"delta\"");
            if (!delta.empty() && onDelta) onDelta(Utf8ToWide(delta));
            continue;
        }
        if (method == "turn/completed") {
            std::wstring done;
            const auto status = ExtractJsonString(line, "\"status\"");
            if (status == "failed") {
                const auto message = ExtractJsonString(line, "\"message\"");
                done = message.empty() ? L"Codex turn 失败" : Utf8ToWide(message);
            } else if (status == "interrupted") {
                done = L"Codex turn 已中断";
            }
            if (onDone) onDone(std::move(done));
            return;
        }
'@
if (-not $Text.Contains($OldEvents)) { throw "Codex event parser block not found" }
$Text = $Text.Replace($OldEvents, $NewEvents)

$OldDesktop = @'
std::wstring DesktopDirectory() {
    wchar_t profile[32768]{};
    const DWORD count = GetEnvironmentVariableW(L"USERPROFILE", profile, static_cast<DWORD>(std::size(profile)));
    if (count > 0 && count < std::size(profile)) return (fs::path(profile) / L"Desktop").wstring();
    return ModuleDirectory().wstring();
}
'@
$NewDesktop = @'
std::wstring DesktopDirectory() {
    wchar_t profile[32768]{};
    const DWORD count = GetEnvironmentVariableW(L"USERPROFILE", profile, static_cast<DWORD>(std::size(profile)));
    if (count > 0 && count < std::size(profile)) {
        const auto desktop = fs::path(profile) / L"Desktop";
        std::error_code ec;
        if (fs::exists(desktop, ec) && fs::is_directory(desktop, ec)) return desktop.wstring();
    }
    return ModuleDirectory().wstring();
}
'@
if (-not $Text.Contains($OldDesktop)) { throw "DesktopDirectory block not found" }
$Text = $Text.Replace($OldDesktop, $NewDesktop)

[System.IO.File]::WriteAllText((Resolve-Path $Path), $Text, [System.Text.UTF8Encoding]::new($false))

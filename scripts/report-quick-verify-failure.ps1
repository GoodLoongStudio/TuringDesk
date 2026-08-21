param(
    [string]$Stage = "unknown",
    [int]$ExitCode = 1,
    [string]$Repository = "GoodLoongStudio/TuringDesk"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ReportRoot = Join-Path $Root ".tools\quick-verify\reports"
$LogRoot = Join-Path $env:LOCALAPPDATA "TuringDesk\logs"
$DesktopLog = Join-Path $LogRoot "desktop.log"
$CrashReport = Join-Path $LogRoot "desktop-crash-latest.json"
$SceneLog = Join-Path $LogRoot "scene-engine.log"
$EngineStatus = Join-Path $env:LOCALAPPDATA "TuringDesk\desktop-engine-status.json"

function Protect-DiagnosticText([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return "" }
    $value = $Text

    foreach ($entry in @(
        @($env:USERPROFILE, "<USERPROFILE>"),
        @($env:LOCALAPPDATA, "<LOCALAPPDATA>"),
        @($env:APPDATA, "<APPDATA>")
    )) {
        $path = [string]$entry[0]
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $value = [regex]::Replace(
            $value,
            [regex]::Escape($path),
            [string]$entry[1],
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }

    $value = [regex]::Replace($value, '(?i)(bearer\s+)[A-Za-z0-9._~+/=-]{10,}', '$1<REDACTED>')
    $value = [regex]::Replace($value, '(?i)((?:api[_-]?key|token|secret)\s*[:=]\s*["'']?)[^"''\s,;]{8,}', '$1<REDACTED>')
    $value = [regex]::Replace($value, '(?i)\bsk-[A-Za-z0-9_-]{10,}\b', '<REDACTED_KEY>')
    return $value
}

function Read-DiagnosticTail([string]$Path, [int]$Lines = 120, [int]$MaxCharacters = 16000) {
    if (-not (Test-Path -LiteralPath $Path)) { return "<not present>" }
    try {
        $text = (Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction Stop) -join "`n"
        $text = Protect-DiagnosticText $text
        if ($text.Length -gt $MaxCharacters) {
            $text = "<truncated to last $MaxCharacters characters>`n" + $text.Substring($text.Length - $MaxCharacters)
        }
        return $text
    }
    catch {
        return "<could not read: $($_.Exception.Message)>"
    }
}

function Read-DiagnosticFile([string]$Path, [int]$MaxCharacters = 20000) {
    if (-not (Test-Path -LiteralPath $Path)) { return "<not present>" }
    try {
        $text = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        $text = Protect-DiagnosticText $text
        if ($text.Length -gt $MaxCharacters) {
            $text = $text.Substring(0, $MaxCharacters) + "`n<truncated>"
        }
        return $text
    }
    catch {
        return "<could not read: $($_.Exception.Message)>"
    }
}

function Resolve-Executable([string]$Name, [string[]]$Candidates) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        if (Test-Path -LiteralPath $expanded) { return $expanded }
    }
    return $null
}

function Get-Commit {
    $git = Resolve-Executable "git" @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    if (-not $git) { return "unknown" }
    try {
        Push-Location $Root
        $sha = (& $git rev-parse HEAD 2>$null).Trim()
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($sha)) { return $sha }
    }
    catch { }
    finally { Pop-Location }
    return "unknown"
}

function Get-Fingerprint([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        $hash = $sha.ComputeHash($bytes)
        return (([BitConverter]::ToString($hash) -replace '-', '').Substring(0, 12)).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Force -Path $ReportRoot | Out-Null

    $commit = Get-Commit
    $shortCommit = if ($commit.Length -ge 7) { $commit.Substring(0, 7) } else { $commit }
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    $timestamp = Get-Date

    $crashText = Read-DiagnosticFile $CrashReport 22000
    $desktopText = Read-DiagnosticTail $DesktopLog 160 18000
    $sceneText = Read-DiagnosticTail $SceneLog 100 10000
    $statusText = Read-DiagnosticFile $EngineStatus 8000

    $crashSignature = ""
    if (Test-Path -LiteralPath $CrashReport) {
        try {
            $crash = Get-Content -LiteralPath $CrashReport -Raw | ConvertFrom-Json
            $crashSignature = "$($crash.exceptionType)|$($crash.message)"
        }
        catch { }
    }

    $fingerprint = Get-Fingerprint "$commit|$Stage|$ExitCode|$crashSignature"
    $marker = "turingdesk-quick-verify:$fingerprint"
    $reportPath = Join-Path $ReportRoot ("quick-verify-{0:yyyyMMdd-HHmmss}-{1}.md" -f $timestamp, $fingerprint)

    $body = @(
        "<!-- $marker -->",
        "# TuringDesk Quick Verify failure",
        "",
        "Automatically generated by the dedicated Windows test-machine verifier.",
        "",
        "- Stage: ``$Stage``",
        "- Exit code: ``$ExitCode``",
        "- Commit: ``$commit``",
        "- Architecture: ``$architecture``",
        "- OS: ``$os``",
        "- Time: ``$($timestamp.ToString('yyyy-MM-dd HH:mm:ss zzz'))``",
        "- Diagnostic fingerprint: ``$fingerprint``",
        "",
        "## Desktop crash report",
        "",
        '```json',
        $crashText,
        '```',
        "",
        "## Desktop log tail",
        "",
        '```text',
        $desktopText,
        '```',
        "",
        "## Scene engine log tail",
        "",
        '```text',
        $sceneText,
        '```',
        "",
        "## Desktop engine status",
        "",
        '```json',
        $statusText,
        '```',
        "",
        "The report is redacted before publication: common API-key/token forms and user profile paths are removed."
    ) -join "`n"

    Set-Content -LiteralPath $reportPath -Value $body -Encoding UTF8
    Write-Host "Quick Verify diagnostic report: $reportPath" -ForegroundColor Yellow

    if ($env:TURINGDESK_DISABLE_GITHUB_REPORT -eq "1") {
        Write-Host "GitHub auto-report disabled by TURINGDESK_DISABLE_GITHUB_REPORT=1." -ForegroundColor DarkGray
        exit 0
    }

    $gh = Resolve-Executable "gh" @(
        "$env:ProgramFiles\GitHub CLI\gh.exe",
        "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe",
        "$env:USERPROFILE\scoop\apps\gh\current\bin\gh.exe"
    )
    if (-not $gh) {
        Write-Host "GitHub CLI (gh) not found; report kept locally. Install gh and run 'gh auth login' to enable automatic issue upload." -ForegroundColor Yellow
        exit 0
    }

    & $gh auth status --hostname github.com *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "GitHub CLI is not authenticated; report kept locally. Run 'gh auth login' once on the test machine." -ForegroundColor Yellow
        exit 0
    }

    $existing = @()
    try {
        $existingJson = & $gh issue list --repo $Repository --state open --search $marker --json number,url --limit 1 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingJson)) {
            $existing = @($existingJson | ConvertFrom-Json)
        }
    }
    catch { $existing = @() }

    if ($existing.Count -gt 0) {
        $number = [int]$existing[0].number
        & $gh issue comment $number --repo $Repository --body-file $reportPath | Out-Host
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Updated existing GitHub issue: $($existing[0].url)" -ForegroundColor Green
            exit 0
        }
    }

    $title = "[quick-verify] $Stage failed @ $shortCommit ($architecture)"
    $created = & $gh issue create --repo $Repository --title $title --body-file $reportPath 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Created GitHub failure issue: $created" -ForegroundColor Green
    }
    else {
        Write-Host "GitHub issue upload failed; report remains local: $created" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "Could not prepare/upload Quick Verify diagnostics: $($_.Exception.Message)" -ForegroundColor Yellow
}

exit 0

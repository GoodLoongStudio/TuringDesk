param(
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = "Stop"
$RuntimeRoot = Join-Path $RepoRoot "runtime\arm64"
$LockPath = Join-Path $RuntimeRoot "runtime-lock.json"
$ManifestPath = Join-Path $RuntimeRoot "runtime-manifest.json"
$CompletePath = Join-Path $RuntimeRoot ".complete"

function Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

function Resolve-RequiredFile([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath)) { throw "Runtime manifest contains an empty path" }
    $path = Join-Path $RuntimeRoot ($RelativePath -replace '/', '\')
    if (-not (Test-Path $path -PathType Leaf)) { throw "RuntimeBundle file missing: $path" }
    return $path
}

function Verify-Hash([string]$RelativePath, [string]$Expected) {
    $path = Resolve-RequiredFile $RelativePath
    $actual = Sha256 $path
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "RuntimeBundle hash mismatch: $RelativePath`nExpected: $Expected`nActual:   $actual"
    }
    Write-Host "OK  $RelativePath" -ForegroundColor DarkGray
}

foreach ($required in @($LockPath, $ManifestPath, $CompletePath)) {
    if (-not (Test-Path $required -PathType Leaf)) { throw "RuntimeBundle is incomplete: $required" }
}

$lock = Get-Content $LockPath -Raw | ConvertFrom-Json
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if ($lock.architecture -ne "arm64" -or $manifest.architecture -ne "arm64") {
    throw "RuntimeBundle must be ARM64"
}
if ([int]$manifest.schema -ne 1) { throw "Unsupported RuntimeBundle manifest schema: $($manifest.schema)" }

$lockHash = Sha256 $LockPath
if ([string]::IsNullOrWhiteSpace([string]$manifest.lockSha256) -or
    $lockHash -ne ([string]$manifest.lockSha256).ToLowerInvariant()) {
    throw "RuntimeBundle was generated from a different runtime-lock.json. Re-run the vendoring workflow."
}

Verify-Hash ([string]$manifest.node.archive) ([string]$manifest.node.sha256)
Verify-Hash ([string]$manifest.deepseekHarness.archive) ([string]$manifest.deepseekHarness.sha256)
Verify-Hash ([string]$manifest.everything.archive) ([string]$manifest.everything.sha256)
Verify-Hash ([string]$manifest.codex.archive) ([string]$manifest.codex.sha256)
Verify-Hash ([string]$manifest.webview2Sdk.loader) ([string]$manifest.webview2Sdk.sha256)

$webHeader = Join-Path $RuntimeRoot "webview2-sdk\build\native\include\WebView2.h"
if (-not (Test-Path $webHeader -PathType Leaf)) { throw "Vendored WebView2.h is missing" }

Write-Host "TuringDesk ARM64 RuntimeBundle integrity: PASS" -ForegroundColor Green
Write-Host "Node $($manifest.node.version) / DeepSeek Harness $($manifest.deepseekHarness.version) / Everything $($manifest.everything.version) / Codex $($manifest.codex.release) / WebView2 SDK $($manifest.webview2Sdk.version)" -ForegroundColor DarkGray

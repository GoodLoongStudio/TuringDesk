param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest\HarnessRuntime"),
    [string]$NodeVersion = "24.19.0",
    [string]$DshVersion = "0.1.0-rc.7",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$core = Join-Path $PSScriptRoot "bootstrap-harness-runtime-core-arm64.ps1"
if (-not (Test-Path $core -PathType Leaf)) {
    throw "Harness bootstrap core is missing: $core"
}

& $core -Destination $Destination -NodeVersion $NodeVersion -DshVersion $DshVersion -Force:$Force
if ($LASTEXITCODE -ne 0) {
    throw "Harness bootstrap failed with exit code $LASTEXITCODE"
}

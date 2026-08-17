param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-arm64",
    [string]$Version = "v0.14.0",
    [string]$NodeVersion = "22.19.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$ArtifactsRoot = Join-Path $Root "artifacts"
$PayloadRoot = Join-Path $ArtifactsRoot "TuringDesk-$Version-$RuntimeIdentifier"
$InstallerProject = Join-Path $Root "installer\TuringDesk.Installer.wixproj"
$InstallerBuildRoot = Join-Path $ArtifactsRoot "installer"
$FinalMsi = Join-Path $ArtifactsRoot "TuringDesk-$Version-$RuntimeIdentifier.msi"

$InstallerPlatform = switch ($RuntimeIdentifier) {
    "win-arm64" { "arm64" }
    "win-x64" { "x64" }
    default { throw "Unsupported Windows runtime identifier: $RuntimeIdentifier" }
}

$versionText = $Version.TrimStart('v', 'V')
$parts = $versionText.Split('.')
if ($parts.Count -eq 2) {
    $ProductVersion = "$versionText.0"
}
elseif ($parts.Count -eq 3) {
    $ProductVersion = $versionText
}
else {
    throw "Version must be vMAJOR.MINOR or vMAJOR.MINOR.PATCH, got: $Version"
}

Write-Host "Building standard Windows installer for TuringDesk $Version ($RuntimeIdentifier)..." -ForegroundColor Cyan

& (Join-Path $PSScriptRoot "package-portable.ps1") `
    -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier `
    -Version $Version `
    -NodeVersion $NodeVersion
if ($LASTEXITCODE -ne 0) {
    throw "Installer payload staging failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $InstallerProject)) {
    throw "WiX installer project is missing: $InstallerProject"
}
if (-not (Test-Path $PayloadRoot)) {
    throw "Installer payload is missing: $PayloadRoot"
}

# WiX's <Files> harvesting creates a Component for each payload file. MSI has a
# hard limit of 65,536 Components. Keep enough headroom for shortcuts, registry
# values and future installer metadata, and fail before spending many minutes in
# the WiX binder if a dependency layout regresses into a huge virtual store.
$PayloadFileCount = @(Get-ChildItem $PayloadRoot -Recurse -File -Force).Count
Write-Host "Installer payload file count: $PayloadFileCount" -ForegroundColor Cyan
if ($PayloadFileCount -gt 60000) {
    throw "Installer payload has $PayloadFileCount files. This exceeds the safe MSI component budget (60,000). Use a flattened/hoisted production dependency layout."
}

if (Test-Path $InstallerBuildRoot) {
    Remove-Item $InstallerBuildRoot -Recurse -Force
}
if (Test-Path $FinalMsi) {
    Remove-Item $FinalMsi -Force
}

Write-Host "Compiling MSI with WiX Toolset..." -ForegroundColor Cyan
& dotnet build $InstallerProject `
    --configuration $Configuration `
    "-p:InstallerPlatform=$InstallerPlatform" `
    "-p:ProductVersion=$ProductVersion" `
    "-p:PayloadRoot=$PayloadRoot"
if ($LASTEXITCODE -ne 0) {
    throw "WiX MSI build failed with exit code $LASTEXITCODE"
}

$builtMsi = Get-ChildItem $InstallerBuildRoot -Filter *.msi -Recurse -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $builtMsi) {
    throw "WiX completed but no MSI was produced under $InstallerBuildRoot"
}

Copy-Item $builtMsi.FullName $FinalMsi -Force
if (-not (Test-Path $FinalMsi) -or (Get-Item $FinalMsi).Length -le 0) {
    throw "Final MSI is missing or empty: $FinalMsi"
}

Write-Host "Standard Windows installer ready:" -ForegroundColor Green
Write-Host "  $FinalMsi" -ForegroundColor Green

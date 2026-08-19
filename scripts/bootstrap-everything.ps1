param(
    [ValidateSet("arm64", "x64")]
    [string]$Architecture,
    [string]$Root
)

$ErrorActionPreference = "Stop"

if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
if (-not $Architecture) {
    $Architecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x64" }
}

$EverythingVersion = "1.4.1.1032"
$EsVersion = "1.1.0.37"
$EverythingArchToken = if ($Architecture -eq "arm64") { "ARM64" } else { "x64" }
$EsArchToken = if ($Architecture -eq "arm64") { "ARM64" } else { "x64" }

$componentRoot = Join-Path $Root "src\TuringDesk.Desktop\ThirdParty\Everything\$Architecture"
$cacheRoot = Join-Path $Root ".tools\third-party\everything"
$downloadRoot = Join-Path $cacheRoot "downloads"
$markerPath = Join-Path $componentRoot ".component-version"
$everythingExe = Join-Path $componentRoot "Everything.exe"
$esExe = Join-Path $componentRoot "es.exe"
$expectedMarker = "Everything=$EverythingVersion`nES=$EsVersion`nArchitecture=$Architecture"

if ((Test-Path $everythingExe) -and (Test-Path $esExe) -and (Test-Path $markerPath)) {
    $currentMarker = (Get-Content $markerPath -Raw).Trim()
    if ($currentMarker -eq $expectedMarker.Trim()) {
        Write-Host "Using cached Everything $EverythingVersion + ES $EsVersion ($Architecture)." -ForegroundColor DarkGray
        exit 0
    }
}

New-Item -ItemType Directory -Force -Path $componentRoot | Out-Null
New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null

$everythingArchiveName = "Everything-$EverythingVersion.$EverythingArchToken.zip"
$esArchiveName = "ES-$EsVersion.$EsArchToken.zip"
$everythingArchive = Join-Path $downloadRoot $everythingArchiveName
$esArchive = Join-Path $downloadRoot $esArchiveName
$shaListPath = Join-Path $downloadRoot "Everything-$EverythingVersion.sha256"

function Download-IfMissing([string]$Url, [string]$Path) {
    if (Test-Path $Path) { return }
    Write-Host "Downloading $([IO.Path]::GetFileName($Path)) from voidtools..." -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path
}

Download-IfMissing "https://www.voidtools.com/$everythingArchiveName" $everythingArchive
Download-IfMissing "https://www.voidtools.com/$esArchiveName" $esArchive
Download-IfMissing "https://www.voidtools.com/Everything-$EverythingVersion.sha256" $shaListPath

$shaLine = Get-Content $shaListPath | Where-Object { $_ -match [regex]::Escape($everythingArchiveName) } | Select-Object -First 1
if (-not $shaLine -or $shaLine -notmatch '^([0-9A-Fa-f]{64})') {
    throw "Official Everything SHA256 entry was not found for $everythingArchiveName"
}
$expectedSha = $Matches[1].ToUpperInvariant()
$actualSha = (Get-FileHash $everythingArchive -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualSha -ne $expectedSha) {
    Remove-Item $everythingArchive -Force -ErrorAction SilentlyContinue
    throw "Everything archive SHA256 mismatch. Expected $expectedSha, got $actualSha"
}

$tempEverything = Join-Path $cacheRoot "extract-everything-$Architecture"
$tempEs = Join-Path $cacheRoot "extract-es-$Architecture"
Remove-Item $tempEverything, $tempEs -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tempEverything, $tempEs | Out-Null

Expand-Archive -Path $everythingArchive -DestinationPath $tempEverything -Force
Expand-Archive -Path $esArchive -DestinationPath $tempEs -Force

$downloadedEverything = Get-ChildItem $tempEverything -Filter "Everything.exe" -Recurse | Select-Object -First 1 -ExpandProperty FullName
$downloadedEs = Get-ChildItem $tempEs -Filter "es.exe" -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $downloadedEverything -or -not $downloadedEs) {
    throw "Everything component archives did not contain the expected executables."
}

Copy-Item $downloadedEverything $everythingExe -Force
Copy-Item $downloadedEs $esExe -Force
Set-Content -Path $markerPath -Value $expectedMarker -NoNewline

Write-Host "Pinned Everything backend ready: Everything $EverythingVersion + ES $EsVersion ($Architecture)." -ForegroundColor Green

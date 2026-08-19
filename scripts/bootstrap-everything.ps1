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

$sharedComponentRoot = Join-Path $Root "src\TuringDesk.Desktop\ThirdParty\Everything"
$componentRoot = Join-Path $sharedComponentRoot $Architecture
$cacheRoot = Join-Path $Root ".tools\third-party\everything"
$downloadRoot = Join-Path $cacheRoot "downloads"
$markerPath = Join-Path $componentRoot ".component-version"
$everythingExe = Join-Path $componentRoot "Everything.exe"
$esExe = Join-Path $componentRoot "es.exe"
$everythingLicense = Join-Path $sharedComponentRoot "LICENSE-Everything.txt"
$esLicense = Join-Path $sharedComponentRoot "LICENSE-ES.txt"
$expectedMarker = "Everything=$EverythingVersion`nES=$EsVersion`nArchitecture=$Architecture"

New-Item -ItemType Directory -Force -Path $componentRoot | Out-Null
New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null

function Download-IfMissing([string]$Url, [string]$Path) {
    if (Test-Path $Path) { return }
    Write-Host "Downloading $([IO.Path]::GetFileName($Path)) from official source..." -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path
}

# Redistribution notices are part of the component, not optional documentation.
Download-IfMissing "https://www.voidtools.com/License.txt" $everythingLicense
Download-IfMissing "https://raw.githubusercontent.com/voidtools/ES/master/LICENSE" $esLicense

if ((Test-Path $everythingExe) -and (Test-Path $esExe) -and (Test-Path $markerPath)) {
    $currentMarker = (Get-Content $markerPath -Raw).Trim()
    if ($currentMarker -eq $expectedMarker.Trim()) {
        Write-Host "Using cached Everything $EverythingVersion + ES $EsVersion ($Architecture)." -ForegroundColor DarkGray
        exit 0
    }
}

$everythingArchiveName = "Everything-$EverythingVersion.$EverythingArchToken.zip"
$esArchiveName = "ES-$EsVersion.$EsArchToken.zip"
$everythingArchive = Join-Path $downloadRoot $everythingArchiveName
$esArchive = Join-Path $downloadRoot $esArchiveName
$shaListPath = Join-Path $downloadRoot "Everything-$EverythingVersion.sha256"

Download-IfMissing "https://www.voidtools.com/$everythingArchiveName" $everythingArchive
Download-IfMissing "https://www.voidtools.com/$esArchiveName" $esArchive
Download-IfMissing "https://www.voidtools.com/Everything-$EverythingVersion.sha256" $shaListPath

$shaLines = @(Get-Content $shaListPath)
$fileIndex = -1
for ($index = 0; $index -lt $shaLines.Count; $index++) {
    if ($shaLines[$index] -match [regex]::Escape($everythingArchiveName)) {
        $fileIndex = $index
        break
    }
}

$shaMatch = $null
if ($fileIndex -ge 0) {
    # voidtools checksum manifests have used more than one text layout over time.
    # Prefer the filename line, then the immediately following line, then previous.
    foreach ($candidateIndex in @($fileIndex, $fileIndex + 1, $fileIndex - 1)) {
        if ($candidateIndex -lt 0 -or $candidateIndex -ge $shaLines.Count) { continue }
        $candidate = [regex]::Match($shaLines[$candidateIndex], '(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])')
        if ($candidate.Success) {
            $shaMatch = $candidate
            break
        }
    }
}

if ($fileIndex -lt 0 -or -not $shaMatch -or -not $shaMatch.Success) {
    throw "Official Everything SHA256 entry was not found for $everythingArchiveName"
}
$expectedSha = $shaMatch.Value.ToUpperInvariant()
$actualSha = (Get-FileHash $everythingArchive -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualSha -ne $expectedSha) {
    Remove-Item $everythingArchive -Force -ErrorAction SilentlyContinue
    throw "Everything archive SHA256 mismatch. Expected $expectedSha, got $actualSha"
}
Write-Host "Verified Everything archive SHA256: $actualSha" -ForegroundColor DarkGray

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

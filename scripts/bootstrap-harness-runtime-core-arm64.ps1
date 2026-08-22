param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest\HarnessRuntime"),
    [string]$NodeVersion = "24.19.0",
    [string]$DshVersion = "0.1.0-rc.7",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$SmartDownload = Join-Path $PSScriptRoot "smart-download.ps1"
if (-not (Test-Path $SmartDownload -PathType Leaf)) { throw "Smart download helper is missing: $SmartDownload" }
. $SmartDownload

$CacheRoot = Join-Path $env:LOCALAPPDATA "TuringDesk\RuntimeCache"
$NodeCache = Join-Path $CacheRoot "Node\$NodeVersion\arm64"
$NpmCache = Join-Path $CacheRoot "npm"
$NpmLogRoot = Join-Path $CacheRoot "logs"
$DownloadRoot = Join-Path $CacheRoot "downloads"
$NodeTarget = Join-Path $Destination "Node"
$DshTarget = Join-Path $Destination "Dsh"
$DshBin = Join-Path $DshTarget "node_modules\@deepseek-ai\dsh\lib\bin.js"
$DshPackageJson = Join-Path $DshTarget "node_modules\@deepseek-ai\dsh\package.json"

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Test-DshRuntime {
    if (-not (Test-Path $DshBin -PathType Leaf) -or -not (Test-Path $DshPackageJson -PathType Leaf)) { return $false }
    try {
        $Package = Get-Content $DshPackageJson -Raw | ConvertFrom-Json
        return $Package.version -eq $DshVersion
    }
    catch { return $false }
}

function Show-NewLogLines {
    param(
        [string]$Path,
        [ref]$Shown,
        [ConsoleColor]$Color = [ConsoleColor]::DarkGray
    )
    if (-not (Test-Path $Path -PathType Leaf)) { return }
    $lines = @(Get-Content $Path -ErrorAction SilentlyContinue)
    if ($lines.Count -le $Shown.Value) { return }
    $lines | Select-Object -Skip $Shown.Value | ForEach-Object { Write-Host $_ -ForegroundColor $Color }
    $Shown.Value = $lines.Count
}

if (-not $Force -and (Test-Path (Join-Path $NodeTarget "node.exe") -PathType Leaf) -and (Test-DshRuntime)) {
    Write-Host "Harness runtime is already ready: Node $NodeVersion + DSH $DshVersion" -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path $NpmCache | Out-Null
New-Item -ItemType Directory -Force -Path $NpmLogRoot | Out-Null
New-Item -ItemType Directory -Force -Path $DownloadRoot | Out-Null
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$NodeFile = "node-v$NodeVersion-win-arm64.zip"
$NodeZip = Join-Path $DownloadRoot $NodeFile
$NodeSums = Join-Path $DownloadRoot "node-v$NodeVersion-SHASUMS256.txt"

Step "Preparing Node.js $NodeVersion ARM64"
Invoke-TuringDeskSmartDownload `
    -Url "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt" `
    -Destination $NodeSums `
    -Name "Node.js checksums" `
    -FileName "SHASUMS256.txt" `
    -TimeoutSeconds 120 | Out-Null

$SumLine = Get-Content $NodeSums | Where-Object { $_ -match "\s+$([regex]::Escape($NodeFile))$" } | Select-Object -First 1
if (-not $SumLine) { throw "Node checksum entry not found for $NodeFile" }
$ExpectedHash = ($SumLine -split '\s+')[0].ToLowerInvariant()

Invoke-TuringDeskSmartDownload `
    -Url "https://nodejs.org/dist/v$NodeVersion/$NodeFile" `
    -Destination $NodeZip `
    -Name "Node.js $NodeVersion ARM64" `
    -ExpectedSha256 $ExpectedHash `
    -FileName $NodeFile `
    -TimeoutSeconds 300 | Out-Null
Write-Host "Node archive verified: $ExpectedHash" -ForegroundColor Green

if ($Force -or -not (Test-Path (Join-Path $NodeCache "node.exe") -PathType Leaf)) {
    Step "Extracting Node.js into persistent runtime cache"
    $ExtractRoot = Join-Path $env:TEMP ("TuringDesk-Node-" + [guid]::NewGuid().ToString("N"))
    try {
        Expand-Archive $NodeZip -DestinationPath $ExtractRoot -Force
        $Source = Join-Path $ExtractRoot "node-v$NodeVersion-win-arm64"
        if (-not (Test-Path (Join-Path $Source "node.exe") -PathType Leaf)) { throw "Extracted Node runtime is incomplete" }
        if (Test-Path $NodeCache) { Remove-Item $NodeCache -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $NodeCache | Out-Null
        Copy-Item (Join-Path $Source "*") $NodeCache -Recurse -Force
    }
    finally { Remove-Item $ExtractRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Step "Installing Node.js runtime into TuringDesk development deployment"
if (Test-Path $NodeTarget) { Remove-Item $NodeTarget -Recurse -Force }
New-Item -ItemType Directory -Force -Path $NodeTarget | Out-Null
Copy-Item (Join-Path $NodeCache "*") $NodeTarget -Recurse -Force
$NodeExe = Join-Path $NodeTarget "node.exe"
$NpmCmd = Join-Path $NodeTarget "npm.cmd"
if (-not (Test-Path $NodeExe -PathType Leaf) -or -not (Test-Path $NpmCmd -PathType Leaf)) { throw "Installed Node runtime is incomplete" }
Write-Host "Node: $(& $NodeExe --version)" -ForegroundColor Green

if ($Force -or -not (Test-DshRuntime)) {
    Step "Installing DeepSeek Harness $DshVersion locally"
    Write-Host "First install is large (DSH has many packages), but npm network/cache activity is now shown live." -ForegroundColor Yellow
    if (Test-Path $DshTarget) { Remove-Item $DshTarget -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $DshTarget | Out-Null

    $OldPath = $env:PATH
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $StdoutLog = Join-Path $NpmLogRoot "dsh-install-$stamp.out.log"
    $StderrLog = Join-Path $NpmLogRoot "dsh-install-$stamp.err.log"
    try {
        $env:PATH = "$NodeTarget;$OldPath"
        & $NpmCmd ping --registry="https://registry.npmjs.org/" --cache "$NpmCache" --loglevel=notice
        if ($LASTEXITCODE -ne 0) { throw "npm registry is not reachable" }

        $Arguments = @(
            "install",
            "--prefix", $DshTarget,
            "--omit=dev",
            "--no-audit",
            "--no-fund",
            "--no-package-lock",
            "--save-exact",
            "--registry=https://registry.npmjs.org/",
            "--cache", $NpmCache,
            "--prefer-offline",
            "--foreground-scripts",
            "--timing",
            "--loglevel=http",
            "--fetch-retries=3",
            "--fetch-retry-mintimeout=1000",
            "--fetch-retry-maxtimeout=10000",
            "--fetch-timeout=60000",
            "@deepseek-ai/dsh@$DshVersion"
        )

        Write-Host "npm cache: $NpmCache" -ForegroundColor DarkGray
        Write-Host "npm log:   $StdoutLog" -ForegroundColor DarkGray
        $Started = Get-Date
        $Process = Start-Process -FilePath $NpmCmd -ArgumentList $Arguments -NoNewWindow -PassThru `
            -RedirectStandardOutput $StdoutLog -RedirectStandardError $StderrLog
        $shownOut = 0
        $shownErr = 0
        while (-not $Process.HasExited) {
            Start-Sleep -Seconds 2
            $Process.Refresh()
            Show-NewLogLines -Path $StdoutLog -Shown ([ref]$shownOut) -Color DarkGray
            Show-NewLogLines -Path $StderrLog -Shown ([ref]$shownErr) -Color Yellow

            $Elapsed = (Get-Date) - $Started
            if ([int]$Elapsed.TotalSeconds -gt 480) {
                try { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue } catch { }
                Write-Host "`n--- npm stdout tail ---" -ForegroundColor Yellow
                if (Test-Path $StdoutLog) { Get-Content $StdoutLog -Tail 120 | Out-Host }
                Write-Host "`n--- npm stderr tail ---" -ForegroundColor Yellow
                if (Test-Path $StderrLog) { Get-Content $StderrLog -Tail 120 | Out-Host }
                throw "npm install timed out after 8 minutes. Cached downloads were kept at $NpmCache. Logs: $StdoutLog ; $StderrLog"
            }
        }
        Show-NewLogLines -Path $StdoutLog -Shown ([ref]$shownOut) -Color DarkGray
        Show-NewLogLines -Path $StderrLog -Shown ([ref]$shownErr) -Color Yellow
        if ($Process.ExitCode -ne 0) {
            Write-Host "`n--- npm stdout tail ---" -ForegroundColor Yellow
            if (Test-Path $StdoutLog) { Get-Content $StdoutLog -Tail 120 | Out-Host }
            Write-Host "`n--- npm stderr tail ---" -ForegroundColor Yellow
            if (Test-Path $StderrLog) { Get-Content $StderrLog -Tail 120 | Out-Host }
            throw "npm install @deepseek-ai/dsh@$DshVersion failed with exit code $($Process.ExitCode). Logs: $StdoutLog ; $StderrLog"
        }
        Write-Host ("DeepSeek Harness dependencies installed in {0:n1}s." -f ((Get-Date) - $Started).TotalSeconds) -ForegroundColor Green
    }
    finally { $env:PATH = $OldPath }
}

if (-not (Test-DshRuntime)) { throw "DeepSeek Harness runtime installation finished but lib/bin.js or package metadata is missing" }

$Manifest = @{
    node = $NodeVersion
    dsh = $DshVersion
    architecture = "arm64"
    installedAt = (Get-Date).ToString("o")
} | ConvertTo-Json
$Manifest | Set-Content -Path (Join-Path $Destination "runtime.json") -Encoding UTF8

Write-Host "`nHarness runtime bootstrap complete." -ForegroundColor Green
Write-Host "Destination: $Destination" -ForegroundColor DarkGray
Write-Host "npm cache: $NpmCache" -ForegroundColor DarkGray

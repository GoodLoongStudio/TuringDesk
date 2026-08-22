param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest\HarnessRuntime"),
    [string]$NodeVersion = "24.19.0",
    [string]$DshVersion = "0.1.0-rc.7",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$CacheRoot = Join-Path $env:LOCALAPPDATA "TuringDesk\RuntimeCache"
$NodeCache = Join-Path $CacheRoot "Node\$NodeVersion\arm64"
$NpmCache = Join-Path $CacheRoot "npm"
$NodeTarget = Join-Path $Destination "Node"
$DshTarget = Join-Path $Destination "Dsh"
$DshBin = Join-Path $DshTarget "node_modules\@deepseek-ai\dsh\lib\bin.js"
$DshPackageJson = Join-Path $DshTarget "node_modules\@deepseek-ai\dsh\package.json"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Download-File([string]$Url, [string]$Path, [string]$Name) {
    if ((Test-Path $Path -PathType Leaf) -and ((Get-Item $Path).Length -gt 0)) {
        Write-Host "Using cached $Name: $Path" -ForegroundColor DarkGray
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $Path -Parent) | Out-Null
    $Partial = "$Path.partial"
    Remove-Item $Partial -Force -ErrorAction SilentlyContinue
    Step "Downloading $Name"
    Write-Host $Url -ForegroundColor DarkGray
    & curl.exe -fL --retry 3 --retry-delay 2 --connect-timeout 20 --max-time 300 --progress-bar $Url -o $Partial
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $Partial -Force -ErrorAction SilentlyContinue
        throw "$Name download failed with curl exit code $LASTEXITCODE"
    }
    Move-Item $Partial $Path -Force
}

function Test-DshRuntime {
    if (-not (Test-Path $DshBin -PathType Leaf) -or -not (Test-Path $DshPackageJson -PathType Leaf)) {
        return $false
    }
    try {
        $Package = Get-Content $DshPackageJson -Raw | ConvertFrom-Json
        return $Package.version -eq $DshVersion
    }
    catch {
        return $false
    }
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    throw "curl.exe was not found. Windows 11 should provide it in System32."
}

if (-not $Force -and (Test-Path (Join-Path $NodeTarget "node.exe") -PathType Leaf) -and (Test-DshRuntime)) {
    Write-Host "Harness runtime is already ready: Node $NodeVersion + DSH $DshVersion" -ForegroundColor Green
    exit 0
}

New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path $NpmCache | Out-Null
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$NodeFile = "node-v$NodeVersion-win-arm64.zip"
$NodeZip = Join-Path $CacheRoot "downloads\$NodeFile"
$NodeSums = Join-Path $CacheRoot "downloads\node-v$NodeVersion-SHASUMS256.txt"
Download-File -Url "https://nodejs.org/dist/v$NodeVersion/$NodeFile" -Path $NodeZip -Name "Node.js $NodeVersion ARM64"
Download-File -Url "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt" -Path $NodeSums -Name "Node.js checksums"

Step "Verifying Node.js SHA256"
$SumLine = Get-Content $NodeSums | Where-Object { $_ -match "\s+$([regex]::Escape($NodeFile))$" } | Select-Object -First 1
if (-not $SumLine) { throw "Node checksum entry not found for $NodeFile" }
$ExpectedHash = ($SumLine -split '\s+')[0].ToLowerInvariant()
$ActualHash = (Get-FileHash $NodeZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualHash -ne $ExpectedHash) {
    Remove-Item $NodeZip -Force -ErrorAction SilentlyContinue
    throw "Node SHA256 mismatch: expected $ExpectedHash, got $ActualHash"
}
Write-Host "Node archive verified." -ForegroundColor Green

if ($Force -or -not (Test-Path (Join-Path $NodeCache "node.exe") -PathType Leaf)) {
    Step "Extracting Node.js into persistent runtime cache"
    $ExtractRoot = Join-Path $env:TEMP ("TuringDesk-Node-" + [guid]::NewGuid().ToString("N"))
    try {
        Expand-Archive $NodeZip -DestinationPath $ExtractRoot -Force
        $Source = Join-Path $ExtractRoot "node-v$NodeVersion-win-arm64"
        if (-not (Test-Path (Join-Path $Source "node.exe") -PathType Leaf)) {
            throw "Extracted Node runtime is incomplete"
        }
        if (Test-Path $NodeCache) { Remove-Item $NodeCache -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $NodeCache | Out-Null
        Copy-Item (Join-Path $Source "*") $NodeCache -Recurse -Force
    }
    finally {
        Remove-Item $ExtractRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Step "Installing Node.js runtime into TuringDesk development deployment"
if (Test-Path $NodeTarget) { Remove-Item $NodeTarget -Recurse -Force }
New-Item -ItemType Directory -Force -Path $NodeTarget | Out-Null
Copy-Item (Join-Path $NodeCache "*") $NodeTarget -Recurse -Force
$NodeExe = Join-Path $NodeTarget "node.exe"
$NpmCmd = Join-Path $NodeTarget "npm.cmd"
if (-not (Test-Path $NodeExe -PathType Leaf) -or -not (Test-Path $NpmCmd -PathType Leaf)) {
    throw "Installed Node runtime is incomplete"
}
Write-Host "Node: $(& $NodeExe --version)" -ForegroundColor Green

if ($Force -or -not (Test-DshRuntime)) {
    Step "Installing DeepSeek Harness $DshVersion locally"
    Write-Host "This is the only heavy first-run step. npm output is shown live; later deploys reuse this runtime." -ForegroundColor Yellow
    if (Test-Path $DshTarget) { Remove-Item $DshTarget -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $DshTarget | Out-Null

    $OldPath = $env:PATH
    try {
        $env:PATH = "$NodeTarget;$OldPath"
        $Arguments = @(
            "install",
            "--prefix", $DshTarget,
            "--omit=dev",
            "--no-audit",
            "--no-fund",
            "--no-package-lock",
            "--save-exact",
            "--cache", $NpmCache,
            "--prefer-offline",
            "--progress=true",
            "--loglevel=notice",
            "--fetch-retries=2",
            "--fetch-retry-mintimeout=1000",
            "--fetch-retry-maxtimeout=10000",
            "--fetch-timeout=60000",
            "@deepseek-ai/dsh@$DshVersion"
        )

        $Started = Get-Date
        $Process = Start-Process -FilePath $NpmCmd -ArgumentList $Arguments -NoNewWindow -PassThru
        while (-not $Process.HasExited) {
            Start-Sleep -Seconds 5
            $Process.Refresh()
            $Elapsed = (Get-Date) - $Started
            if ([int]$Elapsed.TotalSeconds -gt 600) {
                try { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue } catch { }
                throw "npm install timed out after 10 minutes. The persistent npm cache is kept at $NpmCache for retry."
            }
            if (([int]$Elapsed.TotalSeconds % 30) -lt 5) {
                Write-Host ("Still installing DSH... {0:n0}s elapsed" -f $Elapsed.TotalSeconds) -ForegroundColor DarkGray
            }
        }
        if ($Process.ExitCode -ne 0) {
            throw "npm install @deepseek-ai/dsh@$DshVersion failed with exit code $($Process.ExitCode)"
        }
    }
    finally {
        $env:PATH = $OldPath
    }
}

if (-not (Test-DshRuntime)) {
    throw "DeepSeek Harness runtime installation finished but lib/bin.js or package metadata is missing"
}

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

param(
    [string]$NodeVersion = "24.19.0",
    [string]$DshVersion = "0.1.0-rc.7",
    [switch]$SkipNodeInstall
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Root = Join-Path $env:LOCALAPPDATA "TuringDesk"
$CacheRoot = Join-Path $Root "RuntimeCache"
$DownloadRoot = Join-Path $CacheRoot "downloads"
$NpmCache = Join-Path $CacheRoot "npm"
$LogRoot = Join-Path $Root "Logs"
$HarnessLog = Join-Path $LogRoot "harness-system-install.log"
$NodeMsi = Join-Path $DownloadRoot "node-v$NodeVersion-arm64.msi"
$NodeSums = Join-Path $DownloadRoot "node-v$NodeVersion-SHASUMS256.txt"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Refresh-ProcessPath {
    $machine = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $user = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:PATH = (($machine, $user) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ";"
}

function Find-CommandPath([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles "nodejs\$Name"),
        (Join-Path $env:LOCALAPPDATA "Programs\nodejs\$Name")
    )) {
        if (Test-Path $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Download-VerifiedNodeMsi {
    New-Item -ItemType Directory -Force -Path $DownloadRoot | Out-Null

    if (-not (Test-Path $NodeMsi -PathType Leaf)) {
        Step "Downloading official Node.js $NodeVersion ARM64 MSI"
        & curl.exe -fL --retry 3 --retry-delay 2 --connect-timeout 20 --max-time 300 --progress-bar `
            "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-arm64.msi" -o $NodeMsi
        if ($LASTEXITCODE -ne 0) { throw "Node ARM64 MSI download failed: curl exit $LASTEXITCODE" }
    }

    if (-not (Test-Path $NodeSums -PathType Leaf)) {
        & curl.exe -fL --retry 3 --retry-delay 2 --connect-timeout 20 --max-time 120 `
            "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt" -o $NodeSums
        if ($LASTEXITCODE -ne 0) { throw "Node checksum download failed: curl exit $LASTEXITCODE" }
    }

    Step "Verifying official Node.js SHA256"
    $fileName = "node-v$NodeVersion-arm64.msi"
    $line = Get-Content $NodeSums | Where-Object { $_ -match "\s+$([regex]::Escape($fileName))$" } | Select-Object -First 1
    if (-not $line) { throw "SHA256 entry not found for $fileName" }
    $expected = ($line -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash $NodeMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        Remove-Item $NodeMsi -Force -ErrorAction SilentlyContinue
        throw "Node MSI SHA256 mismatch: expected $expected, got $actual"
    }
    Write-Host "Node installer checksum verified." -ForegroundColor Green
}

function Ensure-Node {
    Refresh-ProcessPath
    $node = Find-CommandPath "node.exe"
    $npx = Find-CommandPath "npx.cmd"
    if ($node -and $npx) {
        $version = (& $node --version).Trim()
        Write-Host "Existing Node: $version ($node)" -ForegroundColor Green
        Write-Host "Existing npx:  $npx" -ForegroundColor Green
        return @{ Node = $node; Npx = $npx }
    }

    if ($SkipNodeInstall) {
        throw "Node.js/npx not found and -SkipNodeInstall was specified."
    }
    if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
        throw "curl.exe is required to download the official Node installer."
    }

    Download-VerifiedNodeMsi
    Step "Installing official Node.js $NodeVersion ARM64"
    Write-Host "Windows may show one administrator approval prompt." -ForegroundColor Yellow
    $installer = Start-Process -FilePath "msiexec.exe" -ArgumentList @("/i", "`"$NodeMsi`"", "/passive", "/norestart") -Verb RunAs -Wait -PassThru
    if ($installer.ExitCode -notin @(0, 3010)) {
        throw "Node MSI installation failed with exit code $($installer.ExitCode)"
    }

    Refresh-ProcessPath
    $node = Find-CommandPath "node.exe"
    $npx = Find-CommandPath "npx.cmd"
    if (-not $node -or -not $npx) {
        throw "Node installation completed but node.exe/npx.cmd could not be located."
    }
    return @{ Node = $node; Npx = $npx }
}

function Test-NpmRegistry([string]$NodeDir) {
    $npm = Join-Path $NodeDir "npm.cmd"
    if (-not (Test-Path $npm -PathType Leaf)) { throw "npm.cmd is missing next to Node: $NodeDir" }

    New-Item -ItemType Directory -Force -Path $NpmCache | Out-Null
    Step "Checking npm registry connectivity"
    & $npm ping --registry="https://registry.npmjs.org/" --cache "$NpmCache" --loglevel=notice
    if ($LASTEXITCODE -ne 0) { throw "npm registry ping failed" }

    Step "Checking the pinned DeepSeek Harness package"
    & $npm view "@deepseek-ai/dsh@$DshVersion" version --registry="https://registry.npmjs.org/" --cache "$NpmCache"
    if ($LASTEXITCODE -ne 0) { throw "npm could not resolve @deepseek-ai/dsh@$DshVersion" }
}

function Wait-HarnessReady([int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:3080/" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200 -and $response.Content -match "window\.__DSH_BOOT__") {
                return $true
            }
        }
        catch { }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $false
}

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
New-Item -ItemType Directory -Force -Path $NpmCache | Out-Null
Remove-Item $HarnessLog -Force -ErrorAction SilentlyContinue

$runtime = Ensure-Node
$nodeDir = Split-Path $runtime.Node -Parent
$npm = Join-Path $nodeDir "npm.cmd"
$npx = $runtime.Npx

Step "Runtime versions"
Write-Host "Node: $(& $runtime.Node --version)"
Write-Host "npm:  $(& $npm --version)"
Write-Host "npx:  $npx"

Test-NpmRegistry -NodeDir $nodeDir

Step "Starting the official pinned DeepSeek Harness package"
Write-Host "Command: npx --yes @deepseek-ai/dsh@$DshVersion web" -ForegroundColor DarkGray
Write-Host "First run can be heavy because DSH has a large dependency graph. This run is intentionally visible and logged." -ForegroundColor Yellow

$oldPath = $env:PATH
$env:PATH = "$nodeDir;$oldPath"
try {
    $command = "`"$npx`" --yes --cache `"$NpmCache`" --loglevel=notice @deepseek-ai/dsh@$DshVersion web 1>`"$HarnessLog`" 2>&1"
    $process = Start-Process -FilePath "cmd.exe" -ArgumentList @("/d", "/s", "/c", $command) -PassThru -WindowStyle Hidden

    $started = Get-Date
    $lastShown = Get-Date
    $ready = $false
    while (-not $process.HasExited) {
        if (Wait-HarnessReady -TimeoutSeconds 2) {
            $ready = $true
            break
        }
        $process.Refresh()
        if (((Get-Date) - $lastShown).TotalSeconds -ge 15) {
            $elapsed = [int]((Get-Date) - $started).TotalSeconds
            Write-Host "Still starting Harness... ${elapsed}s" -ForegroundColor DarkGray
            if (Test-Path $HarnessLog) {
                Get-Content $HarnessLog -Tail 12 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
            }
            $lastShown = Get-Date
        }
        if (((Get-Date) - $started).TotalMinutes -ge 10) { break }
    }

    if (-not $ready) {
        $ready = Wait-HarnessReady -TimeoutSeconds 3
    }

    if (-not $ready) {
        if (Test-Path $HarnessLog) {
            Write-Host "`n--- Harness log tail ---" -ForegroundColor Yellow
            Get-Content $HarnessLog -Tail 120 | Out-Host
        }
        if ($process.HasExited) {
            throw "Official DeepSeek Harness exited before Web UI became ready. ExitCode=$($process.ExitCode). Log: $HarnessLog"
        }
        throw "Official DeepSeek Harness did not become ready within 10 minutes. Log: $HarnessLog"
    }

    Write-Host "`nDeepSeek Harness Web UI is READY: http://localhost:3080" -ForegroundColor Green
    Write-Host "Verified marker: window.__DSH_BOOT__" -ForegroundColor Green
    Write-Host "Log: $HarnessLog" -ForegroundColor DarkGray
}
finally {
    if ($process -and -not $process.HasExited) {
        & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
    }
    $env:PATH = $oldPath
}

Write-Host "`nSystem Harness verification passed." -ForegroundColor Green
Write-Host "TuringDeskHarness can now use the system npx fallback when a bundled runtime is not present." -ForegroundColor Green

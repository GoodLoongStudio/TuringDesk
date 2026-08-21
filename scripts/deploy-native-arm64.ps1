param(
    [string]$Repo = "GoodLoongStudio/TuringDesk"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$DeployDir = Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"
$ArtifactName = "TuringDesk-Native-Search-ARM64"
$Workflow = "native-search-windows.yml"
$ExeName = "TuringDesk.exe"
$CodexRepo = "openai/codex"
$CodexRelease = "rust-v0.146.0"
$CodexAsset = "codex-app-server-aarch64-pc-windows-msvc.exe.zip"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Stop-BundledEverything {
    $BundledPath = Join-Path $DeployDir "Everything\Everything.exe"
    if (-not (Test-Path $BundledPath)) { return }

    $Expected = [System.IO.Path]::GetFullPath($BundledPath)
    foreach ($Process in @(Get-Process -Name "Everything" -ErrorAction SilentlyContinue)) {
        try {
            if ($Process.Path -and ([System.IO.Path]::GetFullPath($Process.Path) -eq $Expected)) {
                Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
    Start-Sleep -Milliseconds 200
}

function Stop-CodexRuntime {
    $BundledPath = Join-Path $DeployDir "Codex\codex-app-server.exe"
    if (-not (Test-Path $BundledPath)) { return }

    $Expected = [System.IO.Path]::GetFullPath($BundledPath)
    foreach ($Process in @(Get-Process -Name "codex-app-server" -ErrorAction SilentlyContinue)) {
        try {
            if ($Process.Path -and ([System.IO.Path]::GetFullPath($Process.Path) -eq $Expected)) {
                Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
    Start-Sleep -Milliseconds 100
}

function Stop-DeployedInstance {
    Step "Stopping previous TuringDesk instance"
    foreach ($Process in @(Get-Process -Name "TuringDesk" -ErrorAction SilentlyContinue)) {
        try { Stop-Process -Id $Process.Id -Force -ErrorAction Stop }
        catch { Write-Host "Stop-Process did not terminate PID $($Process.Id); retrying." -ForegroundColor DarkGray }
    }

    for ($i = 0; $i -lt 30; $i++) {
        $Remaining = @(Get-Process -Name "TuringDesk" -ErrorAction SilentlyContinue)
        if ($Remaining.Count -eq 0) { break }
        foreach ($Process in $Remaining) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 100
    }

    if (@(Get-Process -Name "TuringDesk" -ErrorAction SilentlyContinue).Count -gt 0) {
        & taskkill.exe /F /IM TuringDesk.exe 2>$null | Out-Null
    }
    Start-Sleep -Milliseconds 200
    Stop-CodexRuntime
    Stop-BundledEverything
}

function Copy-DeployedBinary([string]$Source, [string]$Destination) {
    $LastError = $null
    for ($i = 1; $i -le 25; $i++) {
        try {
            Copy-Item $Source $Destination -Force -ErrorAction Stop
            return
        }
        catch [System.IO.IOException] {
            $LastError = $_
            Get-Process -Name "TuringDesk" -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 200
        }
        catch {
            $LastError = $_
            break
        }
    }
    throw "Unable to replace deployed TuringDesk.exe after retries: $($LastError.Exception.Message)"
}

function Test-Binary([string]$Exe) {
    Step "Running Native Search self-test"
    & $Exe --self-test | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Self-test failed with exit code $LASTEXITCODE"
    }
}

function Get-MainSha {
    $Sha = ((& gh api "repos/$Repo/commits/main" --jq ".sha") | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Sha)) {
        throw "Unable to resolve main commit SHA"
    }
    return [string]$Sha
}

function Get-RunForCommit([string]$Sha, [switch]$SuccessfulOnly) {
    $Args = @(
        "run", "list",
        "--repo", $Repo,
        "--workflow", $Workflow,
        "--commit", $Sha,
        "--limit", "1",
        "--json", "databaseId,headSha,status,conclusion"
    )
    if ($SuccessfulOnly) { $Args += @("--status", "success") }

    $Json = & gh @Args
    if ($LASTEXITCODE -ne 0) { throw "Unable to query GitHub Actions runs" }

    $Runs = @($Json | ConvertFrom-Json)
    if ($Runs.Count -eq 0) { return $null }
    return $Runs[0]
}

function Start-And-WaitForRun([string]$Sha) {
    Step "No successful ARM64 package for current main; starting CI"
    & gh workflow run $Workflow --repo $Repo --ref main | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Unable to start GitHub Actions workflow" }

    $Run = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $Run = Get-RunForCommit $Sha
        if ($Run) { break }
    }
    if (-not $Run) { throw "Workflow was started but its run could not be found" }

    $RunId = [long]$Run.databaseId
    Step "Waiting for GitHub Actions run $RunId"
    & gh run watch $RunId --repo $Repo --exit-status | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Step "Failed GitHub Actions log for run $RunId"
        & gh run view $RunId --repo $Repo --log-failed | Out-Host
        throw "GitHub Actions build failed (run $RunId)"
    }
    return [long]$RunId
}

function Download-Artifact([long]$RunId) {
    $Temp = Join-Path $env:TEMP ("TuringDesk-ARM64-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $Temp | Out-Null

    Step "Downloading ARM64 artifact from run $RunId"
    & gh run download $RunId --repo $Repo --name $ArtifactName --dir $Temp | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
        throw "Unable to download ARM64 artifact"
    }

    $Found = Get-ChildItem -Path $Temp -Filter $ExeName -Recurse | Select-Object -First 1
    if (-not $Found) {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
        throw "$ExeName was not found in ARM64 artifact"
    }

    return @{
        Exe = $Found.FullName
        Root = $Found.Directory.FullName
        Temp = $Temp
    }
}

function Deploy-BundledEverything([string]$ArtifactRoot) {
    $SourceDir = Join-Path $ArtifactRoot "Everything"
    $DestinationDir = Join-Path $DeployDir "Everything"
    $DestinationExe = Join-Path $DestinationDir "Everything.exe"

    if (-not (Test-Path $SourceDir)) {
        Write-Host "WARNING: ARM64 artifact does not contain bundled Everything." -ForegroundColor Yellow
        return $null
    }

    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    if (-not (Test-Path $DestinationExe)) {
        Step "Installing bundled Everything files"
        Copy-Item (Join-Path $SourceDir "*") $DestinationDir -Recurse -Force
    }
    else {
        Write-Host "Bundled Everything is already installed; keeping the binary to avoid service file locks." -ForegroundColor DarkGray
    }

    $Config = Join-Path $DestinationDir "TuringDesk-Everything.ini"
    @"
[Everything]
run_in_background=1
show_tray_icon=0
check_for_updates_on_startup=0
"@ | Set-Content -Path $Config -Encoding UTF8

    return $DestinationExe
}

function Ensure-EverythingService([string]$EverythingExe) {
    if ([string]::IsNullOrWhiteSpace($EverythingExe) -or -not (Test-Path $EverythingExe)) { return }

    $Service = Get-Service -Name "Everything" -ErrorAction SilentlyContinue
    if (-not $Service) {
        Step "Installing Everything indexing service (one-time)"
        Write-Host "Windows may show one UAC prompt. This is only needed once for NTFS indexing." -ForegroundColor Yellow
        try {
            $Install = Start-Process -FilePath $EverythingExe -ArgumentList "-install-service" -Verb RunAs -Wait -PassThru
            if ($Install.ExitCode -ne 0) {
                Write-Host "WARNING: Everything service installer returned $($Install.ExitCode)." -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "WARNING: Everything service was not installed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    Write-Host "Bundled Everything is configured for background use with no tray icon." -ForegroundColor DarkGray
    Write-Host "TuringDesk will start and stop the client automatically." -ForegroundColor DarkGray
}

function Ensure-CodexRuntime {
    $CodexDir = Join-Path $DeployDir "Codex"
    $CodexExe = Join-Path $CodexDir "codex-app-server.exe"
    if (Test-Path $CodexExe) {
        Write-Host "Codex Agent Runtime is already installed." -ForegroundColor DarkGray
        return $CodexExe
    }

    Step "Installing Codex Agent Runtime (one-time, optional)"
    $Temp = Join-Path $env:TEMP ("TuringDesk-Codex-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $Temp | Out-Null
    try {
        & gh release download $CodexRelease --repo $CodexRepo --pattern $CodexAsset --dir $Temp --clobber | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Host "WARNING: Codex Runtime download failed. L3 will continue with Direct Runtime." -ForegroundColor Yellow
            return $null
        }

        $Zip = Get-ChildItem -Path $Temp -Filter "*.zip" | Select-Object -First 1
        if (-not $Zip) {
            Write-Host "WARNING: Codex Runtime archive was not found. Direct Runtime remains available." -ForegroundColor Yellow
            return $null
        }
        $Expanded = Join-Path $Temp "expanded"
        Expand-Archive $Zip.FullName -DestinationPath $Expanded -Force
        $Found = Get-ChildItem -Path $Expanded -Filter "codex-app-server-aarch64-pc-windows-msvc.exe" -Recurse | Select-Object -First 1
        if (-not $Found) {
            Write-Host "WARNING: Codex app-server executable was not found in the release archive." -ForegroundColor Yellow
            return $null
        }

        New-Item -ItemType Directory -Force -Path $CodexDir | Out-Null
        Copy-Item $Found.FullName $CodexExe -Force
        Write-Host "Codex Agent Runtime installed: $CodexExe" -ForegroundColor Green
        return $CodexExe
    }
    catch {
        Write-Host "WARNING: Codex Runtime install failed: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "L3 will continue with Direct Runtime." -ForegroundColor Yellow
        return $null
    }
    finally {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) was not found in PATH"
}

Step "Checking GitHub CLI authentication"
& gh auth status 2>$null | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not authenticated. Run: gh auth login"
}

Step "Resolving current main commit"
$MainSha = Get-MainSha
Write-Host "main: $MainSha" -ForegroundColor DarkGray

$Run = Get-RunForCommit $MainSha -SuccessfulOnly
if ($Run) {
    $RunId = [long]$Run.databaseId
    Step "Found successful build for current main: run $RunId"
}
else {
    $RunId = [long](Start-And-WaitForRun $MainSha)
}

$Downloaded = Download-Artifact -RunId $RunId
try {
    Test-Binary $Downloaded.Exe

    Step "Deploying to $DeployDir"
    Stop-DeployedInstance
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
    $DeployedExe = Join-Path $DeployDir $ExeName
    Copy-DeployedBinary -Source $Downloaded.Exe -Destination $DeployedExe

    $EverythingExe = Deploy-BundledEverything -ArtifactRoot $Downloaded.Root
    Ensure-EverythingService -EverythingExe $EverythingExe
    Ensure-CodexRuntime | Out-Null

    Step "Starting TuringDesk Native Search"
    Start-Process $DeployedExe
    Write-Host "`nDeployment complete. Press Alt+Space to open Search." -ForegroundColor Green
    Write-Host "Path: $DeployDir"
}
finally {
    Remove-Item $Downloaded.Temp -Recurse -Force -ErrorAction SilentlyContinue
}

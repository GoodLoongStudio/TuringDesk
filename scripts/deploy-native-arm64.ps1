param(
    [string]$Repo = "GoodLoongStudio/TuringDesk",
    [switch]$Full
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$DeployDir = Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"
$ArtifactName = "TuringDesk-Native-Search-ARM64"
$Workflow = "native-search-windows.yml"
$ExeName = "TuringDesk.exe"
$WallpaperExeName = "TuringDeskWallpaper.exe"
$HarnessExeName = "TuringDeskHarness.exe"
$CodexRepo = "openai/codex"
$CodexRelease = "rust-v0.146.0"
$CodexAsset = "codex-app-server-aarch64-pc-windows-msvc.exe.zip"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Stop-ProcessAtPath([string]$ProcessName, [string]$ExpectedPath) {
    if ([string]::IsNullOrWhiteSpace($ExpectedPath)) { return }
    $Expected = [System.IO.Path]::GetFullPath($ExpectedPath)
    foreach ($Process in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
        try {
            if ($Process.Path -and ([System.IO.Path]::GetFullPath($Process.Path) -eq $Expected)) {
                Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
}

function Stop-DeployedInstance {
    Step "Stopping previous TuringDesk processes"
    foreach ($ProcessName in @("TuringDesk", "TuringDeskWallpaper", "TuringDeskHarness")) {
        foreach ($Process in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
            try { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    Stop-ProcessAtPath -ProcessName "codex-app-server" -ExpectedPath (Join-Path $DeployDir "Codex\codex-app-server.exe")
    Start-Sleep -Milliseconds 350
}

function Copy-WithRetry([string]$Source, [string]$Destination) {
    $LastError = $null
    for ($i = 1; $i -le 25; $i++) {
        try {
            Copy-Item $Source $Destination -Force -ErrorAction Stop
            return
        }
        catch {
            $LastError = $_
            Start-Sleep -Milliseconds 200
        }
    }
    throw "Unable to replace $Destination after retries: $($LastError.Exception.Message)"
}

function Test-Binary([string]$Exe, [string]$Name) {
    Step "Running $Name self-test"
    $Process = Start-Process -FilePath $Exe -ArgumentList "--self-test" -Wait -PassThru
    if ($Process.ExitCode -ne 0) {
        throw "$Name self-test failed with exit code $($Process.ExitCode)"
    }
}

function Get-MainSha {
    $Sha = ((& gh api "repos/$Repo/commits/main" --jq ".sha") | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Sha)) {
        throw "Unable to resolve main commit SHA"
    }
    return [string]$Sha
}

function Get-RunForCommit([string]$Sha, [switch]$SuccessfulOnly, [switch]$WorkflowDispatchOnly) {
    $Args = @(
        "run", "list",
        "--repo", $Repo,
        "--workflow", $Workflow,
        "--commit", $Sha,
        "--limit", "1",
        "--json", "databaseId,headSha,status,conclusion,event"
    )
    if ($SuccessfulOnly) { $Args += @("--status", "success") }
    if ($WorkflowDispatchOnly) { $Args += @("--event", "workflow_dispatch") }

    $Json = & gh @Args
    if ($LASTEXITCODE -ne 0) { throw "Unable to query GitHub Actions runs" }
    $Runs = @($Json | ConvertFrom-Json)
    if ($Runs.Count -eq 0) { return $null }
    return $Runs[0]
}

function Wait-ForRun([long]$RunId) {
    Step "Waiting for GitHub Actions run $RunId"
    & gh run watch $RunId --repo $Repo --exit-status | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Step "Failed GitHub Actions log for run $RunId"
        & gh run view $RunId --repo $Repo --log-failed | Out-Host
        throw "GitHub Actions build failed (run $RunId)"
    }
    return $RunId
}

function Start-And-WaitForRun([string]$Sha, [bool]$FullPackage) {
    if ($FullPackage) {
        Step "Starting FULL ARM64 package validation"
        & gh workflow run $Workflow --repo $Repo --ref main -f full_package=true | Out-Host
    }
    else {
        Step "Starting QUICK ARM64 validation"
        & gh workflow run $Workflow --repo $Repo --ref main | Out-Host
    }
    if ($LASTEXITCODE -ne 0) { throw "Unable to start GitHub Actions workflow" }

    $Run = $null
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        $Run = Get-RunForCommit $Sha -WorkflowDispatchOnly
        if ($Run) { break }
    }
    if (-not $Run) { throw "Workflow was started but its run could not be found" }
    return (Wait-ForRun -RunId ([long]$Run.databaseId))
}

function Resolve-Run([string]$Sha) {
    if ($Full) {
        return (Start-And-WaitForRun -Sha $Sha -FullPackage $true)
    }

    $Successful = Get-RunForCommit $Sha -SuccessfulOnly
    if ($Successful) {
        Step "Found successful QUICK build for current main: run $($Successful.databaseId)"
        return [long]$Successful.databaseId
    }

    $Existing = Get-RunForCommit $Sha
    if ($Existing -and $Existing.status -ne "completed") {
        Step "Using validation already running for current main: run $($Existing.databaseId)"
        return (Wait-ForRun -RunId ([long]$Existing.databaseId))
    }

    return (Start-And-WaitForRun -Sha $Sha -FullPackage $false)
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

    $SearchExe = Get-ChildItem -Path $Temp -Filter $ExeName -Recurse | Select-Object -First 1
    $WallpaperExe = Get-ChildItem -Path $Temp -Filter $WallpaperExeName -Recurse | Select-Object -First 1
    $HarnessExe = Get-ChildItem -Path $Temp -Filter $HarnessExeName -Recurse | Select-Object -First 1
    foreach ($Required in @($SearchExe, $WallpaperExe, $HarnessExe)) {
        if (-not $Required) {
            Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
            throw "ARM64 artifact is missing a required native executable"
        }
    }

    $Root = $SearchExe.Directory.FullName
    $HarnessRuntime = Join-Path $Root "HarnessRuntime"
    $HasHarnessRuntime = (Test-Path (Join-Path $HarnessRuntime "Node\node.exe") -PathType Leaf) -and
                         (Test-Path (Join-Path $HarnessRuntime "Dsh\node_modules\@deepseek-ai\dsh\lib\bin.js") -PathType Leaf)

    return @{
        Exe = $SearchExe.FullName
        WallpaperExe = $WallpaperExe.FullName
        HarnessExe = $HarnessExe.FullName
        HarnessRuntime = $HarnessRuntime
        HasHarnessRuntime = $HasHarnessRuntime
        Root = $Root
        Temp = $Temp
    }
}

function Deploy-HarnessRuntime($Downloaded) {
    $DestinationDir = Join-Path $DeployDir "HarnessRuntime"
    $DestinationNode = Join-Path $DestinationDir "Node\node.exe"
    $DestinationDsh = Join-Path $DestinationDir "Dsh\node_modules\@deepseek-ai\dsh\lib\bin.js"

    if ($Downloaded.HasHarnessRuntime) {
        Step "Deploying bundled DeepSeek Harness runtime"
        if (Test-Path $DestinationDir) {
            Remove-Item $DestinationDir -Recurse -Force -ErrorAction Stop
        }
        New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
        Copy-Item (Join-Path $Downloaded.HarnessRuntime "*") $DestinationDir -Recurse -Force
        if (-not (Test-Path $DestinationNode -PathType Leaf)) { throw "Deployed Node runtime is missing: $DestinationNode" }
        if (-not (Test-Path $DestinationDsh -PathType Leaf)) { throw "Deployed DeepSeek Harness entrypoint is missing: $DestinationDsh" }
        Write-Host "Bundled Harness runtime deployed." -ForegroundColor Green
        return $true
    }

    if ((Test-Path $DestinationNode -PathType Leaf) -and (Test-Path $DestinationDsh -PathType Leaf)) {
        Write-Host "QUICK deploy: keeping the existing DeepSeek Harness runtime." -ForegroundColor DarkGray
        return $true
    }

    Write-Host "QUICK deploy: Harness runtime is not installed yet. UI can be tested now; run this script with -Full before validating L4 Harness." -ForegroundColor Yellow
    return $false
}

function Deploy-BundledEverything([string]$ArtifactRoot) {
    $SourceDir = Join-Path $ArtifactRoot "Everything"
    $DestinationDir = Join-Path $DeployDir "Everything"
    $DestinationExe = Join-Path $DestinationDir "Everything.exe"

    if (-not (Test-Path $SourceDir -PathType Container)) {
        if (Test-Path $DestinationExe -PathType Leaf) {
            Write-Host "QUICK deploy: keeping existing Everything runtime." -ForegroundColor DarkGray
            return $DestinationExe
        }
        Write-Host "QUICK deploy: Everything runtime is not installed yet; file search may be unavailable until a -Full deploy." -ForegroundColor Yellow
        return $null
    }

    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    if (-not (Test-Path $DestinationExe)) {
        Step "Installing bundled Everything files"
        Copy-Item (Join-Path $SourceDir "*") $DestinationDir -Recurse -Force
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
    if (-not $Service -and $Full) {
        Step "Installing Everything indexing service (one-time)"
        Write-Host "Windows may show one UAC prompt." -ForegroundColor Yellow
        try {
            Start-Process -FilePath $EverythingExe -ArgumentList "-install-service" -Verb RunAs -Wait | Out-Null
        }
        catch {
            Write-Host "WARNING: Everything service was not installed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

function Ensure-CodexRuntime {
    $CodexDir = Join-Path $DeployDir "Codex"
    $CodexExe = Join-Path $CodexDir "codex-app-server.exe"
    if (Test-Path $CodexExe) { return $CodexExe }
    if (-not $Full) {
        Write-Host "QUICK deploy: skipping optional Codex Runtime download." -ForegroundColor DarkGray
        return $null
    }

    Step "Installing Codex Agent Runtime (one-time, optional)"
    $Temp = Join-Path $env:TEMP ("TuringDesk-Codex-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $Temp | Out-Null
    try {
        & gh release download $CodexRelease --repo $CodexRepo --pattern $CodexAsset --dir $Temp --clobber | Out-Host
        if ($LASTEXITCODE -ne 0) { return $null }
        $Zip = Get-ChildItem -Path $Temp -Filter "*.zip" | Select-Object -First 1
        if (-not $Zip) { return $null }
        $Expanded = Join-Path $Temp "expanded"
        Expand-Archive $Zip.FullName -DestinationPath $Expanded -Force
        $Found = Get-ChildItem -Path $Expanded -Filter "codex-app-server-aarch64-pc-windows-msvc.exe" -Recurse | Select-Object -First 1
        if (-not $Found) { return $null }
        New-Item -ItemType Directory -Force -Path $CodexDir | Out-Null
        Copy-Item $Found.FullName $CodexExe -Force
        return $CodexExe
    }
    catch {
        Write-Host "WARNING: Codex Runtime install failed: $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
    finally {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Should-ShowWallpaperSettings {
    $Config = Join-Path $env:LOCALAPPDATA "TuringDesk\wallpaper.ini"
    if (-not (Test-Path $Config)) { return $true }
    try {
        $Version = Select-String -Path $Config -Pattern '^Version=3$' -ErrorAction Stop
        return -not [bool]$Version
    }
    catch { return $true }
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
Write-Host ($(if ($Full) { "Deploy mode: FULL runtime validation" } else { "Deploy mode: QUICK native validation" })) -ForegroundColor DarkGray

$RunId = [long](Resolve-Run -Sha $MainSha)
$Downloaded = Download-Artifact -RunId $RunId
try {
    Test-Binary -Exe $Downloaded.Exe -Name "Native Search"
    Test-Binary -Exe $Downloaded.WallpaperExe -Name "Native Wallpaper"
    Test-Binary -Exe $Downloaded.HarnessExe -Name "Native Harness host"

    Step "Deploying to $DeployDir"
    Stop-DeployedInstance
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

    $DeployedExe = Join-Path $DeployDir $ExeName
    $DeployedWallpaper = Join-Path $DeployDir $WallpaperExeName
    $DeployedHarness = Join-Path $DeployDir $HarnessExeName
    Copy-WithRetry -Source $Downloaded.Exe -Destination $DeployedExe
    Copy-WithRetry -Source $Downloaded.WallpaperExe -Destination $DeployedWallpaper
    Copy-WithRetry -Source $Downloaded.HarnessExe -Destination $DeployedHarness

    $HarnessReady = Deploy-HarnessRuntime -Downloaded $Downloaded
    $EverythingExe = Deploy-BundledEverything -ArtifactRoot $Downloaded.Root
    Ensure-EverythingService -EverythingExe $EverythingExe
    Ensure-CodexRuntime | Out-Null

    Test-Binary -Exe $DeployedWallpaper -Name "Deployed Wallpaper"
    Test-Binary -Exe $DeployedHarness -Name "Deployed Harness host"

    Step "Starting TuringDesk Wallpaper"
    if (Should-ShowWallpaperSettings) {
        Start-Process -FilePath $DeployedWallpaper -ArgumentList "--settings"
    }
    else {
        Start-Process -FilePath $DeployedWallpaper
    }

    Step "Starting TuringDesk Native Search"
    Start-Process $DeployedExe

    Write-Host "`nDeployment complete. Press Alt+Space to open Search." -ForegroundColor Green
    Write-Host "Use the top-right 设置 button to open the TuringDesk 设置中心." -ForegroundColor Green
    if ($HarnessReady) {
        Write-Host "DeepSeek Harness runtime is available from 设置中心 -> DeepSeek Harness (L4)." -ForegroundColor Green
    }
    else {
        Write-Host "For the first real Harness validation, run: .\scripts\deploy-native-arm64.ps1 -Full" -ForegroundColor Yellow
    }
    Write-Host "Path: $DeployDir" -ForegroundColor DarkGray
}
finally {
    Remove-Item $Downloaded.Temp -Recurse -Force -ErrorAction SilentlyContinue
}

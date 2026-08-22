param(
    [string]$Repo = "GoodLoongStudio/TuringDesk"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$DeployDir = Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"
$ArtifactName = "TuringDesk-Native-Search-ARM64"
$Workflow = "native-search-windows.yml"
$ExeName = "TuringDesk.exe"
$WallpaperExeName = "TuringDeskWallpaper.exe"
$HarnessExeName = "TuringDeskHarness.exe"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Stop-DeployedInstance {
    Step "Stopping previous TuringDesk processes"
    foreach ($ProcessName in @("TuringDesk", "TuringDeskWallpaper", "TuringDeskHarness")) {
        foreach ($Process in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
            try { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    try {
        $runtimeNode = [System.IO.Path]::GetFullPath((Join-Path $DeployDir "Runtime\Node"))
        foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction Stop)) {
            $exe = [string]$process.ExecutablePath
            if (-not $exe) { continue }
            $full = [System.IO.Path]::GetFullPath($exe)
            if ($full.StartsWith($runtimeNode + "\", [System.StringComparison]::OrdinalIgnoreCase)) {
                & taskkill.exe /PID $process.ProcessId /T /F 2>$null | Out-Null
            }
        }
    }
    catch { }
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

function Test-HarnessSmoke([string]$Exe) {
    Step "Running bundled DeepSeek Harness smoke test"
    $Process = Start-Process -FilePath $Exe -ArgumentList "--harness-smoke-test" -Wait -PassThru
    if ($Process.ExitCode -ne 0) {
        $log = Join-Path $env:LOCALAPPDATA "TuringDesk\Logs\harness.log"
        throw "Bundled DeepSeek Harness smoke test failed with exit code $($Process.ExitCode). Log: $log"
    }
}

function Get-MainSha {
    $Sha = ((& gh api "repos/$Repo/commits/main" --jq ".sha") | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Sha)) {
        throw "Unable to resolve main commit SHA"
    }
    return [string]$Sha
}

function Get-RunsForCommit([string]$Sha) {
    $Json = & gh run list --repo $Repo --workflow $Workflow --commit $Sha --limit 20 --json databaseId,headSha,status,conclusion,event,createdAt
    if ($LASTEXITCODE -ne 0) { throw "Unable to query GitHub Actions runs" }
    return @($Json | ConvertFrom-Json)
}

function Wait-ForRun([long]$RunId) {
    Step "Waiting for ARM64 GitHub Actions run $RunId"
    & gh run watch $RunId --repo $Repo --exit-status | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Step "Failed ARM64 GitHub Actions log for run $RunId"
        & gh run view $RunId --repo $Repo --log-failed | Out-Host
        throw "ARM64 GitHub Actions build failed (run $RunId)"
    }
    return $RunId
}

function Start-And-WaitForRun([string]$Sha) {
    $Before = @((Get-RunsForCommit -Sha $Sha) | ForEach-Object { [long]$_.databaseId })
    Step "Starting ARM64-only native validation"
    & gh workflow run $Workflow --repo $Repo --ref main | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Unable to start ARM64 GitHub Actions workflow" }

    $Run = $null
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        $Run = (Get-RunsForCommit -Sha $Sha) |
            Where-Object { ([long]$_.databaseId -notin $Before) -and $_.event -eq "workflow_dispatch" } |
            Sort-Object createdAt -Descending |
            Select-Object -First 1
        if ($Run) { break }
    }
    if (-not $Run) { throw "ARM64 workflow was started but its run could not be found" }
    return (Wait-ForRun -RunId ([long]$Run.databaseId))
}

function Resolve-Run([string]$Sha) {
    $Runs = Get-RunsForCommit -Sha $Sha
    $Successful = $Runs | Where-Object { $_.status -eq "completed" -and $_.conclusion -eq "success" } |
        Sort-Object createdAt -Descending | Select-Object -First 1
    if ($Successful) {
        Step "Found successful ARM64 build for current main: run $($Successful.databaseId)"
        return [long]$Successful.databaseId
    }

    $PushRun = $Runs | Where-Object { $_.event -eq "push" -and $_.status -ne "completed" } |
        Sort-Object createdAt -Descending | Select-Object -First 1
    if ($PushRun) {
        Step "Using automatic ARM64 validation already running: run $($PushRun.databaseId)"
        return (Wait-ForRun -RunId ([long]$PushRun.databaseId))
    }

    return (Start-And-WaitForRun -Sha $Sha)
}

function Download-Artifact([long]$RunId) {
    $Temp = Join-Path $env:TEMP ("TuringDesk-ARM64-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $Temp | Out-Null

    Step "Downloading verified ARM64 artifact from TuringDesk run $RunId"
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

    return @{
        Exe = $SearchExe.FullName
        WallpaperExe = $WallpaperExe.FullName
        HarnessExe = $HarnessExe.FullName
        Temp = $Temp
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

$BundledNode = Join-Path $DeployDir "Runtime\Node\node.exe"
$BundledHarness = Join-Path $DeployDir "Runtime\Node\node_modules\@deepseek-ai\dsh\lib\bin.js"
if (-not (Test-Path $BundledNode -PathType Leaf) -or -not (Test-Path $BundledHarness -PathType Leaf)) {
    throw "Repository-vendored ARM64 RuntimeBundle is not prepared. Run only DEPLOY-NATIVE-ARM64.cmd; its previous step should prepare it."
}
Write-Host "DeepSeek Harness mode: repository-vendored official package + bundled ARM64 Node + TuringDesk WebView2 shell" -ForegroundColor Green
Write-Host "Bundled Node: $BundledNode" -ForegroundColor DarkGray

Step "Resolving current main commit"
$MainSha = Get-MainSha
Write-Host "main: $MainSha" -ForegroundColor DarkGray
Write-Host "Deploy mode: ARM64 only" -ForegroundColor DarkGray

$RunId = [long](Resolve-Run -Sha $MainSha)
$Downloaded = Download-Artifact -RunId $RunId
try {
    Test-Binary -Exe $Downloaded.Exe -Name "Native Search"
    Test-Binary -Exe $Downloaded.WallpaperExe -Name "Native Wallpaper"
    Test-Binary -Exe $Downloaded.HarnessExe -Name "Native Harness shell"

    Step "Deploying ARM64 binaries to $DeployDir"
    Stop-DeployedInstance
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

    $DeployedExe = Join-Path $DeployDir $ExeName
    $DeployedWallpaper = Join-Path $DeployDir $WallpaperExeName
    $DeployedHarness = Join-Path $DeployDir $HarnessExeName
    Copy-WithRetry -Source $Downloaded.Exe -Destination $DeployedExe
    Copy-WithRetry -Source $Downloaded.WallpaperExe -Destination $DeployedWallpaper
    Copy-WithRetry -Source $Downloaded.HarnessExe -Destination $DeployedHarness

    Test-Binary -Exe $DeployedExe -Name "Deployed Search"
    Test-Binary -Exe $DeployedWallpaper -Name "Deployed Wallpaper"
    Test-Binary -Exe $DeployedHarness -Name "Deployed Harness shell"
    Test-HarnessSmoke -Exe $DeployedHarness

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
    Write-Host "DeepSeek Harness is running from the pinned RuntimeBundle in this repository; no npm install occurs on the user machine." -ForegroundColor Green
    Write-Host "Path: $DeployDir" -ForegroundColor DarkGray
}
finally {
    if ($Downloaded -and $Downloaded.Temp) {
        Remove-Item $Downloaded.Temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

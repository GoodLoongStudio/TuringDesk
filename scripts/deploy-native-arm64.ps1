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
    Stop-ProcessAtPath -ProcessName "Everything" -ExpectedPath (Join-Path $DeployDir "Everything\Everything.exe")
    Start-Sleep -Milliseconds 500
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

function Test-Binary([string]$Exe) {
    Step "Running Native Search self-test"
    $Process = Start-Process -FilePath $Exe -ArgumentList "--self-test" -Wait -PassThru
    if ($Process.ExitCode -ne 0) {
        throw "Search self-test failed with exit code $($Process.ExitCode)"
    }
}

function Test-WallpaperBinary([string]$Exe) {
    Step "Running Native Wallpaper self-test"
    $Process = Start-Process -FilePath $Exe -ArgumentList "--self-test" -Wait -PassThru
    if ($Process.ExitCode -ne 0) {
        throw "Wallpaper self-test failed with exit code $($Process.ExitCode)"
    }
}

function Test-HarnessBinary([string]$Exe) {
    Step "Running Native Harness host self-test"
    $Process = Start-Process -FilePath $Exe -ArgumentList "--self-test" -Wait -PassThru
    if ($Process.ExitCode -ne 0) {
        throw "Harness host self-test failed with exit code $($Process.ExitCode)"
    }
}

function Install-WallpaperShortcut([string]$WallpaperExe) {
    try {
        $Programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\TuringDesk"
        New-Item -ItemType Directory -Force -Path $Programs | Out-Null
        $ShortcutPath = Join-Path $Programs "TuringDesk 壁纸设置.lnk"
        $Shell = New-Object -ComObject WScript.Shell
        $Shortcut = $Shell.CreateShortcut($ShortcutPath)
        $Shortcut.TargetPath = $WallpaperExe
        $Shortcut.Arguments = "--settings"
        $Shortcut.WorkingDirectory = $DeployDir
        $Shortcut.Description = "TuringDesk Wallpaper Settings"
        $Shortcut.Save()
        Write-Host "Wallpaper shortcut installed: $ShortcutPath" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "WARNING: Unable to create Wallpaper shortcut: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Should-ShowWallpaperSettings {
    $Config = Join-Path $env:LOCALAPPDATA "TuringDesk\wallpaper.ini"
    if (-not (Test-Path $Config)) { return $true }
    try {
        $Version = Select-String -Path $Config -Pattern '^Version=3$' -SimpleMatch:$false -ErrorAction Stop
        return -not [bool]$Version
    }
    catch {
        return $true
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

    $SearchExe = Get-ChildItem -Path $Temp -Filter $ExeName -Recurse | Select-Object -First 1
    $WallpaperExe = Get-ChildItem -Path $Temp -Filter $WallpaperExeName -Recurse | Select-Object -First 1
    $HarnessExe = Get-ChildItem -Path $Temp -Filter $HarnessExeName -Recurse | Select-Object -First 1
    if (-not $SearchExe) {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
        throw "$ExeName was not found in ARM64 artifact"
    }
    if (-not $WallpaperExe) {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
        throw "$WallpaperExeName was not found in ARM64 artifact"
    }
    if (-not $HarnessExe) {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
        throw "$HarnessExeName was not found in ARM64 artifact"
    }

    $Root = $SearchExe.Directory.FullName
    $HarnessRuntime = Join-Path $Root "HarnessRuntime"
    $BundledNode = Join-Path $HarnessRuntime "Node\node.exe"
    $BundledDsh = Join-Path $HarnessRuntime "Dsh\node_modules\@deepseek-ai\dsh\lib\bin.js"
    if (-not (Test-Path $BundledNode -PathType Leaf)) {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
        throw "Bundled Node runtime was not found in ARM64 artifact: $BundledNode"
    }
    if (-not (Test-Path $BundledDsh -PathType Leaf)) {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
        throw "Bundled DeepSeek Harness runtime was not found in ARM64 artifact: $BundledDsh"
    }

    return @{
        Exe = $SearchExe.FullName
        WallpaperExe = $WallpaperExe.FullName
        HarnessExe = $HarnessExe.FullName
        HarnessRuntime = $HarnessRuntime
        Root = $Root
        Temp = $Temp
    }
}

function Deploy-HarnessRuntime([string]$ArtifactRoot) {
    $SourceDir = Join-Path $ArtifactRoot "HarnessRuntime"
    $DestinationDir = Join-Path $DeployDir "HarnessRuntime"
    if (-not (Test-Path $SourceDir -PathType Container)) {
        throw "ARM64 artifact does not contain HarnessRuntime"
    }

    Step "Deploying bundled DeepSeek Harness runtime"
    if (Test-Path $DestinationDir) {
        Remove-Item $DestinationDir -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    Copy-Item (Join-Path $SourceDir "*") $DestinationDir -Recurse -Force

    $Node = Join-Path $DestinationDir "Node\node.exe"
    $Dsh = Join-Path $DestinationDir "Dsh\node_modules\@deepseek-ai\dsh\lib\bin.js"
    if (-not (Test-Path $Node -PathType Leaf)) { throw "Deployed Node runtime is missing: $Node" }
    if (-not (Test-Path $Dsh -PathType Leaf)) { throw "Deployed DeepSeek Harness entrypoint is missing: $Dsh" }
    Write-Host "Bundled Harness runtime deployed." -ForegroundColor Green
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
    Test-WallpaperBinary $Downloaded.WallpaperExe
    Test-HarnessBinary $Downloaded.HarnessExe

    Step "Deploying to $DeployDir"
    Stop-DeployedInstance
    New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

    $DeployedExe = Join-Path $DeployDir $ExeName
    $DeployedWallpaper = Join-Path $DeployDir $WallpaperExeName
    $DeployedHarness = Join-Path $DeployDir $HarnessExeName
    Copy-WithRetry -Source $Downloaded.Exe -Destination $DeployedExe
    Copy-WithRetry -Source $Downloaded.WallpaperExe -Destination $DeployedWallpaper
    Copy-WithRetry -Source $Downloaded.HarnessExe -Destination $DeployedHarness
    Deploy-HarnessRuntime -ArtifactRoot $Downloaded.Root

    Test-WallpaperBinary $DeployedWallpaper
    Test-HarnessBinary $DeployedHarness
    Install-WallpaperShortcut $DeployedWallpaper

    $EverythingExe = Deploy-BundledEverything -ArtifactRoot $Downloaded.Root
    Ensure-EverythingService -EverythingExe $EverythingExe
    Ensure-CodexRuntime | Out-Null

    $ShowWallpaperSettings = Should-ShowWallpaperSettings
    Step "Starting TuringDesk Wallpaper"
    if ($ShowWallpaperSettings) {
        Start-Process -FilePath $DeployedWallpaper -ArgumentList "--settings"
    }
    else {
        Start-Process -FilePath $DeployedWallpaper
    }

    Step "Starting TuringDesk Native Search"
    Start-Process $DeployedExe
    Write-Host "`nDeployment complete. Press Alt+Space to open Search." -ForegroundColor Green
    Write-Host "Search 'TuringDesk 壁纸设置' or use the wallpaper tray icon to change scenes." -ForegroundColor Green
    Write-Host "Open AI settings to launch the independent DeepSeek Harness (L4) window." -ForegroundColor Green
    Write-Host "Wallpaper settings: $DeployedWallpaper --settings" -ForegroundColor DarkGray
    Write-Host "Harness host: $DeployedHarness" -ForegroundColor DarkGray
    Write-Host "Harness log: $(Join-Path $env:LOCALAPPDATA 'TuringDesk\Logs\harness.log')" -ForegroundColor DarkGray
    Write-Host "Path: $DeployDir"
}
finally {
    Remove-Item $Downloaded.Temp -Recurse -Force -ErrorAction SilentlyContinue
}

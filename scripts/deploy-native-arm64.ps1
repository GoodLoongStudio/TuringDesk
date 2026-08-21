param(
    [string]$Repo = "GoodLoongStudio/TuringDesk"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$DeployDir = Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"
$ArtifactName = "TuringDesk-Native-Search-ARM64"
$Workflow = "native-search-windows.yml"
$ExeName = "TuringDesk.exe"

function Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Stop-DeployedInstance {
    $Target = (Join-Path $DeployDir $ExeName).ToLowerInvariant()
    Get-CimInstance Win32_Process -Filter "Name='TuringDesk.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant() -eq $Target } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
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
    if ($SuccessfulOnly) {
        $Args += @("--status", "success")
    }

    $Json = & gh @Args
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query GitHub Actions runs"
    }

    $Runs = @($Json | ConvertFrom-Json)
    if ($Runs.Count -eq 0) {
        return $null
    }
    return $Runs[0]
}

function Start-And-WaitForRun([string]$Sha) {
    Step "No successful ARM64 package for current main; starting CI"
    & gh workflow run $Workflow --repo $Repo --ref main | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start GitHub Actions workflow"
    }

    $Run = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $Run = Get-RunForCommit $Sha
        if ($Run) { break }
    }

    if (-not $Run) {
        throw "Workflow was started but its run could not be found"
    }

    $RunId = [long]$Run.databaseId
    Step "Waiting for GitHub Actions run $RunId"
    & gh run watch $RunId --repo $Repo --exit-status | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Actions build failed"
    }

    return $RunId
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
        Temp = $Temp
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
    Copy-Item $Downloaded.Exe $DeployedExe -Force

    $Everything = Get-Process Everything -ErrorAction SilentlyContinue
    if (-not $Everything) {
        Write-Host "NOTE: Everything is not running. L2 file search is unavailable; L1 and L3 still work." -ForegroundColor Yellow
    }

    Step "Starting TuringDesk Native Search"
    Start-Process $DeployedExe
    Write-Host "`nDeployment complete. Press Alt+Space to open Search." -ForegroundColor Green
    Write-Host "Path: $DeployDir"
}
finally {
    Remove-Item $Downloaded.Temp -Recurse -Force -ErrorAction SilentlyContinue
}

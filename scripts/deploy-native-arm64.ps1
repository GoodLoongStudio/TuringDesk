param(
    [string]$Repo = "GoodLoongStudio/TuringDesk"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$BuildDir = Join-Path $Root "build\arm64-local"
$DeployDir = Join-Path $env:LOCALAPPDATA "TuringDesk\NativeTest"
$ArtifactName = "TuringDesk-Native-Search-ARM64"
$Workflow = "native-search-windows.yml"
$ExeName = "TuringDesk.exe"

function Step([string]$text) {
    Write-Host "`n==> $text" -ForegroundColor Cyan
}

function Stop-DeployedInstance {
    $target = (Join-Path $DeployDir $ExeName).ToLowerInvariant()
    Get-CimInstance Win32_Process -Filter "Name='TuringDesk.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant() -eq $target } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Test-Binary([string]$exe) {
    Step "运行 Native Search 自测"
    & $exe --self-test
    if ($LASTEXITCODE -ne 0) { throw "--self-test failed with exit code $LASTEXITCODE" }
}

function Try-LocalBuild {
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { return $null }

    try {
        Step "检测到 CMake：优先本地 ARM64 增量编译（最快）"
        & cmake -S $Root -B $BuildDir -A ARM64
        if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
        & cmake --build $BuildDir --config Release --parallel
        if ($LASTEXITCODE -ne 0) { throw "CMake build failed" }

        $exe = Join-Path $BuildDir "src\native\Release\$ExeName"
        if (-not (Test-Path $exe)) { throw "Build succeeded but $ExeName was not found" }
        return $exe
    }
    catch {
        Write-Warning "本地 ARM64 编译不可用：$($_.Exception.Message)"
        Write-Host "自动切换到 GitHub Actions ARM64 Artifact。" -ForegroundColor Yellow
        return $null
    }
}

function Download-LatestArtifact {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "未找到 GitHub CLI (gh)。请安装或把 gh 加入 PATH。"
    }

    Step "检查 GitHub CLI 登录状态"
    & gh auth status 2>$null
    if ($LASTEXITCODE -ne 0) { throw "gh 尚未登录，请先执行: gh auth login" }

    Step "查找 main 最新成功 ARM64 构建"
    $json = & gh run list --repo $Repo --workflow $Workflow --branch main --status success --limit 1 --json databaseId,headSha,conclusion
    if ($LASTEXITCODE -ne 0) { throw "无法读取 GitHub Actions runs" }
    $runs = @($json | ConvertFrom-Json)

    if ($runs.Count -eq 0) {
        Step "没有可用成功构建，触发一次 ARM64 CI"
        & gh workflow run $Workflow --repo $Repo --ref main
        if ($LASTEXITCODE -ne 0) { throw "无法触发 workflow" }
        Start-Sleep -Seconds 4
        $json = & gh run list --repo $Repo --workflow $Workflow --branch main --limit 1 --json databaseId,headSha,status,conclusion
        $runs = @($json | ConvertFrom-Json)
        if ($runs.Count -eq 0) { throw "已触发 workflow，但没有找到 run" }
        & gh run watch $runs[0].databaseId --repo $Repo --exit-status
        if ($LASTEXITCODE -ne 0) { throw "ARM64 CI 构建失败" }
    }

    $runId = $runs[0].databaseId
    $temp = Join-Path $env:TEMP ("TuringDesk-ARM64-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $temp | Out-Null

    try {
        Step "下载 ARM64 Artifact (run $runId)"
        & gh run download $runId --repo $Repo --name $ArtifactName --dir $temp
        if ($LASTEXITCODE -ne 0) { throw "Artifact 下载失败" }
        $exe = Get-ChildItem -Path $temp -Filter $ExeName -Recurse | Select-Object -First 1
        if (-not $exe) { throw "Artifact 中没有找到 $ExeName" }

        $cache = Join-Path $Root "build\arm64-artifact"
        Remove-Item $cache -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $cache | Out-Null
        Copy-Item $exe.FullName (Join-Path $cache $ExeName) -Force
        return (Join-Path $cache $ExeName)
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Step "同步 main"
if (Test-Path (Join-Path $Root ".git")) {
    & git -C $Root fetch origin main
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }
    $branch = (& git -C $Root branch --show-current).Trim()
    if ($branch -eq "main") {
        & git -C $Root pull --ff-only origin main
        if ($LASTEXITCODE -ne 0) { throw "git pull --ff-only failed；请先处理本地改动" }
    } else {
        Write-Host "当前分支是 $branch；不自动切分支，继续构建当前工作树。" -ForegroundColor Yellow
    }
}

$exe = Try-LocalBuild
if (-not $exe) { $exe = Download-LatestArtifact }

Test-Binary $exe

Step "部署到 $DeployDir"
Stop-DeployedInstance
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
Copy-Item $exe (Join-Path $DeployDir $ExeName) -Force

$everything = Get-Process Everything -ErrorAction SilentlyContinue
if (-not $everything) {
    Write-Host "提示：Everything 当前未运行，L2 文件搜索会不可用；L1/L3 不受影响。" -ForegroundColor Yellow
}

Step "启动 TuringDesk Native Search"
Start-Process (Join-Path $DeployDir $ExeName)
Write-Host "`n部署完成。按 Alt+Space 打开 Search。" -ForegroundColor Green
Write-Host "路径: $DeployDir"

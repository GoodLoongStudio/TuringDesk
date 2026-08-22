Set-StrictMode -Version Latest

function Test-TuringDeskDownloadedFile {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string]$ExpectedSha256
    )

    if (-not (Test-Path $Path -PathType Leaf)) { return $false }
    if ((Get-Item $Path).Length -le 0) { return $false }
    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) { return $true }

    try {
        $actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        return $actual -eq $ExpectedSha256.ToLowerInvariant()
    }
    catch {
        return $false
    }
}

function Get-TuringDeskBrowserDownloadCandidates {
    param([Parameter(Mandatory=$true)][string]$FileName)

    $roots = New-Object System.Collections.Generic.List[string]
    $profile = [Environment]::GetFolderPath('UserProfile')
    if (-not [string]::IsNullOrWhiteSpace($profile)) {
        $roots.Add((Join-Path $profile 'Downloads'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:OneDrive)) {
        $roots.Add((Join-Path $env:OneDrive 'Downloads'))
    }

    foreach ($root in $roots | Select-Object -Unique) {
        if (-not (Test-Path $root -PathType Container)) { continue }
        $exact = Join-Path $root $FileName
        if (Test-Path $exact -PathType Leaf) { $exact }
        Get-ChildItem -Path $root -File -Filter "$([System.IO.Path]::GetFileNameWithoutExtension($FileName))*$([System.IO.Path]::GetExtension($FileName))" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            ForEach-Object { $_.FullName }
    }
}

function Invoke-TuringDeskBitsDownload {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$Destination,
        [Parameter(Mandatory=$true)][string]$Name,
        [int]$TimeoutSeconds = 300,
        [int]$StallSeconds = 25
    )

    if (-not (Get-Command Start-BitsTransfer -ErrorAction SilentlyContinue)) { return $false }

    $job = $null
    try {
        Write-Host "Trying Windows BITS (system/browser-like network path)..." -ForegroundColor DarkGray
        $job = Start-BitsTransfer -Source $Url -Destination $Destination -DisplayName "TuringDesk $Name" -Asynchronous -ErrorAction Stop
        $started = Get-Date
        $lastProgress = Get-Date
        [Int64]$lastBytes = -1
        while ($true) {
            $job = Get-BitsTransfer -Id $job.Id -ErrorAction Stop
            if ($job.JobState -eq 'Transferred') {
                Complete-BitsTransfer -BitsJob $job -ErrorAction Stop
                return $true
            }
            if ($job.JobState -in @('Error','TransientError','Cancelled','Acknowledged')) {
                throw "BITS state: $($job.JobState); $($job.ErrorDescription)"
            }

            $bytes = [Int64]$job.BytesTransferred
            $total = [Int64]$job.BytesTotal
            if ($bytes -ne $lastBytes) {
                $lastBytes = $bytes
                $lastProgress = Get-Date
            }
            if ($total -gt 0) {
                $percent = [Math]::Min(100, [Math]::Round(($bytes * 100.0) / $total, 1))
                Write-Progress -Activity "Downloading $Name" -Status "$percent%" -PercentComplete $percent
            }

            if (((Get-Date) - $started).TotalSeconds -ge $TimeoutSeconds) {
                throw "BITS timed out after $TimeoutSeconds seconds"
            }
            if ($bytes -gt 0 -and ((Get-Date) - $lastProgress).TotalSeconds -ge $StallSeconds) {
                throw "BITS stalled for $StallSeconds seconds"
            }
            Start-Sleep -Milliseconds 750
        }
    }
    catch {
        Write-Host "BITS did not complete: $($_.Exception.Message)" -ForegroundColor DarkGray
        if ($job) {
            try { Remove-BitsTransfer -BitsJob $job -ErrorAction SilentlyContinue } catch { }
        }
        Remove-Item $Destination -Force -ErrorAction SilentlyContinue
        return $false
    }
    finally {
        Write-Progress -Activity "Downloading $Name" -Completed
    }
}

function Invoke-TuringDeskSmartDownload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$Destination,
        [Parameter(Mandatory=$true)][string]$Name,
        [string]$ExpectedSha256,
        [string]$FileName,
        [int]$TimeoutSeconds = 300
    )

    if ([string]::IsNullOrWhiteSpace($FileName)) {
        try { $FileName = [System.IO.Path]::GetFileName(([Uri]$Url).AbsolutePath) } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($FileName)) { $FileName = [System.IO.Path]::GetFileName($Destination) }

    New-Item -ItemType Directory -Force -Path (Split-Path $Destination -Parent) | Out-Null

    if (Test-TuringDeskDownloadedFile -Path $Destination -ExpectedSha256 $ExpectedSha256) {
        Write-Host "Using cached ${Name}: $Destination" -ForegroundColor DarkGray
        return $Destination
    }
    Remove-Item $Destination -Force -ErrorAction SilentlyContinue

    $isGenericMetadata = $FileName -ieq 'SHASUMS256.txt' -or $FileName -ieq 'LICENSE.txt'
    if (-not $isGenericMetadata) {
        foreach ($candidate in @(Get-TuringDeskBrowserDownloadCandidates -FileName $FileName)) {
            if (Test-TuringDeskDownloadedFile -Path $candidate -ExpectedSha256 $ExpectedSha256) {
                Write-Host "Reusing browser download for ${Name}: $candidate" -ForegroundColor Green
                Copy-Item $candidate $Destination -Force
                return $Destination
            }
        }
    }

    $partial = "$Destination.partial"
    Remove-Item $partial -Force -ErrorAction SilentlyContinue

    Write-Host "Downloading $Name" -ForegroundColor Cyan
    Write-Host $Url -ForegroundColor DarkGray

    if (Invoke-TuringDeskBitsDownload -Url $Url -Destination $partial -Name $Name -TimeoutSeconds $TimeoutSeconds) {
        if (Test-TuringDeskDownloadedFile -Path $partial -ExpectedSha256 $ExpectedSha256) {
            Move-Item $partial $Destination -Force
            return $Destination
        }
        Write-Host "BITS download failed integrity validation; trying fallback." -ForegroundColor Yellow
        Remove-Item $partial -Force -ErrorAction SilentlyContinue
    }

    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        Write-Host "Trying curl over IPv4 with low-speed detection..." -ForegroundColor DarkGray
        & curl.exe --ipv4 -fL --retry 3 --retry-all-errors --retry-delay 1 --connect-timeout 15 --max-time $TimeoutSeconds --speed-limit 65536 --speed-time 20 --progress-bar $Url -o $partial
        if ($LASTEXITCODE -eq 0 -and (Test-TuringDeskDownloadedFile -Path $partial -ExpectedSha256 $ExpectedSha256)) {
            Move-Item $partial $Destination -Force
            return $Destination
        }
        Remove-Item $partial -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Trying Windows Invoke-WebRequest fallback..." -ForegroundColor DarkGray
    try {
        Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing -TimeoutSec $TimeoutSeconds -ErrorAction Stop
        if (Test-TuringDeskDownloadedFile -Path $partial -ExpectedSha256 $ExpectedSha256) {
            Move-Item $partial $Destination -Force
            return $Destination
        }
    }
    catch {
        Write-Host "Invoke-WebRequest failed: $($_.Exception.Message)" -ForegroundColor DarkGray
    }
    Remove-Item $partial -Force -ErrorAction SilentlyContinue

    throw "Unable to download $Name from the official source after cache, browser-download reuse, BITS, IPv4 curl, and Invoke-WebRequest fallbacks."
}

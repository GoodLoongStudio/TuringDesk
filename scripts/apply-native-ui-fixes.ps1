$ErrorActionPreference = "Stop"

function Read-Utf8([string]$Path) {
    return [System.IO.File]::ReadAllText((Resolve-Path $Path), [System.Text.Encoding]::UTF8)
}

function Write-Utf8([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Resolve-Path $Path), $Text, [System.Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $Text = Read-Utf8 $Path
    if (-not $Text.Contains($Old)) {
        throw "Expected text not found in $Path"
    }
    Write-Utf8 $Path ($Text.Replace($Old, $New))
}

$Header = "src/native/include/turingdesk/SearchWindow.h"
$Search = "src/native/src/SearchWindow.cpp"
$Everything = "src/native/src/EverythingSearch.cpp"
$CMake = "src/native/CMakeLists.txt"
$Deploy = "scripts/deploy-native-arm64.ps1"

Replace-Exact $Header `
'    HWND settingsButton_{};' `
"    HWND settingsButton_{};`n    HWND closeButton_{};"

Replace-Exact $Search `
'#include "turingdesk/ModelSettingsWindow.h"' `
"#include `"turingdesk/ModelSettingsWindow.h`"`n#include `"turingdesk/L3CliWindow.h`""

Replace-Exact $Search `
'constexpr int kSettingsButtonId = 101;' `
"constexpr int kSettingsButtonId = 101;`nconstexpr int kCloseButtonId = 102;"

$FormatNeedle = @'
    writeFactory_->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                                    DWRITE_FONT_STRETCH_NORMAL, 12.5f, L"zh-CN", subtitleFormat_.GetAddressOf());
'@
$FormatReplacement = $FormatNeedle + @'
    if (titleFormat_) titleFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    if (subtitleFormat_) subtitleFormat_->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
'@
Replace-Exact $Search $FormatNeedle $FormatReplacement

$ButtonNeedle = @'
    SendMessageW(settingsButton_, WM_SETFONT,
                 reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);
'@
$ButtonReplacement = $ButtonNeedle + @'

    closeButton_ = CreateWindowExW(0, L"BUTTON", L"×",
                                   WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                   kWindowWidth - 52, kWindowHeight - 48, 32, 30, hwnd_,
                                   reinterpret_cast<HMENU>(kCloseButtonId), instance_, nullptr);
    if (!closeButton_) return false;
    SendMessageW(closeButton_, WM_SETFONT,
                 reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);
'@
Replace-Exact $Search $ButtonNeedle $ButtonReplacement

$CommandNeedle = '        if (LOWORD(wParam) == kSettingsButtonId && HIWORD(wParam) == BN_CLICKED) { OpenModelSettings(); return 0; }'
$CommandReplacement = $CommandNeedle + "`n        if (LOWORD(wParam) == kCloseButtonId && HIWORD(wParam) == BN_CLICKED) { DestroyWindow(hwnd_); return 0; }"
Replace-Exact $Search $CommandNeedle $CommandReplacement

$SizeNeedle = '        if (settingsButton_) MoveWindow(settingsButton_, std::max(120, width - 118), 16, 98, 38, TRUE);'
$SizeReplacement = $SizeNeedle + "`n        if (closeButton_) MoveWindow(closeButton_, std::max(20, width - 52), std::max(70, static_cast<int>(HIWORD(lParam)) - 48), 32, 30, TRUE);"
Replace-Exact $Search $SizeNeedle $SizeReplacement

Replace-Exact $Search `
'        if (y > renderTarget_->GetSize().height - 20) break;' `
'        if (y > renderTarget_->GetSize().height - 56) break;'

$SearchText = Read-Utf8 $Search
$StartPattern = '(?s)void SearchWindow::StartL3\(const std::wstring& prompt\) \{.*?\n\}\n\nvoid SearchWindow::OpenModelSettings'
$StartReplacement = @'
void SearchWindow::StartL3(const std::wstring& prompt) {
    if (l3_.Busy()) {
        gL3Generation.fetch_add(1, std::memory_order_relaxed);
        l3_.Stop();
    }
    if (!ShowL3CliWindow(instance_, hwnd_, l3_, prompt)) {
        SetStatus(L"L3 CLI 启动失败", L"请重试或检查 AI 设置。");
    }
}

void SearchWindow::OpenModelSettings
'@
$UpdatedSearch = [regex]::Replace($SearchText, $StartPattern, $StartReplacement, 1)
if ($UpdatedSearch -eq $SearchText) { throw "Unable to replace SearchWindow::StartL3" }
Write-Utf8 $Search $UpdatedSearch

Replace-Exact $CMake `
'    src/ModelSettingsWindow.cpp' `
"    src/ModelSettingsWindow.cpp`n    src/L3CliWindow.cpp"

$EverythingNeedle = @'
    std::wstring commandLine = L"\"" + executable.wstring() + L"\" -startup";
'@
$EverythingReplacement = @'
    const auto configPath = executable.parent_path() / L"TuringDesk-Everything.ini";
    const auto config = configPath.wstring();
    WritePrivateProfileStringW(L"Everything", L"run_in_background", L"1", config.c_str());
    WritePrivateProfileStringW(L"Everything", L"show_tray_icon", L"0", config.c_str());
    WritePrivateProfileStringW(L"Everything", L"check_for_updates_on_startup", L"0", config.c_str());
    std::wstring commandLine = L"\"" + executable.wstring() + L"\" -config \"" + config + L"\" -startup";
'@
Replace-Exact $Everything $EverythingNeedle $EverythingReplacement

$StopNeedle = @'
function Stop-DeployedInstance {
    Step "Stopping previous TuringDesk instance"
'@
$StopReplacement = @'
function Stop-DeployedInstance {
    Step "Stopping previous TuringDesk instance"

    $BundledEverythingExe = Join-Path $DeployDir "Everything\Everything.exe"
    Get-Process -Name "Everything" -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Path -and ([System.IO.Path]::GetFullPath($_.Path) -eq [System.IO.Path]::GetFullPath($BundledEverythingExe))) {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
    Start-Sleep -Milliseconds 150
'@
Replace-Exact $Deploy $StopNeedle $StopReplacement

$DeployBlockPattern = '(?s)function Deploy-BundledEverything\(\[string\]\$ArtifactRoot\) \{.*?\n\}\n\nfunction Ensure-Everything'
$DeployBlockReplacement = @'
function Deploy-BundledEverything([string]$ArtifactRoot) {
    $SourceDir = Join-Path $ArtifactRoot "Everything"
    $DestinationDir = Join-Path $DeployDir "Everything"
    $DestinationExe = Join-Path $DestinationDir "Everything.exe"

    if (-not (Test-Path $SourceDir)) {
        Write-Host "WARNING: ARM64 artifact does not contain bundled Everything." -ForegroundColor Yellow
        return $null
    }

    Step "Installing bundled Everything files"
    New-Item -ItemType Directory -Force -Path $DestinationDir | Out-Null
    Copy-Item (Join-Path $SourceDir "*") $DestinationDir -Recurse -Force
    return $DestinationExe
}

function Ensure-Everything
'@
$DeployText = Read-Utf8 $Deploy
$UpdatedDeploy = [regex]::Replace($DeployText, $DeployBlockPattern, $DeployBlockReplacement, 1)
if ($UpdatedDeploy -eq $DeployText) { throw "Unable to replace Deploy-BundledEverything" }
Write-Utf8 $Deploy $UpdatedDeploy

$EnsurePattern = '(?s)    try \{\n        # Suppress Everything.*?\n    Start-Sleep -Milliseconds 800'
$EnsureReplacement = @'
    $EverythingConfig = Join-Path (Split-Path $EverythingExe -Parent) "TuringDesk-Everything.ini"
    @"
[Everything]
run_in_background=1
show_tray_icon=0
check_for_updates_on_startup=0
"@ | Set-Content -Path $EverythingConfig -Encoding UTF8

    Step "Starting bundled Everything for L2 (hidden)"
    Start-Process -FilePath $EverythingExe -ArgumentList @("-config", $EverythingConfig, "-startup") -WindowStyle Hidden | Out-Null
    Start-Sleep -Milliseconds 800
'@
$DeployText = Read-Utf8 $Deploy
$UpdatedDeploy = [regex]::Replace($DeployText, $EnsurePattern, $EnsureReplacement, 1)
if ($UpdatedDeploy -eq $DeployText) { throw "Unable to replace Ensure-Everything startup" }
Write-Utf8 $Deploy $UpdatedDeploy

Write-Host "Native UI/L3/Everything patch applied."

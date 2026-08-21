$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ShellHost = Join-Path $Root "shellhost\TuringDesk.ShellHost.exe"

if (-not (Test-Path $ShellHost)) {
    throw "TuringDesk ShellHost is missing: $ShellHost"
}

Start-Process -FilePath $ShellHost -ArgumentList "--preview" -WorkingDirectory $Root

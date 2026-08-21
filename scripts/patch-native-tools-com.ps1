$ErrorActionPreference = "Stop"
$Path = "src/native/src/NativeTools.cpp"
$Text = [System.IO.File]::ReadAllText((Resolve-Path $Path), [System.Text.Encoding]::UTF8)
$Needle = "if (uninitialize) CoUninitialize();"
$Count = ([regex]::Matches($Text, [regex]::Escape($Needle))).Count
if ($Count -ne 4) { throw "Expected 4 stale COM cleanup lines, found $Count" }
$Lines = $Text -split "`n"
$Lines = @($Lines | Where-Object { -not $_.Contains($Needle) })
$Updated = [string]::Join("`n", $Lines)
[System.IO.File]::WriteAllText((Resolve-Path $Path), $Updated, [System.Text.UTF8Encoding]::new($false))

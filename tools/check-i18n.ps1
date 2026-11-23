# Checks that every translatable UI text has a translation in each assets/i18n/*.json file.
# Exits with code 1 when something is missing, so CI fails instead of shipping untranslated text.
#
#   pwsh tools/check-i18n.ps1
param(
    [string]$Src = (Join-Path $PSScriptRoot '..\src'),
    [string]$I18n = (Join-Path $PSScriptRoot '..\assets\i18n')
)

$keys = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
function Add-Key([string]$value) { [void]$keys.Add(($value -replace '\\"', '"' -replace '\\n', "`n")) }

foreach ($f in Get-ChildItem $Src -Recurse -Filter *.cs) {
    $t = [IO.File]::ReadAllText($f.FullName)
    # literal keys: L.T("...") and L.F("...")
    foreach ($m in [regex]::Matches($t, 'L\.[TF]\(\s*"((?:[^"\\]|\\.)*)"')) { Add-Key $m.Groups[1].Value }
    # L.F(condition ? "a" : "b", ...)
    foreach ($m in [regex]::Matches($t, 'L\.F\([^"]*\?\s*"((?:[^"\\]|\\.)*)"\s*:\s*"((?:[^"\\]|\\.)*)"')) { Add-Key $m.Groups[1].Value; Add-Key $m.Groups[2].Value }
}

# texts that are translated through variables
$bg = [IO.File]::ReadAllText((Join-Path $Src 'BackgroundServices.cs'))
foreach ($m in [regex]::Matches($bg, '(?:Title|Description|OnText|OffText) = "((?:[^"\\]|\\.)*)"')) { Add-Key $m.Groups[1].Value }
foreach ($m in [regex]::Matches($bg, 'const string G\w+ = "([^"]+)"')) { Add-Key $m.Groups[1].Value }
$usb = [IO.File]::ReadAllText((Join-Path $Src 'UsbTopology.cs'))
foreach ($m in [regex]::Matches($usb, 'ControllerKind\.\w+ => "([^"]+)"|_ => "([^"]+)",')) {
    Add-Key $(if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value })
}
foreach ($k in 'mouse', 'keyboard') { Add-Key $k }
$startup = [IO.File]::ReadAllText((Join-Path $Src 'StartupApps.cs'))
foreach ($m in [regex]::Matches($startup, '"((?:Current user|All users)[^"]*)"')) { Add-Key $m.Groups[1].Value }
$hotkeys = [IO.File]::ReadAllText((Join-Path $Src 'Hotkeys.cs'))
foreach ($m in [regex]::Matches($hotkeys, '"Ctrl\+Alt\+\w", "([^"]+)"')) { Add-Key $m.Groups[1].Value }
$main = [IO.File]::ReadAllText((Join-Path $Src 'Ui/MainForm.cs'))
foreach ($m in [regex]::Matches($main, '\(Page\.\w+, "([^"]+)"\)')) { Add-Key $m.Groups[1].Value }

Write-Host "Translatable texts: $($keys.Count)"
$failed = $false
foreach ($json in Get-ChildItem $I18n -Filter *.json) {
    # read keys line by line: ConvertFrom-Json in Windows PowerShell treats "Device" and "device" as duplicates
    $have = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($json.FullName, [Text.Encoding]::UTF8)) {
        $m = [regex]::Match($line, '^\s*"((?:[^"\\]|\\.)*)"\s*:')
        if ($m.Success) { [void]$have.Add(($m.Groups[1].Value -replace '\\"', '"')) }
    }
    $missing = @($keys | Where-Object { -not $have.Contains($_) })
    $unused = @($have | Where-Object { -not $keys.Contains($_) })
    Write-Host "$($json.Name): $($missing.Count) missing, $($unused.Count) unused"
    $missing | ForEach-Object { Write-Host "  missing: $_" }
    $unused | ForEach-Object { Write-Host "  unused:  $_" }
    if ($missing.Count -gt 0) { $failed = $true }
}
if ($failed) { exit 1 }

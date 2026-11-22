# Builds StaScan into dist\ and creates dist\StaScan.lnk.
# There is no StaScan.exe on purpose: the app runs via Microsoft-signed dotnet.exe,
# because Smart App Control may block an unsigned exe.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path dist) { Remove-Item dist -Recurse -Force }

dotnet publish StaScan.csproj -c Release -p:DebugType=none -o dist
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }

$dotnet = (Get-Command dotnet).Source
$dll = Join-Path $PSScriptRoot 'dist\StaScan.dll'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $PSScriptRoot 'dist\StaScan.lnk'))
$lnk.TargetPath = $dotnet
$lnk.Arguments = "`"$dll`""
$lnk.WorkingDirectory = Join-Path $PSScriptRoot 'dist'
$lnk.WindowStyle = 7   # minimized: no console flash
$lnk.Description = 'StaScan'
$lnk.Save()

Write-Host "Done: $PSScriptRoot\dist\StaScan.lnk"

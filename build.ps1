# Builds dist\StaScan.exe (self-contained single file, no .NET install needed)
# and, if Inno Setup 6 is installed, the installer dist\StaScan-Setup-<version>.exe.
#
#   .\build.ps1                  version from StaScan.csproj
#   .\build.ps1 -Version 1.2.0   override (used by the release workflow)
param([string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content StaScan.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Building StaScan $Version"

if (Test-Path dist) { Remove-Item dist -Recurse -Force }

dotnet publish StaScan.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -p:Version=$Version -o dist
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    & $iscc /Q "/DAppVersion=$Version" installer\StaScan.iss
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit code $LASTEXITCODE)" }
    Write-Host "Done: $PSScriptRoot\dist\StaScan.exe and $PSScriptRoot\dist\StaScan-Setup-$Version.exe"
} else {
    Write-Host "Done: $PSScriptRoot\dist\StaScan.exe (Inno Setup 6 not found, installer skipped)"
}

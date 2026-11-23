# Generates winget manifests for a published StaScan release.
#
#   pwsh tools/make-winget-manifest.ps1 -Version 1.2.0
#
# Reads the installer URL and SHA-256 from the GitHub release, writes the three manifest files to
# packaging/winget/manifests/t/turq-co/StaScan/<version>/, and validates them with "winget validate"
# when winget is available. Submit them to https://github.com/microsoft/winget-pkgs with
# "wingetcreate submit <folder>" or a pull request.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repo = 'turq-co/stascan'
)
$ErrorActionPreference = 'Stop'

$release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/tags/v$Version" -Headers @{ 'User-Agent' = 'StaScan-packaging' }
$asset = $release.assets | Where-Object { $_.name -eq "StaScan-Setup-$Version.exe" }
if (-not $asset) { throw "Release v$Version has no StaScan-Setup-$Version.exe" }
$sha = if ($asset.digest -like 'sha256:*') { $asset.digest.Substring(7).ToUpperInvariant() } else {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $tmp
    (Get-FileHash $tmp -Algorithm SHA256).Hash
}

$id = 'turq-co.StaScan'
$dir = Join-Path $PSScriptRoot "..\packaging\winget\manifests\t\turq-co\StaScan\$Version"
New-Item -ItemType Directory -Force $dir | Out-Null
$schema = '1.6.0'

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | Set-Content (Join-Path $dir "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: machine
ElevationRequirement: elevationRequired
UpgradeBehavior: install
Installers:
- Architecture: x64
  InstallerUrl: $($asset.browser_download_url)
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@ | Set-Content (Join-Path $dir "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: turq-co
PublisherUrl: https://github.com/turq-co
PackageName: StaScan
PackageUrl: https://github.com/$Repo
License: GPL-3.0
LicenseUrl: https://github.com/$Repo/blob/main/LICENSE
ShortDescription: Get your Windows PC back to a clean, game-ready state without rebooting.
Description: |-
  StaScan is a system tray utility that closes programs started since sign-in, frees RAM (including the standby list),
  pauses Windows background services for a gaming session, finds leaking drivers, checks mouse and keyboard USB ports
  and measures FPS with PresentMon.
Tags:
- gaming
- fps
- optimization
- ram
- system-tray
ReleaseNotesUrl: $($release.html_url)
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | Set-Content (Join-Path $dir "$id.locale.en-US.yaml") -Encoding utf8

Write-Host "Manifests written to $((Resolve-Path $dir).Path)"
if (Get-Command winget -ErrorAction SilentlyContinue) { winget validate --manifest $dir }

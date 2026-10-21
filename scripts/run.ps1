param([switch]$NoBuild,[ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
if (-not $NoBuild) { & "$PSScriptRoot/build.ps1" -Configuration $Configuration }
$root=Split-Path $PSScriptRoot -Parent
Start-Process -FilePath (Join-Path $root "apps/Desktop/bin/$Configuration/net10.0/Nexolume.exe") -WorkingDirectory $root

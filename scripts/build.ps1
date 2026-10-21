param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 $rustArgs=@('build','--locked','--manifest-path','src/rust/Cargo.toml')
 if ($Configuration -eq 'Release') { $rustArgs += '--release' }
 cargo @rustArgs
 if ($LASTEXITCODE -ne 0) { throw 'Rust build failed' }
 dotnet build Nexolume.sln -c $Configuration
 if ($LASTEXITCODE -ne 0) { throw '.NET build failed' }
} finally { Pop-Location }

param([switch]$NoBuild,[ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 if (-not $NoBuild) { & "$PSScriptRoot/build.ps1" -Configuration $Configuration }
 cargo test --locked --manifest-path src/rust/Cargo.toml
 if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed' }
 & "tests/Integration/bin/$Configuration/net10.0/Integration.exe"
 if ($LASTEXITCODE -ne 0) { throw 'Native integration tests failed' }
 & "tests/DotNet/bin/$Configuration/net10.0/UiTests.exe"
 if ($LASTEXITCODE -ne 0) { throw 'Avalonia UI tests failed' }
 $report=Join-Path $root 'artifacts/validation/windows-native.txt'
 $process=Start-Process -FilePath "apps/Desktop/bin/$Configuration/net10.0/Nexolume.exe" -ArgumentList "--smoke-test=`"$report`"" -WindowStyle Hidden -PassThru
 if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Native startup test timed out' }
 if ($process.ExitCode -ne 0) { throw "Native startup failed: $report" }
 Get-Content -LiteralPath $report
} finally { Pop-Location }

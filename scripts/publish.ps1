$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 & "$PSScriptRoot/build.ps1" -Configuration Release
 & "$PSScriptRoot/test.ps1" -NoBuild -Configuration Release
 dotnet publish apps/Desktop/Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/Nexolume-win-x64 -p:PublishReadyToRun=true
 if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
 Copy-Item README.md artifacts/Nexolume-win-x64/README.md
 New-Item -ItemType Directory -Force artifacts/Nexolume-win-x64/docs | Out-Null
 Copy-Item docs/* artifacts/Nexolume-win-x64/docs -Recurse -Force
 $report=Join-Path $root 'artifacts/validation/portable-native.txt'
 $portable=Start-Process -FilePath (Join-Path $root 'artifacts/Nexolume-win-x64/Nexolume.exe') -ArgumentList "--smoke-test=`"$report`"" -WindowStyle Hidden -PassThru
 if (-not $portable.WaitForExit(30000)) { $portable.Kill(); throw 'Portable app timed out' }
 if ($portable.ExitCode -ne 0) { throw "Portable app failed: $report" }
 Compress-Archive -Path artifacts/Nexolume-win-x64/* -DestinationPath artifacts/Nexolume-win-x64.zip -Force
} finally { Pop-Location }

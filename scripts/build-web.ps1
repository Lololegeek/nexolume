<#
  Builds the browser version of Nexolume as a static site (no backend).

    scripts/build-web.ps1                      Release site in artifacts/web
    scripts/build-web.ps1 -Configuration Debug Faster unoptimised build (same layout)
    scripts/build-web.ps1 -EngineOnly          Only rebuilds the Rust engine into apps/Browser/wwwroot/engine
    scripts/build-web.ps1 -Serve               Builds, then serves artifacts/web on http://localhost:8080

  Steps: cargo (office-wasm, wasm32-unknown-unknown) -> wasm-bindgen (--target web) -> optional
  wasm-opt -> dotnet publish apps/Browser -> precache.json (offline cache list, SHA-256 per file)
  -> hosting files (Vercel, GitHub Pages, nginx are documented in docs/WEB.md).
#>
param(
  [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
  [string]$Output = 'artifacts/web',
  [switch]$EngineOnly,
  [switch]$Serve,
  [int]$Port = 8080
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
  $bindgenVersion = '0.2.130'
  $installed = (& wasm-bindgen --version 2>$null)
  if ($LASTEXITCODE -ne 0 -or "$installed" -notmatch [regex]::Escape($bindgenVersion)) {
    throw "wasm-bindgen-cli $bindgenVersion est requis : cargo install wasm-bindgen-cli --version $bindgenVersion --locked"
  }
  if (-not ((& rustup target list --installed) -contains 'wasm32-unknown-unknown')) {
    throw 'Cible Rust absente : rustup target add wasm32-unknown-unknown'
  }

  $cargoProfile = if ($Configuration -eq 'Release') { 'wasm-release' } else { 'dev' }
  $cargoDir = if ($Configuration -eq 'Release') { 'wasm-release' } else { 'debug' }
  Write-Host "[1/5] Moteur Rust (office-wasm, profil $cargoProfile)"
  cargo build --locked --manifest-path src/rust/Cargo.toml -p office-wasm --target wasm32-unknown-unknown --profile $cargoProfile
  if ($LASTEXITCODE -ne 0) { throw 'cargo build office-wasm failed' }

  Write-Host '[2/5] wasm-bindgen'
  $bindgenOut = 'src/rust/target/web-bindgen'
  wasm-bindgen "src/rust/target/wasm32-unknown-unknown/$cargoDir/office_wasm.wasm" --target web --no-typescript --out-dir $bindgenOut
  if ($LASTEXITCODE -ne 0) { throw 'wasm-bindgen failed' }
  $wasmOpt = Get-Command wasm-opt -ErrorAction SilentlyContinue
  if (-not $wasmOpt) {
    # The .NET wasm-tools workload ships binaryen with Emscripten; reuse it when no wasm-opt is on PATH.
    $bundled = Get-ChildItem "$env:ProgramFiles/dotnet/packs/Microsoft.NET.Runtime.Emscripten.*.Sdk.*/*/tools/bin/wasm-opt.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if ($bundled) { $wasmOpt = Get-Command $bundled.FullName }
  }
  if ($wasmOpt -and $Configuration -eq 'Release') {
    & $wasmOpt.Source -Oz --enable-bulk-memory --enable-nontrapping-float-to-int --enable-sign-ext "$bindgenOut/office_wasm_bg.wasm" -o "$bindgenOut/office_wasm_bg.wasm"
    if ($LASTEXITCODE -ne 0) { throw 'wasm-opt failed' }
  } elseif ($Configuration -eq 'Release') {
    Write-Host '      wasm-opt (binaryen) absent : moteur non post-optimisé (fonctionnel, un peu plus lourd).'
  }
  $engineDir = 'apps/Browser/wwwroot/engine'
  Copy-Item "$bindgenOut/office_wasm.js", "$bindgenOut/office_wasm_bg.wasm" $engineDir -Force
  if ($EngineOnly) { return }

  Write-Host "[3/5] dotnet publish apps/Browser ($Configuration)"
  $publish = "artifacts/web-publish-$Configuration"
  if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
  dotnet publish apps/Browser/Browser.csproj -c $Configuration -o $publish -nologo
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish apps/Browser failed' }

  Write-Host "[4/5] Site statique -> $Output"
  if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
  Copy-Item "$publish/wwwroot" $Output -Recurse
  # Hosting helpers: GitHub Pages must not run Jekyll (it hides _framework); 404.html serves the
  # app for deep links. Vercel and nginx configs are copied for reference/deployment.
  New-Item -ItemType File -Force (Join-Path $Output '.nojekyll') | Out-Null
  Copy-Item (Join-Path $Output 'index.html') (Join-Path $Output '404.html')
  Copy-Item deploy/vercel.json (Join-Path $Output 'vercel.json')
  Copy-Item deploy/_headers (Join-Path $Output '_headers')
  # Precompressed copies (.br/.gz) produced by the SDK are kept for servers that use them.

  Write-Host '[5/5] Cache hors ligne (precache.json)'
  $full = (Resolve-Path $Output).Path
  $skip = @('precache.json', 'sw.js', '404.html', 'vercel.json', '_headers', '.nojekyll')
  $files = Get-ChildItem $full -Recurse -File | Where-Object {
    $skip -notcontains $_.Name -and $_.Extension -notin @('.br', '.gz')
  } | Sort-Object FullName
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $entries = @(); $all = New-Object System.Text.StringBuilder
  foreach ($file in $files) {
    $relative = $file.FullName.Substring($full.Length + 1).Replace('\', '/')
    $hash = $sha.ComputeHash([System.IO.File]::ReadAllBytes($file.FullName))
    $entries += [ordered]@{ url = "./$relative"; integrity = 'sha256-' + [Convert]::ToBase64String($hash) }
    [void]$all.Append($relative).Append([Convert]::ToBase64String($hash))
  }
  $version = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($all.ToString()))) -replace '-', '').Substring(0, 16).ToLowerInvariant()
  $manifest = [ordered]@{ version = $version; files = $entries } | ConvertTo-Json -Depth 4 -Compress
  [System.IO.File]::WriteAllText((Join-Path $full 'precache.json'), $manifest, (New-Object System.Text.UTF8Encoding $false))
  $size = ($files | Measure-Object Length -Sum).Sum / 1MB
  Write-Host ("      {0} fichiers, {1:N1} Mo non compressés, version {2}" -f $files.Count, $size, $version)

  if ($Serve) {
    Write-Host "Serveur local : http://localhost:$Port/ (Ctrl+C pour arrêter)"
    node scripts/serve-web.mjs $Output $Port
  }
} finally { Pop-Location }

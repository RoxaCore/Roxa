$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
$pub = Join-Path $root "installer\publish"
$out = Join-Path $root "installer\output"

# Only our own processes. Killing every process named "xray" on the machine also
# takes down an unrelated core such as v2rayN's, which must stay untouched, so the
# engine is identified by its executable path being inside this repository instead.
Stop-Process -Name "Roxa","VBCSCompiler" -Force -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name = 'xray.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

Push-Location $root
try {
    & "C:\Program Files\dotnet\dotnet.exe" publish Roxa.csproj -c Release -p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}
finally {
    Pop-Location
}

Get-ChildItem $pub -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item (Join-Path $pub "logs") -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path (Join-Path $pub "engine\geoip.dat")) { throw "geoip.dat must not be published" }
if (Test-Path (Join-Path $pub "engine\geosite.dat")) { throw "geosite.dat must not be published" }
if (-not (Test-Path (Join-Path $pub "Roxa.exe"))) { throw "publish output incomplete" }

# The engine now lives in Program Files, which the app cannot write to. Without a
# bundled sidecar, VerifyEngineIntegrity would have to CREATE xray.exe.sha256, fail
# on the read-only folder, report a corrupt engine and refuse to connect at all. The
# digest is produced here so it always matches the exact binary being shipped.
$engineExe = Join-Path $pub "engine\xray.exe"
if (-not (Test-Path $engineExe)) { throw "engine\xray.exe missing from publish output" }
$engineHash = (Get-FileHash -Algorithm SHA256 -Path $engineExe).Hash.ToLowerInvariant()
Set-Content -Path "$engineExe.sha256" -Value $engineHash -Encoding ascii -NoNewline
"engine_sha256 = $engineHash"

if (-not (Test-Path $iscc)) { throw "ISCC not found at $iscc" }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $iscc (Join-Path $root "installer\setup.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Get-ChildItem $out -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$pubSize = (Get-ChildItem $pub -Recurse -File | Measure-Object Length -Sum).Sum
$setupSize = $setup.Length
"publish_MB = " + [math]::Round($pubSize / 1MB, 1)
"setup_MB   = " + [math]::Round($setupSize / 1MB, 1)
"setup_path = " + $setup.FullName

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
$pub = Join-Path $root "installer\publish"
$out = Join-Path $root "installer\output"

Stop-Process -Name "XrayClient","VBCSCompiler","xray" -Force -ErrorAction SilentlyContinue

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

Push-Location $root
try {
    & "C:\Program Files\dotnet\dotnet.exe" publish XrayClient.csproj -c Release -p:PublishProfile=win-x64
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}
finally {
    Pop-Location
}

Get-ChildItem $pub -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item (Join-Path $pub "logs") -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path (Join-Path $pub "engine\geoip.dat")) { throw "geoip.dat must not be published" }
if (Test-Path (Join-Path $pub "engine\geosite.dat")) { throw "geosite.dat must not be published" }
if (-not (Test-Path (Join-Path $pub "XrayClient.exe"))) { throw "publish output incomplete" }

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

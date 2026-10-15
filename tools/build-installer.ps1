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

# The .NET Desktop Runtime ships inside the setup so installing needs no network.
#
# This used to be a download performed by the setup itself via Inno's
# DownloadTemporaryFile: synchronous, with no timeout, no progress and no
# Cancel, and over WinHTTP, which does not use the per-user proxy a browser
# uses. On a slow, filtered or proxied network setup sat on "Preparing to
# Install" forever with no way out, so a PC that had never seen .NET could not
# be installed at all. The file is now cached in installer\runtime (gitignored),
# checked against the SHA-256 pinned in setup.iss, and packed into the setup.
# A mismatch or an unreachable mirror fails the build instead of shipping a
# setup that hangs.
$issPath = Join-Path $root "installer\setup.iss"
$issText = [IO.File]::ReadAllText($issPath)
$dotNetFile = [regex]::Match($issText, '#define\s+DotNetFile\s+"([^"]+)"').Groups[1].Value
$dotNetUrl  = [regex]::Match($issText, '#define\s+DotNetUrl\s+"([^"]+)"').Groups[1].Value
$dotNetSha  = [regex]::Match($issText, '#define\s+DotNetSha256\s+"([^"]+)"').Groups[1].Value
if (-not $dotNetFile -or -not $dotNetUrl -or -not $dotNetSha) {
    throw "setup.iss must define DotNetFile, DotNetUrl and DotNetSha256"
}

$rtDir = Join-Path $root "installer\runtime"
$rtPath = Join-Path $rtDir $dotNetFile
if (-not (Test-Path $rtPath)) {
    New-Item -ItemType Directory -Force -Path $rtDir | Out-Null
    "runtime: not cached; downloading $dotNetFile"
    $downloaded = $false
    try {
        # Invoke-WebRequest honors the per-user (WinINET) proxy, the same one a
        # browser uses, which is what makes this work behind a proxy setup.
        Invoke-WebRequest -Uri $dotNetUrl -OutFile $rtPath -UseBasicParsing -TimeoutSec 900
        $downloaded = $true
    } catch {
        "runtime: WinINET download failed ($($_.Exception.Message)); retrying with curl.exe"
        if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
            & curl.exe -sS -L --max-time 900 -o $rtPath $dotNetUrl
            $downloaded = ($LASTEXITCODE -eq 0)
        }
    }
    if (-not $downloaded) {
        Remove-Item $rtPath -Force -ErrorAction SilentlyContinue
        throw "could not download the .NET runtime from $dotNetUrl"
    }
}
$rtHash = (Get-FileHash -Algorithm SHA256 -Path $rtPath).Hash.ToLowerInvariant()
if ($rtHash -ne $dotNetSha.ToLowerInvariant()) {
    Remove-Item $rtPath -Force -ErrorAction SilentlyContinue
    throw "runtime SHA-256 mismatch: setup.iss pins $dotNetSha but the file is $rtHash"
}
"runtime_sha256 = $rtHash"

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

# Roxa

**A VPN client for Windows, built on Xray-core.**

Roxa is a desktop GUI around Xray-core. It supports system-proxy mode and a
full TUN mode with a kill-switch, per-application bypass, live delay
measurement, and update notifications. The interface is available in 14
languages.

---

## Requirements

| | |
|---|---|
| **Operating system** | Windows 10 version 1607 (build 14393) or newer, including Windows 11 |
| **Architecture** | x64 |
| **.NET** | .NET Desktop Runtime 8 — **installed automatically by setup** |
| **Administrator** | Once for installation, and again for TUN mode |

Windows 7, 8 and 8.1 are not supported: .NET 8 does not run on them, so
neither the runtime nor Roxa can.

---

## Installation

1. Download one of two installers:

   | File | Size | Needs internet |
   |---|---|---|
   | `Roxa-Setup-<version>.exe` | 75 MiB | **No** — the .NET Desktop Runtime is packed inside |
   | `Roxa-Setup-<version>-online.exe` | 19 MiB | Only if .NET is missing |

   Pick the offline one on a slow, filtered or metered connection. Both
   install exactly the same program.

2. Run it. Setup asks for administrator rights once and installs
   **per-machine** into `C:\Program Files\Roxa`, which a normal account
   cannot write to.

3. If the .NET Desktop Runtime is missing, the offline installer installs
   it silently. The online installer downloads it from Microsoft while you
   watch — live megabyte counter, working **Cancel**, a 10-minute deadline
   and an abort after 90 seconds without progress — and verifies it against
   a SHA-256 pinned inside the installer before running it. If anything goes
   wrong, setup opens Microsoft's download page and tells you the reason.

4. Launch Roxa and add a server, or paste a subscription link.

To uninstall, use *Apps & features*. Uninstall runs a cleanup pass first, so
the TUN adapter, its routes, the firewall rules and the system proxy are all
restored before the files are removed. The install folder and
`%ProgramData%\Roxa` go with it; your servers and settings in
`%APPDATA%\Roxa` are left in place.

---

## Windows SmartScreen warning

**The installer and `Roxa.exe` are currently not code-signed.** Windows will
therefore show:

> *Windows protected your PC*
> *Microsoft Defender SmartScreen prevented an unrecognized app from starting.*

To run it anyway:

1. Click **More info**.
2. Click **Run anyway**.

This warning appears because the application has no Authenticode certificate,
not because anything is wrong with it. The SHA-256 of every published build
is posted alongside the download so you can verify the file yourself:

```powershell
Get-FileHash .\Roxa-Setup-<version>.exe -Algorithm SHA256
```

---

## Reporting a problem

- **In-app:** use the bug report entry in Settings. If you have enabled
  *Send crash reports*, a report containing the exception and the last 4000
  characters of the log is queued and uploaded. This is **off by default**
  and only ever sends what you see in the report preview.
- **Manually:** the logs are written to the `logs` folder next to `Roxa.exe`
  (`tun.log`, `engine.log`, `parser.log`). Attach the relevant one when you
  report a problem.

When reporting a connection problem, include the operating system version and
whether you were in TUN mode or system-proxy mode.

---

## What Roxa sends

Roxa has no telemetry, no analytics and no crash reporting by default.
Connections it makes are listed in full in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) and summarised here:

| Destination | Purpose |
|---|---|
| `up.roxacore.com` | Version check, announcements, opt-in crash reports |
| `get.geojs.io`, `ipwho.is` | Country detection, used to target announcements |
| `api.github.com`, `gh-proxy.com` | Xray-core update check and download |
| `wintun.net` | TUN driver download |
| `cloudflare.com`, `gstatic.com`, `google.com` | Delay and speed measurement, through your tunnel |
| Your own subscription URL | Fetching the server list you added |

Setup contacts exactly one host, and only in one case: if you chose the
`-online` installer *and* the .NET Desktop Runtime is missing, it fetches it
from `builds.dotnet.microsoft.com`, which is where Microsoft publishes it.
The file is checked against a SHA-256 pinned inside the installer before it
is run, and the fallback on any failure is Microsoft's own download page. The
offline installer contacts no one.

Your server list is encrypted with Windows DPAPI and never leaves your
machine except as part of an actual connection.

---

## Building from source

```powershell
dotnet build -c Release
tools\build-installer.ps1
```

The app is framework-dependent and publishes to `installer\publish`, which
`setup.iss` packages. `engine\xray.exe` and `engine\wintun.dll` are the only
bundled binaries. Building the installers additionally needs **Inno Setup 6**
(`ISCC.exe`) next to the .NET SDK.

---

## License

Roxa is free software under the **GNU General Public License, version 3**
(or, at your option, any later version). The full license text is in
[`LICENSE`](LICENSE). Every source file carries the corresponding notice.

### The name and logo are not covered

The GPL gives you the right to run, study, modify and redistribute the
*code*. It does not transfer the **"Roxa" name, the logo, or the visual
identity** of this project — those remain with the Roxa project and are not
licensed under the GPL.

In practice that means:

- You may fork this code, change it, and distribute your own build.
- You must keep the GPL notices intact and make your own source available.
- You must **not** call your build "Roxa", and must **not** reuse the Roxa
  icon or branding, unless you have written permission from the project.
- If you redistribute an unmodified build, you may keep calling it Roxa.

This is the trademark reservation permitted by section 7(e) of the GPL. It
protects users from mistaking an unofficial build for the real one; it does
not restrict what you do with the code.

### Third-party components

Third-party components keep their own licenses and are listed in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt): Xray-core is MPL-2.0,
Wintun carries the WireGuard Prebuilt Binaries License, both fonts are
SIL OFL-1.1, and every managed dependency is MIT. The GPL and these licenses
coexist because Roxa runs those components as **separate programs**, not by
linking them into a single work.

### Country flag images

`Assets/Flags/*.png` (90 files) entered this repository in a single commit
with no note of where they came from: no licence, no attribution, no upstream
URL. That gap is recorded here rather than glossed over.

- **Roxa does not claim these images.** They are not covered by the GPL-3.0
  that covers the code, and nothing here should be read as relicensing them.
- **Roxa does not claim they are free to redistribute.** An unrecorded
  licence is not a licence; treat them as unlicensed material of unknown
  provenance.
- They are rendered unmodified — no file is edited or recoloured.

If you hold rights in any of these images, write to `roxacore.admin@gmail.com`
and the file will be removed or replaced. The two-letter codes are ISO 3166-1
alpha-2 values, which carry no copyright; only the artwork is in question.

---

## Security and privacy

- [`SECURITY.md`](SECURITY.md) — how to report a vulnerability privately, and
  the response-time targets.
- [`PRIVACY.md`](PRIVACY.md) — exactly what Roxa stores on disk and every
  network destination it contacts.

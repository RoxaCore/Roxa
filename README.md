# Roxa

**A free VPN client for Windows: paste a VLESS, VMess, Trojan or Shadowsocks
link and connect in one click.**

![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)
![Platform: Windows x64](https://img.shields.io/badge/platform-Windows%2010%20%26%2011%20x64-0078D6)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![Version](https://img.shields.io/badge/version-3.0.14-brightgreen)

If you have a subscription link or a server address from a provider, Roxa is
what turns it into a working connection: it reads the link, keeps a list of
your servers, measures which one answers fastest and routes your traffic
through it — either as a **system proxy** for the apps you choose, or as a
**full TUN tunnel** with a kill-switch and per-application bypass, so
nothing leaks if the tunnel drops. It runs on Xray-core, an open-source
proxy engine, and it is free software under the GPL.

The interface ships in 14 languages — فارسی, English, العربية, 中文, Русский,
Türkçe, اردو, Español, Français, 한국어, Deutsch, 日本語, Português (BR) and
हिन्दी — and asks for one on first run.

**Contents:** [Features](#features) · [Requirements](#requirements) ·
[Installation](#installation) · [Updating](#updating) ·
[SmartScreen](#windows-smartscreen-warning) ·
[Reporting a problem](#reporting-a-problem) ·
[What Roxa sends](#what-roxa-sends) ·
[Building from source](#building-from-source) · [License](#license) ·
[Security and privacy](#security-and-privacy)

---

## Features

| | |
|---|---|
| **Smart connect** | One click: a fast TCP ping on every server, a real-delay test on the **top 20** candidates through the tunnel, then connect to the fastest responder. |
| **Find better** | While connected, checks **10** more servers per batch in the background and switches when one answers faster (15-minute pool). |
| **Protocols** | Share links in `vless://`, `vmess://`, `trojan://` and `ss://` (Shadowsocks), added one at a time or through a subscription URL. |
| **Two proxy modes** | System-proxy for everyday use, or full TUN mode with a kill-switch and per-application bypass. |
| **Subscriptions** | Add servers by hand or paste a subscription URL; lists refresh automatically and can be exported. |
| **Live measurements** | TCP ping plus real proxied delay with jitter and success rate per server, and a full speed test. |
| **In-app updates** | Background download and install on exit, a manual check window with progress / cancel / retry, and a 10-day question when auto-install is off. |
| **14 languages** | The list in the intro above, chosen on first run and changeable in Settings. |

The **20** and **10** in the table are fixed values compiled into the
program, not settings — they exist so one click stays fast on a large
server list.

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

1. Download one of two installers from the official download page —
   **[roxacore.com](https://roxacore.com/)**, where the *Download*
   button always offers the current version:

   | File | Size | Needs internet |
   |---|---|---|
   | `[Roxa-Setup-<version>.exe]([url](http://roxacore.com/dl/Roxa-Setup-3.0.14-offline.exe))` | 75.3 MiB | **No** - the .NET Desktop Runtime is packed inside |
   | `[Roxa-Setup-<version>-online.exe]([url](https://roxacore.com/dl/Roxa-Setup-3.0.14.exe))` | 19.6 MiB | Only if .NET is missing |

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

4. Launch Roxa and add a server by hand (`vless://`, `vmess://`, `trojan://`
   or `ss://`), or paste a subscription link.

To uninstall, use *Apps & features*. Uninstall runs a cleanup pass first, so
the TUN adapter, its routes, the firewall rules and the system proxy are all
restored before the files are removed. The install folder and
`%ProgramData%\Roxa` go with it; your servers and settings in
`%APPDATA%\Roxa` are left in place.

---

## Updating

Roxa updates itself — no browser, no manual zip:

- **Automatic (on by default):** a new version is downloaded in the
  background and installed when you exit the app. The update banner keeps an
  **Install now** button for installing immediately.
- **Manual:** Settings → *App Update* → the check button opens a small window
  that downloads on its own behind a percentage bar (Cancel while it runs,
  Retry after a failure) and then installs and restarts the app.
- **The question:** with the automatic install turned off, a yes/no window
  asks about a pending new version — once per version, and about the same
  version no more often than every 10 days.

Every download is verified against the SHA-256 that accompanies the release
before anything is installed; a mismatch aborts the update and the version
you had stays in place.

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

- **In-app:** use the bug report entry in Settings. A crash is also captured
  automatically: the exception and the last 4000 characters of the log are
  scrubbed (URLs, UUIDs and IP addresses), queued locally, and uploaded only
  when the install's crash-report setting allows it. That setting is **off
  by default** and is stored per install; anything queued but never sent is
  deleted after 30 days.
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
| `roxacore.com` | Version check, announcements, opt-in crash reports |
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

The long version of each point below lives in
[`LICENSE-NOTES.md`](LICENSE-NOTES.md).

### The name and logo are not covered

You may fork the code and ship your own build, but the **"Roxa" name, the
logo and the visual identity** are not licensed under the GPL: a modified
build must not call itself Roxa or reuse the icon without written
permission. That is the reservation section 7(e) of the GPL allows, and it
does not restrict what you do with the code.

### Third-party components

Xray-core (MPL-2.0), Wintun, both fonts (SIL OFL-1.1) and every managed
dependency (MIT) keep their own licenses, listed in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). They coexist with the
GPL because Roxa runs them as separate programs, not by linking them in.

### Country flag images

The 90 flags are Twemoji artwork under CC BY 4.0, cropped to one size and
otherwise untouched; they are not covered by the GPL and Roxa claims no
rights over them. Credits: [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt),
section 6.

---

## Security and privacy

- [`SECURITY.md`](SECURITY.md) — how to report a vulnerability privately, and
  the response-time targets.
- [`PRIVACY.md`](PRIVACY.md) — exactly what Roxa stores on disk and every
  network destination it contacts.

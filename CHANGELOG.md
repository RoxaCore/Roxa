# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Export and import of the subscription list, as buttons on the Subscriptions
  page. This exists because DPAPI binds `subscriptions.json` to one Windows
  account: moving to another machine, or reinstalling Windows, used to mean
  re-entering every link by hand. The exported file is plain JSON on purpose —
  it must not be tied to an account — and the page says so, because it
  contains the links themselves. Import never removes or replaces anything:
  an identical URL is skipped and a name collision gets a distinct name.

### Security

- The engine download is now fail-closed end to end. An Xray-core release
  asset whose digest GitHub did not publish is refused before any request is
  made, rather than being installed unverified when the direct download
  happened to succeed. The digest gate runs first, so the gh-proxy mirror is
  only reached once the expected hash is known, and its response is checked
  like any other.
- The two cleartext test endpoints moved to TLS: the cachefly speed-test
  fallback and the gstatic `generate_204` probe used for real-delay
  measurement.
- `subscriptions.json` is encrypted with Windows DPAPI (current user), like
  `servers.json`. A file written by an older version is re-encrypted the first
  time it is read. If DPAPI is unavailable the list is written in plain text
  rather than being dropped, and is re-encrypted on the next successful save.
- A queued crash report is now deleted once it is 30 days old. The cap of 30
  reports was never matched by a deadline, so a report the user never
  consented to send stayed on disk indefinitely; the sweep runs at every
  start, with or without consent, because it deletes rather than uploads.
  Turning *Send crash reports* off now empties the queue immediately instead
  of leaving already-collected reports waiting for a consent that was just
  declined.
- What a crash report collects automatically is scrubbed before it leaves the
  machine, and before it is written to the pending queue: a URL carrying a
  path or query, a UUID, and an IPv4 address become `<url>`, `<uuid>` and
  `<ip>`. Loopback addresses are kept. The text a user types into a manual
  report is not altered.
- Opening an announcement link requires https. The server already refuses to
  store anything else, but the client accepted http as well and handed the
  value straight to the shell, which launches whatever scheme it is given.
- The source hash the server keeps as a label now has a deadline of its own:
  90 days. A report loses `src` 90 days after it was filed, and an install
  identifier's row loses `ip`/`ip_last` 90 days after that install last
  checked in. The report and the row stay; only the label goes. Previously
  these hashes were kept as long as the record did, which PRIVACY.md stated
  plainly but which no passage of time ever shortened.
- The login delay under global pressure is capped at 2 seconds, and past 400
  failed attempts in an hour the panel answers 429 instead of waiting at all.
  The previous ladder topped out at 20 seconds of `sleep` per request, and each
  one holds a PHP worker for that long, so a few hundred parallel guesses could
  have taken the whole panel down — the exact outage a global lockout was
  meant to prevent. The per-IP lockout of 20 failures an hour is unchanged.
- When DPAPI is unavailable and `subscriptions.json` has to be written as plain
  text, the user is told on screen instead of only in the log. Losing the
  encryption silently is the worst outcome here: the fallback already favours
  keeping the links over keeping them encrypted, and that trade should never
  be made where nobody can see it.

## [3.0.8] - 2026-10-15

### Added

- Public-release documentation: a full `README.md`, `SECURITY.md` with a
  private vulnerability-reporting channel and response-time targets,
  `PRIVACY.md` listing every file Roxa writes and every network destination
  it contacts, and `THIRD-PARTY-NOTICES.txt` with the complete MIT text and
  copyright line of each bundled dependency.
- The SIL OFL-1.1 texts for the two bundled fonts, shipped next to them in
  `Assets/Fonts`.
- The version number is shown at the bottom of the Settings page, read from
  the assembly version so it can never drift from the build.
- The installer refuses Windows versions older than 10 version 1607
  (build 14393) and Windows Server Core, with an explanation instead of a
  misleading runtime error.
- Crash and bug reports are opt-in. A fresh install sends nothing until the
  user explicitly enables reporting in Settings.

### Changed

- Licensed under the **GNU General Public License v3.0-or-later**. The full
  text is in `LICENSE`, every source file now carries the corresponding
  notice, and the README states that the license does not cover the Roxa
  name or visual identity.
- The four top-level tabs are restyled as segmented pills, with a keyboard
  focus ring and an inactive-contrast ratio raised from 2.75:1 to 4.66:1.
- Every string shown by the installer is English; the emoji was dropped from
  the Settings tab label.
- Twelve additional languages are translated. Untranslated keys fall back to
  English rather than showing a raw key.
- Layout is script-aware for right-to-left languages, with per-codepoint
  font fallback so Persian and Arabic never render as boxes.
- Automatic updating stops at a notification. The banner button opens the
  release page over https instead of downloading a payload, the
  *Auto-update app* checkbox is gone from Settings, and `--update-apply`
  is refused outright.
- Installation is per-machine in `C:\Program Files\Roxa` and takes one
  administrator prompt; setup no longer installs per-user.
- The build script stops only Roxa's own processes. It used to kill every
  `xray.exe` on the machine, which also took down an unrelated core such
  as v2rayN's.

### Fixed

- The installer did not compile at all: an `#` preprocessor directive inside
  `[Code]` and the misspelled `skipifdoesntexist` flag both broke ISCC.
- The kill-switch notified on intentional disconnects and the network
  watcher reacted to its own reconnects.
- The wintun download URL was dead; the correct per-architecture DLL is now
  picked.
- Manual DNS servers were swept away by the firewall cleanup.
- Parser diagnostics went to `<app>\logs`, which a normal account cannot
  create in Program Files, so a failed parse was lost twice over: the
  exception was already caught and the one file that could explain it was
  never written. They now use the tunnel log's directory, which falls back
  to `%LOCALAPPDATA%\Roxa\logs`.
- Exclusion routes (`/32`) survived a crash and stayed in the routing table
  forever. They are now persisted and swept on the next start.

### Security

- The .NET runtime download is verified against a SHA-256 pinned inside the
  installer; the hash is never fetched over the network.
- The app never downloads or executes an update itself any more. The
  staging and rollback path is still compiled but has no caller:
  `--update-apply` removes whatever was staged and exits non-zero, so the
  running program never executes a payload a remote config pointed at.
- Settings are written atomically. An unreadable file is quarantined as
  `.bad` instead of being silently discarded.
- The installer requires administrator rights and installs per-machine
  into Program Files, which a normal account cannot write to. The program
  itself still runs `asInvoker`, so ordinary use never prompts for
  elevation: everything it writes lives outside the install directory —
  settings and servers in `%APPDATA%\Roxa`, logs in
  `%LOCALAPPDATA%\Roxa\logs`, tunnel state in `%ProgramData%\Roxa`.
- `%ProgramData%\Roxa` is created with inheritance broken and only SYSTEM
  and Administrators given full control. Another local account can read
  the tunnel state — engine process id, adapter, routes, the proxy and DNS
  to restore — but cannot change what Roxa will act on.
- The engine ships with the digest it must match, so verifying it inside
  Program Files never writes anything. A binary that fails verification
  while Roxa is not elevated stops with one clear message asking for
  administrator rights instead of attempting a re-download into a folder
  it cannot write to.
- Host routes are restored and held routes persist across crashes.

## [3.0.7] - 2026-10-10

### Added

- Update progress bar.
- A "full test" menu that runs ping, real delay and speed in sequence.
- Application bypass from TUN: a `/32` route is pinned through the physical
  gateway per app, so an exempted application really does skip the tunnel.
- A tunnel health probe with a kill-switch that blocks egress at the WFP
  layer when the tunnel drops, plus an emergency release button and a
  `roxa-unblock.cmd` recovery script.
- "Bypass VPN even without TUN": an internal PID-aware proxy router forwards
  exempted apps directly while everything else still goes upstream.
- Direct-path testing: ping, real delay and speed can be measured over the
  machine's own connection instead of the tunnel.

### Changed

- The kill-switch was redesigned around dynamic WFP filters instead of static
  firewall rules.
- sing-box support was removed entirely; Xray-core is the only engine.
- The product was renamed from Xray to Roxa throughout the visible UI.

### Fixed

- A multi-second UI freeze when connecting with TUN enabled.
- Disconnect no longer destroys another VPN's system proxy permanently.
- The "real delay" test crashed the whole application on cancellation: an
  `OperationCanceledException` escaped a semaphore wait, travelled through
  `Task.WhenAll`, and was rethrown on the dispatcher.
- Removing an app from the TUN exemption list left its routes behind, so its
  traffic kept bypassing the tunnel.

## [3.0.1] - 2026-10-01

### Added

- User profiles on the panel, showing IP and operating system.
- A stable per-install identifier, plus country detection, so announcements
  and updates can be targeted.

## [3.0.0] - 2026-09-14

### Added

- First public build: modern UI, server sorting, latency testing and
  persistence.

<!--
  Release comparison links go here once the repository has a remote.
  Add them in this form and remove this comment:

  [Unreleased]: https://github.com/<owner>/<repo>/compare/v3.0.8...HEAD
  [3.0.8]:      https://github.com/<owner>/<repo>/releases/tag/v3.0.8
  [3.0.7]:      https://github.com/<owner>/<repo>/releases/tag/v3.0.7
  [3.0.1]:      https://github.com/<owner>/<repo>/releases/tag/v3.0.1
  [3.0.0]:      https://github.com/<owner>/<repo>/releases/tag/v3.0.0
-->

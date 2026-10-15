# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
- A single global mutex is used; a second launch now brings the existing
  window forward instead of silently exiting.

### Fixed

- The installer did not compile at all: an `#` preprocessor directive inside
  `[Code]` and the misspelled `skipifdoesntexist` flag both broke ISCC.
- The kill-switch notified on intentional disconnects and the network
  watcher reacted to its own reconnects.
- The wintun download URL was dead; the correct per-architecture DLL is now
  picked.
- Manual DNS servers were swept away by the firewall cleanup.
- Exclusion routes (`/32`) survived a crash and stayed in the routing table
  forever. They are now persisted and swept on the next start.

### Security

- The .NET runtime download is verified against a SHA-256 pinned inside the
  installer; the hash is never fetched over the network.
- An update is applied with a backup and rolled back if the new build fails
  to start.
- Settings are written atomically. An unreadable file is quarantined as
  `.bad` instead of being silently discarded.
- The app installs per-user and runs with `asInvoker`, so no elevation
  prompt is ever shown and Program Files never becomes unwritable.
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

# Privacy Policy

**Short version:** Roxa has no telemetry, no analytics, no advertising and no
crash reporting by default. It makes only the network connections listed
below.

Last reviewed: 2026-10-15, against Roxa 3.0.8.

## What stays on your machine

| File | Contains | Encrypted? |
|---|---|---|
| `%APPDATA%\Roxa\servers.json` | Your servers: addresses, ports, UUIDs, passwords, keys | **Yes** — Windows DPAPI, current user |
| `%APPDATA%\Roxa\subscriptions.json` | Subscription names and URLs | **Yes** — Windows DPAPI, current user. A file written by an older version is re-encrypted the first time it is read |
| `%APPDATA%\Roxa\settings.json` | Preferences, language, and an anonymous install identifier | No |
| `%APPDATA%\Roxa\*.txt`, `*.flag` | Firewall rules and route pins used for TUN bypass | No |
| `%LOCALAPPDATA%\Roxa\logs\`, `<app>\logs\` | Operational logs | No |
| `%ProgramData%\Roxa\tun-state.json` | Engine process id, adapter name, routes, and the proxy/DNS settings to restore after TUN mode. No credentials | No — another local account can still **read** it, but Roxa tightens the folder permissions when it is installed and again at every start (SYSTEM and Administrators: full control, everyone else: read only) so that account cannot change what Roxa will act on |
| `%TEMP%\roxa-config-*.json` | Full Xray configuration, including your credentials | No — access is restricted to your account, but the file is **kept on disk** for troubleshooting. It is deleted only when Roxa removes configurations older than 24 hours, at the next start |
| A file you pick with *Export subscriptions* | Subscription names and URLs, as plain JSON | **No, and it cannot be** — the point of the export is a file that is not tied to one Windows account, so it can be moved to another machine. Protect it the way you protect the links themselves |

Roxa does **not** log traffic destinations. The Xray configuration uses
`loglevel: warning` with no access or error log file, so the engine never
writes the sites you visit to disk. Test configurations use `loglevel: none`.

The temporary configuration is written to `%TEMP%` with an ACL that grants
access only to your account, then handed to the engine. It is left in place on
purpose so a support request can be diagnosed against what was actually run.
Roxa removes configurations older than 24 hours when it starts, so one
survives at most until the next time you open the program. If you want it gone
sooner, deleting `%TEMP%\roxa-config-*.json` while Roxa is closed is safe.

## Connections Roxa makes

| Destination | Sent | Received | When |
|---|---|---|---|
| `up.roxacore.com/api/check.php` | App version, interface language, country code, **install identifier**, a one-way hash of the address you came from, and (only if *Send usage statistics* is on) the number of connections you have made | Available update, announcements | On start, then **every 60 minutes** |
| `up.roxacore.com/api/announcements.php` | Last-seen announcement id, language, country code | Announcements | On start, then every 60 minutes |
| `up.roxacore.com/api/report.php` | Crash report: exception, up to 4 000 characters of log tail, install identifier, and a one-way hash of the address you came from | — | **Only if you turn on "Send crash reports"** |
| `get.geojs.io`, `ipwho.is` | Your IP address (implicitly — you cannot ask for a country without revealing it) | Country code | On every check **while you are not connected**. The result is cached in `settings.json` and is not requested again while the tunnel is up |
| `api.github.com/repos/XTLS/Xray-core/releases/latest` | Your IP address (implicitly — any web request reveals it) | Xray-core release metadata | When checking for an engine update |
| `gh-proxy.com` | Your IP address (implicitly) | Xray-core binary | When downloading an engine update. **This is a third-party mirror, not operated by Roxa.** It can see that you fetched an Xray-core build and when |
| `www.wintun.net` | — | Wintun driver archive | First time TUN mode is used |
| `speed.cloudflare.com`, `cachefly.cachefly.net`, `cp.cloudflare.com`, `www.gstatic.com`, `www.google.com` | — | Fixed-size test payloads | During a delay or speed test, **through your tunnel** |
| `www.msftconnecttest.com`, `1.1.1.1` | — | Connectivity probes | When checking whether the network changed |
| **Your own subscription URL** | — | Your server list | When you add or refresh a subscription |

Every request to a destination in this table except your own subscription URL
is made by Roxa itself. Requests to your subscription URL are made only when
you ask for them, or automatically if you enable *Auto-update subscriptions*.

## The install identifier

A random GUID is generated on first run and stored in `settings.json`. It is
**not tied to crash reporting** — it is sent with every version check, which
happens on start and then every 60 minutes, and additionally with crash
reports if you have enabled those. The server therefore knows how many
installs are checking in and how often, but nothing else about you.

It contains no personal information and cannot be traced to you by itself.
You can remove it by deleting the `ClientId` entry from `settings.json` while
Roxa is closed; a new one is generated on the next start. Note that the old
one has already been seen by the server, so this prevents future correlation
rather than erasing past ones.

## The Roxa server keeps no IP address

Your IP is read from the connection itself (`REMOTE_ADDR`) — never from a
header, so it cannot be spoofed and cannot be influenced by anyone in between.
It is then replaced with a keyed one-way hash before anything is written to
disk, and the address itself is discarded.

The hash uses a random per-server salt that is never published and is not part
of this repository, so the server cannot turn a stored hash back into your
address, and neither can anyone who obtains a copy of the server's data
without also obtaining that salt. What the hash is good for is only the
question "is this the same source as the previous request?", which is what the
rate limiter needs.

The salted hashes are kept for at most one hour in the rate-limiter files and
are overwritten as they age out. Longer-lived records — the install
identifier's row in the client list, and any crash report — carry the hash of
the source as a label, which is why the panel says *source* and not *IP*.

That label has a deadline of its own: **90 days**. A report loses its source
hash 90 days after it was filed, and an install identifier's row loses its
source hash 90 days after that install last checked in. The report itself and
the row stay — the connection history is what makes a report useful, and the
address is in neither of them. Only the label is dropped.

**What this does not hide:** sending a request at all reveals your IP to the
server for the duration of that request, and the third-party services in the
table above (`get.geojs.io`, `ipwho.is`, `api.github.com`, `gh-proxy.com`)
receive your IP directly because you asked them for something. Hashing limits
what is *retained*; it does not limit what is *seen*. If you want neither,
connect through the tunnel first — the version check then runs from the
server's address, not yours.

## Crash reporting is opt-in

*Send crash reports* is **off** in a fresh install. Nothing is uploaded until
you turn it on. Reports are queued in `%APPDATA%\Roxa\reports-pending` and
only uploaded while the setting stays enabled. Turning it off stops uploads and
empties the queue, so nothing you asked not to send is still waiting on disk;
the setting is also visible in Settings at any time.

The queue holds at most 30 reports; adding one past that removes the oldest.
Reports are removed as soon as they are sent, and reports with no content are
discarded without being sent. A report that could not be sent because you never
enabled the setting is deleted once it is **30 days old** — the sweep runs
every time Roxa starts, whether or not sending is enabled. Sweeping and
discarding are local deletions; neither one uploads anything. If you want a
queued report gone before then, empty the `reports-pending` folder while Roxa
is closed.

The parts a report collects automatically are scrubbed before they leave the
machine: a URL that carries a path or a query becomes `<url>`, a UUID becomes
`<uuid>`, and an IPv4 address becomes `<ip>` — in the file on disk as well as
in what is sent. Loopback addresses are left alone. Description text you typed
yourself is sent as you wrote it.

## What Roxa does not do

- No telemetry, analytics, crash analytics, or usage statistics.
- No advertising, tracking pixels, or third-party SDKs.
- No selling or sharing of data with third parties.
- No reading of your browsing history, files, or other applications' data.
- No account, no registration, no email address.
- No IP address is written to disk by the update server — only a keyed,
  one-way hash that cannot be reversed without the server's private salt.

## Third parties

Roxa makes no request to any advertising, analytics or tracking domain. The
third-party components it ships, and their licenses, are listed in
`THIRD-PARTY-NOTICES.txt`.

## Changes

Any change to this list is documented in `CHANGELOG.md` under `Security`.

> Roxa has no telemetry, analytics or automatic crash reporting.
> **Servers and subscription links are both encrypted with Windows DPAPI**
> (an older file is re-encrypted automatically on first run). But **the
> engine's temporary config is plain text**, and although access to it is
> restricted to your own account, it stays on disk for 24 hours. Sending
> crash reports is entirely optional and off by default; **each report in the
> queue stays at most 30 days**, and turning the option off empties the queue.
> **Your IP address is not stored on the server's disk** — only an
> irreversible salted hash remains, and that for at most one hour in the
> rate-limiter counters. The full list of connections is above, including
> that `gh-proxy.com` (a third-party proxy) sees your address and the Xray-core file request. Any future change is recorded in `CHANGELOG.md`.

# Privacy Policy

**Short version:** Roxa has no telemetry, no analytics, no advertising and no
crash reporting by default. It makes only the network connections listed
below.

Last reviewed: 2026-10-15, against Roxa 3.0.8.

## What stays on your machine

| File | Contains | Encrypted? |
|---|---|---|
| `%APPDATA%\Roxa\servers.json` | Your servers: addresses, ports, UUIDs, passwords, keys | **Yes** — Windows DPAPI, current user |
| `%APPDATA%\Roxa\subscriptions.json` | Subscription names and URLs | No |
| `%APPDATA%\Roxa\settings.json` | Preferences, language, and an anonymous install identifier | No |
| `%APPDATA%\Roxa\*.txt`, `*.flag` | Firewall rules and route pins used for TUN bypass | No |
| `%LOCALAPPDATA%\Roxa\logs\`, `<app>\logs\` | Operational logs | No |
| `%TEMP%\roxa-config-*.json` | Full Xray configuration, including your credentials | No — **deleted on shutdown** and swept on the next start |

Roxa does **not** log traffic destinations. The Xray configuration uses
`loglevel: warning` with no access or error log file, so the engine never
writes the sites you visit to disk. Test configurations use `loglevel: none`.

## Connections Roxa makes

| Destination | Sent | Received | When |
|---|---|---|---|
| `up.roxacore.com/api/check.php` | App version, interface language, country code, **install identifier**, and (only if *Send usage statistics* is on) the number of connections you have made | Available update, announcements | On start, then **every 60 minutes** |
| `up.roxacore.com/api/announcements.php` | Last-seen announcement id, language, country code | Announcements | On start, then every 60 minutes |
| `up.roxacore.com/api/report.php` | Crash report: exception, up to 4 000 characters of log tail, install identifier | — | **Only if you turn on "Send crash reports"** |
| `get.geojs.io`, `ipwho.is` | Your IP address (implicitly — you cannot ask for a country without revealing it) | Country code | On every check **while you are not connected**. The result is cached in `settings.json` and is not requested again while the tunnel is up |
| `api.github.com/repos/XTLS/Xray-core/releases/latest` | — | Xray-core release metadata | When checking for an engine update |
| `gh-proxy.com` | — | Xray-core binary | When downloading an engine update |
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

## Crash reporting is opt-in

*Send crash reports* is **off** in a fresh install. Nothing is uploaded until
you turn it on. Reports are queued in `%APPDATA%\Roxa\reports-pending` and
only uploaded while the setting stays enabled. Turning it off stops uploads;
the setting is also visible in Settings at any time.

## What Roxa does not do

- No telemetry, analytics, crash analytics, or usage statistics.
- No advertising, tracking pixels, or third-party SDKs.
- No selling or sharing of data with third parties.
- No reading of your browsing history, files, or other applications' data.
- No account, no registration, no email address.

## Third parties

Roxa makes no request to any advertising, analytics or tracking domain. The
third-party components it ships, and their licenses, are listed in
`THIRD-PARTY-NOTICES.txt`.

## Changes

Any change to this list is documented in `CHANGELOG.md` under `Security`.

> **فارسی:** رُخا هیچ تله‌متری، تحلیل یا گزارشِ خودکارِ خطا ندارد. سرورهای
> شما با DPAPIِ ویندوز رمزنگاری می‌شوند، ولی **لینکِ اشتراک متنِ ساده** است و
> چون توکن دارد آن را محافظت کنید. ارسالِ گزارشِ کرش کاملاً اختیاری و
> پیش‌فرضِ آن خاموش است. فهرستِ کاملِ اتصال‌ها در بالا آمده و در آینده هر
> تغییری در `CHANGELOG.md` ثبت می‌شود.

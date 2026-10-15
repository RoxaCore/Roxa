# Security Policy

## Reporting a vulnerability

**Do not open a public issue for a security vulnerability.**

Report it privately through one of these channels:

- **GitHub private vulnerability reporting** — once this repository is
  public, use the *Security* tab and click *Report a vulnerability*. This is
  the preferred channel because it reaches the maintainers directly and keeps
  the report out of public view until it is fixed.
- **Email** — `security@roxacore.com`
### What to include

- The Roxa version (shown at the bottom of the Settings page).
- Windows version and build.
- Whether TUN mode or system-proxy mode was in use.
- Steps to reproduce, and the expected versus actual behaviour.
- The relevant log excerpt from `logs\tun.log` or `logs\engine.log`.

Please do **not** include your server addresses, subscription links, UUIDs or
keys in a report. If a report cannot be reproduced without them, say so and a
private exchange can be arranged.

## Supported versions

Only the latest released version receives security fixes. There is no
long-term support branch.

| Version | Supported |
|---|---|
| 3.0.8 | yes |
| < 3.0.8 | no |

## What is in scope

These are the areas a security report is most likely to concern:

- **Temp configuration files.** Roxa writes a full Xray configuration,
  containing your server credentials, to `%TEMP%\roxa-config-*.json` while the
  engine is running. It is deleted on shutdown, and stale copies from a crash
  are swept on the next start. A report that these files survive, or are
  readable by another user account, is in scope.
- **Credential storage.** The server list in `%APPDATA%\Roxa\servers.json` and
  the subscription list in `subscriptions.json` are encrypted with Windows DPAPI
  for the current user; `subscriptions.json` also carries subscription URLs,
  which embed a secret token, and a file written by an older version is
  re-encrypted the first time it is read. Anything that makes either file
  readable outside the user's own account is in scope.
- **Tunnel state.** `%ProgramData%\Roxa\tun-state.json` records the engine PID,
  the interface's previous DNS servers and the previous system proxy so that a
  crash can be undone on the next start. Its directory is writable only by
  SYSTEM and Administrators, and every value read back is validated before it
  is acted on. A report that a local, non-elevated account can influence what
  an elevated Roxa does with it is in scope.
- **System state.** TUN mode installs routes, a firewall filter set and DNS
  changes, and can modify the system proxy. Reports about state surviving a
  crash, being applied without consent, or being incompletely restored are in
  scope.
- **Update supply chain.** The .NET runtime is verified against a SHA-256
  pinned in the installer; Xray-core and Wintun downloads are verified against
  a digest. A report that any of these can be substituted is in scope.
- **Injection.** Subscription links, server names and imported JSON reach the
  generated Xray configuration and several file paths.

## What is out of scope

- Vulnerabilities in Xray-core, Wintun, the .NET runtime or any bundled NuGet
  package that do not require Roxa's specific handling to trigger. Report
  those upstream — the components and their sources are listed in
  `THIRD-PARTY-NOTICES.txt`.
- The absence of Authenticode code signing. It is a known, deliberate
  limitation and is disclosed in `README.md`.
- Reports that require a compromised Windows account, an already-elevated
  attacker, or physical access.
- Anything that needs a server you do not control.

## Response times

These are targets, not guarantees.

| Stage | Target |
|---|---|
| Acknowledgement | 3 business days |
| Initial triage and severity | 7 business days |
| Fix or mitigation for a confirmed issue | 30 days |
| Public disclosure after a fix ships | coordinated, at the reporter's preference |

If a report is accepted, the fix is released in the next version and credited
in `CHANGELOG.md` under `Security`, unless the reporter prefers otherwise.

> **فارسی:** آسیب‌پذیری را در معرضِ عموم گزارش نکنید. از تبِ *Security* و
> گزینهٔ *Report a vulnerability* استفاده کنید. نسخهٔ رُخا، نسخهٔ ویندوز و
> حالتِ اتصال (TUN یا پروکسی) را حتماً ذکر کنید، ولی **آدرسِ سرور، لینکِ
> اشتراک و کلیدها را نفرستید.** فقط آخرین نسخهٔ منتشرشده پشتیبانی می‌شود.

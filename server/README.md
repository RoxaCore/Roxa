# Roxa update server

Version checker, announcements, and the management panel. Needs only PHP 8.1+ and a
web server; no database involved, all data is JSON.

## Installation

1. Put the contents of this folder in the web root (e.g. `~/public_html/up.roxacore.com`).
2. **Move the data folder out of the web root** — this is the most important step:

   ```bash
   ROXA_DATA=/var/lib/roxa php tools/migrate-data.php --apply
   ```

   Without `ROXA_DATA`, `config.php` still creates `roxa-data/` **next to the web
   root** by default (i.e. outside `server/`). If your web root *is* `server/`, then
   `roxa-data/` is already outside. If the web root is the repository root, be sure
   to point `ROXA_DATA` at a path outside it.

   Why: as long as the data lived inside the document root, its only protection was
   `.htaccess` — which is ineffective on nginx and any web server without
   `AllowOverride`, leaving `auth.json` (the admin password) downloadable.

3. Create the admin password (command line only):

   ```bash
   php tools/create-admin.php
   ```

   **Web-based account creation has been removed.** Without `auth.json`, `/admin/`
   returns 503, and there is no way to create the password from outside.

## Web server requirements

`.htaccess` files are only honored on Apache with `AllowOverride`. Without it,
the `.htaccess` protection is void (see the next section).

```apache
<Directory "/var/www/up.roxacore.com">
    AllowOverride FileInfo AuthConfig Limit
    Require all granted
</Directory>
```

If you are on **nginx**, `AllowOverride` means nothing; you must write it yourself:

```nginx
location ~* \.(json|lock|salt|tmp)$ { deny all; }
location ~ /\. { deny all; }
```

Also deny the `data/` folder. (However, if you have set `ROXA_DATA` properly,
the data is not under the document root at all and this is merely a second layer.)

## What is protected

| Layer | What it does |
|---|---|
| `DATA_DIR` outside the web root | `auth.json`, `stats.json`, `reports.json` and `.salt` cannot be requested over HTTP at all. |
| `server/.htaccess` | On Apache, denies `.json`/`.lock`/`.salt`/`.tmp` and dotfiles. |
| `server/data/.htaccess` | A third layer for anyone who moved `DATA_DIR` back inside. |
| Yellow banner in the panel | If `DATA_DIR` is under the document root, the panel warns on **every** page and shows the path. Silence in this state would be the most dangerous option. Detection reads the program's own path and does not depend on the web server; `DOCUMENT_ROOT` is only a fallback. |

If `.salt` leaks, the IP hashes become brute-forceable offline; that is why it is
both covered by `.htaccess` and written with `chmod 600`.

## Privacy

- Raw IPs are never stored anywhere; only salted HMAC-SHA256 hashes (the `src` label in the panel).
- The salt lives in `DATA_DIR/.salt` and you **must not** copy it elsewhere, because
  doing so makes old hashes brute-forceable too.
- The rate limiter and the login counters work off the same hash and are cleared
  after a one-hour window.
- The source label (`src` in `reports.json` and `ip`/`ip_last` in `stats.json`) is kept
  for at most **90 days**, then dropped; the report itself and the client row stay.
  The cap lives in `SRC_HASH_TTL_DAYS` in `lib.php` and is applied to every
  fresh report and every fresh check-in.
- Do not back up `.salt` unless you want to keep the hashes usable.

Full details of the behavior are in [`../PRIVACY.md`](../PRIVACY.md).

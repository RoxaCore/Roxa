# Roxa

**A VPN client for Windows, built on Xray-core.**

Roxa is a desktop GUI around Xray-core. It supports system-proxy mode and a
full TUN mode with a kill-switch, per-application bypass, live delay
measurement, and automatic updates. The interface is available in 14
languages.

> **فارسی:** رُخا یک کلاینت VPN برای ویندوز است که روی Xray-core ساخته شده.
> هم حالتِ پروکسیِ سیستم و هم حالتِ کاملِ TUN با کیل‌سوییچ، معافیتِ برنامه‌ها،
> اندازه‌گیریِ زندهٔ تأخیر و به‌روزرسانیِ خودکار دارد. رابط به ۱۴ زبان موجود است.

---

## Requirements / نیازمندی‌ها

| | |
|---|---|
| **Operating system** | Windows 10 version 1607 (build 14393) or newer, including Windows 11 |
| **Architecture** | x64 |
| **.NET** | .NET Desktop Runtime 8 — **installed automatically by setup** |
| **Administrator** | Only needed for TUN mode |

Windows 7, 8 and 8.1 are not supported: Microsoft no longer ships the .NET 8
runtime for them, so neither the runtime nor Roxa can run there.

> **فارسی:** ویندوز ۷، ۸ و ۸٫۱ پشتیبانی نمی‌شوند چون مایکروسافت دیگر رانتایمِ
> دات‌نت ۸ را برای آن‌ها منتشر نمی‌کند. برای حالتِ TUN به دسترسیِ
> Administrator نیاز است؛ حالتِ پروکسیِ سیستم بدونِ آن کار می‌کند.

---

## Installation / نحوهٔ نصب

1. Download `Roxa-Setup-<version>.exe`.
2. Run it. Setup installs **per-user** into
   `%LOCALAPPDATA%\Programs\Roxa` — no administrator prompt appears.
3. If the .NET Desktop Runtime is missing, setup downloads it from
   Microsoft and verifies its SHA-256 before running it.
4. Launch Roxa and add a server, or paste a subscription link.

To uninstall, use *Apps & features*. Uninstall runs a cleanup pass first, so
the TUN adapter, its routes, the firewall rules and the system proxy are all
restored before the files are removed.

> **فارسی:** نصب‌کننده برایِ کاربرِ جاری نصب می‌کند و هیچ درخواستِ ادمین نمی‌دهد.
> اگر رانتایمِ دات‌نت نباشد، خودش ازِ مایکروسافت دانلود و با SHA-256 تأیید
> می‌کند. برای حذف، از «برنامه‌ها و ویژگی‌ها» اقدام کنید؛ حذف اول پاکسازی
> (آداپتر، مسیرها، فایروال، پروکسیِ سیستم) را اجرا می‌کند.

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
Get-FileHash .\Roxa-Setup-3.0.8.exe -Algorithm SHA256
```

> **فارسی:** چون نصب‌کننده امضایِ دیجیتال ندارد، ویندوز پیامِ
> «Windows protected your PC» را نشان می‌دهد. رویِ **More info** و بعد
> **Run anyway** بزنید. این پیام به‌خاطرِ نبودِ گواهی است، نه مشکلِ امنیتی.
> هشدارِ SHA-256 هر نسخه کنارِ لینکِ دانلود منتشر می‌شود.

---

## Reporting a problem / گزارشِ مشکل

- **In-app:** use the bug report entry in Settings. If you have enabled
  *Send crash reports*, a report containing the exception and the tail of the
  log is queued and uploaded. This is **off by default** and only ever sends
  what you see in the report preview.
- **Manually:** the logs are written to the `logs` folder next to `Roxa.exe`
  (`tun.log`, `engine.log`, `parser.log`). Attach the relevant one when you
  report a problem.

When reporting a connection problem, include the operating system version and
whether you were in TUN mode or system-proxy mode.

> **فارسی:** از داخلِ برنامه، گزینهٔ گزارشِ باگ در تنظیمات را بزنید. اگر
> «ارسالِ گزارشِ کرش» را روشن کرده باشید (که پیش‌فرض خاموش است)، گزارشِ خطا
> همراهِ ۴۰۰۰ کاراکترِ آخرِ لاگ ارسال می‌شود. لاگ‌ها در پوشهٔ `logs` کنارِ
> `Roxa.exe` هستند. هنگامِ گزارشِ مشکلِ اتصال، نسخهٔ ویندوز و اینکه در حالتِ
> TUN بودید یا پروکسی را ذکر کنید.

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

Your server list is encrypted with Windows DPAPI and never leaves your
machine except as part of an actual connection.

> **فارسی:** رُخا هیچ تله‌متری یا گزارشِ خودکارِ خطا ندارد. فهرستِ کاملِ
> اتصال‌ها در `THIRD-PARTY-NOTICES.txt` آمده. لیستِ سرورهای شما با DPAPIِ
> ویندوز رمزنگاری می‌شود و جز در هنگامِ اتصالِ واقعی، جایی نمی‌رود.

---

## Building from source

```powershell
dotnet build -c Release
tools\build-installer.ps1
```

The app is framework-dependent and publishes to `installer\publish`, which
`setup.iss` packages. `engine\xray.exe` and `engine\wintun.dll` are the only
bundled binaries.

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

> **فارسی:** کدِ رُخا آزاد است و تحتِ GPL-3.0. ولی **نامِ «رُخا»، لوگو و
> هویتِ بصریِ آن مالِ پروژه است و با GPL منتقل نمی‌شود.** می‌توانید سورس را
> تغییر دهید و توزیع کنید، ولی نام و لوگو را نباید بدونِ اجازهٔ کتبی استفاده
> کنید. این بندِ ۷(e) قراردادِ GPL است.

### Third-party components

Third-party components keep their own licenses and are listed in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt): Xray-core is MPL-2.0,
Wintun carries the WireGuard Prebuilt Binaries License, both fonts are
SIL OFL-1.1, and every managed dependency is MIT. The GPL and these licenses
coexist because Roxa runs those components as **separate programs**, not by
linking them into a single work.

---

## Security and privacy

- [`SECURITY.md`](SECURITY.md) — how to report a vulnerability privately, and
  the response-time targets.
- [`PRIVACY.md`](PRIVACY.md) — exactly what Roxa stores on disk and every
  network destination it contacts.

// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Net;

namespace Roxa.Core;

public sealed class TestRouteScope : IDisposable
{
    private readonly List<string> _ips = new();
    private bool _disposed;

    internal void Track(string ip) { lock (_ips) _ips.Add(ip); }

    /// <summary>آیا واقعاً مسیری برای دور زدن اضافه شده است؟ (برای نمایش پیام در لاگ/وضعیت)</summary>
    public bool HasRoutes { get { lock (_ips) return _ips.Count > 0; } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        string[] ips;
        lock (_ips) { ips = _ips.ToArray(); _ips.Clear(); }
        // route delete پروسه‌محور است و Dispose روی رشتهٔ UI صدا زده می‌شود؛
        // آزادسازی مسیرها در پس‌زمینه انجام می‌شود تا UI ثانیه‌ای قفل نشود.
        if (ips.Length > 0)
            Task.Factory.StartNew(() =>
            {
                foreach (var ip in ips) TestRouteBypass.Release(ip);
            }, TaskCreationOptions.LongRunning);
    }
}

public static class TestRouteBypass
{
    private sealed class Held
    {
        public string Via = "";
        public int Refs;

        // مسیرِ ازپیش‌موجودِ خودِ کاربر برایِ همین IP، اگر قبل از ما آنجا بوده.
        //
        // بدونِ این، delete در خطِ افزودن و باز هم delete در Release یعنی نابودیِ
        // دائمیِ مسیری که هیچ‌کس نمی‌دانسته وجود داشته — کاربر ممکن است برایِ یک
        // میزبانِ خاص مسیرِ ثابتِ دستی گذاشته باشد و ما بی‌خبر آن را پاک می‌کنیم.
        // null یعنی چیزی نبوده، یا چیزی بوده که قابلِ بازسازی نیست (مثلاً On-link).
        public string? OriginalVia;
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Held> _held = new(StringComparer.OrdinalIgnoreCase);
    private static string? _via;
    private static long _viaAt;

    private static readonly string HeldFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Roxa", "test-routes.txt");

    public static bool TestsViaTun { get; set; }

    public static bool IsBypassing => !TestsViaTun && AdminHelper.IsRunningAsAdmin() && TunManager.IsCaptureActive;

    /// <param name="forceDirect">
    /// برای تشخیص سلامت مسیر مستقیم (چک اینترنت): حتی وقتی «تست از مسیر تونل»
    /// تنظیم شده هم بایپس ساخته شود، چون تونلِ بعد از تعویض شبکه ممکن است مرده باشد.
    /// </param>
    public static async Task<TestRouteScope> BeginAsync(IEnumerable<string?>? hosts, bool forceDirect = false)
    {
        var scope = new TestRouteScope();
        try
        {
            if ((!forceDirect && TestsViaTun) || hosts == null) return scope;
            if (!AdminHelper.IsRunningAsAdmin()) return scope;
            // دور زدن لازم است وقتی TUN خودمان فعال باشد یا TUNِ بیگانه‌ای (مثل v2rayN)
            // مسیر پیش‌فرض را بگیرد؛ در حالت عادی (بدون TUN) مسیرِ عادی همان مستقیم است
            // و نیازی به اضافه‌کردن مسیر نیست.
            if (!TunManager.IsCaptureActive && !TunManager.AnyTunDefaultRoute()) return scope;

            var wanted = new List<string>();
            foreach (var raw in hosts)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string h = raw.Trim();
                if (!wanted.Contains(h, StringComparer.OrdinalIgnoreCase)) wanted.Add(h);
            }
            if (wanted.Count == 0) return scope;

            // route print (Gateway) و حلقهٔ route delete/add پروسه‌محورند. اینها قبلاً
            // روی رشتهٔ UI اجرا می‌شدند و هر TCP ping در اتصال هوشمند/تست‌ها UI را
            // چند صد میلی‌ثانیه می‌گرفت؛ اکنون در پس‌زمینهٔ LongRunning اجرا می‌شوند.
            string? via = await Task.Run(Gateway);
            if (via == null) return scope;

            var guard = TunManager.CaptureHostRoutes();
            var ips = await TunManager.ResolveIpsAsync(wanted);

            await Task.Factory.StartNew(() =>
            {
                foreach (var ip in ips)
                {
                    if (guard.Contains(ip) || TunExclusion.IsPinned(ip)) continue;
                    bool ours = false;
                    lock (_lock)
                    {
                        if (_held.TryGetValue(ip, out var held))
                        {
                            held.Refs++;
                            ours = true;
                        }
                        else
                        {
                            // قبل از دست‌زدن ببین خودِ کاربر مسیری برایِ این IP
                            // دارد یا نه؛ اگر داشته باشد باید بعداً برگردانده شود.
                            string? original = TunManager.GetHostRouteVia(ip);

                            TunManager.RunRoute($"delete {ip} mask 255.255.255.255");
                            if (TunManager.RunRoute($"add {ip} mask 255.255.255.255 {via}") == 0)
                            {
                                _held[ip] = new Held { Via = via, Refs = 1, OriginalVia = original };
                                SaveHeld();
                                ours = true;
                            }
                            else if (original != null)
                            {
                                // مسیرِ کاربر را پاک کردیم ولی مسیرِ خودمان ساخته
                                // نشد؛ بی‌صدا رهایش نکن.
                                try { TunManager.RunRoute($"add {ip} mask 255.255.255.255 {original}"); } catch { }
                                TunManager.Log($"host route restore failed for {ip} -> {original}");
                            }
                        }
                    }
                    if (ours) scope.Track(ip);
                }
            }, TaskCreationOptions.LongRunning);
        }
        catch { }
        return scope;
    }

    internal static void Release(string ip)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(ip, out var held)) return;
            held.Refs--;
            if (held.Refs > 0) return;
            _held.Remove(ip);
            SaveHeld();
            if (TunManager.CaptureHostRoutes().Contains(ip)) return;
            if (TunExclusion.IsPinned(ip)) return;
            try { TunManager.RunRoute($"delete {ip} mask 255.255.255.255"); } catch { }
            RestoreOriginal(ip, held.OriginalVia);
        }
    }

    /// <summary>
    /// مسیرِ خودِ کاربر را برمی‌گرداند. فقط وقتی یک گیت‌ویِ واقعی داشته؛ On-link و
    /// هر چیزِ دیگر قابلِ بازسازی با route.exe نیست و لاگ می‌شود تا حداقل معلوم باشد
    /// چه چیزی از دست رفته.
    /// </summary>
    private static void RestoreOriginal(string ip, string? original)
    {
        if (original == null) return;
        if (!IPAddress.TryParse(original, out _))
        {
            TunManager.Log($"pre-existing host route {ip} had non-restorable gateway '{original}'");
            return;
        }
        try
        {
            if (TunManager.RunRoute($"add {ip} mask 255.255.255.255 {original}") == 0)
                TunManager.Log($"restored pre-existing host route {ip} -> {original}");
        }
        catch { }
    }

    /// <summary>
    /// بازنویسیِ فایلِ پشتیبان از رویِ وضعیتِ فعلی. باید داخلِ _lock صدا زده شود.
    /// </summary>
    private static void SaveHeld()
    {
        try
        {
            string? dir = Path.GetDirectoryName(HeldFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = new List<string>(_held.Count);
            foreach (var kv in _held)
                lines.Add($"{kv.Key}\t{kv.Value.Via}\t{kv.Value.OriginalVia ?? ""}");
            File.WriteAllLines(HeldFile, lines);
        }
        catch { /* اگر نوشتِ نشد، فقط شبکهٔ امنِ بعد از کرش را از دست می‌دهیم */ }
    }

    /// <summary>
    /// مسیرهایِ /32 که از اجرایِ قبلی جا مانده‌اند را پاک می‌کند و مسیرِ خودِ کاربر
    /// را برمی‌گرداند. هم در شروعِ برنامه و هم در <c>--cleanup</c> (هنگامِ حذف) صدا
    /// زده می‌شود.
    ///
    /// دلیلش فقط کرش نیست: <see cref="TestRouteScope.Dispose"/> آزادسازی را در
    /// پس‌زمینه می‌گذارد، پس اگر برنامه بلافاصله بعد از تست بسته شود، آن تَک هرگز
    /// تمام نمی‌شود و مسیرها می‌مانند. اگر بعداً شبکه عوض شود، گیت‌ویِ مرده سرِ جایش
    /// می‌ماند و IPِ سرور — دقیقاً چیزی که برایِ اتصالِ بعدی لازم است — غیرقابلِ
    /// دسترس می‌شود.
    /// </summary>
    public static void ReleaseOrphanedHostRoutes()
    {
        string[] lines;
        try
        {
            if (!File.Exists(HeldFile)) return;
            lines = File.ReadAllLines(HeldFile);
        }
        catch { return; }

        foreach (var line in lines)
        {
            try
            {
                var parts = line.Split('\t');
                if (parts.Length == 0 || !IPAddress.TryParse(parts[0], out _)) continue;
                string ip = parts[0];
                string original = parts.Length > 2 ? parts[2] : "";

                lock (_lock)
                {
                    if (_held.ContainsKey(ip)) continue;   // همین حالا زنده و نگه‌داشته شده
                }

                try { TunManager.RunRoute($"delete {ip} mask 255.255.255.255"); } catch { }
                RestoreOriginal(ip, string.IsNullOrEmpty(original) ? null : original);
            }
            catch { }
        }

        // فقط وقتی فایل را پاک کن که چیزِ زنده‌ای نمانده. وگرنه رکوردِ مسیرهایِ
        // همین حالا نگه‌داشته‌شده را هم می‌سوزانیم و اگر همان لحظه کرش کنیم، همان
        // مسیرها بی‌صاحب می‌شوند.
        lock (_lock)
        {
            if (_held.Count > 0)
            {
                SaveHeld();
            }
            else
            {
                try { File.Delete(HeldFile); } catch { }
            }
        }
    }

    private static string? Gateway()
    {
        long now = Environment.TickCount64;
        if (_via != null && now - _viaAt < 5000) return _via;
        _via = TunManager.GetDefaultGatewayVia();
        _viaAt = now;
        return _via;
    }
}

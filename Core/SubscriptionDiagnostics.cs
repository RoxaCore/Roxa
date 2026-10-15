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
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Net;
using Roxa.Localization;

namespace Roxa.Core;

/// <summary>نتیجه‌ی عیب‌یابی: خط‌های گزارش + اینکه آیا مشکلی دیده شده است.</summary>
public sealed class DiagReport
{
    public List<string> Lines { get; } = new();
    public bool Healthy { get; private set; } = true;
    public int IssueCount { get; private set; }

    public void Issue(string line)
    {
        Healthy = false;
        IssueCount++;
        Lines.Add(line);
    }
}

/// <summary>
/// عیب‌یابی زنجیره‌ی دریافت اشتراک به همان ترتیبی که واقعاً خراب می‌شود:
/// DNS سیستم ← پراکسی ← دریافت HTTP از پنل ← نسخه‌ی موتور.
/// برای دکمه‌ی «عیب‌یابی اتصال» در سربرگ اشتراک.
/// </summary>
public static class SubscriptionDiagnostics
{
    public static async Task<DiagReport> RunAsync(string? url, string? proxyAddress, CancellationToken ct = default)
    {
        var report = new DiagReport();

        // ۱) DNS: اول باقی‌مانده‌ی 127.0.0.1 را پیدا و (در صورت امکان) ترمیم می‌کنیم، بعد resolve می‌گیریم
        if (OperatingSystem.IsWindows())
        {
            var dead = TunDns.FindDeadStubAdapters();
            foreach (var d in dead)
                report.Issue(string.Format(TranslationManager.Get("Diag_DnsDead"), d.Alias));
            if (dead.Count > 0)
            {
                int fixedCount = TunDns.SweepDeadAdapters(out bool needAdmin);
                if (fixedCount > 0)
                {
                    foreach (var d in dead.Take(fixedCount))
                        report.Lines.Add(string.Format(TranslationManager.Get("Diag_DnsFixed"), d.Alias));
                }
                else if (needAdmin)
                {
                    report.Issue(TranslationManager.Get("Diag_DnsNeedAdmin"));
                }
            }
            else
            {
                report.Lines.Add(TranslationManager.Get("Diag_DnsClean"));
            }
        }

        string? host = null;
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)) host = uri.Host;
        if (host != null)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                string shown = string.Join(", ", addrs.Take(3).Select(a => a.ToString()));
                report.Lines.Add(string.Format(TranslationManager.Get("Diag_DnsOk"), host, shown));
            }
            catch (Exception ex)
            {
                report.Issue(string.Format(TranslationManager.Get("Diag_DnsFail"), ex.Message));
            }
        }

        // ۲) پراکسی سیستم (اگر مرده/خراب باشد، برنامه‌های دیگر را هم گیج می‌کند)
        bool systemProxyOn = false;
        if (SystemProxy.GetCurrent(out bool enable, out string server, out _))
        {
            systemProxyOn = enable;
            if (!enable) report.Lines.Add(TranslationManager.Get("Diag_ProxyOff"));
            else if (SystemProxy.IsDeadLoopback(server)) report.Issue(string.Format(TranslationManager.Get("Diag_ProxyDead"), server));
            else report.Lines.Add(string.Format(TranslationManager.Get("Diag_ProxyOn"), server));
        }

        // ۳) دریافت واقعی همان لینک اشتراک
        if (string.IsNullOrWhiteSpace(url))
        {
            report.Lines.Add(TranslationManager.Get("Diag_NoUrl"));
        }
        else
        {
            var probe = await SubscriptionManager.ProbeAsync(url, proxyAddress, forceDirect: false, ct);
            if (probe.Error != null)
            {
                report.Issue(string.Format(TranslationManager.Get("Diag_FetchErr"), probe.Error));
            }
            else if (!probe.Success)
            {
                string status = probe.StatusCode > 0 ? $"{probe.StatusCode} {probe.Reason}".Trim() : "?";
                report.Issue(string.Format(TranslationManager.Get("Diag_FetchHttp"), status, probe.BodyHead));
            }
            else
            {
                report.Lines.Add(string.Format(TranslationManager.Get("Diag_FetchOk"), probe.StatusCode, probe.ServerCount, probe.ElapsedMs));
            }

            // اگر مسیرِ تنظیم‌شده شکست خورد، بلافاصله بدون هیچ پراکسی‌ای هم امتحان می‌کنیم؛
            // جوابِ این تست دقیقاً می‌گوید مقصر پراکسی است یا شبکه/پنل.
            if (!probe.Success && probe.Error == null)
            {
                var direct = await SubscriptionManager.ProbeAsync(url, proxyAddress: null, forceDirect: true, ct);
                if (direct.Success)
                    report.Lines.Add(string.Format(TranslationManager.Get("Diag_FetchDirectOk"), direct.StatusCode, direct.ServerCount));
            }
            else if (probe.Error != null && systemProxyOn)
            {
                var direct = await SubscriptionManager.ProbeAsync(url, proxyAddress: null, forceDirect: true, ct);
                if (direct.Success)
                    report.Lines.Add(string.Format(TranslationManager.Get("Diag_FetchDirectOk"), direct.StatusCode, direct.ServerCount));
            }
        }

        // ۴) نسخه‌ی موتور
        string version = await XrayEngine.GetVersionAsync(ct);
        if (version == "?")
        {
            report.Issue(string.Format(TranslationManager.Get("Diag_Engine"), version));
        }
        else if (!XrayEngine.IsVersionSupported(version))
        {
            report.Issue(string.Format(TranslationManager.Get("Eng_WarnOld"), version, XrayEngine.MinEngineMajor));
        }
        else
        {
            report.Lines.Add(string.Format(TranslationManager.Get("Diag_Engine"), version));
        }

        return report;
    }
}

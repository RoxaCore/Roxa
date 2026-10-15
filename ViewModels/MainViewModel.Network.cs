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

using System.Net.NetworkInformation;
using System.Threading;
using Roxa.Core;
using Roxa.Localization;

namespace Roxa.ViewModels;

// ---------- واکنش به تغییر شبکه ----------
//
// قبلاً هیچ‌جا به رویدادِ شبکه گوش داده نمی‌شد (NetworkChange صفر بار در کلِ پروژه).
// نتیجه: با خاموش/روشن‌کردنِ وای‌فای، کیل‌سوییچ درست مسدود می‌کرد ولی هیچ‌کس تونل را
// دوباره بالا نمی‌آورد و کاربر می‌ماند با اینترنتِ مرده تا دستی «قطع + وصل» بزند.
//
// اینجا گیت‌ویِ فیزیکی و اینترنتِ مستقیم را می‌سنجیم؛ وقتی برگشتند همان سرورِ قبلی را
// خودکار وصل می‌کنیم. اگر برنگشت یا سقفِ تلاش تمام شد، به‌جای سکوت به کاربر خبر می‌دهیم.
public partial class MainViewModel
{
    // رویدادِ شبکه چندین بار در چند میلی‌ثانیه شلیک می‌شود (لینک بالا/پایین، DHCP، آدرس‌ها).
    // بدونِ debounce، هر بار یک قطع+وصلِ کامل انجام می‌شد.
    private const int NetDebounceMs = 4000;

    // بعد از آرام‌شدنِ رویدادها اینقدر صبر می‌کنیم تا لینک و DHCP واقعاً بالا بیاید.
    private const int NetSettleMs = 3000;

    // چند بار گیت‌وی را دوباره بسنجیم؛ هر بار ۱٫۵ ثانیه ⇒ حدودِ ۳۰ ثانیه.
    private const int NetGatewayTries = 20;
    private const int NetGatewayStepMs = 1500;

    // چند بار اینترنتِ مستقیم را بسنجیم؛ هر بار با ۳ ثانیه فاصله.
    // (بایپسِ مسیر یک بار ساخته می‌شود و در همهٔ این تلاش‌ها نگه داشته می‌شود.)
    private const int NetOnlineTries = 5;
    private const int NetOnlineStepMs = 3000;

    // سقفِ وصل‌هایِ مجددِ خودکار؛ در شبکهٔ پرنوسان حلقهٔ بی‌نهایت نسازد.
    private const int NetMaxReconnects = 2;

    // اگر از آخرینِ تلاشِ خودکار اینقدر گذشته باشد، شمارندهٔ سقف از نو شروع می‌شود؛
    // وگرنه دو قطعِ وای‌فای در دو ساعتِ مختلف، همدیگر را از سِقف می‌انداختند.
    private const long NetTryWindowMs = 120000;

    // بعد از هر قطع/وصلِ خودِ برنامه، رویدادِ شبکه را اینقدر نادیده بگیر؛
    // مسیرها و DNS در همین پنجره عوض می‌شوند و رویدادِ کاذب می‌سازند.
    private const int NetSelfChangeSuppressMs = 20000;

    // برایِ اینکه بفهمیم «واقعاً مشکلی هست؟» یا فقط رویدادِ کاذبِ شبکه است،
    // از مسیرِ خودِ تونل یک پروب می‌زنیم؛ اینقدر فرصت می‌دهیم.
    private const int NetTunnelCheckMs = 4000;

    private long _netEventTick;
    private long _netLastTryTick;
    // خودِ قطع/وصلِ ما مسیر و DNS را عوض می‌کند و همین رویدادِ شبکه را شلیک می‌کند؛
    // بدونِ این پنجره، وصلِ مجددِ خودکار بلافاصله یک وصلِ مجددِ دیگر راه می‌انداخت
    // (حلقهٔ قطع+وصل هر ~۱۲ ثانیه، با اعلانِ «اینترنت مسدود شد» در هر حلقه).
    private long _netSuppressUntil;
    private int _netRecoverRunning;
    private int _netReconnects;
    private bool _netWatcherStarted;

    /// <summary>
    /// رویدادهایِ شبکه را برایِ مدتِ کوتاهِ بعد از هر تغییرِ مسیرِ خودِ برنامه نادیده می‌گیرد.
    /// </summary>
    private void SuppressNetworkWatcher(int ms) =>
        Interlocked.Exchange(ref _netSuppressUntil, Environment.TickCount64 + ms);

    /// <summary>
    /// به تغییرِ شبکه (خاموش/روشن‌کردنِ وای‌فای، عوض‌شدنِ مودم، گرفتنِ آدرسِ تازه) گوش می‌دهد.
    /// فقط یک بار صدا زده می‌شود؛ عمرِ این رویدادها برابرِ عمرِ برنامه است.
    /// </summary>
    private void StartNetworkWatcher()
    {
        if (_netWatcherStarted) return;
        _netWatcherStarted = true;
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch (Exception ex) { AppendLog($"network watcher failed: {ex.Message}"); }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => NoteNetworkChange();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => NoteNetworkChange();

    /// <summary>
    /// فقط وقتی ارزش دارد که وصل باشیم؛ قطعِ دستیِ کاربر هم مسیرها را حذف می‌کند و
    /// همین رویداد را شلیک می‌کند، و نباید وصلِ خودکارِ بعدی را راه بیندازد.
    /// </summary>
    private void NoteNetworkChange()
    {
        if (!IsConnected) return;

        // تغییرِ مسیرِ خودِ برنامه (قطع/وصل، تعویضِ سرور) رویدادِ شبکه شلیک می‌کند؛
        // نباید آن را «شبکه عوض شد» تفسیر کرد.
        if (Environment.TickCount64 < Interlocked.Read(ref _netSuppressUntil)) return;

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _netLastTryTick) > NetTryWindowMs)
            Interlocked.Exchange(ref _netReconnects, 0);

        Interlocked.Exchange(ref _netEventTick, now);

        // تک‌نفره: اگر همین حالا یک بازیابی در جریان است، فقط تایمرش را عقب می‌اندازیم.
        if (Interlocked.CompareExchange(ref _netRecoverRunning, 1, 0) != 0) return;

        // روی رشتهٔ UI می‌رویم؛ Connect/Disconnect و پراپرتی‌های UI همگی رشتهٔ UI می‌خواهند.
        Post(() => _ = RecoverAfterNetworkChangeAsync());
    }

    private async Task RecoverAfterNetworkChangeAsync()
    {
        try
        {
            // ۱) صبر تا رویدادها آرام شوند؛ هر رویدادِ تازه، تایمر را عقب می‌اندازد.
            while (Environment.TickCount64 - Interlocked.Read(ref _netEventTick) < NetDebounceMs)
                await Task.Delay(500);
            if (!IsConnected) return;

            // ۲) چند ثانیه دیگر هم صبر کن تا DHCP/لینک تثبیت شود.
            await Task.Delay(NetSettleMs);
            if (!IsConnected) return;

            // ۲٫۵) اصلاً مشکلی داریم؟ اگر همین حالا از مسیرِ خودِ تونل به اینترنت
            // می‌رسیم، پس تونل سالم است و آن رویدادِ شبکه چیزی جز هیاهو نبوده:
            // خودِ اتصال (ساختِ TUN، عوض‌شدنِ مسیرها و DNS)، تعویضِ سرور، یا یک
            // رویدادِ بی‌اهمیتِ دیگر. دست‌زدن به اتصالِ سالم یعنی قطعِ چند ثانیه‌ایِ
            // بی‌جهت — دقیقاً همان چیزی که کاربر از آن شکایت داشت.
            // پروب باید از خودِ پورتِ موتور برود نه مستقیم؛ وگرنه اگر تونل مرده ولی
            // اینترنتِ مستقیم سالم باشد، اشتباه «سالم» می‌خواندیمش و بازیابی را می‌پراندیم.
            int tunnelPort = _engine.HttpPort;
            if (tunnelPort > 0)
            {
                bool tunnelHealthy;
                try { tunnelHealthy = await NetHealth.IsInternetAvailableAsync(NetTunnelCheckMs, tunnelPort); }
                catch { tunnelHealthy = false; }
                if (tunnelHealthy)
                {
                    TunManager.Log("net-change: tunnel still healthy; ignoring network event");
                    return;
                }
            }

            AppendLog(TranslationManager.Instance["Log_NetChanged"]);
            TunManager.Log("net-change: recovery starting");

            // ۳) گیت‌ویِ فیزیکی باید برگردد؛ بدون آن وصل‌کردن بیهوده است.
            // این خواندنِ محلیِ جدولِ مسیر است و بلاکِ WFPِ کیل‌سوییچ رویش اثری ندارد.
            bool gatewayBack = false;
            for (int i = 0; i < NetGatewayTries; i++)
            {
                if (!string.IsNullOrEmpty(await Task.Run(TunManager.GetDefaultGatewayVia)))
                { gatewayBack = true; break; }
                if (!IsConnected) return;
                await Task.Delay(NetGatewayStepMs);
            }
            if (!gatewayBack) { NotifyNetworkDown(); return; }

            // ۴) گیت‌وی هست؛ آیا اینترنتِ مستقیم واقعاً برگشته؟
            // پروب باید با بایپسِ اجباری برود، چون تونلِ فعلی مرده است و پروبِ از مسیرِ
            // همان تونل همیشه شکست می‌خورد. خودِ پروب‌ها را Roxa.exe می‌فرستد که در
            // فهرستِ مجوزهای کیل‌سوییچ هست، پس حتی با بلاکِ WFP هم از شبکهٔ تازه سالم می‌شود.
            // بایپس یک بار ساخته می‌شود و در همهٔ تلاش‌ها می‌ماند؛ ساختنِ آن پروسهٔ
            // route.exe لازم دارد و تکرارش پنج بار، اتصال را کند می‌کرد.
            bool online = false;
            try
            {
                using var bypass = await TestRouteBypass.BeginAsync(NetHealth.ProbeHosts, forceDirect: true);
                for (int i = 0; i < NetOnlineTries; i++)
                {
                    if (await NetHealth.IsInternetAvailableAsync(timeoutMs: 2500)) { online = true; break; }
                    if (!IsConnected) return;
                    await Task.Delay(NetOnlineStepMs);
                }
            }
            catch { }
            if (!online) { NotifyNetworkDown(); return; }

            // ۵) شبکه سالم است ولی تونلِ قبلی مرده؛ همان سرور را دوباره وصل کن.
            if (IsConnecting || IsTesting || IsSwitchingServer) return;

            var server = _engine.CurrentServer ?? SelectedServer;
            if (server == null || !Servers.Contains(server)) return;

            int tries = Interlocked.Increment(ref _netReconnects);
            if (tries > NetMaxReconnects)
            {
                AppendLog(TranslationManager.Instance["Log_NetReconnectGaveUp"]);
                NotifyNetworkDown();
                return;
            }
            Interlocked.Exchange(ref _netLastTryTick, Environment.TickCount64);

            AppendLog(string.Format(TranslationManager.Instance["Log_NetReconnecting"], server.Name));
            TunManager.Log($"net-change: reconnecting to {server.Name} (try {tries}/{NetMaxReconnects})");
            DisconnectCore(false);
            await AwaitCleanupAsync();
            if (IsConnecting || IsTesting || IsSwitchingServer) return;

            SelectedServer = server;
            await Connect();
        }
        catch (Exception ex)
        {
            AppendLog($"network recovery failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _netRecoverRunning, 0);
        }
    }

    /// <summary>
    /// به‌جایِ سکوتِ مرگبار: اگر وصلِ خودکار ممکن نبود، کاربر باید بداند که باید خودش وصل شود.
    /// </summary>
    private void NotifyNetworkDown()
    {
        string msg = TranslationManager.Instance["Toast_NetDownReconnect"];
        Status = msg;
        ToastHandler?.Invoke(msg);
        AppendLog(msg);
    }
}

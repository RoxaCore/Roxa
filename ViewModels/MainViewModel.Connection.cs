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

using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;
using Roxa.Models;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{

    // با تغییر مقدار، رویداد PropertyChanged می‌نشیند تا سربرگ «در حال اتصال…»
    // هنگام اتصال TUN ناپدید نشود و UI از عملیات عقب نماند.
    [ObservableProperty] private bool _isConnecting;

    // وقتی تعویض TUN هنگام اتصال باعث قطع+وصل مجدد می‌شود، به‌جای دو نوتیفیشن
    // «ROXA قطع/وصل شد» و «متصل شد به …»، فقط یک نوتیفیشن ادغام‌شده نشان داده می‌شود.
    private bool? _tunToggleReconnect;

    [ObservableProperty] private bool _isSwitchingServer;

    public bool ShowDisconnectedStatus => !IsConnected && !IsSwitchingServer;

    partial void OnIsSwitchingServerChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDisconnectedStatus));
    }

    private void RefreshConnectionFlags()
    {
        string? activeId = null;
        if (IsConnected && _engine.CurrentServer != null)
            activeId = _engine.CurrentServer.Id;
        foreach (var s in Servers)
            s.IsActiveConnection = activeId != null && string.Equals(s.Id, activeId, StringComparison.Ordinal);
    }

    /// <summary>
    /// اگر اینترنت قطع باشد پیام «اینترنت قطع است» نشان می‌دهد و true برمی‌گرداند تا
    /// به‌جای پیام گمراه‌کنندهٔ «هیچ سروری پاسخ نداد»، دلیل واقعی دیده شود.
    /// </summary>
    private async Task<bool> NoInternetAsync()
    {
        // در حالت اتصال، پروب‌ها از مسیر پروکسی داخلی موتور هم می‌روند؛
        // وگرنه در TUN پروب‌های خامِ پورت ۸۰ وارد تونل می‌شوند و «اینترنت قطع» غلط دیده می‌شود.
        int? proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
        if (await NetHealth.IsInternetAvailableAsync(proxyPort: proxyPort)) return false;

        // بعد از عوض‌شدن شبکه (قطعی WiFi و وصل‌شدن دوباره) اولین پروب اغلب به‌خاطر
        // بالا آمدن DHCP/DNS شکست می‌خورد؛ یک بار دیگر با فاصلهٔ کوتاه.
        await Task.Delay(700);
        if (await NetHealth.IsInternetAvailableAsync(proxyPort: proxyPort, timeoutMs: 2000)) return false;

        // تونلِ بعد از تعویض شبکه مرده ولی مسیر مستقیم سالم است: پروب مستقیم با
        // بایپس (همان مکانیزم مرحلهٔ ۱ «یافتن سرور بهتر»). اگر جواب داد یعنی
        // اینترنت قطع نیست و نباید جلوی کار (مثل جستجو که تونل را تازه می‌کند) گرفته شود.
        if (IsConnected)
        {
            try
            {
                using var bypass = await TestRouteBypass.BeginAsync(NetHealth.ProbeHosts, forceDirect: true);
                if (await NetHealth.IsInternetAvailableAsync(timeoutMs: 2000))
                {
                    AppendLog(TranslationManager.Instance["Log_TunnelDeadDirectOk"]);
                    return false;
                }
            }
            catch { }
        }

        string offline = TranslationManager.Instance["Toast_NoInternet"];
        Status = offline;
        ToastHandler?.Invoke(offline);
        AppendLog(offline);
        return true;
    }


    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        var server = SelectedServer;
        bool? tunReconnect = _tunToggleReconnect;
        _tunToggleReconnect = null;
        if (server == null || !Servers.Contains(server)) return;
        if (IsConnecting) return;
        IsConnecting = true;
        ConnectCommand.NotifyCanExecuteChanged();
        try
        {
            string issue = server.TransportIssue;
            if (issue.Length > 0)
            {
                Status = issue;
                ToastHandler?.Invoke(issue);
                AppendLog(string.Format(TranslationManager.Instance["Log_Error"], issue));
                if (tunReconnect != null) NotificationHelper.ShowError(issue);
                return;
            }
            // قطع‌بودن اینترنت را قبل از شروع اتصال (و اتصال‌مجددِ تعویض TUN) بگوی؛
            // وگرنه موتور بالا می‌آید و کاربر فکر می‌کند وصل است.
            if (await NoInternetAsync()) return;
            Status = TranslationManager.Instance["Status_Connecting"];
            await _engine.StartAsync(server, SetSystemProxy, _settings, tunExclusionHosts: Servers.Select(s => s.Address),
                progress: s => Post(() => Status = s));
            IsConnected = true;
            // خودِ اتصال رویدادِ شبکه شلیک می‌کند (ساختِ TUN، عوض‌شدنِ مسیرها، سوییچِ DNS).
            // ویندوز آن را با تأخیرِ کم اعلام می‌کند — یعنی وقتی IsConnected تازه true شده
            // هنوز در صفِ تحویل است. اگر پنجرهٔ محافظ همین‌جا شروع نشود، گوشِ شبکه همان
            // رویداد را «شبکه عوض شد» می‌فهمد و چند ثانیه بعد وصلِ مجددِ خودکار راه
            // می‌انداخت: هم در اتصالِ اولِ هر اجرای برنامه، هم بعد از تعویضِ سرور.
            SuppressNetworkWatcher(NetSelfChangeSuppressMs);
            TunKillSwitch.SetTunnelUp(true, _engine.HttpPort);
            if (!IsTesting && SetSystemProxy && !LeaveSystemProxyAsIs && !_engine.IsTunActive)
                ApplySystemProxyEndpoint();
            if (_engine.IsTunActive)
            {
                // SweepPersistedPins داخل Start روی رشتهٔ UI و در قفل اجرا می‌شد و
                // می‌توانست با route.exe چند ثانیه سربرگ را نگه دارد؛ به پس‌زمینه رفت.
                var tunExcluded = _settings.TunExcludedApps?.ToList();
                _ = Task.Run(() => TunExclusion.Start(tunExcluded));
            }
            LiveDownSpeed = "0 B/s";
            LiveUpSpeed = "0 B/s";
            SpeedBlinkOpacity = 1.0;
            RefreshConnectionFlags();
            Status = string.Format(TranslationManager.Instance["Status_ConnectedTo"], server.Name);
            // اگر گزارشی در صف مانده (مثلاً کرشِ هنگام قطع شبکه)، حالا از مسیر تونل ارسال شود.
            _ = Task.Run(async () =>
            {
                try { await Core.BugReport.FlushPendingAsync(_engine.HttpPort); } catch { }
            });
            _settings.ActiveServerId = server.Id;
            _settings.ConnectCount++;
            SaveSettings();
            AppendLog(string.Format(TranslationManager.Instance["Log_Connected"], _engine.HttpPort));
            if (tunReconnect is bool tunOn)
            {
                string tunLine = TranslationManager.Get(tunOn ? "Notify_TunOn" : "Notify_TunOff");
                string connLine = string.Format(TranslationManager.Get("Notify_Connected"), server.Name, server.LatencyDisplay);
                NotificationHelper.Show("Roxa", tunLine + "\n" + connLine);
            }
            else
                NotificationHelper.ShowConnected(server.Name, server.LatencyDisplay);

            // Initialize byte counters for speed calculation
            _ = Task.Run(() =>
            {
                try
                {
                    var ni = FindActiveNetworkInterface();
                    if (ni != null)
                    {
                        _lastInterfaceId = ni.Id;
                        var stats = ni.GetIPStatistics();
                        _lastBytesIn = stats.BytesReceived;
                        _lastBytesOut = stats.BytesSent;
                    }
                }
                catch { }
            });

            // آپدیت تأخیر واقعی بعد از اتصال
            _ = Task.Run(async () =>
            {
                try
                {
                    using var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{_engine.HttpPort}"), UseProxy = true };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                    var sw = Stopwatch.StartNew();
                    using var resp = await http.GetAsync("http://www.gstatic.com/generate_204");
                    sw.Stop();
                    if (resp.IsSuccessStatusCode)
                    {
                        int ms = (int)sw.ElapsedMilliseconds;
                        Post(() => { if (server.RealDelayMs <= 0) server.RecordRealDelay(ms); });
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            IsConnected = false;
            RefreshConnectionFlags();
            Status = TranslationManager.Instance["Status_ConnectError"];
            NotificationHelper.ShowError(TranslationManager.Get("Notify_ConnectFailed"));
            AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
            FlushLog();
        }
        finally { IsConnecting = false; ConnectCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanConnect() => SelectedServer != null && !IsConnected && !IsTesting && !IsConnecting;

    [RelayCommand]
    private void Disconnect() => DisconnectCore(true);

    private void DisconnectCore(bool notify)
    {
        // kill پروسه، حذف مسیرها و بازگردانی DNS با powershell چند ثانیه طول می‌کشد؛
        // اجرای همزمان آن روی رشتهٔ UI، قطع اتصال دستی/تعویض سرور/اتصال هوشمند را
        // می‌گرفت. اکنون در پس‌زمینهٔ LongRunning اجرا می‌شود و گیت داخلی Stop()
        // هم‌زمانی آن با پاکسازی StartAsync را سریالایز می‌کند.
        _cleanupTask = Task.Factory.StartNew(() => { try { _engine.StopAndCleanupTun(); } catch { } }, TaskCreationOptions.LongRunning);
        // حذفِ مسیرها و بازگردانیِ DNS رویدادِ شبکه شلیک می‌کند؛ بدونِ این پنجره،
        // گوشِ شبکه همان «شبکه عوض شد» تفسیرش می‌کرد و وصلِ مجددِ خودکار راه می‌افتاد.
        SuppressNetworkWatcher(NetSelfChangeSuppressMs);
        TunKillSwitch.SetTunnelUp(false, 0);
        IsConnected = false;
        RefreshConnectionFlags();
        AppendLog(TranslationManager.Instance["Log_Disconnected"]);
        if (notify) NotificationHelper.ShowDisconnected();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private Task? _cleanupTask;

    /// <summary>
    /// تا پاکسازیِ پس‌زمینهٔ اتصال قبلی تمام شود صبر می‌کند؛ بدون آن، پینگ/تست مرحلهٔ ۱
    /// اتصال هوشمند با DNSِ مردهٔ تونل و مسیرهای در حال حذف هم‌زمان می‌شود و ثانیه‌ها می‌ایستد.
    /// </summary>
    private async Task AwaitCleanupAsync()
    {
        var t = _cleanupTask;
        if (t == null || t.IsCompleted) return;
        Status = TranslationManager.Instance["Stg_Cleanup"];
        try { await t; } catch { }
    }

    private static readonly TimeSpan RestartCleanupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// قطعِ کاملِ اتصال پیش از ری‌استارت. DisconnectCore پاکسازی را در پس‌زمینه رها
    /// می‌کند و بلافاصله برمی‌گردد؛ اینجا تا پایانِ حذفِ مسیرها، بازگردانیِ DNS و
    /// بستنِ موتور صبر می‌کنیم تا نمونهٔ بعدی روی TUN و WFPِ نیمه‌کاره بالا نیاید.
    /// سقفِ زمانی دارد تا ری‌استارت هرگز به خاطرِ گیر کردنِ پاکسازی متوقف نشود.
    /// </summary>
    public async Task PrepareRestartAsync()
    {
        if (IsConnected) DisconnectCore(false);
        var t = _cleanupTask;
        if (t == null || t.IsCompleted) return;
        try { await Task.WhenAny(t, Task.Delay(RestartCleanupTimeout)); }
        catch { }
    }

    [RelayCommand]
    private async Task Reconnect()
    {
        if (SelectedServer == null) return;
        if (IsConnected) Disconnect();
        await AwaitCleanupAsync();
        await Connect();
    }

    [RelayCommand]
    private async Task ConnectServer(ServerInfo? server)
    {
        if (server != null) SelectedServer = server;
        RefreshConnectionFlags();
        if (SelectedServer == null || IsTesting || IsConnecting) return;
        bool switching = false;
        try
        {
            if (IsConnected)
            {
                var active = _engine.CurrentServer;
                if (active != null && string.Equals(active.Id, SelectedServer.Id, StringComparison.Ordinal)) return;
                switching = true;
                IsSwitchingServer = true;
                Disconnect();
                await AwaitCleanupAsync();
            }
            await Connect();
        }
        finally { if (switching) IsSwitchingServer = false; }
    }

    // ---------- تست ----------

    [RelayCommand]
    private void ToggleTun() => UseTunMode = !UseTunMode;

    [RelayCommand]
    private void ToggleSystemProxy() => SetSystemProxy = !SetSystemProxy;

    [RelayCommand]
    private async Task SmartConnect()
    {
        if (IsTesting) return;
        if (Servers.Count == 0) { Status = TranslationManager.Instance["Status_NoServers"]; ToastHandler?.Invoke(TranslationManager.Instance["Toast_NoServerForConnect"]); return; }
        if (await NoInternetAsync()) return;
        if (IsConnected) Disconnect();

        var ct = NewTestToken();
        IsTesting = true;
        IsSmartConnecting = true;

        try
        {
            // پاکسازی اتصال قبلی (kill + حذف مسیرها + بازگردانی DNS) باید تمام شود؛
            // وگرنه پینگ‌های مرحلهٔ ۱ با DNSِ مردهٔ تونل هم‌زمان می‌شوند و ثانیه‌ها می‌ایستند.
            await AwaitCleanupAsync();

            int totalServers = Servers.Count;
            int tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = totalServers;
            OnPropertyChanged(nameof(SearchProgressText));

            // --- مرحله ۱: پینگ سریع TCP روی همه سرورها ---
            Status = TranslationManager.Instance["Status_SmartStage1"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_FindingBest"]);
            AppendLog(TranslationManager.Instance["Log_SmartStage1"]);

            // یک scope مشترک برای همهٔ پینگ‌ها: قبلاً هر پینگ جداگانه route print +
            // resolve + افزودن/حذف مسیر اجرا می‌کرد و شروع اتصال هوشمند می‌خوابید.
            using var bypassScope = await TestRouteBypass.BeginAsync(Servers.Select(s => (string?)s.Address));

            foreach (var s in Servers) { s.LatencyMs = -1; s.PingMs = -1; }

            using var sem1 = new SemaphoreSlim(20);
            var pingTasks = Servers.ToList().Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem1.WaitAsync(ct);
                try
                {
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port, 3000, bypassScope);
                    Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally
                {
                    try { sem1.Release(); } catch { }
                    tested++;
                    Post(() => { SearchTestedCount = tested; OnPropertyChanged(nameof(SearchProgressText)); });
                }
            });
            try { await Task.WhenAll(pingTasks); }
            catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            // صبر تا همه Post() ها اجرا بشن
            await Dispatcher.UIThread.InvokeAsync(() => { });
            AutoSortAfterTest("latency");

            // --- انتخاب ۱۲ کاندید برتر ---
            var candidates = Servers.Where(s => s.LatencyMs > 0).OrderBy(s => s.LatencyMs).Take(12).ToList();

            // ذخیره مابقی سرورها برای جستجوی تدریجی
            var candidateIds = new HashSet<string>(candidates.Select(c => c.Id));
            _searchPool = Servers.Where(s => !candidateIds.Contains(s.Id)).ToList();
            _lastSearchTime = DateTime.Now;
            AppendLog(string.Format(TranslationManager.Instance["Log_SearchPool"], _searchPool.Count));

            // آپدیت تعداد کل برای فاز تأخیر واقعی
            tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = candidates.Count;
            OnPropertyChanged(nameof(SearchProgressText));

            if (candidates.Count == 0)
            {
                IsTesting = false;
                IsSmartConnecting = false;
                if (await NoInternetAsync()) return;
                Status = TranslationManager.Instance["Status_SmartNoResponse"];
                ToastHandler?.Invoke(TranslationManager.Instance["Toast_NoServerResponded"]);
                AppendLog(TranslationManager.Instance["Log_NoPing"]);
                return;
            }

            AppendLog(string.Format(TranslationManager.Instance["Log_SmartCandidates"], candidates.Count));

            // --- مرحله ۲: تأخیر واقعی batch (multi-outbound) ---
            Status = string.Format(TranslationManager.Instance["Status_SmartRealDelay"], candidates.Count);
            AppendLog(string.Format(TranslationManager.Instance["Log_SmartTesting"], candidates.Count));

            foreach (var c in candidates) { c.RealDelayMs = -1; c.RealDelaySuccessRate = -1; c.RealDelayJitter = 0; }

            try
            {
                await RealDelayTester.TestBatchAsync(candidates, ct, (s, result) =>
                {
                    Post(() => s.RecordRealDelay(result));
                    int done = Interlocked.Increment(ref tested);
                    Post(() => { SearchTestedCount = done; OnPropertyChanged(nameof(SearchProgressText)); });
                });
            }
            catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            await Dispatcher.UIThread.InvokeAsync(() => { });

            // --- انتخاب نهایی: کمترین تأخیر واقعی ---
            var finalBest = candidates
                .Where(s => s.RealDelayMs > 0 && s.LatencyMs > 0)
                .OrderBy(s => s.RealDelayMs)
                .ThenBy(s => s.LatencyMs)
                .FirstOrDefault();

            IsTesting = false;
            IsSmartConnecting = false;

            if (finalBest == null)
            {
                Status = TranslationManager.Instance["Status_SmartRealFailed"];
                ToastHandler?.Invoke(TranslationManager.Instance["Toast_RealDelayFailed"]);
                NotificationHelper.ShowError(TranslationManager.Get("Notify_SmartRealDelayFailed"));
                AppendLog(TranslationManager.Instance["Log_SmartFailedAll"]);
                return;
            }

            Status = string.Format(TranslationManager.Instance["Status_SmartBest"], finalBest.Name, finalBest.LatencyDisplay);
            ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Toast_ConnectingTo"], finalBest.Name, finalBest.LatencyDisplay));
            NotificationHelper.ShowSmartConnect(finalBest.Name, finalBest.LatencyDisplay);
            AppendLog(string.Format(TranslationManager.Instance["Log_SmartConnect"], finalBest.Name));

            SelectedServer = finalBest;
            RefreshConnectionFlags();
            await Connect();
            FocusServerHandler?.Invoke(finalBest);
        }
        catch (OperationCanceledException)
        {
            IsTesting = false;
            IsSmartConnecting = false;
            Status = TranslationManager.Instance["Status_SmartCancelled"];
        }
        catch (Exception ex)
        {
            IsTesting = false;
            IsSmartConnecting = false;
            Status = string.Format(TranslationManager.Instance["Log_Error"], ex.Message);
            AppendLog(string.Format(TranslationManager.Instance["Log_SmartError"], ex.Message));
        }
        finally { FlushLog(); }
    }

    // ---------- یافتن سرور بهتر ----------
    [RelayCommand]
    private async Task FindBetterServer()
    {
        if (IsTesting)
        {
            Status = TranslationManager.Instance["Status_FindBusy"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_AnotherTestRunning"]);
            AppendLog(TranslationManager.Instance["Log_FindBusy"]);
            return;
        }
        if (!IsConnected)
        {
            Status = TranslationManager.Instance["Status_FindNotConnected"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_FirstConnect"]);
            AppendLog(TranslationManager.Instance["Log_FindNotConnected"]);
            return;
        }
        if (SelectedServer == null)
        {
            Status = TranslationManager.Instance["Status_FindNoServer"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_SelectServerFirst"]);
            AppendLog(TranslationManager.Instance["Log_FindNoSelection"]);
            return;
        }
        // اینترنت قطع است؟ قبل از اتلافِ وقتِ تست بگو تا «هیچ سروری پاسخ نداد» دیده نشود.
        if (await NoInternetAsync()) return;

        // بررسی انقضا: اگه ۱۵ دقیقه گذشته، از اول شروع کن
        if (_searchPool.Count == 0 || (DateTime.Now - _lastSearchTime) > SearchExpiry)
        {
            AppendLog(TranslationManager.Instance["Log_PoolExpired"]);
            _searchPool = Servers.Where(s => s.Id != SelectedServer.Id).ToList();
            // shuffle for variety
            var rng = new Random();
            _searchPool = _searchPool.OrderBy(_ => rng.Next()).ToList();
        }

        // برداشتن batch بعدی
        var batch = _searchPool.Take(SearchBatchSize).ToList();
        _searchPool.RemoveRange(0, Math.Min(SearchBatchSize, _searchPool.Count));
        _lastSearchTime = DateTime.Now;

        if (batch.Count == 0)
        {
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_AllChecked"]);
            AppendLog(TranslationManager.Instance["Log_FindNoMore"]);
            return;
        }

        var ct = NewTestToken();
        IsTesting = true;
        IsFindingBetter = true;

        try
        {
            int totalTests = batch.Count * 2;
            int tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = totalTests;
            OnPropertyChanged(nameof(SearchProgressText));

            Status = string.Format(TranslationManager.Instance["Status_FindTestingBatch"], batch.Count);
            AppendLog(string.Format(TranslationManager.Instance["Log_FindBetter"], batch.Count, _searchPool.Count));
            ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Toast_CheckingNew"], batch.Count));

            // --- مرحله ۱: TCP ping سریع روی batch ---
            // scope مشترک برای کل batch؛ قبلاً هر پینگ جداگانه route/resolve اجرا می‌کرد.
            int invalidDomains = 0;

            static async Task<bool> IsHostUnresolvableAsync(string? address)
            {
                string host = (address ?? "").Trim();
                if (host.Length == 0) return true;
                if (System.Net.IPAddress.TryParse(host, out _)) return false;
                try
                {
                    var resolved = await System.Net.Dns.GetHostAddressesAsync(host);
                    return resolved.Length == 0;
                }
                catch (System.Net.Sockets.SocketException) { return true; }
                catch (ArgumentException) { return true; }
            }

            using var bypassScope = await TestRouteBypass.BeginAsync(batch.Select(s => (string?)s.Address));
            using var sem = new SemaphoreSlim(10);
            var pingTasks = batch.Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    if (await IsHostUnresolvableAsync(s.Address))
                    {
                        Interlocked.Increment(ref invalidDomains);
                        AppendLog(string.Format(TranslationManager.Instance["Log_BatchDomainInvalid"], s.Address));
                        Post(() => s.RecordPing(-2));
                        return;
                    }
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port, 3000, bypassScope);
                    Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally
                {
                    try { sem.Release(); } catch { }
                    tested++;
                    Post(() => { SearchTestedCount = tested; OnPropertyChanged(nameof(SearchProgressText)); });
                }
            });
            try { await Task.WhenAll(pingTasks); }
            catch (OperationCanceledException) { }

            await Dispatcher.UIThread.InvokeAsync(() => { });
            AutoSortAfterTest("latency");

            // فیلتر: فقط سرورهایی که TCP موفق داشتن
            var survivors = batch.Where(s => s.LatencyMs > 0).ToList();
            if (survivors.Count == 0)
            {
                IsTesting = false;
                IsFindingBetter = false;
                if (await NoInternetAsync()) return;
                Status = TranslationManager.Instance["Status_FindNoCandidate"];
                bool allInvalid = invalidDomains >= batch.Count;
                ToastHandler?.Invoke(TranslationManager.Instance[allInvalid ? "Toast_BatchAllInvalidDomains" : "Toast_NoServerInBatch"]);
                AppendLog(TranslationManager.Instance["Log_FindNoSurvivors"]);
                return;
            }

            AppendLog(string.Format(TranslationManager.Instance["Log_FindSurvivors"], survivors.Count));

            // --- مرحله ۲: تأخیر واقعی batch (multi-outbound) ---
            AppendLog(string.Format(TranslationManager.Instance["Log_FindRealBatch"], survivors.Count));

            foreach (var c in survivors) { c.RealDelayMs = -1; c.RealDelaySuccessRate = -1; c.RealDelayJitter = 0; }

            try
            {
                await RealDelayTester.TestBatchAsync(survivors, ct, (s, result) =>
                {
                    Post(() => s.RecordRealDelay(result));
                    int done = Interlocked.Increment(ref tested);
                    Post(() => { SearchTestedCount = done; OnPropertyChanged(nameof(SearchProgressText)); });
                });
            }
            catch (OperationCanceledException) { }

            // اجرای Post های باقی‌مانده UI قبل از مقایسه
            await Dispatcher.UIThread.InvokeAsync(() => { });

            var bestCandidate = survivors
                .Where(s => s.RealDelayMs > 0)
                .OrderBy(s => s.RealDelayMs)
                .ThenBy(s => s.LatencyMs)
                .FirstOrDefault();
            int bestMs = bestCandidate?.RealDelayMs ?? int.MaxValue;

            // --- مقایسه با سرور فعلی ---
            if (bestCandidate != null && bestMs < int.MaxValue)
            {
                int currentMs = SelectedServer.RealDelayMs > 0 ? SelectedServer.RealDelayMs
                    : SelectedServer.LatencyMs > 0 ? SelectedServer.LatencyMs : 999;

                if (bestMs < currentMs)
                {
                    // سرور بهتر پیدا شد → سوییچ
                    AppendLog(string.Format(TranslationManager.Instance["Log_FindSwitch"], SelectedServer.Name, currentMs, bestCandidate.Name, bestMs));
                    ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Toast_SwitchTo"], bestCandidate.Name, bestMs));

                    Disconnect();
                    SelectedServer = bestCandidate;
                    RefreshConnectionFlags();
                    await AwaitCleanupAsync();
                    await Connect();
                    AutoSortAfterTest("latency");
                    FocusServerHandler?.Invoke(bestCandidate);
                }
                else
                {
                    AppendLog(TranslationManager.Instance["Log_FindBest"]);
                    ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Toast_StillBest"], currentMs));
                    Status = string.Format(TranslationManager.Instance["Status_FindStillBest"], currentMs);
                }
            }
            else
            {
                Status = TranslationManager.Instance["Status_FindRealFailed"];
                ToastHandler?.Invoke(TranslationManager.Instance["Toast_RealDelayNoConfirm"]);
                AppendLog(TranslationManager.Instance["Log_FindRealFailedAll"]);
            }
        }
        catch (OperationCanceledException)
        {
            Status = TranslationManager.Instance["Status_FindCancelled"];
            ToastHandler?.Invoke(TranslationManager.Instance["Status_FindCancelled"]);
        }
        catch (Exception ex)
        {
            Status = string.Format(TranslationManager.Instance["Log_Error"], ex.Message);
            AppendLog(string.Format(TranslationManager.Instance["Log_FindError"], ex.Message));
        }
        finally
        {
            IsTesting = false;
            IsFindingBetter = false;
            FlushLog();
        }
    }


}

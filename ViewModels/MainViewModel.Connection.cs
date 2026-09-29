using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XrayClient.Core;
using XrayClient.Localization;
using XrayClient.Models;

namespace XrayClient.ViewModels;

public partial class MainViewModel : ViewModelBase
{

    private bool _isConnecting;

    private void RefreshConnectionFlags()
    {
        string? activeId = null;
        if (IsConnected && _engine.CurrentServer != null)
            activeId = _engine.CurrentServer.Id;
        foreach (var s in Servers)
            s.IsActiveConnection = activeId != null && string.Equals(s.Id, activeId, StringComparison.Ordinal);
    }


    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        var server = SelectedServer;
        if (server == null || !Servers.Contains(server)) return;
        if (_isConnecting) return;
        _isConnecting = true;
        ConnectCommand.NotifyCanExecuteChanged();
        try
        {
            Status = TranslationManager.Instance["Status_Connecting"];
            await _engine.StartAsync(server, SetSystemProxy, _settings, tunExclusionHosts: Servers.Select(s => s.Address));
            IsConnected = true;
            if (_engine.IsTunActive)
                TunExclusion.Start(_settings.TunExcludedApps);
            LiveDownSpeed = "0 B/s";
            LiveUpSpeed = "0 B/s";
            SpeedBlinkOpacity = 1.0;
            RefreshConnectionFlags();
            Status = string.Format(TranslationManager.Instance["Status_ConnectedTo"], server.Name);
            _settings.ActiveServerId = server.Id;
            SaveSettings();
            AppendLog(string.Format(TranslationManager.Instance["Log_Connected"], _engine.HttpPort));
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
        finally { _isConnecting = false; ConnectCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanConnect() => SelectedServer != null && !IsConnected && !IsTesting && !_isConnecting;

    [RelayCommand]
    private void Disconnect()
    {
        _engine.StopAndCleanupTun();
        IsConnected = false;
        RefreshConnectionFlags();
        AppendLog(TranslationManager.Instance["Log_Disconnected"]);
        NotificationHelper.ShowDisconnected();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Reconnect()
    {
        if (SelectedServer == null) return;
        if (IsConnected) Disconnect();
        await Connect();
    }

    [RelayCommand]
    private async Task ConnectServer(ServerInfo? server)
    {
        if (server != null) SelectedServer = server;
        RefreshConnectionFlags();
        if (SelectedServer == null || IsTesting || _isConnecting) return;
        if (IsConnected)
        {
            var active = _engine.CurrentServer;
            if (active != null && string.Equals(active.Id, SelectedServer.Id, StringComparison.Ordinal)) return;
            Disconnect();
        }
        await Connect();
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
        if (IsConnected) Disconnect();

        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        var ct = _testCts.Token;
        IsTesting = true;
        IsSmartConnecting = true;

        try
        {
            int totalServers = Servers.Count;
            int tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = totalServers;
            OnPropertyChanged(nameof(SearchProgressText));

            // --- مرحله ۱: پینگ سریع TCP روی همه سرورها ---
            Status = TranslationManager.Instance["Status_SmartStage1"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_FindingBest"]);
            AppendLog(TranslationManager.Instance["Log_SmartStage1"]);

            foreach (var s in Servers) { s.LatencyMs = -1; s.PingMs = -1; }

            using var sem1 = new SemaphoreSlim(20);
            var pingTasks = Servers.ToList().Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem1.WaitAsync(ct);
                try
                {
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port, 3000);
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

        var oldCts2 = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts2?.Dispose();
        var ct = _testCts.Token;
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
            using var sem = new SemaphoreSlim(10);
            var pingTasks = batch.Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port, 3000);
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
                Status = TranslationManager.Instance["Status_FindNoCandidate"];
                ToastHandler?.Invoke(TranslationManager.Instance["Toast_NoServerInBatch"]);
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

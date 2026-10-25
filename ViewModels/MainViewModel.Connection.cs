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

    // When the value changes, the PropertyChanged event fires so the «در حال اتصال…» header
    // does not disappear while TUN connects and the UI does not fall behind the operation.
    [ObservableProperty] private bool _isConnecting;

    // When toggling TUN while connected causes a disconnect+reconnect, instead of two
    // notifications («ROXA قطع/وصل شد» and «متصل شد به …») a single merged notification is shown.
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
    /// If the internet is down it shows the «اینترنت قطع است» message and returns true so
    /// that the real reason is visible instead of the misleading «هیچ سروری پاسخ نداد» message.
    /// </summary>
    private async Task<bool> NoInternetAsync()
    {
        // While connected, the probes also go through the engine's internal proxy path;
        // otherwise in TUN mode the raw port-80 probes enter the tunnel and «اینترنت قطع» is wrongly reported.
        int? proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
        if (await NetHealth.IsInternetAvailableAsync(proxyPort: proxyPort)) return false;

        // After a network change (WiFi dropping and reconnecting) the first probe often fails because
        // DHCP/DNS is still coming up; try once more after a short delay.
        await Task.Delay(700);
        if (await NetHealth.IsInternetAvailableAsync(proxyPort: proxyPort, timeoutMs: 2000)) return false;

        // The tunnel is dead after a network switch but the direct route is healthy: a direct probe with
        // bypass (the same mechanism as stage 1 «یافتن سرور بهتر»). If it answers, the
        // internet is not down and work (such as a search that refreshes the tunnel) must not be blocked.
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
        if (server == null) return;
        if (!Servers.Contains(server))
        {
            // A node replaced by a subscription refresh must not swallow the request silently:
            // find-better would disconnect to switch and then nothing happened, which looks to
            // the user like the app "only ever reconnects to the same server".
            TunManager.Log($"connect refused: {server.Address}:{server.Port} is no longer in the list");
            return;
        }
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
            // Report the dead internet before starting the connection (and the TUN-toggle reconnect);
            // otherwise the engine comes up and the user thinks it is connected.
            if (await NoInternetAsync()) return;
            Status = TranslationManager.Instance["Status_Connecting"];
            await _engine.StartAsync(server, SetSystemProxy, _settings, tunExclusionHosts: Servers.Select(s => s.Address),
                progress: s => Post(() => Status = s));
            IsConnected = true;
            // The connection itself fires a network event (TUN creation, route changes, DNS switch).
            // Windows reports it with a short delay — meaning when IsConnected has just become true it is
            // still in the delivery queue. If the guard window is not started right here, the network listener reads that
            // event as «شبکه عوض شد» and seconds later kicks off an automatic reconnect: both on the
            // first connection of every program run, and after switching servers.
            SuppressNetworkWatcher(NetSelfChangeSuppressMs);
            TunKillSwitch.SetTunnelUp(true, _engine.HttpPort);
            if (!IsTesting && SetSystemProxy && !LeaveSystemProxyAsIs && !_engine.IsTunActive)
                ApplySystemProxyEndpoint();
            if (_engine.IsTunActive)
            {
                // SweepPersistedPins ran inside Start on the UI thread and under a lock and
                // could hold the header for several seconds with route.exe; it moved to the background.
                var tunExcluded = _settings.TunExcludedApps?.ToList();
                _ = Task.Run(() => TunExclusion.Start(tunExcluded));
            }
            LiveDownSpeed = "0 B/s";
            LiveUpSpeed = "0 B/s";
            SpeedBlinkOpacity = 1.0;
            RefreshConnectionFlags();
            Status = string.Format(TranslationManager.Instance["Status_ConnectedTo"], server.Name);
            // If a report is still pending (e.g. a crash during a network drop), send it now through the tunnel.
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

            // Update the real delay after connecting
            _ = Task.Run(async () =>
            {
                try
                {
                    using var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{_engine.HttpPort}"), UseProxy = true };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                    var sw = Stopwatch.StartNew();
                    using var resp = await http.GetAsync("https://www.gstatic.com/generate_204");
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
        // Killing the process, removing routes and restoring DNS via powershell takes several
        // seconds; running it synchronously on the UI thread used to stall manual disconnect,
        // server switching and smart connect. It now runs on a LongRunning background thread and
        // the internal Stop() gate serializes it with StartAsync's cleanup.
        _cleanupTask = Task.Factory.StartNew(() => { try { _engine.StopAndCleanupTun(); } catch { } }, TaskCreationOptions.LongRunning);
        // Removing routes and restoring DNS fires a network event; without this window,
        // the network listener would interpret it as «شبکه عوض شد» and start an automatic reconnect.
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
    /// Waits for the previous connection's background cleanup to finish; without it, the
    /// stage-1 ping/test of smart connect overlaps the tunnel's dead DNS and the routes being removed and stalls for seconds.
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
    /// Fully disconnects before the restart. DisconnectCore leaves the cleanup running in the
    /// background and returns immediately; here we wait until route removal, DNS restore and
    /// engine shutdown finish so the next instance does not start on a half-torn-down TUN and WFP.
    /// It has a timeout so the restart never hangs on a stuck cleanup.
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

    // ---------- Test ----------

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
            // Cleanup of the previous connection (kill + route removal + DNS restore) must finish;
            // otherwise stage 1 pings race the tunnel's dead DNS and stall for seconds.
            await AwaitCleanupAsync();

            int totalServers = Servers.Count;
            int tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = totalServers;
            OnPropertyChanged(nameof(SearchProgressText));

            // --- Stage 1: fast TCP ping on all servers ---
            Status = TranslationManager.Instance["Status_SmartStage1"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_FindingBest"]);
            AppendLog(TranslationManager.Instance["Log_SmartStage1"]);

            // One shared scope for all the pings: previously each ping ran route print +
            // resolve + add/remove route on its own, and smart connect startup would stall.
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

            // Wait until all Post() calls have run
            await Dispatcher.UIThread.InvokeAsync(() => { });
            AutoSortAfterTest("latency");

            // --- Pick the top 12 candidates ---
            var candidates = Servers.Where(s => s.LatencyMs > 0).OrderBy(s => s.LatencyMs).Take(12).ToList();

            // Save the remaining servers for the gradual search
            var candidateIds = new HashSet<string>(candidates.Select(c => c.Id));
            _searchPool = Servers.Where(s => !candidateIds.Contains(s.Id)).ToList();
            _lastSearchTime = DateTime.Now;
            AppendLog(string.Format(TranslationManager.Instance["Log_SearchPool"], _searchPool.Count));

            // Update the total count for the real-delay phase
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

            // --- Stage 2: batch real delay (multi-outbound) ---
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

            // --- Final selection: lowest real delay ---
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

    // ---------- Finding a better server ----------
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
        // Internet down? Say so before wasting time on the test, so «no server responded» never shows.
        if (await NoInternetAsync()) return;

        // A refresh (or any other change of the list) replaces ServerInfo objects; the pool
        // would keep testing detached copies — their ping and delay results never reach the
        // table (the columns stay "—") and switching to one is refused by Connect(). Drop
        // anything that is no longer listed before the expiry check below.
        int stale = _searchPool.RemoveAll(s => !Servers.Contains(s));
        if (stale > 0)
            TunManager.Log($"find-better: dropped {stale} stale pool entries (the server list changed)");

        // Expiry check: if 15 minutes have passed, start over
        if (_searchPool.Count == 0 || (DateTime.Now - _lastSearchTime) > SearchExpiry)
        {
            AppendLog(TranslationManager.Instance["Log_PoolExpired"]);
            _searchPool = Servers.Where(s => s.Id != SelectedServer.Id).ToList();
            // shuffle for variety
            var rng = new Random();
            _searchPool = _searchPool.OrderBy(_ => rng.Next()).ToList();
        }

        // Take the next batch
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

            // --- Stage 1: fast TCP ping on the batch ---
            // One shared scope for the whole batch; previously each ping ran route/resolve on its own.
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

            // Filter: only the servers that had a successful TCP ping
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

            // --- Stage 2: batch real delay (multi-outbound) ---
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

            // Run the remaining UI Post() calls before comparing
            await Dispatcher.UIThread.InvokeAsync(() => { });

            var bestCandidate = survivors
                .Where(s => s.RealDelayMs > 0)
                .OrderBy(s => s.RealDelayMs)
                .ThenBy(s => s.LatencyMs)
                .FirstOrDefault();
            int bestMs = bestCandidate?.RealDelayMs ?? int.MaxValue;

            // --- Compare against the current server ---
            if (bestCandidate != null && bestMs < int.MaxValue)
            {
                int currentMs = SelectedServer.RealDelayMs > 0 ? SelectedServer.RealDelayMs
                    : SelectedServer.LatencyMs > 0 ? SelectedServer.LatencyMs : 999;

                if (bestMs < currentMs)
                {
                    // Better server found → switch
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

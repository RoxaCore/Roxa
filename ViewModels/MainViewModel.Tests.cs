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
using System.Net.Http;
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

    private void BeginTestProgress(int total)
    {
        TestTotalCount = total;
        TestTestedCount = 0;
        RefreshTestsBypassingTun();
    }

    private void TickTestProgress()
    {
        Post(() =>
        {
            if (TestTestedCount < TestTotalCount)
                TestTestedCount++;
        });
    }

    private void EndTestProgress()
    {
        RefreshTestsBypassingTun();
        Post(() =>
        {
            if (TestTotalCount > 0)
                TestTestedCount = TestTotalCount;
            if (Core.TestRouteBypass.IsBypassing && !string.IsNullOrEmpty(Status)
                && !Status.Contains(TranslationManager.Instance["Test_BypassTun"]))
                Status += " — " + TranslationManager.Instance["Test_BypassTun"];
        });
    }

    /// <summary>
    /// Concurrency cap for the "full test" — deliberately lower than the single-stage test.
    /// The speed stage builds a fresh xray process per server; raising it causes a
    /// sudden CPU/RAM spike and slows the whole system down.
    /// </summary>
    private const int FullTestPingConcurrency = 4;
    private const int FullTestSpeedConcurrency = 2;

    /// <summary>Delay between starting back-to-back servers (milliseconds) so connections/processes come up staggered.</summary>
    private const int FullTestStaggerMs = 75;

    /// <summary>Pause between the three stages so the UI thread can breathe and the previous stage's processes close.</summary>
    private const int FullTestSettleMs = 300;

    /// <summary>
    /// Runs the items staggered, with a concurrency cap and a short delay between starts.
    /// Unlike <c>Task.WhenAll</c> over the whole list, the tasks are not all built at
    /// once here; each item starts only after a slot frees up and the delay has passed.
    /// That way the UI does not lock and resources are not saturated in one go.
    /// </summary>
    private static async Task RunThrottledAsync(
        IReadOnlyList<ServerInfo> targets,
        int concurrency,
        int staggerMs,
        CancellationToken ct,
        Func<ServerInfo, Task> body)
    {
        using var sem = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = new List<Task>(targets.Count);

        foreach (var s in targets)
        {
            if (ct.IsCancellationRequested) break;
            try { await sem.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            var captured = s;
            tasks.Add(Task.Run(async () =>
            {
                try { await body(captured); }
                catch { }
                finally { try { sem.Release(); } catch { } }
            }, CancellationToken.None));

            if (staggerMs > 0)
            {
                try { await Task.Delay(staggerMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        try { await Task.WhenAll(tasks); }
        catch { }
    }

    /// <summary>
    /// A short stop to give the UI thread a chance: queued CollectionChanged events and
    /// Status updates are flushed and the previous stage's xray processes get to close.
    /// </summary>
    private static async Task SettleBeforeUiAsync(CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            await Task.Delay(FullTestSettleMs, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunRealDelayTestsAsync(IReadOnlyList<ServerInfo> targets, CancellationToken ct)
    {
        if (targets.Count == 0 || ct.IsCancellationRequested) return;

        try
        {
            await RealDelayTester.TestBatchAsync(targets, ct, (s, result) =>
            {
                Post(() => s.RecordRealDelay(result));
                TickTestProgress();
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled; servers with no result are marked at the bottom.
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
        }

        foreach (var s in targets)
            if (s.RealDelayMs == -1) s.RecordRealDelay(-2);
    }


     [RelayCommand]
     private async Task TestAll()
     {
         if (IsTesting) return;
         if (Servers.Count == 0) { Status = TranslationManager.Instance["Status_NoServers"]; return; }
         var ct = NewTestToken();
         IsTesting = true;
         BeginTestProgress(Servers.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_PingAll"], Servers.Count);
            foreach (var s in Servers)
            {
                s.LatencyMs = -1;
                s.PingMs = -1;
            }
            using var sem = new SemaphoreSlim(8);
            var tasks = Servers.ToList().Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port);
                    if (!ct.IsCancellationRequested)
                        Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
             int okCount = Servers.Count(s => s.LatencyMs >= 0);
             Status = string.Format(TranslationManager.Instance["Status_PingDoneCount"], okCount, Servers.Count);
             AppendLog(Status);
             AutoSortAfterTest("latency");
             SaveServers();
             EndTestProgress();
         }
         finally { IsTesting = false; ConnectCommand.NotifyCanExecuteChanged(); }
     }

     [RelayCommand]
     private async Task TestSpeed()
     {
         if (IsTesting) return;
         var checkedServers = Servers.Where(s => s.IsChecked).ToList();
         var targets = checkedServers.Count > 0 ? checkedServers : Servers.ToList();
         if (targets.Count == 0) { Status = TranslationManager.Instance["Status_NoServers"]; return; }
         var ct = NewTestToken();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
               Status = string.Format(TranslationManager.Instance["Status_SpeedAllProxy"], targets.Count);
               foreach (var s in targets) s.RecordSpeed(-1);
                try
                {
                    await SpeedTester.TestBatchAsync(targets, ct, (s, r) =>
                    {
                        Post(() => Status = string.Format(TranslationManager.Instance["Status_TestSpeedOne"], s.Name));
                        Post(() => s.RecordSpeed(r));
                        TickTestProgress();
                    });
                }
                catch (OperationCanceledException) { }
              foreach (var s in targets)
                  if (s.DownloadMbps == -1) s.RecordSpeed(-2);
              int okCount = targets.Count(s => s.DownloadMbps >= 0);
              Status = string.Format(TranslationManager.Instance["Status_SpeedDoneCount"], okCount, targets.Count);
             AppendLog(Status);
             AutoSortAfterTest("speed");
             SaveServers();
             EndTestProgress();
         }
         finally { IsTesting = false; ConnectCommand.NotifyCanExecuteChanged(); }
     }

    [RelayCommand]
    private async Task TestOneServer(ServerInfo? server)
    {
        var checkedServers = Servers.Where(s => s.IsChecked).ToList();
        var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
        if (targets.Count == 0) return;
        if (IsTesting) return;
        var ct = NewTestToken();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            using var sem = new SemaphoreSlim(8);
            var tasks = targets.Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port);
                    if (!ct.IsCancellationRequested)
                        Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { try { sem.Release(); } catch { } TickTestProgress(); }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            AutoSortAfterTest("latency");
            EndTestProgress();
        }
        finally { IsTesting = false; SaveServers(); }
    }

    [RelayCommand]
    private async Task TestOneSpeed(ServerInfo? server)
    {
        var checkedServers = Servers.Where(s => s.IsChecked).ToList();
        var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
        if (targets.Count == 0 || IsTesting) return;
        var ct = NewTestToken();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_SpeedAll"], targets.Count);
            foreach (var s in targets) s.RecordSpeed(-1);
              using var sem = new SemaphoreSlim(SpeedTester.MaxConcurrency);
              var tasks = targets.Select(async s =>
              {
                  if (ct.IsCancellationRequested) return;
                  await sem.WaitAsync(ct);
                  try
                  {
                      if (ct.IsCancellationRequested) return;
                      Post(() => Status = string.Format(TranslationManager.Instance["Status_TestSpeedOne"], s.Name));
                      var speedResult = await SpeedTester.TestAsync(s, ct);
                      Post(() => s.RecordSpeed(speedResult));
                  }
                  catch { Post(() => s.RecordSpeed(-2)); }
                  finally { try { sem.Release(); } catch { } TickTestProgress(); }
              });
              try { await Task.WhenAll(tasks); }
              catch (OperationCanceledException) { }
            foreach (var s in targets)
                if (s.DownloadMbps == -1) s.RecordSpeed(-2);
             int okCount = targets.Count(s => s.DownloadMbps >= 0);
             Status = string.Format(TranslationManager.Instance["Status_SpeedDoneCount"], okCount, targets.Count);
             AutoSortAfterTest("speed");
             SaveServers();
             EndTestProgress();
        }
        finally { IsTesting = false; }
    }

    // ---------- multi-select ----------
    public string SelectedCountText => SelectedCount > 0 ? string.Format(TranslationManager.Instance["SelectedCount"], SelectedCount) : "";
    private bool HasSelection() => SelectedCount > 0;

    public void UpdateSelectedCount()
    {
        SelectedCount = Servers.Count(s => s.IsChecked);
        OnPropertyChanged(nameof(SelectedCountText));
        TestSelectedCommand.NotifyCanExecuteChanged();
        TestSelectedSpeedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        TestRealDelaySelectedCommand.NotifyCanExecuteChanged();
    }

    public void ClearAllChecks()
    {
        foreach (var s in Servers)
            s.IsChecked = false;
    }

    [RelayCommand]
    private void ClearSelection() => ClearAllChecks();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestSelected()
    {
        var targets = Servers.Where(s => s.IsChecked).ToList();
        if (targets.Count == 0 || IsTesting) return;
        var ct = NewTestToken();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_PingSelected"], targets.Count);
            foreach (var s in targets)
            {
                s.LatencyMs = -1;
                s.PingMs = -1;
            }
            using var sem = new SemaphoreSlim(8);
            var tasks = targets.Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port);
                    if (!ct.IsCancellationRequested)
                        Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            Status = string.Format(TranslationManager.Instance["Status_PingSelDone"], targets.Count(t => t.LatencyMs >= 0));
            AutoSortAfterTest("latency");
            SaveServers();
            EndTestProgress();
        }
        finally { IsTesting = false; TestSelectedCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestSelectedSpeed()
    {
        var targets = Servers.Where(s => s.IsChecked).ToList();
        if (targets.Count == 0 || IsTesting) return;
        var ct = NewTestToken();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_SpeedSelected"], targets.Count);
            foreach (var s in targets) s.RecordSpeed(-1);
            using var sem = new SemaphoreSlim(SpeedTester.MaxConcurrency);
            var tasks = targets.Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    var speedResult = await SpeedTester.TestAsync(s, ct);
                    Post(() => s.RecordSpeed(speedResult));
                }
                catch { Post(() => s.RecordSpeed(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            Status = string.Format(TranslationManager.Instance["Status_SpeedSelDone"], targets.Count(t => t.DownloadMbps >= 0));
            AutoSortAfterTest("speed");
            SaveServers();
            EndTestProgress();
        }
        finally { IsTesting = false; TestSelectedSpeedCommand.NotifyCanExecuteChanged(); }
    }

     [RelayCommand(CanExecute = nameof(HasSelection))]
     private async Task RemoveSelected()
     {
         var doomed = Servers.Where(s => s.IsChecked).ToList();
         if (doomed.Count == 0) return;
         if (ConfirmDeleteHandler != null)
         {
             bool confirmed = await ConfirmDeleteHandler(string.Format(TranslationManager.Instance["Dlg_ConfirmDeleteSelected"], doomed.Count));
             if (!confirmed) return;
         }
         if (doomed.Any(s => ReferenceEquals(s, SelectedServer)) && IsConnected) Disconnect();
         foreach (var d in doomed)
             Servers.Remove(d);
         if (SelectedServer != null && doomed.Contains(SelectedServer)) SelectedServer = null;
         SaveServers();
         RefreshGroups();
          AppendLog(string.Format(TranslationManager.Instance["Log_ServersDeleted"], doomed.Count));
     }

    [RelayCommand]
    private void ToggleGroupSelection(ServerGroup? group)
    {
        if (group == null) return;
        bool allChecked = group.Servers.Count > 0 && group.Servers.All(s => s.IsChecked);
        foreach (var s in group.Servers)
            s.IsChecked = !allChecked;
    }

     [RelayCommand]
     private async Task TestGroup(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var ct = NewTestToken();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_GroupPing"], group!.Title, targets.Count);
            foreach (var s in targets)
            {
                s.LatencyMs = -1;
                s.PingMs = -1;
            }
            using var sem = new SemaphoreSlim(8);
            var tasks = targets.Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port);
                    if (!ct.IsCancellationRequested)
                        Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
             try { await Task.WhenAll(tasks); }
             catch (OperationCanceledException) { }
             int okCount = targets.Count(t => t.LatencyMs >= 0);
             Status = string.Format(TranslationManager.Instance["Status_GroupPingDone"], okCount);
             AutoSortAfterTest("latency");
             SaveServers();
             EndTestProgress();
         }
         finally { IsTesting = false; }
     }

     [RelayCommand]
     private async Task TestGroupRealDelay(ServerGroup? group)
     {
          var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
          var ct = NewTestToken();
          IsTesting = true;
          BeginTestProgress(targets.Count);
          try
          {
             Status = string.Format(TranslationManager.Instance["Status_GroupReal"], group!.Title, targets.Count);
             foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
             await RunRealDelayTestsAsync(targets, ct);
               int okCount = targets.Count(t => t.LatencyMs >= 0);
                Status = string.Format(TranslationManager.Instance["Status_GroupRealDone"], okCount);
              AutoSortAfterTest("latency");
              SaveServers();
              EndTestProgress();
         }
         finally { IsTesting = false; }
     }

     [RelayCommand]
     private async Task TestGroupSpeed(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var ct = NewTestToken();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
              Status = string.Format(TranslationManager.Instance["Status_GroupSpeed"], group!.Title, targets.Count);
              foreach (var s in targets) s.RecordSpeed(-1);
               using var sem = new SemaphoreSlim(SpeedTester.MaxConcurrency);
               var tasks = targets.Select(async s =>
               {
                   if (ct.IsCancellationRequested) return;
                   await sem.WaitAsync(ct);
                   try
                   {
                       if (ct.IsCancellationRequested) return;
                       Post(() => Status = string.Format(TranslationManager.Instance["Status_TestSpeedOne"], s.Name));
                       var speedResult = await SpeedTester.TestAsync(s, ct);
                       Post(() => s.RecordSpeed(speedResult));
                   }
                   catch { Post(() => s.RecordSpeed(-2)); }
                   finally { try { sem.Release(); } catch { } TickTestProgress(); }
               });
               try { await Task.WhenAll(tasks); }
               catch (OperationCanceledException) { }
              foreach (var s in targets)
                  if (s.DownloadMbps == -1) s.RecordSpeed(-2);
              int okCount = targets.Count(s => s.DownloadMbps >= 0);
               Status = string.Format(TranslationManager.Instance["Status_GroupSpeedDone"], okCount);
             AutoSortAfterTest("speed");
             SaveServers();
             EndTestProgress();
         }
         finally { IsTesting = false; }
     }

     /// <summary>
     /// Full group test: first ping (the fastest test), then real delay, then the speed test.
     /// All three stages share one cancellation token so the "stop" button halts the whole chain;
     /// after each stage, when cancelled, the next stage does not run.
     /// </summary>
     [RelayCommand]
     private async Task TestGroupFull(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var ct = NewTestToken();
         IsTesting = true;
         try
         {
             // ---- stage 1: TCP ping (the fastest) ----
             BeginTestProgress(targets.Count);
             Status = string.Format(TranslationManager.Instance["Status_GroupPing"], group!.Title, targets.Count);
             foreach (var s in targets) { s.LatencyMs = -1; s.PingMs = -1; }
             await RunThrottledAsync(targets, FullTestPingConcurrency, FullTestStaggerMs, ct, async s =>
             {
                 try
                 {
                     var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port);
                     if (!ct.IsCancellationRequested)
                         Post(() => s.RecordPing(ok ? ms : -2));
                 }
                 catch { Post(() => s.RecordPing(-2)); }
                 finally { TickTestProgress(); }
             });
             await SettleBeforeUiAsync(ct);
             AutoSortAfterTest("latency");
             EndTestProgress();
             if (ct.IsCancellationRequested) return;

             // ---- stage 2: real delay ----
             await SettleBeforeUiAsync(ct);
             BeginTestProgress(targets.Count);
             Status = string.Format(TranslationManager.Instance["Status_GroupReal"], group.Title, targets.Count);
             foreach (var s in targets) { s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
             await RunRealDelayTestsAsync(targets, ct);
             await SettleBeforeUiAsync(ct);
             AutoSortAfterTest("latency");
             EndTestProgress();
             if (ct.IsCancellationRequested) return;

             // ---- stage 3: speed test ----
             BeginTestProgress(targets.Count);
             Status = string.Format(TranslationManager.Instance["Status_GroupSpeed"], group.Title, targets.Count);
             foreach (var s in targets) s.RecordSpeed(-1);
             await RunThrottledAsync(targets, FullTestSpeedConcurrency, FullTestStaggerMs, ct, async s =>
             {
                 try
                 {
                     Post(() => Status = string.Format(TranslationManager.Instance["Status_TestSpeedOne"], s.Name));
                     var speedResult = await SpeedTester.TestAsync(s, ct);
                     Post(() => s.RecordSpeed(speedResult));
                 }
                 catch { Post(() => s.RecordSpeed(-2)); }
                 finally { TickTestProgress(); }
             });
             foreach (var s in targets)
                 if (s.DownloadMbps == -1) s.RecordSpeed(-2);
             await SettleBeforeUiAsync(ct);
             AutoSortAfterTest("speed");
             EndTestProgress();

             if (!ct.IsCancellationRequested)
                 Status = string.Format(TranslationManager.Instance["Status_GroupFullDone"], group.Title);
         }
         finally { SaveServers(); IsTesting = false; }
     }

     [RelayCommand]
     private async Task UpdateGroupServers(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var ct = NewTestToken();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_GroupUpdate"], group!.Title, targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RecordSpeed(-1); }
            await RunRealDelayTestsAsync(targets, ct);
             int okCount = targets.Count(s => s.LatencyMs >= 0);
             Status = string.Format(TranslationManager.Instance["Status_GroupUpdateDone"], okCount);
             AutoSortAfterTest("latency");
             SaveServers();
             EndTestProgress();
         }
         catch (Exception ex) { Status = string.Format(TranslationManager.Instance["Log_Error"], ex.Message); }
         finally { IsTesting = false; }
     }

     // ---------- real delay tab ----------
     [RelayCommand]
     private async Task TestRealDelayAll()
     {
         if (IsTesting) return;
         var checkedServers = Servers.Where(s => s.IsChecked).ToList();
         var targets = checkedServers.Count > 0 ? checkedServers : Servers.ToList();
         if (targets.Count == 0) { Status = TranslationManager.Instance["Status_NoServers"]; return; }
         var ct = NewTestToken();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_RealAll"], targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
            await RunRealDelayTestsAsync(targets, ct);
            int okCount = targets.Count(t => t.LatencyMs >= 0);
            Status = string.Format(TranslationManager.Instance["Status_RealDone"], okCount);
            AutoSortAfterTest("latency");
            SaveServers();
            EndTestProgress();
         }
         finally { IsTesting = false; }
     }

     [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestRealDelaySelected()
    {
        var targets = Servers.Where(s => s.IsChecked).ToList();
        if (targets.Count == 0 || IsTesting) return;
        var ct = NewTestToken();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
             Status = string.Format(TranslationManager.Instance["Status_RealSelected"], targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
            await RunRealDelayTestsAsync(targets, ct);
            Status = string.Format(TranslationManager.Instance["Status_RealSelDone"], targets.Count(t => t.LatencyMs >= 0));
            AutoSortAfterTest("latency");
            SaveServers();
            EndTestProgress();
        }
        finally { IsTesting = false; TestRealDelaySelectedCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand]
    private async Task TestOneRealDelay(ServerInfo? server)
    {
        var checkedServers = Servers.Where(s => s.IsChecked).ToList();
        var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
        if (targets.Count == 0 || IsTesting) return;
        var ct = NewTestToken();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_RealCount"], targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
            await RunRealDelayTestsAsync(targets, ct);
            int okCount = targets.Count(s => s.LatencyMs >= 0);
            Status = string.Format(TranslationManager.Instance["Status_RealDoneCount"], okCount, targets.Count);
            AutoSortAfterTest("latency");
            SaveServers();
            EndTestProgress();
        }
        finally { IsTesting = false; }
    }

    // ---------- subscription ----------


     [RelayCommand]
     private void StopTest()
     {
         // We do not swap the CTS so running tasks keep seeing their cancelled token;
         // we only cancel and defer dispose (the token is still reachable).
         var cts = _testCts;
         try { cts?.Cancel(); } catch { }
         IsTesting = false;
          Status = TranslationManager.Instance["Log_TestStopped"];
          AppendLog(TranslationManager.Instance["Log_TestStopped"]);
          Post(() => { TestTestedCount = 0; TestTotalCount = 0; });
      }

    /// <summary>
    /// Builds a fresh CTS per test and cancels the previous one.
    /// Its dispose is deferred because its token may still be held by earlier tasks.
    /// </summary>
    private CancellationToken NewTestToken()
    {
        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch { }
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(3000); } catch { }
                try { oldCts.Dispose(); } catch { }
            });
        }
        return _testCts.Token;
    }
}

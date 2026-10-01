using System.Collections.ObjectModel;
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
            foreach (var s in targets)
                if (s.RealDelayMs == -1) s.RecordRealDelay(-2);
        }

        foreach (var s in targets)
            if (s.RealDelayMs == -1) s.RecordRealDelay(-2);
    }


     [RelayCommand]
     private async Task TestAll()
     {
         if (IsTesting) return;
         if (Servers.Count == 0) { Status = TranslationManager.Instance["Status_NoServers"]; return; }
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         BeginTestProgress(Servers.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_PingAll"], Servers.Count);
            bool resetReal = SelectedPingType == PingType.RealDelay;
            foreach (var s in Servers)
            {
                s.LatencyMs = -1;
                if (resetReal) { s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
                else s.PingMs = -1;
            }
            if (SelectedPingType == PingType.RealDelay)
            {
                await RunRealDelayTestsAsync(Servers.ToList(), _testCts.Token);
            }
            else
            {
            using var sem = new SemaphoreSlim(8);
            var tasks = Servers.ToList().Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await LatencyTester.TcpAsync(s.Address, s.Port)
                    };
                    if (!_testCts.Token.IsCancellationRequested)
                        Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
             try { await Task.WhenAll(tasks); }
             catch (OperationCanceledException) { }
            }
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
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
               Status = string.Format(TranslationManager.Instance["Status_SpeedAllProxy"], targets.Count);
               foreach (var s in targets) s.RecordSpeed(-1);
                try
                {
                    await SpeedTester.TestBatchAsync(targets, _testCts.Token, (s, r) =>
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
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            if (SelectedPingType == PingType.RealDelay)
            {
                foreach (var s in targets) { s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
                await RunRealDelayTestsAsync(targets, default);
            }
            else
            {
            using var sem = new SemaphoreSlim(8);
            var tasks = targets.Select(async s =>
            {
                await sem.WaitAsync();
                try
                {
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await LatencyTester.TcpAsync(s.Address, s.Port)
                    };
                    Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { try { sem.Release(); } catch { } TickTestProgress(); }
            });
            await Task.WhenAll(tasks);
            }
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
        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_SpeedAll"], targets.Count);
            foreach (var s in targets) s.RecordSpeed(-1);
              using var sem = new SemaphoreSlim(SpeedTester.MaxConcurrency);
              var tasks = targets.Select(async s =>
              {
                  if (_testCts.Token.IsCancellationRequested) return;
                  await sem.WaitAsync(_testCts.Token);
                  try
                  {
                      if (_testCts.Token.IsCancellationRequested) return;
                      Post(() => Status = string.Format(TranslationManager.Instance["Status_TestSpeedOne"], s.Name));
                      var speedResult = await SpeedTester.TestAsync(s, _testCts.Token);
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

    // ---------- انتخاب چندتایی ----------
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
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_PingSelected"], targets.Count);
            bool resetReal = SelectedPingType == PingType.RealDelay;
            foreach (var s in targets)
            {
                s.LatencyMs = -1;
                if (resetReal) { s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
                else s.PingMs = -1;
            }
            if (SelectedPingType == PingType.RealDelay)
            {
                await RunRealDelayTestsAsync(targets, _testCts.Token);
            }
            else
            {
            using var sem = new SemaphoreSlim(8);
            var tasks = targets.Select(async s =>
            {
                await sem.WaitAsync();
                try
                {
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await LatencyTester.TcpAsync(s.Address, s.Port)
                    };
                    Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
            await Task.WhenAll(tasks);
            }
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
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_SpeedSelected"], targets.Count);
            foreach (var s in targets) s.RecordSpeed(-1);
            using var sem = new SemaphoreSlim(SpeedTester.MaxConcurrency);
            var tasks = targets.Select(async s =>
            {
                await sem.WaitAsync();
                try
                {
                    var speedResult = await SpeedTester.TestAsync(s);
                    Post(() => s.RecordSpeed(speedResult));
                }
                catch { Post(() => s.RecordSpeed(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
            await Task.WhenAll(tasks);
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
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_GroupPing"], group!.Title, targets.Count);
            bool resetReal = SelectedPingType == PingType.RealDelay;
            foreach (var s in targets)
            {
                s.LatencyMs = -1;
                if (resetReal) { s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
                else s.PingMs = -1;
            }
            if (SelectedPingType == PingType.RealDelay)
            {
                await RunRealDelayTestsAsync(targets, _testCts.Token);
            }
            else
            {
            using var sem = new SemaphoreSlim(8);
            var tasks = targets.Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await LatencyTester.TcpAsync(s.Address, s.Port)
                    };
                    if (!_testCts.Token.IsCancellationRequested)
                        Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally { sem.Release(); TickTestProgress(); }
            });
             try { await Task.WhenAll(tasks); }
             catch (OperationCanceledException) { }
            }
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
          var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
          oldCts?.Dispose();
          IsTesting = true;
          BeginTestProgress(targets.Count);
          try
          {
             Status = string.Format(TranslationManager.Instance["Status_GroupReal"], group!.Title, targets.Count);
             foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
             await RunRealDelayTestsAsync(targets, _testCts.Token);
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
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
              Status = string.Format(TranslationManager.Instance["Status_GroupSpeed"], group!.Title, targets.Count);
              foreach (var s in targets) s.RecordSpeed(-1);
               using var sem = new SemaphoreSlim(SpeedTester.MaxConcurrency);
               var tasks = targets.Select(async s =>
               {
                   if (_testCts.Token.IsCancellationRequested) return;
                   await sem.WaitAsync(_testCts.Token);
                   try
                   {
                       if (_testCts.Token.IsCancellationRequested) return;
                       Post(() => Status = string.Format(TranslationManager.Instance["Status_TestSpeedOne"], s.Name));
                       var speedResult = await SpeedTester.TestAsync(s, _testCts.Token);
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

     [RelayCommand]
     private async Task UpdateGroupServers(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_GroupUpdate"], group!.Title, targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RecordSpeed(-1); }
            await RunRealDelayTestsAsync(targets, _testCts.Token);
             int okCount = targets.Count(s => s.LatencyMs >= 0);
             Status = string.Format(TranslationManager.Instance["Status_GroupUpdateDone"], okCount);
             AutoSortAfterTest("latency");
             SaveServers();
             EndTestProgress();
         }
         catch (Exception ex) { Status = string.Format(TranslationManager.Instance["Log_Error"], ex.Message); }
         finally { IsTesting = false; }
     }

     // ---------- تب تأخیر واقعی ----------
     [RelayCommand]
     private async Task TestRealDelayAll()
     {
         if (IsTesting) return;
         var checkedServers = Servers.Where(s => s.IsChecked).ToList();
         var targets = checkedServers.Count > 0 ? checkedServers : Servers.ToList();
         if (targets.Count == 0) { Status = TranslationManager.Instance["Status_NoServers"]; return; }
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         BeginTestProgress(targets.Count);
         try
         {
            Status = string.Format(TranslationManager.Instance["Status_RealAll"], targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
            await RunRealDelayTestsAsync(targets, _testCts.Token);
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
        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
             Status = string.Format(TranslationManager.Instance["Status_RealSelected"], targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
            await RunRealDelayTestsAsync(targets, _testCts.Token);
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
        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        IsTesting = true;
        BeginTestProgress(targets.Count);
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_RealCount"], targets.Count);
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RealDelaySuccessRate = -1; s.RealDelayJitter = 0; }
            await RunRealDelayTestsAsync(targets, _testCts.Token);
            int okCount = targets.Count(s => s.LatencyMs >= 0);
            Status = string.Format(TranslationManager.Instance["Status_RealDoneCount"], okCount, targets.Count);
            AutoSortAfterTest("latency");
            SaveServers();
            EndTestProgress();
        }
        finally { IsTesting = false; }
    }

    // ---------- اشتراک ----------


     [RelayCommand]
     private void StopTest()
     {
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Cancel();
         oldCts?.Dispose();
         IsTesting = false;
          Status = TranslationManager.Instance["Log_TestStopped"];
          AppendLog(TranslationManager.Instance["Log_TestStopped"]);
          Post(() => { TestTestedCount = 0; TestTotalCount = 0; });
      }


}

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

     partial void OnSelectAllCheckedChanged(bool value)
     {
         foreach (var s in Servers)
             s.IsChecked = value;
     }

    /// <summary>بازسازی گروه‌های تاشو از روی سرورها و ساب‌ها. وضعیت باز/بسته هر گروه حفظ می‌شود.</summary>
    public void RefreshGroups()
    {
        var keepSelected = SelectedServer;
        var expanded = Groups.ToDictionary(g => g.Key, g => g.IsExpanded);
        Groups.Clear();
        foreach (var s in Servers)
            s.Owner = this;
        RefreshConnectionFlags();

        var seen = new HashSet<string>();
        int dupeCount = 0;
        foreach (var s in Servers)
        {
            string key = $"{s.Address}:{s.Port}:{s.UserId}:{s.Password}";
            if (!seen.Add(key)) dupeCount++;
        }
        DuplicateCount = dupeCount;

        // ساب‌ها — جدیدترین بالاتر
        foreach (var sub in Subscriptions.AsEnumerable().Reverse())
        {
            bool isExp = expanded.TryGetValue(sub.Name, out bool e) ? e : !_settings.CollapsedGroups.Contains(sub.Name);
            var group = new ServerGroup(this, sub.Name, sub.Name, sub, isExp);
            var sortedServers = Servers.Where(s => s.SubscriptionName == sub.Name).ToList();
            SortServers(sortedServers);
            foreach (var s in sortedServers)
                group.Servers.Add(s);
            Groups.Add(group);
        }
        var manual = Servers.Where(s => string.IsNullOrEmpty(s.SubscriptionName)).ToList();
        if (manual.Count > 0)
        {
            bool isExp = expanded.TryGetValue("", out bool e) ? e : !_settings.CollapsedGroups.Contains("");
            var group = new ServerGroup(this, "", TranslationManager.Instance["Group_Manual"], null, isExp);
            SortServers(manual);
            foreach (var s in manual)
                group.Servers.Add(s);
            Groups.Add(group);
        }

        // تب «همه سرورها» — آخرین تب، بسته به صورت پیش‌فرض
        bool allExp = expanded.TryGetValue("__all__", out bool ae) ? ae : false;
        var allGroup = new ServerGroup(this, "__all__", TranslationManager.Instance["AllServers_Title"], null, allExp);
        var allServers = Servers.ToList();
        SortServers(allServers);
        foreach (var s in allServers)
            allGroup.Servers.Add(s);
        Groups.Add(allGroup);

        if (SelectedServer == null && keepSelected != null && Servers.Contains(keepSelected))
            SelectedServer = keepSelected;
        foreach (var s in Servers)
            s.IsSelected = ReferenceEquals(s, SelectedServer);
        RefreshConnectionFlags();
    }

    private void UpdateGroupContent(ServerGroup group)
    {
        var servers = Servers.Where(s =>
            string.IsNullOrEmpty(s.SubscriptionName) ? string.IsNullOrEmpty(group.Key) :
            s.SubscriptionName == group.Key).ToList();
        SortServers(servers);
        group.Servers.Clear();
        foreach (var s in servers)
            group.Servers.Add(s);
        group.UpdateTitle();
    }

    private static List<ServerInfo> PrioritySorted(IEnumerable<ServerInfo> source)
    {
        var list = source.ToList();
        list.Sort(ServerInfo.CompareByPriority);
        return list;
    }

    private void SortServers(List<ServerInfo> servers)
    {
        try
        {
            var connected = servers.FirstOrDefault(s => s.IsActiveConnection);
            var rest = servers.Where(s => s != connected).ToList();

            List<ServerInfo> sorted;
            switch (SortField)
            {
                case var sf when sf == TranslationManager.Instance["Sort_Latency"]:
                    sorted = rest.OrderBy(s => s.LatencyMs < 0 ? int.MaxValue : s.LatencyMs).ToList();
                    break;
                case var sf when sf == TranslationManager.Instance["Sort_Speed"]:
                    sorted = rest.OrderByDescending(s => s.DownloadMbps < 0 ? -1 : s.DownloadMbps).ToList();
                    break;
                case var sf when sf == TranslationManager.Instance["Sort_Name"]:
                    sorted = rest.OrderBy(s => s.Name).ToList();
                    break;
                default:
                    sorted = PrioritySorted(rest);
                    break;
            }

            servers.Clear();
            if (connected != null) servers.Add(connected);
            foreach (var s in sorted) servers.Add(s);
        }
        catch
        {
        }
    }

    private void AutoSortAfterTest(string metric)
    {
        var keepSelected = SelectedServer;
        try
        {
            if (GlobalSort)
            {
                var allServers = Servers.ToList();
                var connected = allServers.FirstOrDefault(s => s.IsActiveConnection);
                var rest = allServers.Where(s => s != connected).ToList();
                rest.Sort(ServerInfo.CompareByPriority);

                var sorted = new List<ServerInfo>();
                if (connected != null) sorted.Add(connected);
                sorted.AddRange(rest);

                var grouped = sorted.GroupBy(s => s.SubscriptionName ?? "").ToDictionary(g => g.Key, g => g.ToList());
                foreach (var g in Groups)
                {
                    g.Servers.Clear();
                    if (g.Key == "__all__")
                    {
                        foreach (var s in sorted) g.Servers.Add(s);
                        continue;
                    }
                    if (grouped.TryGetValue(g.Key == "__all__" ? "" : g.Key, out var groupServers))
                        foreach (var s in groupServers) g.Servers.Add(s);
                }
            }
            else
            {
                foreach (var g in Groups)
                {
                    if (g.Key == "__all__")
                    {
                        g.Servers.Clear();
                        foreach (var s in Servers) g.Servers.Add(s);
                        continue;
                    }
                var connected = g.Servers.FirstOrDefault(s => s.IsActiveConnection);
                var rest = g.Servers.Where(s => s != connected).ToList();
                rest.Sort(ServerInfo.CompareByPriority);

                g.Servers.Clear();
                if (connected != null) g.Servers.Add(connected);
                foreach (var s in rest) g.Servers.Add(s);
            }
        }
        }
        finally
        {
            if (keepSelected != null && Servers.Contains(keepSelected))
            {
                if (SelectedServer == null)
                    SelectedServer = keepSelected;
                // force IsSelected/IsActiveConnection refresh after list rebuild
                foreach (var s in Servers)
                    s.IsSelected = ReferenceEquals(s, SelectedServer);
                RefreshConnectionFlags();
                // re-push selection so DataGrid :selected / row bg re-applies after Clear()
                var current = SelectedServer;
                if (current != null)
                {
                    SelectedServer = null;
                    SelectedServer = current;
                }
            }
            SelectionRefreshHandler?.Invoke();
        }
    }



    public void SetGroupExpanded(string key, bool expanded)
    {
        if (expanded) _settings.CollapsedGroups.Remove(key);
        else _settings.CollapsedGroups.Add(key);
        SaveSettings();
    }


      [RelayCommand]
      private void SelectGroupServers(ServerGroup? group)
      {
          if (group == null) return;
          foreach (var s in group.Servers)
              s.IsChecked = true;
          AppendLog(string.Format(TranslationManager.Instance["Log_AllGroupSelected"], group.Title));
      }

      [RelayCommand]
      private async Task RemoveGroupServers(ServerGroup? group)
      {
          if (group == null) return;
          var toRemove = group.Servers.ToList();
          if (ConfirmDeleteHandler != null)
          {
              bool confirmed = await ConfirmDeleteHandler(string.Format(TranslationManager.Instance["Dlg_ConfirmDeleteFromGroup"], toRemove.Count, group.Title));
              if (!confirmed) return;
          }
          foreach (var s in toRemove)
          {
              if (ReferenceEquals(SelectedServer, s) && IsConnected) Disconnect();
              if (ReferenceEquals(SelectedServer, s)) SelectedServer = null;
              Servers.Remove(s);
          }
          SaveServers();
          RefreshGroups();
          AppendLog(string.Format(TranslationManager.Instance["Log_RemovedFromGroup"], toRemove.Count, group.Title));
      }

      [RelayCommand]
      private void ToggleSortDirection()
      {
          SortAscending = !SortAscending;
      }

      // --- متدهای عمومی برای Tray Menu ---

}

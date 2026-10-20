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
using Roxa.Localization;
using Roxa.Models;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{

     partial void OnSelectAllCheckedChanged(bool value)
     {
         foreach (var s in Servers)
             s.IsChecked = value;
     }

    /// <summary>Rebuilds the collapsible groups from servers and subscriptions. Each group's open/closed state is preserved.</summary>
    public void RefreshGroups()
    {
        var keepSelected = SelectedServer;
        var expanded = Groups.ToDictionary(g => g.Key, g => g.IsExpanded);
        Groups.Clear();
        foreach (var s in Servers)
            s.Owner = this;
        RefreshConnectionFlags();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int dupeCount = 0;
        foreach (var s in Servers)
        {
            // The single key (address+port+protocol+transport); it must be identical to the
            // RemoveDuplicates key so the button's counter removes exactly what it counts.
            if (!seen.Add(DupeKey(s))) dupeCount++;
        }
        DuplicateCount = dupeCount;

        var built = new List<ServerGroup>();

        // Subscriptions — newest first
        foreach (var sub in Subscriptions.AsEnumerable().Reverse())
        {
            bool isExp = expanded.TryGetValue(sub.Name, out bool e) ? e : !_settings.CollapsedGroups.Contains(sub.Name);
            var group = new ServerGroup(this, sub.Name, sub.Name, sub, isExp);
            var sortedServers = Servers.Where(s => s.SubscriptionName == sub.Name).ToList();
            SortServers(sortedServers);
            foreach (var s in sortedServers)
                group.Servers.Add(s);
            built.Add(group);
        }
        var manual = Servers.Where(s => string.IsNullOrEmpty(s.SubscriptionName)).ToList();
        if (manual.Count > 0)
        {
            bool isExp = expanded.TryGetValue("", out bool e) ? e : !_settings.CollapsedGroups.Contains("");
            var group = new ServerGroup(this, "", TranslationManager.Instance["Group_Manual"], null, isExp);
            SortServers(manual);
            foreach (var s in manual)
                group.Servers.Add(s);
            built.Add(group);
        }

        // The "all servers" tab — the last tab, depending on the default
        bool allExp = expanded.TryGetValue("__all__", out bool ae) ? ae : false;
        var allGroup = new ServerGroup(this, "__all__", TranslationManager.Instance["AllServers_Title"], null, allExp);
        var allServers = Servers.ToList();
        SortServers(allServers);
        foreach (var s in allServers)
            allGroup.Servers.Add(s);
        built.Add(allGroup);

        foreach (var g in ApplySavedGroupOrder(built))
            Groups.Add(g);

        if (SelectedServer == null && keepSelected != null && Servers.Contains(keepSelected))
            SelectedServer = keepSelected;
        foreach (var s in Servers)
            s.IsSelected = ReferenceEquals(s, SelectedServer);
        RefreshConnectionFlags();
    }

    /// <summary>Applies the saved order of the group tabs (mouse drag); groups
    /// that are not in the saved order are placed above "all servers".</summary>
    private List<ServerGroup> ApplySavedGroupOrder(List<ServerGroup> built)
    {
        if (_settings.GroupOrder is not { Count: > 0 } order)
            return built;

        var byKey = new Dictionary<string, ServerGroup>(StringComparer.Ordinal);
        foreach (var g in built) byKey[g.Key] = g;

        var result = new List<ServerGroup>();
        foreach (var key in order)
            if (byKey.Remove(key, out var g))
                result.Add(g);

        var leftovers = built.Where(g => !result.Contains(g)).ToList();
        if (leftovers.Count > 0)
        {
            int at = result.FindIndex(g => g.Key == "__all__");
            if (at < 0) at = result.Count;
            result.InsertRange(at, leftovers);
        }
        return result;
    }

    /// <summary>Saves the new order of the group tabs after a mouse drag.</summary>
    public void PersistGroupOrder()
    {
        _settings.GroupOrder = Groups.Select(g => g.Key).ToList();
        SaveSettings();
    }

    private void UpdateGroupContent(ServerGroup group)
    {
        // The "all servers" tab has the key "__all__" and no server has such a SubscriptionName;
        // without this branch the tab went empty after a subscription refresh.
        var servers = group.Key == "__all__"
            ? Servers.ToList()
            : Servers.Where(s =>
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

      // --- public methods for the Tray Menu ---

}

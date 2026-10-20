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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;
using Roxa.Models;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{

    /// <summary>The engine's proxy address for fetching subscriptions (only while connected);
    /// it only sets the order of the automatic chain, it is not a limit.</summary>
    private string? EngineSubProxy => IsConnected && _engine.HttpPort > 0 ? $"127.0.0.1:{_engine.HttpPort}" : null;

    private static string GuessSubName(string url)
    {
        try
        {
            if (UrlParser.IsProtocolLink(url)) return UrlParser.Parse(url).Name;
            var uri = new Uri(url);
            string host = uri.Host.Replace("www.", "");
            int dot = host.IndexOf('.');
            return (dot > 0 ? host[..dot] : host) + "-" + DateTime.Now.ToString("MMdd-HHmm");
        }
        catch { return "sub-" + DateTime.Now.ToString("MMdd-HHmm"); }
    }

    [RelayCommand]
    private async Task AddSubscription()
    {
        string url = (SubUrl ?? "").Trim();
        if (url.Length == 0) { ToastHandler?.Invoke(TranslationManager.Instance["Toast_EnterLink"]); return; }
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            AppendLog(TranslationManager.Instance["Log_SubWarnHttp"]);
        string name = string.IsNullOrWhiteSpace(SubName) ? GuessSubName(url) : SubName.Trim();
        if (Subscriptions.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        { AppendLog(string.Format(TranslationManager.Instance["Log_ServerDup"], name)); return; }
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_Fetching"], name);
            // Automatic chain: proxy/direct/system proxy — previously only one path was
            // tried and its failure (e.g. because of a leftover TUN DNS) broke the whole add.
            var (servers, subInfo) = await SubscriptionManager.FetchSmartAsync(
                url, EngineSubProxy, preferDirect: !UseProxyForSubscriptions,
                progress: s => { Status = s; AppendLog(s); });
            AppendLog(string.Format(TranslationManager.Instance["Log_FetchedCount"], servers.Count));
            var info = new SubscriptionInfo { Name = name, Url = url, LastUpdated = DateTime.Now, ServerCount = servers.Count };
            if (subInfo != null)
            {
                info.DataLimit = subInfo.DataLimit;
                info.UsedTraffic = subInfo.UsedTraffic;
                info.ExpireDate = subInfo.ExpireDate;
                AppendLog(string.Format(TranslationManager.Instance["Log_SubInfo"], info.UsedTraffic, info.DataLimit, info.ExpireDate));
            }
            else
            {
                AppendLog(TranslationManager.Instance["Log_SubInfoMissing"]);
            }
            Subscriptions.Add(info);
            SaveSubs();
            MergeServers(name, servers);
            SubName = "";
            SubUrl = "";
            SelectedSubscriptionName = name;
            Status = string.Format(TranslationManager.Instance["Status_SubAddedCount"], name, servers.Count);
        }
        catch (Exception ex) { Status = TranslationManager.Instance["Status_FetchError"]; AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message)); ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Log_Error"], ex.Message)); }
    }

    private void MergeServers(string subName, List<ServerInfo> fetched)
    {
        string? activeId = SelectedServer?.Id;
        foreach (var o in Servers.Where(s => s.SubscriptionName == subName).ToList())
        {
            if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
            Servers.Remove(o);
        }
        int added = 0;
        foreach (var server in fetched)
        {
            Enrich(server);
            server.SubscriptionName = subName;
            // Duplicate detection with the single key (address+port+protocol+transport), not
            // the full link; previously the name was in the link, so one node could return twice.
            string fdk = DupeKey(server);
            if (Servers.Any(s => string.Equals(DupeKey(s), fdk, StringComparison.Ordinal))) continue;
                Servers.Insert(0, server);
                server.IsHighlighted = true;
                added++;
        }
        SaveServers();
        var sub = Subscriptions.FirstOrDefault(s => s.Name == subName);
        if (sub != null)
        {
            sub.LastUpdated = DateTime.Now;
            sub.ServerCount = fetched.Count;
            SaveSubs();
            foreach (var s in Servers.Where(s => s.SubscriptionName == subName))
            {
                s.SubDataLimit = sub.DataLimit;
                s.SubUsedTraffic = sub.UsedTraffic;
                s.SubExpireDate = sub.ExpireDate;
            }
        }
        SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? SelectedServer;
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_Merged"], subName, added, Servers.Count));
    }

    [RelayCommand]
    private Task UpdateSubscriptions() => UpdateSubscriptionsCore(false);

    [RelayCommand]
    private Task UpdateSubscriptionsNoProxy() => UpdateSubscriptionsCore(true);

    private async Task UpdateSubscriptionsCore(bool forceNoProxy)
    {
        if (Subscriptions.Count == 0) { AppendLog(TranslationManager.Instance["Log_NoSub"]); return; }
        string? activeId = SelectedServer?.Id;
        int total = 0;
        string? engineProxy = EngineSubProxy;
        bool preferDirect = forceNoProxy || !UseProxyForSubscriptions;
        foreach (var sub in Subscriptions.ToList())
        {
            try
            {
                Status = string.Format(TranslationManager.Instance["Status_Fetching"], sub.Name);
                var (servers, subInfo) = await SubscriptionManager.FetchSmartAsync(
                    sub.Url, engineProxy, preferDirect,
                    progress: s => { Status = s; AppendLog(s); });
                if (servers.Count == 0)
                {
                    AppendLog(string.Format(TranslationManager.Instance["Log_FetchNoneKept"], sub.Name));
                    continue;
                }
                foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
                {
                    if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
                    if (ReferenceEquals(SelectedServer, o)) SelectedServer = null;
                    Servers.Remove(o);
                }
                foreach (var server in servers)
                {
                    Enrich(server);
                    server.Owner = this;
                    server.SubscriptionName = sub.Name;
                    string udk = DupeKey(server);
                    if (Servers.Any(s => string.Equals(DupeKey(s), udk, StringComparison.Ordinal))) continue;
                    Servers.Add(server);
                }
                sub.LastUpdated = DateTime.Now;
                sub.ServerCount = servers.Count;
                if (subInfo != null)
                {
                    sub.DataLimit = subInfo.DataLimit;
                    sub.UsedTraffic = subInfo.UsedTraffic;
                    sub.ExpireDate = subInfo.ExpireDate;
                }
                foreach (var s in Servers.Where(s => s.SubscriptionName == sub.Name))
                {
                    s.SubDataLimit = sub.DataLimit;
                    s.SubUsedTraffic = sub.UsedTraffic;
                    s.SubExpireDate = sub.ExpireDate;
                }
                total += servers.Count;
                AppendLog(string.Format(TranslationManager.Instance["Log_SubUpdated"], sub.Name, servers.Count));
            }
            catch (Exception ex) { AppendLog(string.Format(TranslationManager.Instance["Log_SubError"], sub.Name, ex.Message)); }
        }
        SaveSubs();
        SaveServers();
        _settings.LastSubscriptionUpdate = DateTime.Now;
        SaveSettings();
        Post(() => OnPropertyChanged(nameof(LastUpdateDisplay)));
        SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? Servers.FirstOrDefault();
        Status = string.Format(TranslationManager.Instance["Log_Updated"], total, Servers.Count);
        RefreshGroups();
    }

    [RelayCommand]
    private async Task UpdateSingleSubscription(SubscriptionInfo? sub)
    {
        sub ??= Subscriptions.FirstOrDefault(s => s.Name == SelectedSubscriptionName);
        if (sub == null) return;
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_Fetching"], sub.Name);
            var (fetched, subInfo) = await SubscriptionManager.FetchSmartAsync(
                sub.Url, EngineSubProxy, preferDirect: !UseProxyForSubscriptions,
                progress: s => { Status = s; AppendLog(s); });
            if (fetched.Count == 0)
            {
                AppendLog(string.Format(TranslationManager.Instance["Log_FetchNoneKept"], sub.Name));
                Status = string.Format(TranslationManager.Instance["Status_NoServerFromSub"], sub.Name);
                return;
            }
            string? activeId = SelectedServer?.Id;
            foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
            {
                if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
                Servers.Remove(o);
            }
            int added = 0;
            foreach (var server in fetched)
            {
                Enrich(server);
                server.Owner = this;
                server.SubscriptionName = sub.Name;
                string sdk = DupeKey(server);
                if (Servers.Any(s => string.Equals(DupeKey(s), sdk, StringComparison.Ordinal))) continue;
                Servers.Add(server);
                added++;
            }
            SaveServers();
            sub.LastUpdated = DateTime.Now;
            sub.ServerCount = fetched.Count;
            if (subInfo != null)
            {
                sub.DataLimit = subInfo.DataLimit;
                sub.UsedTraffic = subInfo.UsedTraffic;
                sub.ExpireDate = subInfo.ExpireDate;
            }
            foreach (var s in Servers.Where(s => s.SubscriptionName == sub.Name))
            {
                s.SubDataLimit = sub.DataLimit;
                s.SubUsedTraffic = sub.UsedTraffic;
                s.SubExpireDate = sub.ExpireDate;
            }
            SaveSubs();
            SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? SelectedServer;
            foreach (var g in Groups)
                UpdateGroupContent(g);
            _settings.LastSubscriptionUpdate = DateTime.Now;
            SaveSettings();
            Post(() => OnPropertyChanged(nameof(LastUpdateDisplay)));
            Status = string.Format(TranslationManager.Instance["Status_SubUpdatedN"], sub.Name, added);
        }
        catch (Exception ex) { Status = TranslationManager.Instance["Status_UpdateError"]; AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message)); }
    }

     [RelayCommand]
     private async Task RemoveSubscription(SubscriptionInfo? sub)
     {
         sub ??= Subscriptions.FirstOrDefault(s => s.Name == SelectedSubscriptionName);
         if (sub == null) return;
         if (ConfirmDeleteHandler != null)
         {
             bool confirmed = await ConfirmDeleteHandler(string.Format(TranslationManager.Instance["Dlg_ConfirmDeleteSub"], sub.Name));
             if (!confirmed) return;
         }
         Subscriptions.Remove(sub);
         SaveSubs();
         foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
         {
             if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
             Servers.Remove(o);
         }
         SaveServers();
         if (SelectedSubscriptionName == sub.Name)
             SelectedSubscriptionName = Subscriptions.Count > 0 ? Subscriptions[0].Name : "";
         RefreshGroups();
         AppendLog(string.Format(TranslationManager.Instance["Log_SubRemoved"], sub.Name));
     }

    /// <summary>Export/import file schema: name and link only. The other fields belong to
    /// the previous machine (last-update date, server counters, usage) and are not
    /// restored; the servers themselves are rebuilt on the next refresh.</summary>
    private sealed class SubExportEntry
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }

    private static Task<string?> PickPath(Func<Task<string?>>? handler)
        => handler == null ? Task.FromResult<string?>(null) : handler();

    /// <summary>
    /// Exports the subscriptions to a JSON file.
    ///
    /// Deliberately plain text, not DPAPI: the very problem is that DPAPI is tied to
    /// this machine's Windows account, and the user needs a file for moving to another
    /// machine or keeping a backup before reinstalling Windows that does not depend on
    /// any one account. The resulting file holds the subscription link in readable form
    /// and must be protected like the link itself — the UI Hint says exactly that.
    /// </summary>
    [RelayCommand]
    private async Task ExportSubscriptions()
    {
        if (Subscriptions.Count == 0)
        { ToastHandler?.Invoke(TranslationManager.Instance["Toast_SubExportNone"]); return; }

        string? path = await PickPath(ExportSubsHandler);
        if (string.IsNullOrWhiteSpace(path)) return;   // the user cancelled

        try
        {
            var list = Subscriptions
                .Select(s => new SubExportEntry { Name = s.Name, Url = s.Url })
                .ToList();
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
            Status = string.Format(TranslationManager.Instance["Status_SubExported"], list.Count);
            ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Toast_SubExported"], list.Count));
            AppendLog(string.Format(TranslationManager.Instance["Log_SubExported"], path));
        }
        catch (Exception ex)
        {
            Status = TranslationManager.Instance["Status_SubFailed"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_SubExportFailed"]);
            AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
        }
    }

    /// <summary>
    /// Imports subscriptions from a file.
    ///
    /// Nothing is deleted or replaced: an existing link is skipped, and a duplicate
    /// subscription with the same name gets a distinct value so no name is lost. The
    /// servers of each new subscription are fetched along the same path as the refresh
    /// button; otherwise a restored backup would hand the user an empty list.
    /// </summary>
    [RelayCommand]
    private async Task ImportSubscriptions()
    {
        string? path = await PickPath(ImportSubsHandler);
        if (string.IsNullOrWhiteSpace(path)) return;

        var added = new List<SubscriptionInfo>();
        int skipped = 0;
        try
        {
            string json = await File.ReadAllTextAsync(path);
            var incoming = JsonSerializer.Deserialize<List<SubExportEntry>>(json);
            if (incoming == null || incoming.Count == 0)
            { ToastHandler?.Invoke(TranslationManager.Instance["Toast_SubImportEmpty"]); return; }

            foreach (var e in incoming)
            {
                string url = (e.Url ?? "").Trim();
                if (url.Length == 0) { skipped++; continue; }
                if (Subscriptions.Any(x => string.Equals(x.Url, url, StringComparison.Ordinal)))
                { skipped++; continue; }   // that link already exists
                string name = string.IsNullOrWhiteSpace(e.Name) ? GuessSubName(url) : e.Name.Trim();
                if (Subscriptions.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    name = name + "-" + DateTime.Now.ToString("MMdd-HHmm");
                var info = new SubscriptionInfo { Name = name, Url = url };
                Subscriptions.Add(info);
                added.Add(info);
            }

            if (added.Count > 0) SaveSubs();
            Status = string.Format(TranslationManager.Instance["Status_SubImported"], added.Count);
            ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Toast_SubImported"], added.Count, skipped));
        }
        catch (Exception ex)
        {
            Status = TranslationManager.Instance["Status_SubFailed"];
            ToastHandler?.Invoke(TranslationManager.Instance["Toast_SubImportFailed"]);
            AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
            return;
        }

        foreach (var info in added)
            await UpdateSingleSubscription(info);
    }

    [RelayCommand]
    private async Task CopySubscriptionUrl(SubscriptionInfo? sub)
    {
        if (sub == null || Clipboard == null) return;
         await Clipboard.SetTextAsync(sub.Url);
         ToastHandler?.Invoke(TranslationManager.Instance["Toast_SubLinkCopied"]);
     }

    [RelayCommand]
    private async Task CopyGroupLinks(ServerGroup? group)
    {
        if (group == null || Clipboard == null) return;
        var links = string.Join(Environment.NewLine, group.Servers.Select(s => s.Link));
        if (links.Length == 0) return;
        await Clipboard.SetTextAsync(links);
        ToastHandler?.Invoke(TranslationManager.Instance["Toast_GroupLinksCopied"]);
    }

    [RelayCommand]
    private async Task UpdateSingleSubscriptionNoProxy(SubscriptionInfo? sub)
    {
        sub ??= Subscriptions.FirstOrDefault(s => s.Name == SelectedSubscriptionName);
        if (sub == null) return;
        try
        {
            Status = string.Format(TranslationManager.Instance["Status_Fetching"], sub.Name);
            var (fetched, subInfo) = await SubscriptionManager.FetchSmartAsync(
                sub.Url, EngineSubProxy, preferDirect: true,
                progress: s => { Status = s; AppendLog(s); });
            if (fetched.Count == 0)
            {
                AppendLog(string.Format(TranslationManager.Instance["Log_FetchNoneKept"], sub.Name));
                Status = string.Format(TranslationManager.Instance["Status_NoServerFromSub"], sub.Name);
                return;
            }
            string? activeId = SelectedServer?.Id;
            foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
            {
                if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
                Servers.Remove(o);
            }
            foreach (var s in fetched) { s.SubscriptionName = sub.Name; Servers.Add(s); }
            if (subInfo != null) { sub.DataLimit = subInfo.DataLimit; sub.UsedTraffic = subInfo.UsedTraffic; sub.ExpireDate = subInfo.ExpireDate; }
            sub.ServerCount = fetched.Count;
            sub.LastUpdated = DateTime.Now;
            SaveServers();
            SaveSubs();
            RefreshGroups();
            if (activeId != null) SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId);
            Status = string.Format(TranslationManager.Instance["Status_FetchedFromSub"], fetched.Count, sub.Name);
            AppendLog(string.Format(TranslationManager.Instance["Log_SubUpdated"], sub.Name, fetched.Count));
        }
        catch (Exception ex)
        {
            Status = TranslationManager.Instance["Status_UpdateErrorIn"];
            AppendLog(string.Format(TranslationManager.Instance["Log_UpdateErrorIn"], sub.Name, ex.Message));
        }
    }

    /// <summary>
    /// Diagnosing the subscription fetch chain: system DNS (and repairing a leftover DNS)
    /// ← proxy ← HTTP fetch ← engine version. Details are shown in the "log" tab.
    /// </summary>
    [RelayCommand]
    private async Task DiagnoseSubscriptions()
    {
        try
        {
            string url = (SubUrl ?? "").Trim();
            if (url.Length == 0)
                url = Subscriptions.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Url))?.Url ?? "";
            string? proxyAddress = UseProxyForSubscriptions && IsConnected && _engine.HttpPort > 0
                ? $"127.0.0.1:{_engine.HttpPort}"
                : null;

            Status = TranslationManager.Instance["Status_Diagnosing"];
            AppendLog(TranslationManager.Instance["Status_Diagnosing"]);
            var report = await SubscriptionDiagnostics.RunAsync(url.Length > 0 ? url : null, proxyAddress);
            foreach (var line in report.Lines) AppendLog(line);

            string summary = report.Healthy
                ? TranslationManager.Instance["Status_DiagOk"]
                : string.Format(TranslationManager.Instance["Status_DiagFound"], report.IssueCount);
            Status = summary;
            AppendLog(summary);
            ToastHandler?.Invoke(summary);
            // Per request: after diagnostics finish, the app fully closes once and reopens on
            // the "subscriptions" tab; a short delay so the summary is seen, and the
            // connection is kept with --reconnect (handled by the restart handler).
            await Task.Delay(1800);
            RestartRequested?.Invoke("--tab subscriptions");
        }
        catch (Exception ex)
        {
            string message = string.Format(TranslationManager.Instance["Log_Error"], ex.Message);
            Status = TranslationManager.Instance["Status_FetchError"];
            AppendLog(message);
            ToastHandler?.Invoke(message);
        }
    }


}

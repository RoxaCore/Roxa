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
            var (servers, subInfo) = await SubscriptionManager.FetchWithInfoAsync(url);
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
            if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal))) continue;
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
        string? proxyAddress = !forceNoProxy && UseProxyForSubscriptions && IsConnected && _engine.HttpPort > 0 ? $"127.0.0.1:{_engine.HttpPort}" : null;
        foreach (var sub in Subscriptions.ToList())
        {
            try
            {
                Status = string.Format(TranslationManager.Instance["Status_Fetching"], sub.Name);
                var (servers, subInfo) = proxyAddress != null
                    ? await SubscriptionManager.FetchWithInfoAndProxyAsync(sub.Url, proxyAddress)
                    : forceNoProxy
                        ? await SubscriptionManager.FetchWithInfoDirectAsync(sub.Url)
                        : await SubscriptionManager.FetchWithInfoAsync(sub.Url);
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
                    if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal))) continue;
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
            string? proxyAddress = UseProxyForSubscriptions && IsConnected && _engine.HttpPort > 0 ? $"127.0.0.1:{_engine.HttpPort}" : null;
            var (fetched, subInfo) = proxyAddress != null
                ? await SubscriptionManager.FetchWithInfoAndProxyAsync(sub.Url, proxyAddress)
                : await SubscriptionManager.FetchWithInfoAsync(sub.Url);
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
                if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal))) continue;
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
            var (fetched, subInfo) = await SubscriptionManager.FetchWithInfoDirectAsync(sub.Url);
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
    /// عیب‌یابی زنجیره‌ی دریافت اشتراک: DNS سیستم (و ترمیم DNS جامانده) ← پروکسی ← دریافت HTTP ← نسخه‌ی موتور.
    /// جزئیات در سربرگ «لاگ» نمایش داده می‌شود.
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

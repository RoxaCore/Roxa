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
using System.Net.Http;
using System.Text.Json;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;
using Roxa.Models;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{

    [RelayCommand]
    private void AddLink()
    {
        string input = UrlParser.UnwrapEncoded((ServerLink ?? "").Trim());
        if (input.Length == 0) return;
        int added = 0;
        int duplicates = 0;
        ServerInfo? lastAdded = null;
        foreach (var rawLine in input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string line = UrlParser.UnwrapEncoded(rawLine);
            if (!UrlParser.IsProtocolLink(line)) { AppendLog(TranslationManager.Instance["Log_InvalidLink"]); continue; }
            try
            {
                var server = UrlParser.Parse(line);
                Enrich(server);
                string dk = DupeKey(server);
                if (Servers.Any(s => string.Equals(DupeKey(s), dk, StringComparison.Ordinal)))
                {
                    AppendLog(string.Format(TranslationManager.Instance["Log_ServerDup"], server.Name));
                    Status = string.Format(TranslationManager.Instance["Status_DupServer"], server.Name);
                    ToastHandler?.Invoke(string.Format(TranslationManager.Instance["Status_DupServer"], server.Name));
                    duplicates++;
                    continue;
                }
                Servers.Insert(0, server);
                server.IsHighlighted = true;
                lastAdded = server;
                added++;
                AppendLog(string.Format(TranslationManager.Instance["Log_ServerAdded"], server.Name));
            }
            catch (FormatException ex)
            {
                AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
                Status = ex.Message;
                ToastHandler?.Invoke(ex.Message);
            }
            catch (Exception ex)
            {
                AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
                Status = ex.Message;
                ToastHandler?.Invoke(ex.Message);
            }
        }
        if (added > 0) { SaveServers(); SelectedServer = lastAdded ?? Servers.FirstOrDefault(); Status = string.Format(TranslationManager.Instance["Status_ServersAdded"], added); }
        else if (duplicates > 0) { Status = string.Format(TranslationManager.Instance["Status_DupCount"], duplicates); }
        ServerLink = "";
        RefreshGroups();
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        if (Clipboard == null) { Status = TranslationManager.Instance["Status_ClipboardNoAccess"]; return; }
        var text = await Clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) { Status = TranslationManager.Instance["Status_ClipboardEmpty"]; return; }
        ServerLink = UrlParser.UnwrapEncoded(text.Trim());
    }

     [RelayCommand]
     private async Task RemoveServer(ServerInfo? server)
     {
         var checkedServers = Servers.Where(s => s.IsChecked).ToList();
         var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
         if (targets.Count == 0) return;
         if (ConfirmDeleteHandler != null)
         {
             bool confirmed = await ConfirmDeleteHandler(string.Format(TranslationManager.Instance["Dlg_ConfirmDeleteN"], targets.Count));
             if (!confirmed) return;
         }
         foreach (var s in targets)
         {
             if (ReferenceEquals(SelectedServer, s) && IsConnected) Disconnect();
             Servers.Remove(s);
             if (ReferenceEquals(SelectedServer, s)) SelectedServer = null;
         }
         SaveServers();
         RefreshGroups();
          AppendLog(string.Format(TranslationManager.Instance["Log_ServersDeleted"], targets.Count));
      }

    [RelayCommand]
    private void CloneServer(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null) return;
        var clone = server.Clone();
        Enrich(clone);
        clone.Link = UrlBuilder.Build(clone);
        Servers.Add(clone);
        SaveServers();
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_Copied"], clone.Name));
    }

    [RelayCommand]
    private void RemoveDuplicates()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dupes = new List<ServerInfo>();
        foreach (var s in Servers)
        {
            // همان کلید واحد شمارش (DupeKey = ServerInfo.GetDupeKey): همهٔ فیلدهای
            // مؤثر بر اتصال، بدون نام و لینک؛ شمارش و حذف از یک تعریف واحد می‌آیند.
            if (!seen.Add(DupeKey(s))) dupes.Add(s);
        }
        foreach (var d in dupes)
        {
            if (ReferenceEquals(SelectedServer, d) && IsConnected) Disconnect();
            Servers.Remove(d);
        }
        SaveServers();
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_DupesRemoved"], dupes.Count));
    }

    [RelayCommand]
    private void SelectServer(ServerInfo? server)
    {
        if (server != null) SelectedServer = server;
    }

    [RelayCommand]
    private async Task EditServer(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null || EditServerHandler == null) return;
        string backup = JsonSerializer.Serialize(server);
        if (await EditServerHandler(server))
        {
            server.Link = UrlBuilder.Build(server);
            Enrich(server);
            SaveServers();
            OnPropertyChanged(nameof(HomeServerName));
            OnPropertyChanged(nameof(HomeShortName));
            OnPropertyChanged(nameof(HomeProtocol));
            OnPropertyChanged(nameof(HomeIp));
            OnPropertyChanged(nameof(HomeFlag));
            OnPropertyChanged(nameof(HomeAddress));
            AppendLog(string.Format(TranslationManager.Instance["Log_ServerEdited"], server.Name));
        }
        else
        {
            var restored = JsonSerializer.Deserialize<ServerInfo>(backup);
            if (restored != null)
                foreach (var prop in typeof(ServerInfo).GetProperties().Where(p => p.CanWrite && p.Name != nameof(ServerInfo.Id)))
                    prop.SetValue(server, prop.GetValue(restored));
        }
    }

    [RelayCommand]
    private async Task ShareServer(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null) return;
        string link = UrlBuilder.Build(server);
        server.Link = link;
        await CopyTextAsync(link);
        Status = TranslationManager.Instance["Status_Ready"];
        AppendLog(string.Format(TranslationManager.Instance["Log_Copied"], server.Name));
        if (ShowQrHandler != null)
            await ShowQrHandler(server.Name, link);
    }

    [RelayCommand]
    private async Task CopyLink(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null) return;
        await CopyTextAsync(UrlBuilder.Build(server));
        AppendLog(string.Format(TranslationManager.Instance["Log_Copied"], server.Name));
    }

    [RelayCommand]
    private void Sort(string? field)
    {
        if (!string.IsNullOrWhiteSpace(field)) SortField = field;
        string sortKey = SortOptions.Zip(ServerGroup.SortOptionKeys, (opt, k) => (opt, k))
            .FirstOrDefault(x => x.opt == SortField).k ?? "none";
        List<ServerInfo> ordered = sortKey switch
        {
            "name" => Servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "speed" => Servers.OrderByDescending(s => s.DownloadMbps).ToList(),
            _ => PrioritySorted(Servers)
        };
        Servers.Clear();
        foreach (var s in ordered)
            Servers.Add(s);
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_Sorted"], SortField));
    }

    // ---------- اتصال ----------


    [RelayCommand]
    private void ExportServers()
    {
        string path = Path.Combine(Storage.DataDir, "servers_export.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Servers.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        AppendLog(string.Format(TranslationManager.Instance["Log_Exported"], path));
    }

    [RelayCommand]
    private void ExportShareUrls()
    {
        var urls = Servers.Where(s => !string.IsNullOrEmpty(s.Link)).Select(s => UrlBuilder.Build(s)).ToList();
        if (urls.Count == 0) { AppendLog(TranslationManager.Instance["Status_NoServers"]); return; }
        _ = CopyTextAsync(string.Join(Environment.NewLine, urls));
        AppendLog(string.Format(TranslationManager.Instance["Log_LinksCopied"], urls.Count));
    }

    [RelayCommand]
    private void ExportSubscriptionBase64()
    {
        var urls = Servers.Where(s => !string.IsNullOrEmpty(s.Link)).Select(s => UrlBuilder.Build(s)).ToList();
        if (urls.Count == 0) { AppendLog(TranslationManager.Instance["Status_NoServers"]); return; }
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", urls)));
        _ = CopyTextAsync(b64);
         AppendLog(TranslationManager.Instance["Log_SubCopied"]);
     }


}

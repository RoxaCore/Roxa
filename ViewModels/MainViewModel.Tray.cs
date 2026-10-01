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

      public async Task TraySmartConnectAsync()
      {
          if (SmartConnectCommand.CanExecute(null))
              await SmartConnect();
      }

      public void CancelSmartConnect()
      {
          if (IsSmartConnecting || IsFindingBetter)
          {
              _testCts.Cancel();
              IsTesting = false;
              IsSmartConnecting = false;
              IsFindingBetter = false;
              Status = TranslationManager.Instance["Status_Stopped"];
          }
      }

      public void TrayToggleSystemProxy()
      {
          SetSystemProxy = !SetSystemProxy;
      }

      public async Task TrayUpdateSubscriptionsAsync(bool useProxy)
      {
          if (Subscriptions.Count == 0)
          {
              AppendLog(TranslationManager.Instance["Log_NoSub"]);
              return;
          }
          Status = useProxy ? TranslationManager.Instance["Status_UpdateWithProxy"] : TranslationManager.Instance["Status_UpdateNoProxy"];
          string? activeId = SelectedServer?.Id;
          int total = 0;
          string? proxyAddress = useProxy && _engine.HttpPort > 0 ? $"127.0.0.1:{_engine.HttpPort}" : null;

              foreach (var sub in Subscriptions.ToList())
              {
                  try
                  {
                      Status = string.Format(TranslationManager.Instance["Status_Fetching"], sub.Name);
                      var (servers, subInfo) = proxyAddress != null
                          ? await SubscriptionManager.FetchWithInfoAndProxyAsync(sub.Url, proxyAddress)
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


}

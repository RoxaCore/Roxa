using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using XrayClient.Models;

namespace XrayClient.ViewModels;

/// <summary>یک گروه تاشو در لیست سرورها: یا یک سابسکریپشن یا سرورهای دستی.</summary>
public partial class ServerGroup : ObservableObject
{
    public string Key { get; }
    public string Title { get; }
    public SubscriptionInfo? Subscription { get; }
    public MainViewModel Owner { get; }
    public ObservableCollection<ServerInfo> Servers { get; } = new();

    public string[] SortOptions { get; } = { "بدون مرتب", "تأخیر (کم)", "سرعت (بیش)", "نام" };

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _allServersChecked;

    [ObservableProperty]
    private string _groupSortField = "بدون مرتب";

    public bool IsSubscription => Subscription != null;
    public bool IsNotSubscription => !IsSubscription;
    public string CountText => $"{Servers.Count} سرور";

    public ServerGroup(MainViewModel owner, string key, string title, SubscriptionInfo? subscription, bool expanded)
    {
        Owner = owner;
        Key = key;
        Title = title;
        Subscription = subscription;
        _isExpanded = expanded;
        Servers.CollectionChanged += OnServersChanged;
    }

    private void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(CountText));

    partial void OnIsExpandedChanged(bool value) => Owner.SetGroupExpanded(Key, value);

    partial void OnAllServersCheckedChanged(bool value)
    {
        foreach (var s in Servers)
            s.IsChecked = value;
    }

    partial void OnGroupSortFieldChanged(string value) => SortGroupServers();

    public void SortGroupServers()
    {
        try
        {
            List<ServerInfo> sorted;

            switch (GroupSortField)
            {
                case "تأخیر (کم)":
                    sorted = Servers
                        .OrderBy(s => s.LatencyMs < 0 ? int.MaxValue : s.LatencyMs)
                        .ToList();
                    break;

                case "سرعت (بیش)":
                    sorted = Servers
                        .OrderByDescending(s => s.DownloadMbps < 0 ? 0 : s.DownloadMbps)
                        .ToList();
                    break;

                case "نام":
                    sorted = Servers
                        .OrderBy(s => s.Name)
                        .ToList();
                    break;

                default:
                    return;
            }

            Servers.Clear();
            foreach (var s in sorted)
                Servers.Add(s);
        }
        catch
        {
            // اگر خطا بود، سرورها حفظ شوند
        }
    }
}

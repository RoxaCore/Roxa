using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Roxa.Localization;
using Roxa.Models;

namespace Roxa.ViewModels;

public partial class ServerGroup : ObservableObject
{
    public string Key { get; }
    public string Title { get; }
    public SubscriptionInfo? Subscription { get; }
    public MainViewModel Owner { get; }
    public ObservableCollection<ServerInfo> Servers { get; } = new();

    public static string[] SortOptionKeys { get; } = { "none", "latency", "speed", "name" };

    private static readonly Dictionary<string, string> SortLabelToKey = new()
    {
        ["Sort_None"] = "none",
        ["Sort_Latency"] = "latency",
        ["Sort_Speed"] = "speed",
        ["Sort_Name"] = "name"
    };

    public string[] SortOptions => [
        TranslationManager.Instance["Sort_None"],
        TranslationManager.Instance["Sort_Latency"],
        TranslationManager.Instance["Sort_Speed"],
        TranslationManager.Instance["Sort_Name"]
    ];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _allServersChecked;

    [ObservableProperty]
    private string _groupSortField = "";

    public bool IsSubscription => Subscription != null;
    public bool IsAllGroup => Key == "__all__";
    public bool IsManualGroup => !IsSubscription && !IsAllGroup;
    public bool IsNotSubscription => !IsSubscription;
    public bool IsNotAllGroup => !IsAllGroup;
    public string CountText => string.Format(TranslationManager.Instance["ServerCount"], Servers.Count);

    public ServerGroup(MainViewModel owner, string key, string title, SubscriptionInfo? subscription, bool expanded)
    {
        Owner = owner;
        Key = key;
        Title = title;
        Subscription = subscription;
        _isExpanded = expanded;
        _groupSortField = TranslationManager.Instance["Sort_None"];
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

    public void UpdateTitle()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CountText));
    }

    public void SortGroupServers()
    {
        try
        {
            string? key = null;
            foreach (var kv in SortLabelToKey)
            {
                if (TranslationManager.Instance[kv.Key] == GroupSortField)
                {
                    key = kv.Value;
                    break;
                }
            }

            List<ServerInfo> sorted;
            switch (key)
            {
                case "latency":
                    sorted = Servers.OrderBy(s => s.LatencyMs < 0 ? int.MaxValue : s.LatencyMs).ToList();
                    break;
                case "speed":
                    sorted = Servers.OrderByDescending(s => s.DownloadMbps < 0 ? -1 : s.DownloadMbps).ToList();
                    break;
                case "name":
                    sorted = Servers.OrderBy(s => s.Name).ToList();
                    break;
                default:
                    sorted = Servers.ToList();
                    sorted.Sort(ServerInfo.CompareByPriority);
                    break;
            }

            var keepSelected = Owner.SelectedServer;
            Servers.Clear();
            foreach (var s in sorted)
                Servers.Add(s);
            if (Owner.SelectedServer == null && keepSelected != null && Servers.Contains(keepSelected))
                Owner.SelectedServer = keepSelected;
        }
        catch
        {
        }
    }
}

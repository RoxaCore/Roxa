using CommunityToolkit.Mvvm.ComponentModel;

namespace XrayClient.ViewModels;

public partial class TunExcludedItem : ObservableObject
{
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isEnabled = true;
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace XrayClient.ViewModels;

public partial class KillSwitchItem : ObservableObject
{
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _name = "";
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using XrayClient.Core;
using XrayClient.Models;

namespace XrayClient.Views;

public partial class ServerEditorWindow : Window
{
    public ServerInfo? EditedServer
    {
        get => DataContext as ServerInfo;
        set
        {
            DataContext = value;
            if (value != null)
                value.PropertyChanged += (_, _) => RefreshPreview();
        }
    }

    public ServerEditorWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var box = this.FindControl<TextBox>("PreviewBox");
        if (box != null && EditedServer != null)
        {
            try { box.Text = UrlBuilder.Build(EditedServer); }
            catch { box.Text = EditedServer.Link; }
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (EditedServer == null) { Close(false); return; }
        if (string.IsNullOrWhiteSpace(EditedServer.Name) || string.IsNullOrWhiteSpace(EditedServer.Address))
            return;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}

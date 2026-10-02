using Avalonia.Controls;
using Avalonia.Interactivity;
using Roxa.Core;
using Roxa.Models;

namespace Roxa.Views;

public partial class ServerEditorWindow : Window
{
    private ServerInfo? _subscribed;

    public ServerInfo? EditedServer
    {
        get => DataContext as ServerInfo;
        set
        {
            // بدون detach، هر بار باز شدن دیالوگ یک handler دیگر روی ServerInfo
            // (که عمرش در کل برنامه است) اضافه می‌کرد → نشت و اجرای چندباره RefreshPreview.
            if (_subscribed != null) _subscribed.PropertyChanged -= OnEditedServerChanged;
            DataContext = value;
            _subscribed = value;
            if (value != null) value.PropertyChanged += OnEditedServerChanged;
        }
    }

    private void OnEditedServerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshPreview();

    public ServerEditorWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshPreview();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_subscribed != null)
        {
            _subscribed.PropertyChanged -= OnEditedServerChanged;
            _subscribed = null;
        }
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

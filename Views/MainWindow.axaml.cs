using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using XrayClient.Models;
using XrayClient.ViewModels;

namespace XrayClient.Views;

public partial class MainWindow : Window
{
    private int _resizingColumn = -1;
    private double _resizeStartX = 0;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Shutdown();
        };
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.Clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        vm.EditServerHandler = async server =>
        {
            var dlg = new ServerEditorWindow { EditedServer = server };
            return await dlg.ShowDialog<bool>(this);
        };
        vm.ShowQrHandler = async (name, link) =>
        {
            var dlg = new QrShareWindow(name, link, TopLevel.GetTopLevel(this)?.Clipboard);
            await dlg.ShowDialog(this);
        };
        vm.ConfirmDeleteHandler = async message =>
        {
            bool result = false;
            var btnYes = new Button { Content = "بله", Width = 80 };
            var btnNo = new Button { Content = "خیر", Width = 80 };
            
            var window = new Window
            {
                Title = "تأیید حذف",
                Width = 350,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Spacing = 15,
                    Margin = new Avalonia.Thickness(20),
                    Children =
                    {
                        new TextBlock { Text = message },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 10,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Children = { btnYes, btnNo }
                        }
                    }
                }
            };
            
            btnYes.Click += (s, e) => { result = true; window.Close(); };
            btnNo.Click += (s, e) => { result = false; window.Close(); };
            
            await window.ShowDialog(this);
            return result;
        };
        
        // Setup column resize handles
        SetupResizeHandles();
        
        await vm.OnStartedAsync();
    }

    private void SetupResizeHandles()
    {
        var header = this.FindControl<Grid>("TableHeader");
        if (header == null) return;

        for (int i = 0; i < 9; i++)
        {
            var handle = this.FindControl<Border>($"ResizeHandle{i}");
            if (handle != null)
            {
                int columnIndex = i;
                handle.PointerPressed += (s, e) =>
                {
                    _resizingColumn = columnIndex;
                    _resizeStartX = e.GetPosition(this).X;
                };
                handle.PointerMoved += (s, e) =>
                {
                    if (_resizingColumn >= 0 && (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed))
                    {
                        var currentX = e.GetPosition(this).X;
                        var delta = currentX - _resizeStartX;
                        
                        if (DataContext is MainViewModel vm && header.ColumnDefinitions != null)
                        {
                            var colDef = header.ColumnDefinitions[_resizingColumn];
                            var newWidth = Math.Max(50, colDef.ActualWidth + delta);
                            colDef.Width = new GridLength(newWidth);
                            
                            // Store in ViewModel
                            if (vm.ColumnWidths.ContainsKey(_resizingColumn))
                                vm.ColumnWidths[_resizingColumn] = newWidth;
                            
                            _resizeStartX = currentX;
                        }
                    }
                };
                handle.PointerReleased += (s, e) =>
                {
                    _resizingColumn = -1;
                };
            }
        }
    }
}

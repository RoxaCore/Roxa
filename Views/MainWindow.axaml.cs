using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XrayClient.Core;
using XrayClient.Models;
using XrayClient.ViewModels;

namespace XrayClient.Views;

public partial class MainWindow : Window
{
    private int _resizingColumn = -1;
    private double _resizeStartX = 0;
    private bool _forceClose = false;

    public MainWindow()
    {
        InitializeComponent();
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "roxa-icon.ico");
        if (System.IO.File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);
        Opened += OnOpened;
        Activated += OnActivated;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose) return;
        e.Cancel = true;
        Hide();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private async void OnActivated(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is not ViewModels.MainViewModel vm) return;
            if (vm.Clipboard == null) return;
            var asyncData = (Avalonia.Input.IAsyncDataTransfer)vm.Clipboard;
            var text = await asyncData.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;
            var trimmed = text.Trim();
            if (!Core.UrlParser.IsProtocolLink(trimmed)) return;
            if (vm.Servers.Any(s => string.Equals(s.Link, trimmed, StringComparison.Ordinal))) return;
            vm.ServerLink = trimmed;
            vm.AddLinkCommand.Execute(null);
        }
        catch { }
    }

    public void ShowToast(string message, int durationMs = 3000)
    {
        ToastText.Text = message;
        ToastBorder.IsVisible = true;
        DispatcherTimer.RunOnce(() => { ToastBorder.IsVisible = false; }, TimeSpan.FromMilliseconds(durationMs));
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
        vm.ToastHandler = message => ShowToast(message);
        vm.RestartRequested += () =>
        {
            var app = Application.Current as App;
            app?.DisposeTray();
            var exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            System.Diagnostics.Process.Start(exe);
            Environment.Exit(0);
        };
        vm.OpenDnsSettingsHandler = () => OpenDnsDialog();
        vm.ShowAdminWarningHandler = () =>
        {
            var tr = Localization.TranslationManager.Instance;
            var btnOk = new Button { Content = tr["Dlg_Continue"], Width = 80 };
            var btnRestart = new Button { Content = tr["Dlg_RestartAdmin"], Width = 160 };
            var win = new Window
            {
                Title = tr["Dlg_NeedsAdmin"],
                Width = 400,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Spacing = 15,
                    Margin = new Avalonia.Thickness(20),
                    Children =
                    {
                        new TextBlock { Text = tr["Dlg_TUNAdminMsg"], TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 10,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Children = { btnOk, btnRestart }
                        }
                    }
                }
            };
            btnOk.Click += (_, _) => win.Close();
            btnRestart.Click += (_, _) => { Core.AdminHelper.RestartAsAdmin(); win.Close(); };
            win.ShowDialog(this);
        };
        vm.ConfirmDeleteHandler = async message =>
        {
            bool result = false;
            var tr = Localization.TranslationManager.Instance;
            var btnYes = new Button { Content = tr["Dlg_Yes"], Width = 80 };
            var btnNo = new Button { Content = tr["Dlg_No"], Width = 80 };
            
            var window = new Window
            {
                Title = tr["Dlg_ConfirmDelete"],
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

    private void SubscriptionCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is string groupKey && DataContext is MainViewModel vm)
        {
            MainTabControl.SelectedItem = ServersTab;
            var group = vm.Groups.FirstOrDefault(g => g.Key == groupKey);
            if (group != null && !group.IsExpanded)
                group.IsExpanded = true;
        }
    }

    private void ServerCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && DataContext is MainViewModel vm)
        {
            var panel = border.Child;
            if (panel is Grid grid)
            {
                // Find the server info from the DataContext of the border
                var dc = border.DataContext;
                if (dc is Models.ServerInfo server)
                {
                    vm.SelectServerCommand.Execute(server);
                }
            }
        }
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

    private void OpenDnsDialog()
    {
        if (DataContext is not MainViewModel vm) return;

        var txtServers = new TextBox
        {
            Text = vm.CustomDnsServers,
            PlaceholderText = "1.1.1.1, 8.8.8.8",
            Padding = new Avalonia.Thickness(10, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            FontSize = 13,
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };

        var txtJson = new TextBox
        {
            Text = vm.CustomDnsAdvancedJson,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 140,
            Padding = new Avalonia.Thickness(10, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            FontFamily = "Consolas",
            FontSize = 12
        };

        var tr = Localization.TranslationManager.Instance;
        var btnSave = new Button
        {
            Content = tr["Dlg_Save"],
            Padding = new Avalonia.Thickness(24, 10),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E7C6B")),
            Foreground = Avalonia.Media.Brushes.White,
            FontSize = 13,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        };

        var btnCancel = new Button
        {
            Content = tr["Dlg_Cancel"],
            Padding = new Avalonia.Thickness(24, 10),
            CornerRadius = new Avalonia.CornerRadius(8),
            FontSize = 13
        };

        var window = new Window
        {
            Title = tr["Dlg_DNSSettings"],
            Width = 500,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F8F9FA")),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = tr["Dlg_DNSSimple"], FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 13 },
                    txtServers,
                    new TextBlock { Text = tr["Dlg_DNSHint"], FontSize = 11, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#888888")) },
                    new TextBlock { Text = tr["Dlg_DNSAdvanced"], FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 13 },
                    txtJson,
                    new TextBlock { Text = "مثال: {\"servers\": [{\"address\": \"1.1.1.1\"}]}", FontSize = 11, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#888888")) },
                    new TextBlock
                    {
                        FontSize = 11,
                        Margin = new Avalonia.Thickness(0, 4, 0, 0),
                        Text = tr["Dlg_DNSDocs"]
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Margin = new Avalonia.Thickness(0, 8, 0, 0),
                        Children = { btnCancel, btnSave }
                    }
                }
            }
        };

        btnSave.Click += (_, _) =>
        {
            vm.CustomDnsServers = txtServers.Text ?? "";
            vm.CustomDnsAdvancedJson = txtJson.Text ?? "";
            window.Close();
        };
        btnCancel.Click += (_, _) => window.Close();

        window.ShowDialog(this);
    }

    public void SwitchToSettingsTab()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (MainTabControl != null)
                MainTabControl.SelectedIndex = 3;
        });
    }
}

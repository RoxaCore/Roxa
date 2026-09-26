using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    private string? _lastClipboardLink;

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
            if (string.Equals(_lastClipboardLink, trimmed, StringComparison.Ordinal)) return;
            if (vm.Servers.Any(s => string.Equals(s.Link, trimmed, StringComparison.Ordinal))) return;

            bool confirmed = await ConfirmImportFromClipboard(trimmed);
            if (!confirmed) return;
            _lastClipboardLink = trimmed;
            vm.ServerLink = trimmed;
            vm.AddLinkCommand.Execute(null);
        }
        catch { }
    }

    private async Task<bool> ConfirmImportFromClipboard(string link)
    {
        var tr = Localization.TranslationManager.Instance;
        bool result = false;
        var btnYes = new Button { Content = tr["Dlg_Yes"], Width = 80 };
        var btnNo = new Button { Content = tr["Dlg_No"], Width = 80 };
        var window = new Window
        {
            Title = tr["Add_Clipboard"],
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
                    new TextBlock { Text = $"{tr["Add_Clipboard"]}\n{link}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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
        btnYes.Click += (_, _) => { result = true; window.Close(); };
        btnNo.Click += (_, _) => { result = false; window.Close(); };
        await window.ShowDialog(this);
        return result;
    }

    public void ShowToast(string message, int durationMs = 3000)
    {
        ToastText.Text = message;
        ToastBorder.IsVisible = true;
        DispatcherTimer.RunOnce(() => { ToastBorder.IsVisible = false; }, TimeSpan.FromMilliseconds(durationMs));
    }

    private void ServerGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dg && DataContext is MainViewModel vm
            && dg.SelectedItem is ServerInfo s && !ReferenceEquals(vm.SelectedServer, s))
            vm.SelectedServer = s;
    }

    private void ForceRowSelection(MainViewModel vm, ServerInfo server)
    {
        foreach (var g in vm.Groups)
        {
            if (!g.Servers.Contains(server)) continue;
            var exp = this.GetVisualDescendants().OfType<Expander>()
                .FirstOrDefault(e => ReferenceEquals(e.DataContext, g));
            var dg = exp?.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
            if (dg != null && !ReferenceEquals(dg.SelectedItem, server))
                dg.SelectedItem = server;
        }
    }

    private void ReapplyRowSelection()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MainViewModel vm || vm.SelectedServer == null) return;
            ForceRowSelection(vm, vm.SelectedServer);
        }, DispatcherPriority.Loaded);
    }

    private void FocusServer(ServerInfo server)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MainViewModel vm) return;
            foreach (var g in vm.Groups)
            {
                int idx = g.Servers.IndexOf(server);
                if (idx > 0)
                {
                    g.Servers.RemoveAt(idx);
                    g.Servers.Insert(0, server);
                }
                if (g.Servers.Contains(server)) g.IsExpanded = true;
            }
            vm.SelectedServer = server;

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(vm.SelectedServer, server))
                    vm.SelectedServer = server;

                ForceRowSelection(vm, server);

                var group = vm.Groups.FirstOrDefault(g => g.Servers.Count > 0 && ReferenceEquals(g.Servers[0], server))
                    ?? vm.Groups.FirstOrDefault(g => g.Servers.Contains(server));
                if (group == null) return;
                var expander = this.GetVisualDescendants().OfType<Expander>()
                    .FirstOrDefault(e => ReferenceEquals(e.DataContext, group));
                var outer = this.FindControl<ScrollViewer>("GroupsScrollViewer");
                if (expander == null || outer == null) return;

                var pos = expander.TranslatePoint(new Point(0, 0), outer);
                if (pos != null)
                    outer.Offset = new Vector(outer.Offset.X, Math.Max(0, outer.Offset.Y + pos.Value.Y - 4));

                var grid = expander.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
                if (grid == null) return;
                grid.ScrollIntoView(server, null);
                var inner = grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (inner != null) inner.Offset = new Vector(inner.Offset.X, 0);
            }, DispatcherPriority.Loaded);
        });
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
        vm.FocusServerHandler = FocusServer;
        vm.SelectionRefreshHandler = ReapplyRowSelection;
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

    private static ServerInfo? FindServerFromVisual(object? source)
    {
        if (source is not Visual v) return null;
        for (var cur = v; cur != null; cur = cur.GetVisualParent())
        {
            if (cur is DataGridRow row && row.DataContext is ServerInfo server)
                return server;
        }
        return null;
    }

    private void ServerGrid_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var server = FindServerFromVisual(e.Source);
        if (server == null) return;

        var selected = grid.SelectedItems?.OfType<ServerInfo>().ToList() ?? new List<ServerInfo>();
        bool multi = selected.Count > 1 && selected.Contains(server);

        ContextMenu menu;
        if (multi)
        {
            if (DataContext is MainViewModel vm)
            {
                foreach (var s in vm.Servers)
                    s.IsChecked = selected.Contains(s);
                vm.UpdateSelectedCount();
            }
            menu = (ContextMenu)this.FindResource("MultiServerContextMenu")!;
        }
        else
        {
            if (!selected.Contains(server) || selected.Count > 1)
                grid.SelectedItem = server;
            menu = (ContextMenu)this.FindResource("ServerRowContextMenu")!;
        }

        menu.DataContext = server;
        e.Handled = true;
        if (e.Source is Control target)
            menu.Open(target);
        else
            menu.Open(grid);
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
                    new TextBlock { Text = string.Format(tr["Dlg_DNSExample"], "{\"servers\": [{\"address\": \"1.1.1.1\"}]}"), FontSize = 11, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#888888")) },
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

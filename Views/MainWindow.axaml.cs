using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Roxa.Models;
using Roxa.ViewModels;

namespace Roxa.Views;

public partial class MainWindow : Window
{
    private bool _forceClose = false;
    private bool _startupTabApplied = false;

    public MainWindow()
    {
        InitializeComponent();
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "roxa-icon.ico");
        if (System.IO.File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);
        // فشار روی هدر گروه اغلب توسط کنترل‌های داخلی (مثلاً دکمهٔ Expander) handle می‌شود؛
        // باید با handledEventsToo ثبت شود تا درگ سربرگ‌ها کار کند.
        AddHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed, handledEventsToo: true);
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
            var trimmed = Core.UrlParser.UnwrapEncoded(text.Trim());
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
        vm.RestartRequested += (string extraArgs) =>
        {
            try
            {
                var app = Application.Current as App;
                app?.DisposeTray();
                bool wasConnected = vm.IsConnected;
                // عیب‌یابی اشتراکات: ری‌استارت باید بدون اتصال باشد، حتی اگر قبلاً وصل بوده.
                var argList = extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                bool noReconnect = argList.Remove("--no-reconnect");
                var exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--wait-exit" + (wasConnected && !noReconnect ? " --reconnect" : "") + (argList.Count > 0 ? " " + string.Join(" ", argList) : ""),
                    UseShellExecute = true
                };
                if (Core.AdminHelper.IsRunningAsAdmin())
                    psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
            Environment.Exit(0);
        };
        vm.OpenDnsSettingsHandler = () => OpenDnsDialog();
        // ری‌استارت با ‎--tab subscriptions‎ (عیب‌یابی اشتراکات): بازشدن در تب اشتراکات؛ فقط یک‌بار.
        if (!_startupTabApplied)
        {
            var sargs = Program.StartupArgs;
            for (int i = 0; i + 1 < sargs.Length; i++)
            {
                if (sargs[i] == "--tab" && sargs[i + 1] == "subscriptions")
                {
                    MainTabControl.SelectedItem = SubscriptionsTab;
                    _startupTabApplied = true;
                    break;
                }
            }
        }
        vm.PickTunExcludedAppHandler = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("*.exe") { Patterns = new[] { "*.exe" } },
                    FilePickerFileTypes.All
                }
            });
            return files != null && files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
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
                        new TextBlock { Text = tr[Core.TunRecovery.LeftoverNeedsAdmin ? "Dlg_LeftoverAdminMsg" : "Dlg_TUNAdminMsg"], TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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
        vm.UpdateShutdownRequested += () => (Application.Current as App)?.ExitApp();
        vm.ShowBugReportHandler = (info, submit) =>
        {
            var tr = Localization.TranslationManager.Instance;
            var input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Height = 110,
                FontSize = 13,
                PlaceholderText = tr["Dlg_ReportHint"],
            };
            var btnSend = new Button { Content = tr["Btn_SendReport"], Padding = new Avalonia.Thickness(14, 6) };
            var btnCancel = new Button { Content = tr["Dlg_Cancel"], Padding = new Avalonia.Thickness(14, 6) };
            var win = new Window
            {
                Title = tr["Dlg_ReportTitle"],
                Width = 460,
                Height = 360,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Margin = new Avalonia.Thickness(16),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = info,
                            FontSize = 12,
                            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9ca3af")),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                        input,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 10,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Children = { btnSend, btnCancel }
                        }
                    }
                }
            };
            btnSend.Click += async (_, _) =>
            {
                btnSend.IsEnabled = false;
                try { await submit(input.Text ?? ""); } catch { }
                win.Close();
            };
            btnCancel.Click += (_, _) => win.Close();
            win.ShowDialog(this);
        };
        if (vm.TunDisabledNeedsAdmin || Core.TunRecovery.LeftoverNeedsAdmin)
            Dispatcher.UIThread.Post(() => vm.ShowAdminWarningHandler?.Invoke(), DispatcherPriority.Loaded);
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
        vm.ConfirmTunEnableHandler = async () =>
        {
            bool result = false;
            var tr = Localization.TranslationManager.Instance;
            var btnYes = new Button { Content = tr["Dlg_Yes"], Width = 80 };
            var btnNo = new Button { Content = tr["Dlg_No"], Width = 80 };
            
            var window = new Window
            {
                Title = tr["Dlg_ConfirmTun"],
                Width = 430,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Spacing = 15,
                    Margin = new Avalonia.Thickness(20),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = tr["Dlg_ConfirmTun_Msg"],
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            TextAlignment = Avalonia.Media.TextAlignment.Center
                        },
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

    // --- درگ سربرگ گروه‌ها برای تغییر ترتیب تب‌ها ---
    private ServerGroup? _dragGroup;
    private bool _dragActive;
    private Point _pressPoint;
    private IPointer? _dragPointer;

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // هر فشاری (حتی handleشده توسط کنترل‌های داخلی) ابتدا وضعیت درگ قبلی را بازنشانی می‌کند
        _dragGroup = null;
        _dragActive = false;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual src) return;

        // پیمایش تا Expander؛ فشار داخل محتوا (جدول سرورها) یا روی کنترل‌های تعاملی رد می‌شود
        Expander? expander = null;
        bool skip = false;
        for (var cur = src; cur != null; cur = cur.GetVisualParent())
        {
            if (cur is DataGrid or DataGridRow) { skip = true; break; }
            if (cur is CheckBox or ComboBox or TextBox) { skip = true; break; }
            if (cur is Button b && b is not Avalonia.Controls.Primitives.ToggleButton) { skip = true; break; }
            if (cur is Expander ex) { expander = ex; break; }
        }
        if (skip || expander == null) return;
        if (DataContext is not MainViewModel vm) return;
        if (expander.DataContext is not ServerGroup group || !vm.Groups.Contains(group)) return;

        _dragGroup = group;
        _pressPoint = e.GetPosition(GroupsItemsControl);
    }

    private void GroupsItemsControl_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragGroup == null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            // دکمه بدون رویداد Released رها شد (رویداد توسط کنترل دیگری مصرف شده)
            EndGroupDrag();
            return;
        }
        var p = e.GetPosition(GroupsItemsControl);
        if (!_dragActive)
        {
            if (Math.Abs(p.X - _pressPoint.X) < 6 && Math.Abs(p.Y - _pressPoint.Y) < 6) return;
            _dragActive = true;
            _dragPointer = e.Pointer;
            e.Pointer.Capture(GroupsItemsControl);
            GroupsItemsControl.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        if (DataContext is not MainViewModel vm) return;
        int cur = vm.Groups.IndexOf(_dragGroup);
        if (cur < 0) { EndGroupDrag(); return; }
        int target = GroupIndexAt(e);
        if (target >= 0 && target != cur)
            vm.Groups.Move(cur, target);
    }

    private void GroupsItemsControl_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragGroup == null) return;
        EndGroupDrag();
    }

    private void GroupsItemsControl_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragGroup == null) { _dragActive = false; return; }
        EndGroupDrag();
    }

    private void EndGroupDrag()
    {
        bool persist = _dragActive;
        _dragGroup = null;
        _dragActive = false;
        GroupsItemsControl.Cursor = null;
        if (_dragPointer != null)
        {
            try { _dragPointer.Capture(null!); } catch { }
            _dragPointer = null;
        }
        if (persist && DataContext is MainViewModel vm)
            vm.PersistGroupOrder();
    }

    private int GroupIndexAt(PointerEventArgs e)
    {
        if (GroupsItemsControl.ItemsPanelRoot is not Panel panel) return -1;
        var p = e.GetPosition(panel);
        for (int i = 0; i < panel.Children.Count; i++)
        {
            var b = panel.Children[i].Bounds;
            if (p.X >= b.X && p.X <= b.X + b.Width && p.Y >= b.Y && p.Y <= b.Y + b.Height)
                return i;
        }
        return -1;
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

using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Threading;
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
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "roxa-icon.ico");
        if (System.IO.File.Exists(iconPath))
            Icon = new WindowIcon(iconPath);
        Opened += OnOpened;
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Shutdown();
        };
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
        vm.OpenDnsSettingsHandler = () => OpenDnsDialog();
        vm.ShowAdminWarningHandler = () =>
        {
            var btnOk = new Button { Content = "ادامه", Width = 80 };
            var btnRestart = new Button { Content = "اجرای مجدد به عنوان ادمین", Width = 160 };
            var win = new Window
            {
                Title = "نیاز به دسترسی Administrator",
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
                        new TextBlock { Text = "حالت TUN نیاز به دسترسی Administrator دارد.\nبرای فعال کردن، برنامه باید مجدداً با دسترسی ادمین اجرا شود.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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

        var btnSave = new Button
        {
            Content = "ذخیره",
            Padding = new Avalonia.Thickness(24, 10),
            CornerRadius = new Avalonia.CornerRadius(8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0E7C6B")),
            Foreground = Avalonia.Media.Brushes.White,
            FontSize = 13,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        };

        var btnCancel = new Button
        {
            Content = "لغو",
            Padding = new Avalonia.Thickness(24, 10),
            CornerRadius = new Avalonia.CornerRadius(8),
            FontSize = 13
        };

        var window = new Window
        {
            Title = "تنظیمات DNS",
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
                    new TextBlock { Text = "DNS ساده", FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 13 },
                    txtServers,
                    new TextBlock { Text = "IPv4/IPv6 با کاما جدا شده", FontSize = 11, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#888888")) },
                    new TextBlock { Text = "DNS پیشرفته (JSON)", FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 13 },
                    txtJson,
                    new TextBlock { Text = "مثال: {\"servers\": [{\"address\": \"1.1.1.1\"}]}", FontSize = 11, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#888888")) },
                    new TextBlock
                    {
                        FontSize = 11,
                        Margin = new Avalonia.Thickness(0, 4, 0, 0),
                        Text = "مستندات: https://xtls.github.io/config/dns.html"
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
}

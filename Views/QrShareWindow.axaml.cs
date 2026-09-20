using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using QRCoder;

namespace XrayClient.Views;

public partial class QrShareWindow : Window
{
    private readonly string _link;
    private readonly IClipboard? _clipboard;

    public QrShareWindow(string name, string link, IClipboard? clipboard)
    {
        InitializeComponent();
        _link = link;
        _clipboard = clipboard;
        var tr = Localization.TranslationManager.Instance;
        Title = tr["Dlg_ShareQR"];
        this.FindControl<TextBlock>("NameText")!.Text = name;
        this.FindControl<TextBox>("LinkBox")!.Text = link;
        this.FindControl<Button>("CopyBtn")!.Content = tr["Dlg_CopyLink"];
        this.FindControl<Button>("CloseBtn")!.Content = tr["Dlg_Close"];
        RenderQr(link);
    }

    // برای دیزاینر
    public QrShareWindow()
    {
        InitializeComponent();
        _link = "";
    }

    private void RenderQr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        byte[] png = qr.GetGraphic(8);
        using var ms = new MemoryStream(png);
        this.FindControl<Image>("QrImage")!.Source = new Bitmap(ms);
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (_clipboard != null)
            await _clipboard.SetTextAsync(_link);
        Close();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

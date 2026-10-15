// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using QRCoder;

namespace Roxa.Views;

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
        // XAML این پنجره را ثابت RightToLeft کرده بود؛ باید از زبانِ فعال بیاید.
        // enum را واجد می‌نویسیم چون FlowDirection رویِ خودِ Window هم پراپرتی است
        // و در Avalonia.Media تعریف شده (نه Avalonia.Layout).
        FlowDirection = tr.IsRightToLeft
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;
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
        try
        {
            if (_clipboard != null)
                await _clipboard.SetTextAsync(_link);
        }
        catch
        {
            // async void؛ استثنا نباید از اینجا رد شود و برنامه را بیندازد.
        }
        Close();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

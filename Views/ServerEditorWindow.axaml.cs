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
            // Without detach, each dialog opening added one more handler on ServerInfo
            // (whose lifetime spans the whole app) → a leak plus RefreshPreview running repeatedly.
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
        ApplyFlowDirection();
    }

    /// <summary>
    /// Builds the window direction from the active language. In this window's XAML it was
    /// hard-coded to RightToLeft, and picking English or Urdu left the layout right-to-left
    /// — or the reverse, Urdu shown left-to-right. Here we set the final value after
    /// LoadComponent.
    /// </summary>
    private void ApplyFlowDirection()
    {
        try
        {
            // "FlowDirection" is also a property on Window itself, so this identifier binds
            // to that property and reaching RightToLeft raises CS0176. The enum lives in
            // Avalonia.Media (not Avalonia.Layout).
            FlowDirection = Localization.TranslationManager.Instance.IsRightToLeft
                ? Avalonia.Media.FlowDirection.RightToLeft
                : Avalonia.Media.FlowDirection.LeftToRight;
        }
        catch { }
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

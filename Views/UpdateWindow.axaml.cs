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

namespace Roxa.Views;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        var tr = Localization.TranslationManager.Instance;
        Title = tr["UpdWin_Title"];
        // The window follows the active language; FlowDirection lives in
        // Avalonia.Media and clashes with the Window property of the same name.
        FlowDirection = tr.IsRightToLeft
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;
    }
}

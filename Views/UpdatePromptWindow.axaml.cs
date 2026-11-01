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

public partial class UpdatePromptWindow : Window
{
    /// <summary>True only when the user pressed the yes button; the X counts as no.</summary>
    public bool Accepted { get; private set; }

    // For the designer (AVLN3001: the runtime loader wants a public parameterless ctor).
    public UpdatePromptWindow() : this(new Core.UpdateInfo())
    {
    }

    public UpdatePromptWindow(Core.UpdateInfo info)
    {
        InitializeComponent();
        var tr = Localization.TranslationManager.Instance;
        Title = tr["UpdPrompt_Title"];
        FlowDirection = tr.IsRightToLeft
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;
        MessageText.Text = string.Format(tr["UpdPrompt_Msg"], info.Latest);
        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            NotesText.Text = info.Notes;
            NotesText.IsVisible = true;
        }
        YesBtn.Content = tr["UpdPrompt_Yes"];
        NoBtn.Content = tr["UpdPrompt_No"];
        YesBtn.Click += (_, _) => { Accepted = true; Close(); };
        NoBtn.Click += (_, _) => Close();
    }
}

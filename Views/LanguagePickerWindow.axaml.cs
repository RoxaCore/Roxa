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

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Roxa.Localization;

namespace Roxa.Views;

/// <summary>
/// First-run language picker. Shown instead of the main window when settings.json
/// does not exist yet, so the whole UI is built in the user's language from the
/// very first window instead of opening Persian and asking for a restart later.
///
/// The list uses each language's own endonym and is never translated — a user who
/// cannot read the current UI must still find their language. Selection is applied
/// live (culture, window direction, both labels), so the choice is visible before
/// it is confirmed.
/// </summary>
public partial class LanguagePickerWindow : Window
{
    /// <summary>Raised with the chosen language code after it has been saved.</summary>
    public event Action<string>? LanguageChosen;

    private string _code = Languages.DefaultCode;

    public LanguagePickerWindow()
    {
        InitializeComponent();

        foreach (var (name, _) in Languages.All)
            LangList.Items.Add(name);

        // Default to the product default (English), not to the first row: the list
        // starts with Persian for consistency with the settings page order.
        int idx = Array.FindIndex(Languages.All, l => l.Code == Languages.DefaultCode);
        LangList.SelectedIndex = idx >= 0 ? idx : 0;

        LangList.SelectionChanged += (_, _) => ApplyLanguage();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        if (LangList.SelectedIndex < 0 || LangList.SelectedIndex >= Languages.All.Length) return;
        _code = Languages.All[LangList.SelectedIndex].Code;
        TranslationManager.Instance.SetCulture(_code);

        // The enum is written fully qualified because FlowDirection is also a
        // property on the window and would bind to it.
        FlowDirection = TranslationManager.Instance.IsRightToLeft
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;

        TitleText.Text = TranslationManager.Instance["LangPick_Title"];
        ContinueBtn.Content = TranslationManager.Instance["LangPick_Continue"];
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Save before the main window exists: its constructor reads this file,
            // and the culture is already applied, so no restart is needed.
            var settings = Core.Storage.LoadSettings();
            settings.Language = _code;
            Core.Storage.SaveSettings(settings);
        }
        catch { }

        LanguageChosen?.Invoke(_code);
        Close();
    }
}

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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;

namespace Roxa.ViewModels;

/// <summary>
/// State of the small update window (check → download → install as one modal).
/// It holds only what the window shows — the status line, the bar and which of
/// the three buttons is available. The flow itself runs in MainViewModel,
/// which feeds these properties as it goes; Cancel and Retry are callbacks
/// back into that flow.
/// </summary>
public partial class UpdateWindowViewModel : ViewModelBase
{
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canClose;

    /// <summary>Wired by the window itself: closes it.</summary>
    public Action? CloseWindow { get; set; }

    /// <summary>Wired by MainViewModel: aborts the flow (the window closes with it).</summary>
    public Action? CancelRequested { get; set; }

    /// <summary>Wired by MainViewModel: runs the whole flow again.</summary>
    public Action? RetryRequested { get; set; }

    [RelayCommand] private void Close() => CloseWindow?.Invoke();
    [RelayCommand] private void Cancel() => CancelRequested?.Invoke();
    [RelayCommand] private void Retry() => RetryRequested?.Invoke();

    /// <summary>The check is running: only the status line, and Close gets out of it.</summary>
    public void ShowChecking()
    {
        VersionText = "";
        StatusText = TranslationManager.Instance["UpdWin_Checking"];
        IsProgressVisible = false;
        CanCancel = false;
        CanRetry = false;
        CanClose = true;
    }

    public void ShowVersion(UpdateInfo info)
        => VersionText = string.Format(TranslationManager.Instance["UpdWin_NewVersion"], info.Latest);

    /// <summary>Download in flight: the bar follows the received percentage.</summary>
    public void ShowDownloading(double percent)
    {
        StatusText = TranslationManager.Instance["Upd_Downloading"];
        double p = Math.Clamp(percent, 0, 100);
        Progress = p;
        PercentText = string.Format(TranslationManager.Instance["Upd_Percent"], (int)Math.Round(p));
        IsProgressVisible = true;
        CanCancel = true;
        CanRetry = false;
        CanClose = false;
    }

    /// <summary>Bytes are on disk; only the swap remains — no percentage, no way out.</summary>
    public void ShowInstalling()
    {
        StatusText = TranslationManager.Instance["Upd_Installing"];
        Progress = 100;
        PercentText = string.Format(TranslationManager.Instance["Upd_Percent"], 100);
        IsProgressVisible = true;
        CanCancel = false;
        CanRetry = false;
        CanClose = false;
    }

    public void ShowUpToDate()
    {
        StatusText = TranslationManager.Instance["UpdWin_UpToDate"];
        IsProgressVisible = false;
        CanCancel = false;
        CanRetry = false;
        CanClose = true;
    }

    public void ShowFailed()
    {
        StatusText = TranslationManager.Instance["Upd_Failed"];
        IsProgressVisible = false;
        CanCancel = false;
        CanRetry = true;
        CanClose = true;
    }

    /// <summary>The check itself could not run (offline / server error) — not "up to date".</summary>
    public void ShowCheckFailed()
    {
        StatusText = TranslationManager.Instance["UpdWin_CheckFailed"];
        IsProgressVisible = false;
        CanCancel = false;
        CanRetry = true;
        CanClose = true;
    }

    /// <summary>The flow cancelled itself (or was cancelled): get rid of the window.</summary>
    public void CloseNow() => CloseWindow?.Invoke();
}

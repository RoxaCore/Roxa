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

using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    /// <summary>Report dialog: (automatic info text, the send function carrying the user's text).</summary>
    public Action<string, Func<string, Task>>? ShowBugReportHandler { get; set; }

    [RelayCommand]
    private void OpenBugReport()
    {
        ShowBugReportHandler?.Invoke(BuildReportInfoText(), SendBugReportAsync);
    }

    /// <summary>The automatic part of a report: version, OS, id and connection state.</summary>
    private string BuildReportInfoText()
    {
        string state = IsConnected
            ? string.Format(TranslationManager.Instance["Rpt_StateOn"], SelectedServer?.Name ?? "-")
            : TranslationManager.Instance["Rpt_StateOff"];
        return string.Format(
            TranslationManager.Instance["Rpt_AutoInfo"],
            RemoteConfig.AppVersion, BugReport.OsInfo(), DeviceId, state);
    }

    public async Task SendBugReportAsync(string text)
    {
        try
        {
            var p = BugReport.Create("manual");
            p.Message = text ?? "";
            p.Log = GetLogTail(40);
            bool ok = await BugReport.SendAsync(p, IsConnected ? _engine.HttpPort : (int?)null);
            if (ok)
            {
                AppendLog(TranslationManager.Instance["Log_ReportSent"]);
                ToastHandler?.Invoke(TranslationManager.Instance["Toast_ReportSent"]);
            }
            else
            {
                BugReport.Enqueue(p);
                AppendLog(TranslationManager.Instance["Log_ReportFailed"]);
                ToastHandler?.Invoke(TranslationManager.Instance["Log_ReportFailed"]);
            }
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
        }
    }

    /// <summary>The last log lines attached to a report (also used for crashes).</summary>
    public string GetLogTail(int max)
    {
        lock (_logLock)
        {
            int start = Math.Max(0, _allLogLines.Count - max);
            var sb = new System.Text.StringBuilder();
            for (int i = start; i < _allLogLines.Count; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(_allLogLines[i]);
            }
            return sb.ToString();
        }
    }
}

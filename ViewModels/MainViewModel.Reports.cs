using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    /// <summary>دیالوگ گزارش: (متن اطلاعات خودکار، تابع ارسال با متن کاربر).</summary>
    public Action<string, Func<string, Task>>? ShowBugReportHandler { get; set; }

    [RelayCommand]
    private void OpenBugReport()
    {
        ShowBugReportHandler?.Invoke(BuildReportInfoText(), SendBugReportAsync);
    }

    /// <summary>بخش خودکار گزارش: نسخه، سیستم‌عامل، شناسه و وضعیت اتصال.</summary>
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

    /// <summary>آخرین خطوط لاگ برای ضمیمه به گزارش (برای کرش هم از همین استفاده می‌شود).</summary>
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

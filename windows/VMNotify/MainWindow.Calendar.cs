using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;

namespace VMNotify;

public partial class MainWindow
{
    private readonly HttpClient calendarClient = new();
    private readonly CancellationTokenSource calendarLifetime = new();
    private readonly DispatcherTimer calendarTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CalendarUpdates calendarUpdates = null!;
    private bool calendarWorking;

    private void InitializeCalendar()
    {
        calendarUpdates = new(calendarClient, Path.Combine(Path.GetDirectoryName(settingsFile)!, "china-work-calendar.json"));
        if (!previewMode) calendarUpdates.Load();
        CalendarAutoInput.IsChecked = saved.CalendarAutoUpdate;
        CalendarDayInput.Text = saved.CalendarUpdateDay.ToString();
        CalendarTimeInput.Text = saved.CalendarUpdateTime;
        RefreshCalendarStatus();
        calendarTimer.Tick += async (_, _) => {
            if (quitting || calendarWorking) return;
            try {
                if (CalendarUpdateSchedule.NextAttempt(saved, calendarUpdates.State, DateTime.Now) is { } next && next <= DateTime.Now)
                    await RunCalendarUpdate();
                RefreshCalendarStatus();
            } catch (ArgumentException) { RefreshCalendarStatus(); }
        };
        if (!previewMode) calendarTimer.Start();
    }

    private void RefreshCalendarStatus()
    {
        CalendarCoverageText.Text = "已覆盖年份：" + string.Join("、", ChinaWorkCalendar.SupportedYears);
        CalendarResultText.Text = calendarWorking ? "正在更新节假日日历…" : calendarUpdates.State.Message;
        static string Timestamp(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm") ?? "无";
        CalendarHistoryText.Text = "最近尝试：" + Timestamp(calendarUpdates.State.LastAttempt)
            + "\n最近成功：" + Timestamp(calendarUpdates.State.LastSuccess);
        try {
            var next = CalendarUpdateSchedule.NextAttempt(saved, calendarUpdates.State, DateTime.Now);
            CalendarNextText.Text = calendarWorking ? "正在执行更新，请稍候。" : next == null ? "自动更新未开启，可随时手动更新。"
                : next <= DateTime.Now ? "自动更新待执行（程序运行时检查）。" : $"下次自动尝试：{next:yyyy-MM-dd HH:mm}（本机时间）";
        } catch (ArgumentException ex) { CalendarNextText.Text = "自动更新设置有误：" + ex.Message; }
    }

    private async void UpdateCalendarClicked(object sender, RoutedEventArgs e) => await RunCalendarUpdate();

    private async Task RunCalendarUpdate()
    {
        if (previewMode || calendarWorking || quitting) return;
        calendarWorking = true; UpdateCalendarButton.IsEnabled = false;
        RefreshCalendarStatus();
        try { await calendarUpdates.Update(calendarLifetime.Token); }
        catch (OperationCanceledException) { }
        finally {
            calendarWorking = false;
            if (!quitting) {
                UpdateCalendarButton.IsEnabled = true;
                RefreshCalendarStatus();
                Diagnostics.Write("Calendar update: " + calendarUpdates.State.Message);
            }
        }
    }
}

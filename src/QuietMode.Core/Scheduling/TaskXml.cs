using System;
using System.Globalization;
using System.Text;
using QuietMode.Core.Models;

namespace QuietMode.Core.Scheduling
{
    /// <summary>
    /// 唯一任务的任务 XML。放在 Core 里是为了能离线单测：XML 里每一个数字都直接决定
    /// 「夜里会不会漏跑最后十分钟」「白天重试会不会撞进夜间窗口」，不能靠肉眼核对。
    ///
    /// 约定：
    ///   * 夜间触发器从 nightStart 起，Repetition 的 Duration 等于整个窗口长度；
    ///   * 白天触发器从 dayStart 起，附加一段有界重试（失败可再试，且在下一个夜间窗口前结束）；
    ///   * 两个触发器都按本地时间每天一次，跨午夜由窗口长度覆盖，不依赖磁盘号或用户名。
    /// </summary>
    public static class TaskXml
    {
        public const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        /// <summary>夜间窗口的分钟数（nightStart → dayStart，跨午夜自动 +1 天）。非法返回 0。</summary>
        public static int WindowMinutes(string nightStart, string dayStart)
        {
            int n = TimeText.ToMinutes(nightStart);
            int d = TimeText.ToMinutes(dayStart);
            if (n < 0 || d < 0 || n == d) return 0;
            int span = d - n;
            if (span <= 0) span += TimeOfDay.MinutesPerDay;
            return span;
        }

        /// <summary>ISO-8601 时长（Task Scheduler 的 Repetition/Duration 只认这个）。0 分量省略。</summary>
        public static string IsoDuration(int minutes)
        {
            if (minutes <= 0) return "";
            int h = minutes / 60, m = minutes % 60;
            if (h > 0 && m > 0) return "PT" + h.ToString(CultureInfo.InvariantCulture) + "H" + m.ToString(CultureInfo.InvariantCulture) + "M";
            if (h > 0) return "PT" + h.ToString(CultureInfo.InvariantCulture) + "H";
            return "PT" + m.ToString(CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>白天重试窗口：dayStart → nightStart 之间，留 30 分钟余量，最长 4 小时。</summary>
        public static int DayRetryMinutes(string nightStart, string dayStart)
        {
            int span = WindowMinutes(dayStart, nightStart);
            if (span <= 30) return 0;
            return Math.Min(4 * 60, span - 30);
        }

        public static string Build(ScheduleSettings s, string command, string arguments, string workingDir,
                                   string description, string taskUri, DateTime today)
        {
            if (s == null) s = new ScheduleSettings();
            int interval = s.ReconcileIntervalMinutes > 0 ? s.ReconcileIntervalMinutes : 10;
            string night = string.IsNullOrWhiteSpace(s.NightStart) ? "23:58" : s.NightStart.Trim();
            string day = string.IsNullOrWhiteSpace(s.DayStart) ? "10:02" : s.DayStart.Trim();
            if (TimeText.ToMinutes(night) < 0) night = "23:58";
            if (TimeText.ToMinutes(day) < 0) day = "10:02";

            // 夜间重复必须覆盖整个窗口：23:58 → 10:02 是 10 小时 4 分。
            // 早先写死 PT10H，最后 4 分钟没人管；窗口一改更是全错。
            int windowMinutes = WindowMinutes(night, day);
            if (windowMinutes <= 0) windowMinutes = 10 * 60 + 4;
            string nightDuration = IsoDuration(windowMinutes);

            int dayRetry = DayRetryMinutes(night, day);
            string dayInterval = IsoDuration(Math.Max(15, interval * 3));
            string dayDuration = dayRetry >= 15 ? IsoDuration(dayRetry) : "";

            string start = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"" + Namespace + "\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Author>QuietMode</Author>");
            sb.AppendLine("    <Description>" + Escape(description ?? "QuietMode 统一调度：维护闸门 + 磁盘脱机 + 风扇配置") + "</Description>");
            if (!string.IsNullOrEmpty(taskUri)) sb.AppendLine("    <URI>" + Escape(taskUri) + "</URI>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine(CalendarTrigger(start + "T" + night + ":00", "PT" + interval.ToString(CultureInfo.InvariantCulture) + "M",
                                          nightDuration, true));
            sb.AppendLine(CalendarTrigger(start + "T" + day + ":00", dayDuration.Length > 0 ? dayInterval : "",
                                          dayDuration, dayDuration.Length > 0));
            sb.AppendLine("    <BootTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Delay>PT2M</Delay>");
            sb.AppendLine("    </BootTrigger>");
            sb.AppendLine("    <EventTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Subscription>&lt;QueryList&gt;&lt;Query Id=\"0\" Path=\"System\"&gt;&lt;Select Path=\"System\"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>");
            sb.AppendLine("      <Delay>PT30S</Delay>");
            sb.AppendLine("    </EventTrigger>");
            sb.AppendLine("    <EventTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Subscription>&lt;QueryList&gt;&lt;Query Id=\"0\" Path=\"Security\"&gt;&lt;Select Path=\"Security\"&gt;*[System[Provider[@Name='Microsoft-Windows-Security-Auditing'] and EventID=4801]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>");
            sb.AppendLine("      <Delay>PT10S</Delay>");
            sb.AppendLine("    </EventTrigger>");
            sb.AppendLine("    <LogonTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Delay>PT45S</Delay>");
            sb.AppendLine("    </LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>S-1-5-18</UserId>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            // IgnoreNew：同一时刻只有一个 Worker，绝不允许两个进程同时动磁盘。
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            // 永不唤醒睡眠中的机器：为了安静而把用户叫醒是本末倒置。
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + Escape(command) + "</Command>");
            sb.AppendLine("      <Arguments>" + Escape(arguments) + "</Arguments>");
            if (!string.IsNullOrEmpty(workingDir))
                sb.AppendLine("      <WorkingDirectory>" + Escape(workingDir) + "</WorkingDirectory>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private static string CalendarTrigger(string startBoundary, string interval, string duration, bool withRepetition)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("    <CalendarTrigger>");
            sb.AppendLine("      <StartBoundary>" + startBoundary + "</StartBoundary>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <ScheduleByDay>");
            sb.AppendLine("        <DaysInterval>1</DaysInterval>");
            sb.AppendLine("      </ScheduleByDay>");
            if (withRepetition && interval.Length > 0)
            {
                sb.AppendLine("      <Repetition>");
                sb.AppendLine("        <Interval>" + interval + "</Interval>");
                sb.AppendLine("        <Duration>" + duration + "</Duration>");
                // 窗口一结束就停：不让「夜间重复」跑进白天。
                sb.AppendLine("        <StopAtDurationEnd>true</StopAtDurationEnd>");
                sb.AppendLine("      </Repetition>");
            }
            sb.Append("    </CalendarTrigger>");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}

using System;
using QuietMode.Core.Models;

namespace QuietMode.Core.Scheduling
{
    public enum TimeBand
    {
        Day = 0,
        Night
    }

    /// <summary>
    /// 一次时间判定的完整结果。带原因，便于日志和 UI 说明「为什么现在是这个状态」，
    /// 而不是只给一个布尔值。
    /// </summary>
    public sealed class WindowEvaluation
    {
        public TimeBand Band = TimeBand.Day;
        public int NowMinutes;
        public int NightStartMinutes = -1;
        public int DayStartMinutes = -1;
        /// <summary>本次实际用于判定白天起点的时间（开机补偿时与 DayStartMinutes 不同）。</summary>
        public int EffectiveDayStartMinutes = -1;
        public bool StartupOverrideApplied;
        public string Reason = "";

        public bool IsNight { get { return Band == TimeBand.Night; } }

        public string BandText { get { return Band == TimeBand.Night ? "夜间" : "白天"; } }

        public string ToLine()
        {
            return BandText + "（现在 " + TimeOfDay.Format(NowMinutes)
                   + "，夜间 " + TimeOfDay.Format(NightStartMinutes)
                   + " → 白天 " + TimeOfDay.Format(EffectiveDayStartMinutes)
                   + (StartupOverrideApplied ? "，已应用开机补偿" : "")
                   + "）：" + Reason;
        }
    }

    public static class TimeOfDay
    {
        public const int MinutesPerDay = 24 * 60;

        /// <summary>解析 HH:mm；非法返回 -1。分钟必须两位，小时允许一位。</summary>
        public static int Parse(string text)
        {
            int h, m;
            if (!TimeText.TryParseHhmm(text, out h, out m)) return -1;
            return h * 60 + m;
        }

        public static string Format(int minutes)
        {
            if (minutes < 0) return "--:--";
            int m = ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "{0:00}:{1:00}", m / 60, m % 60);
        }

        public static int Of(DateTime t) { return t.Hour * 60 + t.Minute; }
    }

    /// <summary>
    /// 夜间窗口判定（规格 §6.2）。三个约束必须同时满足，缺一个就会退化成 v1 那种
    /// 「只比较小时」的错误：
    ///   1. 用完整的 HH:mm，不是 Get-Date.Hour；
    ///   2. 跨午夜窗口 23:58 → 次日 10:02 要正确；
    ///   3. 开机补偿要显式、可配置、可解释，不能悄悄改行为。
    /// </summary>
    public static class ScheduleWindow
    {
        public static WindowEvaluation Evaluate(ScheduleSettings s, DateTime now, TimeSpan? uptime)
        {
            WindowEvaluation w = new WindowEvaluation();
            w.NowMinutes = TimeOfDay.Of(now);
            if (s == null) { w.Reason = "没有调度配置，按白天处理"; return w; }

            int night = TimeOfDay.Parse(s.NightStart);
            int day = TimeOfDay.Parse(s.DayStart);
            w.NightStartMinutes = night;
            w.DayStartMinutes = day;
            w.EffectiveDayStartMinutes = day;

            if (night < 0 || day < 0)
            {
                w.Band = TimeBand.Day;
                w.Reason = "时间格式无效（nightStart=" + (s.NightStart ?? "") + "，dayStart=" + (s.DayStart ?? "") + "），按白天处理";
                return w;
            }
            if (night == day)
            {
                w.Band = TimeBand.Day;
                w.Reason = "夜间起点与白天起点相同（" + TimeOfDay.Format(night) + "），窗口为空，按白天处理";
                return w;
            }

            w.Band = InWindow(w.NowMinutes, night, day) ? TimeBand.Night : TimeBand.Day;
            w.Reason = w.Band == TimeBand.Night
                ? "当前处于夜间窗口"
                : "当前不在夜间窗口";

            // 开机补偿：刚开机不到 N 分钟且已经过了「开机后白天起点」，把早晨那次开机算白天。
            // 00:00–05:59 的无人值守重启不在补偿范围内，仍然保持夜间。
            if (w.Band == TimeBand.Night && s.StartupOverrideEnabled && uptime.HasValue)
            {
                int startupDay = TimeOfDay.Parse(s.StartupDayStart);
                int grace = s.StartupGraceMinutes;
                double up = uptime.Value.TotalMinutes;
                if (startupDay >= 0 && grace > 0 && up >= 0 && up < grace
                    && w.NowMinutes >= startupDay && w.NowMinutes < day)
                {
                    w.Band = TimeBand.Day;
                    w.StartupOverrideApplied = true;
                    w.EffectiveDayStartMinutes = startupDay;
                    w.Reason = "开机 " + (int)up + " 分钟（阈值 " + grace + " 分钟）且已过 "
                               + TimeOfDay.Format(startupDay) + "，开盘按白天处理";
                }
            }

            return w;
        }

        /// <summary>
        /// minutes 是否落在 [nightStart, dayStart) 这个可能跨午夜的窗口里。
        /// nightStart &gt; dayStart 表示跨午夜（默认 23:58 → 10:02）。
        /// </summary>
        public static bool InWindow(int minutes, int nightStart, int dayStart)
        {
            if (nightStart < 0 || dayStart < 0 || nightStart == dayStart) return false;
            minutes = ((minutes % TimeOfDay.MinutesPerDay) + TimeOfDay.MinutesPerDay) % TimeOfDay.MinutesPerDay;
            if (nightStart > dayStart) return minutes >= nightStart || minutes < dayStart;
            return minutes >= nightStart && minutes < dayStart;
        }
    }

    /// <summary>
    /// 夜间窗口的身份。「今晚」不是一个布尔值，而是一段具体的窗口：默认 23:58 开始、
    /// 次日 10:02 结束。任何「只对今晚有效」的决定都必须绑定到这个身份上，否则跨午夜、
    /// 重启、改时间之后就无法判断这个决定是不是过期了。
    ///
    /// 取值 = 窗口开始那一天的本地日期（yyyy-MM-dd）。跨午夜窗口在午夜之后仍属于前一天。
    /// </summary>
    public static class NightWindow
    {
        /// <summary>now 所在的夜间窗口；不在窗口内时返回「下一个即将开始的窗口」。</summary>
        public static string Key(ScheduleSettings s, DateTime now)
        {
            if (s == null) return "";
            int night = TimeOfDay.Parse(s.NightStart);
            int day = TimeOfDay.Parse(s.DayStart);
            if (night < 0 || day < 0 || night == day) return "";

            int nowMinutes = TimeOfDay.Of(now);
            if (ScheduleWindow.InWindow(nowMinutes, night, day))
            {
                // 跨午夜：00:00–dayStart 这一段属于前一天开始的那个窗口。
                if (night > day && nowMinutes < day) return Format(now.Date.AddDays(-1));
                return Format(now.Date);
            }
            return Format(night > nowMinutes ? now.Date : now.Date.AddDays(1));
        }

        /// <summary>窗口的结束时刻（用于日志与文档，不参与判定）。</summary>
        public static DateTime EndOf(ScheduleSettings s, DateTime now)
        {
            DateTime start = StartOf(s, now);
            if (start == DateTime.MinValue) return DateTime.MinValue;
            int night = TimeOfDay.Parse(s.NightStart);
            int day = TimeOfDay.Parse(s.DayStart);
            DateTime end = start.Date.AddDays(1).AddMinutes(day);
            if (night < day) end = start.Date.AddMinutes(day);
            return end;
        }

        /// <summary>窗口的开始时刻（本地）。</summary>
        public static DateTime StartOf(ScheduleSettings s, DateTime now)
        {
            string key = Key(s, now);
            if (key.Length == 0) return DateTime.MinValue;
            DateTime date;
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.None, out date))
                return DateTime.MinValue;
            return date.AddMinutes(TimeOfDay.Parse(s.NightStart));
        }

        private static string Format(DateTime d)
        {
            return d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

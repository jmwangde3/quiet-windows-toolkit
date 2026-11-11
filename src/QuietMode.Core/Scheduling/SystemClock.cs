using System;
using System.Runtime.InteropServices;

namespace QuietMode.Core.Scheduling
{
    /// <summary>
    /// 系统时间来源。集中在一处，避免每个模块各自算运行时长。
    ///
    /// 为什么不用 Environment.TickCount：它是 32 位毫秒计数，24.9 天回绕成负数。
    /// ReconcileController 早期用 (TickCount &amp; 0x7FFFFFFF) 兜底，开机一个月后
    /// 会算出一个「刚开机几分钟」的假值，正好落进 schedule.startupGraceMinutes 里，
    /// 把 06:00–10:02 的夜间窗口误判成白天 —— 等于悄悄削弱夜间策略。
    /// 这里改用 GetTickCount64（Vista+，不会回绕），失败才退回单次回绕修正。
    /// </summary>
    public static class SystemClock
    {
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        /// <summary>系统运行时长；拿不到返回 null（调用方按「不做开机补偿」处理）。</summary>
        public static TimeSpan? Uptime()
        {
            try { return TimeSpan.FromMilliseconds(GetTickCount64()); }
            catch { }
            try
            {
                long ms = Environment.TickCount;
                if (ms < 0) ms += 4294967296L;      // 单次回绕（49.7 天内正确）
                return TimeSpan.FromMilliseconds(ms);
            }
            catch { return null; }
        }

        public static DateTime Now { get { return DateTime.Now; } }

        public static string NowIso()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

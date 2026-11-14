using System;
using System.Collections.Generic;
using QuietMode.Core.Json;
using QuietMode.Core.Models;

namespace QuietMode.Core.Scheduling
{
    /// <summary>
    /// 控制器状态（规格 §6.3）。所有状态只能由 StateMachine 产生，
    /// 任何模块都不允许自己改 ModeOn / SchedulePaused 而不经过它。
    /// </summary>
    public enum ControllerState
    {
        DayActive = 0,
        EnteringNight,
        NightActive,
        NightDegraded,
        EnteringDay,
        ManualQuiet,
        ManualNormal,
        SchedulePaused,
        RecoveryPending,
        Error
    }

    public static class ControllerStates
    {
        public static string Text(ControllerState s)
        {
            switch (s)
            {
                case ControllerState.DayActive: return "白天（未静音）";
                case ControllerState.EnteringNight: return "正在进入夜间";
                case ControllerState.NightActive: return "夜间静音已生效";
                case ControllerState.NightDegraded: return "夜间部分生效";
                case ControllerState.EnteringDay: return "正在恢复白天";
                case ControllerState.ManualQuiet: return "手动静音";
                case ControllerState.ManualNormal: return "手动关闭（今晚不再自动开启）";
                case ControllerState.SchedulePaused: return "调度已暂停";
                case ControllerState.RecoveryPending: return "等待恢复";
                default: return "错误";
            }
        }

        public static bool IsNight(ControllerState s)
        {
            return s == ControllerState.NightActive || s == ControllerState.NightDegraded
                   || s == ControllerState.ManualQuiet || s == ControllerState.EnteringNight;
        }

        public static bool TryParse(string text, out ControllerState state)
        {
            state = ControllerState.DayActive;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (ControllerState s in Enum.GetValues(typeof(ControllerState)))
            {
                if (string.Equals(s.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    state = s;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 决策输入。刻意把「已观测事实」和「已存状态」分开：状态机不做探测，
    /// 探测结果由调用方先填进来，这样它可以在没有管理员权限的机器上单测。
    /// </summary>
    public sealed class DecisionInput
    {
        public ScheduleSettings Schedule;
        public QuietModeState State;
        public WindowEvaluation Window;
        /// <summary>boot | resume | unlock | schedule | timer | manual | verify | cli</summary>
        public string Trigger = "manual";
        /// <summary>上一次夜间/白天执行是否失败（由 ReconcileController 回填）。</summary>
        public bool LastRunFailed;
        /// <summary>决策时刻（本地）。必须由调用方显式给出，否则「今晚」无法界定。</summary>
        public DateTime Now = DateTime.Now;
    }

    public sealed class Decision
    {
        public ControllerState State = ControllerState.DayActive;
        /// <summary>none | night | day | restore</summary>
        public string Action = "none";
        public string Reason = "";
        public bool TargetNight;
        public List<string> Notes = new List<string>();

        public bool NeedsWork { get { return Action != "none"; } }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("state", State.ToString());
            o.Set("stateText", ControllerStates.Text(State));
            o.Set("action", Action);
            o.Set("targetNight", TargetNight);
            o.Set("reason", Reason ?? "");
            JsonArray arr;
            o.SetArray("notes", out arr);
            foreach (string n in Notes) arr.Add(n);
            return o;
        }

        public string ToLine()
        {
            string head = ControllerStates.Text(State);
            if (!NeedsWork) return head + "：" + Reason;
            return head + "（动作 " + Action + "）：" + Reason;
        }
    }

    /// <summary>
    /// 唯一的状态决策点（规格 §6.3 / §6.4）。优先级从高到低：
    ///   1. 恢复事务未完成（安全）
    ///   2. 用户手动覆盖（ModeOn / AutoSilentSuppressed）
    ///   3. 用户暂停调度
    ///   4. 自动时间调度
    ///   5. 默认白天
    /// </summary>
    public static class StateMachine
    {
        /// <summary>
        /// 上一次执行是否算「必须再试一次」。NotRun 是「未执行」（没提权 / 没有动作），不当作失败，
        /// 否则界面上会一直挂着「重试」而实际什么都没跑。
        ///
        /// Degraded 要看分类：读不到（ObservationUnknown）不是偏差，每十分钟重跑整条夜间链
        /// 只会制造噪音；而可修复/需人工/安全阻塞的偏差是真的没到位，必须重试。
        /// </summary>
        public static bool IsLastRunFailed(QuietModeState st)
        {
            if (st == null) return false;
            string v = st.LastVerdict ?? "";
            if (v.Length == 0) return false;
            if (string.Equals(v, "Ok", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "NotRun", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(v, "Failed", StringComparison.OrdinalIgnoreCase)) return true;

            string d = st.LastDrift ?? "";
            if (d.Length == 0) return true;      // 旧状态没有分类字段：按「可能有问题」处理，宁可多试一次
            return string.Equals(d, "ActionableDrift", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(d, "UnfixableDrift", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(d, "SafetyBlocked", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 上次开机前留下的手动静音，不在白天开机时自动恢复。
        ///
        /// 手动静音是个持续覆盖（modeOn=true），它会跨关机活下来。夜里开启、第二天中午开机，
        /// 系统就忠实地在中午把夜间链重跑一遍 —— 磁盘置离线、维护任务禁用、风扇静音。逻辑上
        /// 没错（人确实按下过），但没有任何提示，看到的只是「中午盘掉线了」。
        ///
        /// 判定依据是 ModeOnSinceUtc 与本次开机时刻的比较，而不是「开机多久」：只看开机时长
        /// 会误伤「刚开机就按下静音，一分钟后到点 reconcile」这种正常用法；反过来，如果开机
        /// 那几次心跳没跑成，只看时长又会让守卫永远失效。
        ///
        /// 返回非空字符串表示已清除并给出说明，调用方把它塞进 Decision.Notes。
        /// </summary>
        public static string ExpireStaleManualOverride(QuietModeState st, WindowEvaluation w,
                                                      DateTime nowUtc, TimeSpan? uptime)
        {
            if (st == null || !st.ModeOn) return "";
            if (w == null || w.IsNight) return "";          // 夜里保留：静音本来就是要的

            DateTime bootUtc = uptime.HasValue ? nowUtc - uptime.Value : DateTime.MinValue;

            DateTime setUtc;
            bool hasStamp = DateTime.TryParse(st.ModeOnSinceUtc ?? "",
                                              System.Globalization.CultureInfo.InvariantCulture,
                                              System.Globalization.DateTimeStyles.AdjustToUniversal
                                              | System.Globalization.DateTimeStyles.AssumeUniversal,
                                              out setUtc);
            if (hasStamp)
            {
                // 留两分钟余量吸收时钟毛刺：本次开机之后按下的，一律保留。
                if (setUtc >= bootUtc.AddMinutes(-2)) return "";
            }
            else
            {
                // 旧状态（schema ≤ 4）没有时间戳：只有「刚开机」那几次心跳敢判定，别的时候不猜。
                if (!uptime.HasValue || uptime.Value.TotalMinutes > BootGuardMinutes) return "";
            }

            st.ModeOn = false;
            st.ModeOnSinceUtc = "";
            return "上次开机前开启的手动静音未在白天开机时恢复（需要静音请重新点一次）";
        }

        /// <summary>没有 ModeOnSinceUtc 可依据时，「刚开机」的判定阈值（分钟）。</summary>
        private const int BootGuardMinutes = 10;

        public static Decision Decide(DecisionInput input)
        {
            Decision d = new Decision();
            if (input == null) { d.State = ControllerState.Error; d.Reason = "没有决策输入"; return d; }

            QuietModeState st = input.State ?? new QuietModeState();
            ScheduleSettings sc = input.Schedule ?? new ScheduleSettings();
            WindowEvaluation w = input.Window;

            if (w == null)
            {
                d.State = ControllerState.Error;
                d.Reason = "缺少时间窗口判定结果";
                return d;
            }

            // 1. 恢复事务优先级最高：恢复没做完时不许再进夜间。
            if (st.Recovery != null && st.Recovery.Active)
            {
                d.State = ControllerState.RecoveryPending;
                d.Action = "restore";
                d.Reason = "上次恢复未完成（" + (string.IsNullOrEmpty(st.Recovery.Reason) ? "未记录原因" : st.Recovery.Reason) + "）";
                return d;
            }

            // 2. 用户手动覆盖。
            //
            // 手动静音过去无条件返回 action=night，于是每 10 分钟把整条夜间链重跑一遍：
            // 磁盘反复置离线、维护任务反复禁用、风扇配置反复重载。2026-11-14 00:49 到手动静音
            // 之后，00:49/00:58/01:08 各留下一条完整 5 步记录，就是这个原因。
            // 和自动夜间一样处理：已经生效且上次没失败就返回 none，剩下的交给回读后的漂移判定，
            // 真掉队了 RepairTarget 会强制重跑一次。
            if (st.ModeOn)
            {
                d.State = ControllerState.ManualQuiet;
                d.TargetNight = true;
                if (!w.IsNight) d.Notes.Add("当前不在夜间窗口，但手动静音优先于时间调度");
                if (!st.AutoSilentActive)
                {
                    d.Action = "night";
                    d.Reason = "用户手动开启静音";
                }
                else if (input.LastRunFailed)
                {
                    d.Action = "night";
                    d.Reason = "用户手动开启静音；上次执行有失败项，重试";
                }
                else
                {
                    d.Action = "none";
                    d.Reason = "用户手动开启静音（已生效）";
                }
                return d;
            }
            if (w.IsNight && IsSuppressed(st, sc, input.Now))
            {
                // 「今晚不再自动开启」只对按下按钮的那一夜有效。
                // 恢复到白天状态成功之后仍然保持 ManualNormal，绝不能顺手把标记清掉 ——
                // 那正是同一夜每十分钟又进一次夜间的原因。
                d.State = ControllerState.ManualNormal;
                d.TargetNight = false;
                if (st.AutoSilentActive)
                {
                    d.Action = "day";
                    d.Reason = "用户手动关闭了今晚的自动静音，正在恢复白天状态";
                }
                else
                {
                    d.Action = "none";
                    d.Reason = "用户手动关闭了今晚的自动静音（本夜不再进入夜间，下一夜恢复）";
                    d.Notes.Add("窗口 " + NightWindow.Key(sc, input.Now) + " 内保持抑制");
                }
                return d;
            }

            // 3. 暂停 / 调度关闭。
            if (!sc.Enabled)
            {
                d.State = ControllerState.SchedulePaused;
                d.Action = "none";
                d.Reason = "调度已关闭（schedule.enabled=false）";
                return d;
            }
            if (st.SchedulePaused)
            {
                d.State = ControllerState.SchedulePaused;
                d.Action = "none";
                d.Reason = "调度已暂停";
                if (w.IsNight && !st.AutoSilentActive)
                    d.Notes.Add("夜间窗口内但调度被暂停；Resume 后才会进入夜间");
                return d;
            }

            // 4. 自动时间调度。
            if (w.IsNight)
            {
                d.TargetNight = true;
                if (!st.AutoSilentActive)
                {
                    d.State = input.LastRunFailed ? ControllerState.NightDegraded : ControllerState.EnteringNight;
                    d.Action = "night";
                    d.Reason = w.Reason + "，尚未进入夜间";
                }
                else if (input.LastRunFailed)
                {
                    d.State = ControllerState.NightDegraded;
                    d.Action = "night";
                    d.Reason = "上次夜间执行有失败项，重试";
                }
                else
                {
                    d.State = ControllerState.NightActive;
                    d.Action = "none";
                    d.Reason = w.Reason + "，夜间已生效";
                }
                return d;
            }

            // 5. 白天。
            if (st.AutoSilentActive)
            {
                d.State = ControllerState.EnteringDay;
                d.Action = "day";
                d.Reason = w.Reason + "，需要恢复白天状态";
                return d;
            }
            // 白天恢复失败过就再试一次：磁盘还脱机、维护任务还没启用都属于「必须修好」，
            // 不能因为「时间上已经算白天」就当作完成。
            if (input.LastRunFailed && string.Equals(st.LastReconcileAction, "day", StringComparison.OrdinalIgnoreCase))
            {
                d.State = ControllerState.EnteringDay;
                d.Action = "day";
                d.Reason = "上次白天恢复有失败项，重试";
                d.Notes.Add("连续失败 " + st.ConsecutiveFailures + " 次");
                return d;
            }
            d.State = ControllerState.DayActive;
            d.Action = "none";
            d.Reason = w.Reason + "，已是白天状态";
            return d;
        }

        /// <summary>
        /// 抑制标记是否对本时刻有效。空窗口标识是旧版本写下的状态：按有效处理，
        /// 由 NormalizeSuppression 在拿到调度配置后补上窗口 —— 宁可多抑制一夜，
        /// 也不能因为读不懂自己的历史就提前把静音打开。
        /// </summary>
        public static bool IsSuppressed(QuietModeState st, ScheduleSettings s, DateTime now)
        {
            if (st == null || !st.AutoSilentSuppressed) return false;
            string key = NightWindow.Key(s, now);
            if (key.Length == 0) return true;
            if (string.IsNullOrEmpty(st.AutoSilentSuppressedWindow)) return true;
            return string.Equals(st.AutoSilentSuppressedWindow, key, StringComparison.Ordinal);
        }

        /// <summary>
        /// 把抑制标记绑定/解绑到当前夜间窗口。每次决策前调用一次即可（幂等）：
        ///   * 旧版本留下的空窗口 → 绑定到当前窗口（schema 3 → 4 的就地迁移）；
        ///   * 记录的窗口已经过去 → 清除抑制，下一夜自动恢复调度。
        /// </summary>
        public static void NormalizeSuppression(QuietModeState st, ScheduleSettings s, DateTime now)
        {
            if (st == null) return;
            if (!st.AutoSilentSuppressed) { st.AutoSilentSuppressedWindow = ""; return; }
            string key = NightWindow.Key(s, now);
            if (key.Length == 0) return;                 // 调度配置不可用，不动标记
            if (string.IsNullOrEmpty(st.AutoSilentSuppressedWindow))
            {
                st.AutoSilentSuppressedWindow = key;     // 迁移：旧状态视为「本夜抑制」
                return;
            }
            if (!string.Equals(st.AutoSilentSuppressedWindow, key, StringComparison.Ordinal))
            {
                st.AutoSilentSuppressed = false;
                st.AutoSilentSuppressedWindow = "";
            }
        }

        /// <summary>
        /// 用户按下「关闭自动静音」：抑制标记绑定到当前夜间窗口。
        /// 窗口之外按下去等于「跳过下一夜」，这也是用户能理解的语义。
        /// </summary>
        public static void Suppress(QuietModeState st, ScheduleSettings s, DateTime now)
        {
            if (st == null) return;
            st.ModeOn = false;
            st.AutoSilentSuppressed = true;
            st.AutoSilentSuppressedWindow = NightWindow.Key(s, now);
        }

        /// <summary>用户要求手动静音 / 交回自动调度：抑制立即作废。</summary>
        public static void ClearSuppression(QuietModeState st)
        {
            if (st == null) return;
            st.AutoSilentSuppressed = false;
            st.AutoSilentSuppressedWindow = "";
        }

        /// <summary>
        /// 把一次执行结果写回状态。集中放在这里，避免各模块各写各的。
        /// verdict: Ok | Degraded | Failed | NotRun
        /// </summary>
        public static void ApplyRun(QuietModeState st, string action, string verdict, string errorCode,
                                    DateTime utcNow, bool nightSucceeded)
        {
            if (st == null) return;
            string stamp = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            st.LastReconcileUtc = stamp;
            st.LastVerdict = verdict ?? "";
            st.LastErrorCode = errorCode ?? "";
            st.LastReconcileAction = action ?? "";

            bool ok = string.Equals(verdict, "Ok", StringComparison.OrdinalIgnoreCase);
            st.ConsecutiveFailures = ok ? 0 : st.ConsecutiveFailures + 1;

            if (string.Equals(action, "night", StringComparison.OrdinalIgnoreCase))
            {
                if (nightSucceeded)
                {
                    st.AutoSilentActive = true;
                    st.LastNightRunUtc = stamp;
                    // 夜间成功 = 用户要么手动开了、要么自动进了；抑制标记作废。
                    st.AutoSilentSuppressed = false;
                    st.AutoSilentSuppressedWindow = "";
                }
            }
            else if (string.Equals(action, "day", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(action, "restore", StringComparison.OrdinalIgnoreCase))
            {
                if (ok)
                {
                    st.AutoSilentActive = false;
                    st.LastDayRunUtc = stamp;
                    // 刻意不清 AutoSilentSuppressed：白天恢复成功只说明「已经回到白天」，
                    // 不代表用户改主意想今晚再静音一次。清理由窗口切换负责。
                    if (st.Recovery != null) st.Recovery.Active = false;
                }
            }
        }
    }
}

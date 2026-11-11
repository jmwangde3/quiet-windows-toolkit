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
        /// 上一次执行是否算失败。NotRun 是「未执行」（没提权 / 没有动作），不当作失败，
        /// 否则界面上会一直挂着「重试」而实际什么都没跑。
        /// </summary>
        public static bool IsLastRunFailed(QuietModeState st)
        {
            if (st == null) return false;
            string v = st.LastVerdict ?? "";
            if (v.Length == 0) return false;
            return !string.Equals(v, "Ok", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(v, "NotRun", StringComparison.OrdinalIgnoreCase);
        }

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
            if (st.ModeOn)
            {
                d.State = ControllerState.ManualQuiet;
                d.Action = "night";
                d.TargetNight = true;
                d.Reason = "用户手动开启静音";
                if (!w.IsNight) d.Notes.Add("当前不在夜间窗口，但手动静音优先于时间调度");
                return d;
            }
            if (st.AutoSilentSuppressed)
            {
                d.State = ControllerState.ManualNormal;
                d.Action = w.IsNight ? "day" : "none";
                d.Reason = "用户手动关闭了今晚的自动静音";
                if (!w.IsNight) d.Action = "none";
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
            d.State = ControllerState.DayActive;
            d.Action = "none";
            d.Reason = w.Reason + "，已是白天状态";
            return d;
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
                    // 夜间成功后清除「今晚不再自动开启」的抑制标记，否则明天又得手点一次。
                    st.AutoSilentSuppressed = false;
                }
            }
            else if (string.Equals(action, "day", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(action, "restore", StringComparison.OrdinalIgnoreCase))
            {
                if (ok)
                {
                    st.AutoSilentActive = false;
                    st.LastDayRunUtc = stamp;
                    st.AutoSilentSuppressed = false;
                    if (st.Recovery != null) st.Recovery.Active = false;
                }
            }
        }
    }
}

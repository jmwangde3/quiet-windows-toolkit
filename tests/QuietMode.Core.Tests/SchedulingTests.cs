using System;
using System.Collections.Generic;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Configuration;
using QuietMode.Core.Json;
using QuietMode.Core.Models;
using QuietMode.Core.Scheduling;

namespace QuietMode.Core.Tests
{
    /// <summary>
    /// Phase 4 单测：时间窗口、状态机优先级、reconcile 计划顺序、任务 XML 关键设置。
    /// 全部纯逻辑，不触碰系统状态。
    /// </summary>
    public static partial class Program
    {
        private static void RunSchedulingTests()
        {
            Run("时间：跨午夜窗口判定", delegate
            {
                Eq(true, ScheduleWindow.InWindow(23 * 60 + 58, 23 * 60 + 58, 10 * 60 + 2));
                Eq(true, ScheduleWindow.InWindow(0, 23 * 60 + 58, 10 * 60 + 2));
                Eq(true, ScheduleWindow.InWindow(9 * 60 + 59, 23 * 60 + 58, 10 * 60 + 2));
                Eq(false, ScheduleWindow.InWindow(10 * 60 + 2, 23 * 60 + 58, 10 * 60 + 2));
                Eq(false, ScheduleWindow.InWindow(12 * 60, 23 * 60 + 58, 10 * 60 + 2));
                Eq(false, ScheduleWindow.InWindow(23 * 60 + 57, 23 * 60 + 58, 10 * 60 + 2));
            });

            Run("时间：同日窗口与非法配置", delegate
            {
                Eq(true, ScheduleWindow.InWindow(3 * 60, 2 * 60, 10 * 60));
                Eq(false, ScheduleWindow.InWindow(11 * 60, 2 * 60, 10 * 60));
                Eq(false, ScheduleWindow.InWindow(12 * 60, 10 * 60, 10 * 60));
                Eq(false, ScheduleWindow.InWindow(12 * 60, -1, 10 * 60));
            });

            Run("时间：开机补偿只覆盖 06:00 之后的早晨", delegate
            {
                ScheduleSettings s = new ScheduleSettings();
                s.NightStart = "23:58";
                s.DayStart = "10:02";
                s.StartupOverrideEnabled = true;
                s.StartupDayStart = "06:00";
                s.StartupGraceMinutes = 15;

                // 09:39 开机 5 分钟 → 白天
                WindowEvaluation w = ScheduleWindow.Evaluate(s, new DateTime(2026, 11, 11, 9, 39, 0), TimeSpan.FromMinutes(5));
                Eq(TimeBand.Day, w.Band);
                Eq(true, w.StartupOverrideApplied);
                Eq(6 * 60, w.EffectiveDayStartMinutes);

                // 03:00 无人值守重启 → 仍是夜间
                WindowEvaluation night = ScheduleWindow.Evaluate(s, new DateTime(2026, 11, 11, 3, 0, 0), TimeSpan.FromMinutes(5));
                Eq(TimeBand.Night, night.Band);
                Eq(false, night.StartupOverrideApplied);

                // 已经开机 30 分钟 → 不补偿，09:39 仍是夜间
                WindowEvaluation old = ScheduleWindow.Evaluate(s, new DateTime(2026, 11, 11, 9, 39, 0), TimeSpan.FromMinutes(30));
                Eq(TimeBand.Night, old.Band);

                // 关掉补偿 → 09:39 回到夜间
                s.StartupOverrideEnabled = false;
                Eq(TimeBand.Night, ScheduleWindow.Evaluate(s, new DateTime(2026, 11, 11, 9, 39, 0), TimeSpan.FromMinutes(5)).Band);
            });

            Run("状态机：恢复事务优先级最高", delegate
            {
                DecisionInput i = NightInput(new DateTime(2026, 11, 12, 0, 30, 0));
                i.State.Recovery.Active = true;
                i.State.Recovery.Reason = "上次白天恢复没做完";
                i.State.ModeOn = true;          // 手动静音也不能压过恢复
                Decision d = StateMachine.Decide(i);
                Eq(ControllerState.RecoveryPending, d.State);
                Eq("restore", d.Action);
                True(d.Reason.Contains("没做完"), "应带出恢复原因");
            });

            Run("状态机：手动静音压过时间调度", delegate
            {
                DecisionInput i = NightInput(new DateTime(2026, 11, 11, 14, 0, 0));  // 白天
                i.State.ModeOn = true;
                Decision d = StateMachine.Decide(i);
                Eq(ControllerState.ManualQuiet, d.State);
                Eq("night", d.Action);
                True(d.Notes.Exists(n => n.Contains("手动静音")), "应说明手动静音优先");
            });

            Run("状态机：手动关闭只覆盖本夜，白天恢复成功也不能解除抑制", delegate
            {
                ScheduleSettings sc = DefaultSchedule();
                DateTime press = new DateTime(2026, 11, 12, 1, 0, 0);       // 夜间按下
                QuietModeState st = new QuietModeState();
                st.AutoSilentActive = true;

                StateMachine.Suppress(st, sc, press);
                Eq(true, st.AutoSilentSuppressed);
                Eq("2026-11-11", st.AutoSilentSuppressedWindow);           // 跨午夜窗口属于前一天

                // 01:01 需要真的恢复到白天
                Decision need = DecideAt(st, sc, press.AddMinutes(1));
                Eq(ControllerState.ManualNormal, need.State);
                Eq("day", need.Action);

                // 恢复成功：抑制标记必须留着（这就是原先的缺陷所在）
                StateMachine.ApplyRun(st, "day", "Ok", "", press.AddMinutes(2).ToUniversalTime(), false);
                Eq(false, st.AutoSilentActive);
                Eq(true, st.AutoSilentSuppressed);
                Eq("2026-11-11", st.AutoSilentSuppressedWindow);

                // 本夜后续周期一律不动，绝不重新进入夜间
                Decision quiet1 = DecideAt(st, sc, press.AddMinutes(10));
                Eq(ControllerState.ManualNormal, quiet1.State);
                Eq("none", quiet1.Action);
                Eq("none", DecideAt(st, sc, new DateTime(2026, 11, 12, 6, 0, 0)).Action);
                Eq("none", DecideAt(st, sc, new DateTime(2026, 11, 12, 10, 1, 0)).Action);

                // 10:02 之后是白天，正常
                Decision day = DecideAt(st, sc, new DateTime(2026, 11, 12, 10, 2, 0));
                Eq(ControllerState.DayActive, day.State);
                Eq("none", day.Action);

                // 次日 23:58：窗口换了，抑制自动失效，自动静音恢复
                DateTime nextNight = new DateTime(2026, 11, 12, 23, 58, 0);
                StateMachine.NormalizeSuppression(st, sc, nextNight);
                Eq(false, st.AutoSilentSuppressed);
                Decision next = DecideAt(st, sc, nextNight);
                Eq(ControllerState.EnteringNight, next.State);
                Eq("night", next.Action);
            });

            Run("状态机：夜间重启后抑制保持，手动静音可以覆盖", delegate
            {
                ScheduleSettings sc = DefaultSchedule();
                DateTime press = new DateTime(2026, 11, 12, 2, 0, 0);
                QuietModeState st = new QuietModeState();
                StateMachine.Suppress(st, sc, press);

                // 重启：同一窗口、只改内存里的「现在」，标记仍有效
                StateMachine.NormalizeSuppression(st, sc, new DateTime(2026, 11, 12, 3, 30, 0));
                Eq(true, st.AutoSilentSuppressed);
                Eq("none", DecideAt(st, sc, new DateTime(2026, 11, 12, 3, 30, 0)).Action);

                // 手动重新开启静音：抑制立即作废，动作回到 night
                st.ModeOn = true;
                StateMachine.ClearSuppression(st);
                Decision d = DecideAt(st, sc, new DateTime(2026, 11, 12, 3, 40, 0));
                Eq(ControllerState.ManualQuiet, d.State);
                Eq("night", d.Action);
            });

            Run("状态机：手动静音已生效时不再每十分钟重跑整条夜间链", delegate
            {
                // 2026-11-14 实测缺陷：00:49 手动静音后，00:49/00:58/01:08 各留一条完整
                // 5 步 worker 记录 —— 因为 ModeOn 分支无条件返回 action=night，
                // 每根心跳都把磁盘置离线、维护任务禁用、风扇配置重载一遍。
                ScheduleSettings sc = DefaultSchedule();
                QuietModeState st = new QuietModeState();
                st.ModeOn = true;
                st.AutoSilentActive = true;          // 夜间链已经跑过
                st.LastVerdict = "Ok";

                Decision again = DecideAt(st, sc, new DateTime(2026, 11, 14, 1, 8, 0));
                Eq(ControllerState.ManualQuiet, again.State);
                Eq("none", again.Action);            // 只观测，不重跑
                Eq(true, again.TargetNight);         // 相位仍是夜间，漂移判定按夜间算

                // 上次有失败项 → 必须重跑
                st.LastVerdict = "Failed";
                st.LastDrift = "ActionableDrift";
                Decision retry = DecideAt(st, sc, new DateTime(2026, 11, 14, 1, 18, 0));
                Eq("night", retry.Action);

                // 白天手动静音：还没生效时仍然要动作
                QuietModeState fresh = new QuietModeState();
                fresh.ModeOn = true;
                Decision first = DecideAt(fresh, sc, new DateTime(2026, 11, 14, 14, 0, 0));
                Eq(ControllerState.ManualQuiet, first.State);
                Eq("night", first.Action);
            });

            Run("状态机：上次开机前的手动静音不在白天开机时恢复", delegate
            {
                // 2026-11-14 实况：00:49 手动静音 → 01:09 关机 → 12:35 开机。
                // 静音标记原样恢复，12:36 把整条夜间链在中午跑了一遍。
                ScheduleSettings sc = DefaultSchedule();
                DateTime day = new DateTime(2026, 11, 14, 12, 36, 0);
                WindowEvaluation dayWin = ScheduleWindow.Evaluate(sc, day, TimeSpan.FromMinutes(1));
                Eq(TimeBand.Day, dayWin.Band);
                DateTime bootUtc = day.ToUniversalTime().AddMinutes(-1);

                // 开机前按下的 → 清除并给说明
                QuietModeState stale = new QuietModeState();
                stale.ModeOn = true;
                stale.ModeOnSinceUtc = "2026-11-13T16:49:22Z";      // 00:49 本地
                string note = StateMachine.ExpireStaleManualOverride(stale, dayWin, day.ToUniversalTime(), TimeSpan.FromMinutes(1));
                Eq(false, stale.ModeOn);
                Eq("", stale.ModeOnSinceUtc);
                True(note.Contains("未在白天开机时恢复"), "应给出清除原因");

                // 开机之后按下的 → 保留（哪怕是刚开机第一分钟）
                QuietModeState fresh = new QuietModeState();
                fresh.ModeOn = true;
                fresh.ModeOnSinceUtc = bootUtc.AddSeconds(30).ToString("yyyy-MM-ddTHH:mm:ssZ");
                Eq("", StateMachine.ExpireStaleManualOverride(fresh, dayWin, day.ToUniversalTime(), TimeSpan.FromMinutes(1)));
                Eq(true, fresh.ModeOn);

                // 夜间窗口一律保留：静音本来就是要的
                WindowEvaluation nightWin = ScheduleWindow.Evaluate(sc, new DateTime(2026, 11, 14, 2, 0, 0), TimeSpan.FromMinutes(1));
                Eq(TimeBand.Night, nightWin.Band);
                QuietModeState atNight = new QuietModeState();
                atNight.ModeOn = true;
                atNight.ModeOnSinceUtc = "2026-11-13T16:49:22Z";
                Eq("", StateMachine.ExpireStaleManualOverride(atNight, nightWin, new DateTime(2026, 11, 14, 2, 0, 0).ToUniversalTime(), TimeSpan.FromMinutes(1)));
                Eq(true, atNight.ModeOn);

                // 旧状态没有时间戳：只在刚开机那几次心跳里判定，别的时候不猜
                QuietModeState legacy = new QuietModeState();
                legacy.ModeOn = true;
                Eq("", StateMachine.ExpireStaleManualOverride(legacy, dayWin, day.ToUniversalTime(), TimeSpan.FromMinutes(60)));
                Eq(true, legacy.ModeOn);
                True(StateMachine.ExpireStaleManualOverride(legacy, dayWin, day.ToUniversalTime(), TimeSpan.FromMinutes(3)).Length > 0, "刚开机应判定");
                Eq(false, legacy.ModeOn);
            });

            Run("状态机：旧版本的抑制标记按当前窗口迁移，不会永久粘住", delegate
            {
                ScheduleSettings sc = DefaultSchedule();

                // schema 3 留下的裸布尔：没有窗口 → 归到当前窗口
                QuietModeState legacy = new QuietModeState();
                legacy.SchemaVersion = 3;
                legacy.AutoSilentSuppressed = true;
                StateMachine.NormalizeSuppression(legacy, sc, new DateTime(2026, 11, 12, 1, 0, 0));
                Eq("2026-11-11", legacy.AutoSilentSuppressedWindow);
                Eq(true, legacy.AutoSilentSuppressed);

                // 换到第二天夜里：自动松开
                StateMachine.NormalizeSuppression(legacy, sc, new DateTime(2026, 11, 13, 0, 5, 0));
                Eq(false, legacy.AutoSilentSuppressed);
                Eq("", legacy.AutoSilentSuppressedWindow);
            });

            Run("状态机：白天恢复失败会在白天重试", delegate
            {
                ScheduleSettings sc = DefaultSchedule();
                QuietModeState st = new QuietModeState();
                st.LastVerdict = "Failed";
                st.LastReconcileAction = "day";
                st.ConsecutiveFailures = 2;
                Decision d = DecideAt(st, sc, new DateTime(2026, 11, 12, 11, 0, 0));
                Eq(ControllerState.EnteringDay, d.State);
                Eq("day", d.Action);
                True(d.Notes.Exists(n => n.Contains("连续失败 2 次")), "应带出连续失败次数");
            });

            Run("状态机：抑制期间出现真实偏差仍会被修复（decision 走 none，修复由控制器兜底）", delegate
            {
                ScheduleSettings sc = DefaultSchedule();
                QuietModeState st = new QuietModeState();
                StateMachine.Suppress(st, sc, new DateTime(2026, 11, 12, 1, 0, 0));
                Decision d = DecideAt(st, sc, new DateTime(2026, 11, 12, 1, 5, 0));
                Eq("none", d.Action);
                Eq(false, d.TargetNight);
            });

            Run("状态机：暂停时夜间也不动作", delegate
            {
                DecisionInput i = NightInput(new DateTime(2026, 11, 12, 1, 0, 0));
                i.State.SchedulePaused = true;
                Decision d = StateMachine.Decide(i);
                Eq(ControllerState.SchedulePaused, d.State);
                Eq("none", d.Action);
            });

            Run("状态机：夜间四种状态", delegate
            {
                DecisionInput i = NightInput(new DateTime(2026, 11, 12, 1, 0, 0));
                Decision d1 = StateMachine.Decide(i);
                Eq(ControllerState.EnteringNight, d1.State);
                Eq("night", d1.Action);

                i.State.AutoSilentActive = true;
                Decision d2 = StateMachine.Decide(i);
                Eq(ControllerState.NightActive, d2.State);
                Eq("none", d2.Action);

                i.LastRunFailed = true;
                Decision d3 = StateMachine.Decide(i);
                Eq(ControllerState.NightDegraded, d3.State);
                Eq("night", d3.Action);
            });

            Run("状态机：白天需要恢复", delegate
            {
                DecisionInput i = NightInput(new DateTime(2026, 11, 11, 11, 0, 0));
                i.State.AutoSilentActive = true;
                Eq(ControllerState.EnteringDay, StateMachine.Decide(i).State);
                i.State.AutoSilentActive = false;
                Decision d = StateMachine.Decide(i);
                Eq(ControllerState.DayActive, d.State);
                Eq("none", d.Action);
            });

            Run("状态机：写回状态的成功与失败", delegate
            {
                QuietModeState st = new QuietModeState();
                StateMachine.ApplyRun(st, "night", "Ok", "", new DateTime(2026, 11, 11, 15, 58, 0, DateTimeKind.Utc), true);
                Eq(true, st.AutoSilentActive);
                True(st.LastNightRunUtc.Length > 0, "应记录夜间时间");
                Eq(0, st.ConsecutiveFailures);

                st.AutoSilentSuppressed = true;
                StateMachine.ApplyRun(st, "night", "Ok", "", new DateTime(2026, 11, 12, 15, 58, 0, DateTimeKind.Utc), true);
                Eq(false, st.AutoSilentSuppressed);

                StateMachine.ApplyRun(st, "day", "Failed", "DISK_STUCK", new DateTime(2026, 11, 12, 2, 0, 0, DateTimeKind.Utc), false);
                True(st.AutoSilentActive, "白天失败不能清掉夜间标记");
                Eq(1, st.ConsecutiveFailures);
                Eq("DISK_STUCK", st.LastErrorCode);
            });

            Run("计划：夜间顺序 = 维护 → 磁盘 → 停转 → 风扇 → 验证", delegate
            {
                ReconcilePlan p = ReconcilePlanner.Build("night", "timer", "", FullConfig(), NightMaintenance());
                Eq(5, p.Steps.Count);
                Eq("maintenance", p.Steps[0].Module);
                Eq("disk", p.Steps[1].Module);
                Eq("offline", p.Steps[1].Action);
                Eq("spindown", p.Steps[2].Module);
                Eq("apply", p.Steps[2].Action);
                Eq("fan", p.Steps[3].Module);
                Eq("silent", p.Steps[3].Action);
                Eq("verify", p.Steps[4].Module);
            });

            Run("计划：白天顺序 = 磁盘 → 维护 → 停转 → 风扇 → 验证", delegate
            {
                ReconcilePlan p = ReconcilePlanner.Build("day", "schedule", "", FullConfig(), DayMaintenance());
                Eq(5, p.Steps.Count);
                Eq("disk", p.Steps[0].Module);
                Eq("online", p.Steps[0].Action);
                Eq("maintenance", p.Steps[1].Module);
                Eq("spindown", p.Steps[2].Module);
                Eq("fan", p.Steps[3].Module);
                Eq("normal", p.Steps[3].Action);
            });

            Run("计划：维护明细必须随计划下发（否则 worker 空转）", delegate
            {
                MaintenancePlan maint = NightMaintenance();
                ReconcilePlan p = ReconcilePlanner.Build("night", "timer", "", FullConfig(), maint);
                True(p.Maintenance != null, "计划必须挂上 MaintenancePlan 引用");
                True(ReferenceEquals(p.Maintenance, maint), "必须是同一个对象，不能是副本");
                Eq(maint.Steps.Count, p.Maintenance.Steps.Count);
                True(p.Maintenance.Steps.Count > 0, "夜间维护至少要有一步");
                // 请求 JSON 里必须真的带上 maintenance 节点
                string json = p.ToJson().ToJson(true);
                True(json.IndexOf("\"maintenance\"", StringComparison.Ordinal) >= 0, "请求 JSON 缺少 maintenance 节点");
            });

            Run("计划：维护关闭时不下发维护明细", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;
                ReconcilePlan p = ReconcilePlanner.Build("night", "timer", "", cfg, NightMaintenance());
                True(p.Maintenance == null, "关闭维护时不应携带维护步骤");
                Eq("skipped", p.Steps[0].Status);
            });

            Run("计划：关闭的模块标为跳过而不是消失", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Fan.Enabled = false;
                cfg.Disks.Targets.Clear();
                cfg.SpinDown.Enabled = false;
                ReconcilePlan p = ReconcilePlanner.Build("night", "timer", "", cfg, NightMaintenance());
                Eq(5, p.Steps.Count);
                Eq("skipped", p.Steps[1].Status);   // disk
                Eq("skipped", p.Steps[2].Status);   // spindown
                Eq("skipped", p.Steps[3].Status);   // fan
                True(p.Steps[1].Critical == false, "跳过的步骤不应是关键步骤");
                True(p.Steps[2].Critical == false, "关闭的停转不应是关键步骤");
            });

            Run("核对口径：无动作时按目标相位，夜里不套白天条件", delegate
            {
                ReconcilePlan night = ReconcilePlanner.Build("none", "schedule", "", FullConfig(), NightMaintenance(), true);
                Eq("verify", night.Steps[0].Module);
                Eq("night", night.Steps[0].Action);
                ReconcilePlan day = ReconcilePlanner.Build("none", "schedule", "", FullConfig(), NightMaintenance(), false);
                Eq("verify", day.Steps[0].Module);
                Eq("day", day.Steps[0].Action);
            });

            // 2026-11-15 真机缺陷：夜间窗口里托盘点「立即联机硬盘」走 reconcile day，
            // 但目标是取自时间窗口的 targetNight=true，于是展开成整条夜间链，
            // 第一步 disk/offline —— 盘不但没联机，还被再脱一次，用户看到的就是「失败」。
            Run("显式 day 在夜间窗口里必须真的联机，不得被窗口改写成夜间链", delegate
            {
                ReconcilePlan p = ReconcilePlanner.Build("day", "manual", "", FullConfig(), DayMaintenance(), true);
                Eq("day", p.Action);
                Eq("disk", p.Steps[0].Module);
                Eq("online", p.Steps[0].Action);
                Eq("maintenance", p.Steps[1].Module);
                Eq("day", p.Steps[1].Action);
                Eq("normal", p.Steps[3].Action);
                Eq("verify", p.Steps[4].Module);
                Eq("day", p.Steps[4].Action);
            });

            Run("显式 night 在白天窗口里必须真的脱机，不得被窗口改写成白天链", delegate
            {
                ReconcilePlan p = ReconcilePlanner.Build("night", "manual", "", FullConfig(), NightMaintenance(), false);
                Eq("night", p.Action);
                Eq("maintenance", p.Steps[0].Module);
                Eq("night", p.Steps[0].Action);
                Eq("disk", p.Steps[1].Module);
                Eq("offline", p.Steps[1].Action);
                Eq("silent", p.Steps[3].Action);
                Eq("night", p.Steps[4].Action);
            });

            Run("restore 与 day 同链，同样不受窗口改写", delegate
            {
                ReconcilePlan p = ReconcilePlanner.Build("restore", "uninstall", "", FullConfig(), DayMaintenance(), true);
                Eq("restore", p.Action);
                Eq("disk", p.Steps[0].Module);
                Eq("online", p.Steps[0].Action);
                Eq("day", p.Steps[4].Action);
            });

            Run("显式 night/day 不受 targetNight 缺省值影响（null 时也按动作走）", delegate
            {
                ReconcilePlan night = ReconcilePlanner.Build("night", "manual", "", FullConfig(), NightMaintenance(), null);
                Eq("offline", night.Steps[1].Action);
                ReconcilePlan day = ReconcilePlanner.Build("day", "manual", "", FullConfig(), DayMaintenance(), null);
                Eq("online", day.Steps[0].Action);
            });

            Run("漂移修复：无需动作但回读 Failed → 按目标相位重跑", delegate
            {
                Eq("night", ReconcileVerifier.RepairTarget(true, "none", true, ReconcileVerdict.Failed));
                Eq("day", ReconcileVerifier.RepairTarget(true, "none", false, ReconcileVerdict.Failed));
            });

            Run("漂移修复：达标 / 非 apply / 已有动作时都不触发", delegate
            {
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "none", true, ReconcileVerdict.Ok));
                Eq<string>(null, ReconcileVerifier.RepairTarget(false, "none", true, ReconcileVerdict.Failed));
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "night", true, ReconcileVerdict.Failed));
            });

            Run("任务 XML：夜间重复覆盖整个窗口而不是写死 10 小时", delegate
            {
                Eq(604, TaskXml.WindowMinutes("23:58", "10:02"));
                Eq("PT10H4M", TaskXml.IsoDuration(604));
                Eq("PT10H", TaskXml.IsoDuration(600));
                Eq("PT6H", TaskXml.IsoDuration(360));
                Eq("PT45M", TaskXml.IsoDuration(45));
                Eq("", TaskXml.IsoDuration(0));

                Eq(600, TaskXml.WindowMinutes("22:00", "08:00"));
                Eq(360, TaskXml.WindowMinutes("00:00", "06:00"));
                Eq(840, TaskXml.WindowMinutes("22:30", "12:30"));
                Eq(0, TaskXml.WindowMinutes("10:02", "10:02"));
                Eq(0, TaskXml.WindowMinutes("25:00", "10:02"));
            });

            Run("任务 XML：结构合法，关键设置齐全", delegate
            {
                ScheduleSettings s = DefaultSchedule();
                string xml = TaskXml.Build(s, @"C:\QuietMode\qmctl.exe", "reconcile --apply --trigger schedule",
                                           @"C:\QuietMode", "测试", @"\QuietMode\QuietMode-Reconcile",
                                           new DateTime(2026, 11, 12));
                System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                System.Xml.XmlNamespaceManager ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", TaskXml.Namespace);

                Eq("S-1-5-18", doc.SelectSingleNode("//t:Principals/t:Principal/t:UserId", ns).InnerText);
                Eq("HighestAvailable", doc.SelectSingleNode("//t:Principals/t:Principal/t:RunLevel", ns).InnerText);
                Eq("IgnoreNew", doc.SelectSingleNode("//t:Settings/t:MultipleInstancesPolicy", ns).InnerText);
                Eq("false", doc.SelectSingleNode("//t:Settings/t:WakeToRun", ns).InnerText);
                Eq("PT10M", doc.SelectSingleNode("//t:Settings/t:ExecutionTimeLimit", ns).InnerText);
                Eq("reconcile --apply --trigger schedule", doc.SelectSingleNode("//t:Actions/t:Exec/t:Arguments", ns).InnerText);

                System.Xml.XmlNodeList triggers = doc.SelectNodes("//t:Triggers/t:CalendarTrigger", ns);
                Eq(2, triggers.Count);
                Eq("2026-11-12T23:58:00", triggers[0].SelectSingleNode("t:StartBoundary", ns).InnerText);
                Eq("PT10M", triggers[0].SelectSingleNode("t:Repetition/t:Interval", ns).InnerText);
                Eq("PT10H4M", triggers[0].SelectSingleNode("t:Repetition/t:Duration", ns).InnerText);
                Eq("true", triggers[0].SelectSingleNode("t:Repetition/t:StopAtDurationEnd", ns).InnerText);
                Eq("2026-11-12T10:02:00", triggers[1].SelectSingleNode("t:StartBoundary", ns).InnerText);
            });

            Run("任务 XML：白天重试有界，不会撞进下一个夜间窗口", delegate
            {
                Eq(240, TaskXml.DayRetryMinutes("23:58", "10:02"));      // 14 小时白天，封顶 4 小时
                Eq(0, TaskXml.DayRetryMinutes("10:30", "10:00"));        // 白天只有 30 分钟，不排重试

                ScheduleSettings s = DefaultSchedule();
                s.DayStart = "10:02";
                s.NightStart = "23:58";
                string xml = TaskXml.Build(s, "x.exe", "a", "", "", "", new DateTime(2026, 11, 12));
                System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                System.Xml.XmlNamespaceManager ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", TaskXml.Namespace);
                System.Xml.XmlNodeList triggers = doc.SelectNodes("//t:Triggers/t:CalendarTrigger", ns);
                Eq("PT4H", triggers[1].SelectSingleNode("t:Repetition/t:Duration", ns).InnerText);
                Eq("PT30M", triggers[1].SelectSingleNode("t:Repetition/t:Interval", ns).InnerText);

                // 10:02 + 4h = 14:02，远早于 23:58
                True(4 * 60 < TaskXml.WindowMinutes("10:02", "23:58"), "白天重试必须在夜间窗口之前结束");
            });

            Run("任务 XML：自定义时间与开机/解锁触发器都保留", delegate
            {
                ScheduleSettings s = DefaultSchedule();
                s.NightStart = "22:30";
                s.DayStart = "12:30";
                s.ReconcileIntervalMinutes = 15;
                string xml = TaskXml.Build(s, "x.exe", "a", "", "", "", new DateTime(2026, 11, 12));
                System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                System.Xml.XmlNamespaceManager ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", TaskXml.Namespace);
                System.Xml.XmlNodeList triggers = doc.SelectNodes("//t:Triggers/t:CalendarTrigger", ns);
                Eq("2026-11-12T22:30:00", triggers[0].SelectSingleNode("t:StartBoundary", ns).InnerText);
                Eq("PT15M", triggers[0].SelectSingleNode("t:Repetition/t:Interval", ns).InnerText);
                Eq("PT14H", triggers[0].SelectSingleNode("t:Repetition/t:Duration", ns).InnerText);
                Eq(1, doc.SelectNodes("//t:Triggers/t:BootTrigger", ns).Count);
                Eq(1, doc.SelectNodes("//t:Triggers/t:LogonTrigger", ns).Count);
                Eq(2, doc.SelectNodes("//t:Triggers/t:EventTrigger", ns).Count);
                Eq("PT2M", doc.SelectSingleNode("//t:Triggers/t:BootTrigger/t:Delay", ns).InnerText);
            });

            Run("漂移分类：只有可修复偏差才允许自动重跑", delegate
            {
                // Degraded 也可能是可修复偏差（例如维护任务仍在运行），必须重试
                Eq("night", ReconcileVerifier.RepairTarget(true, "none", true, ReconcileVerdict.Degraded));
                Eq("night", ReconcileVerifier.RepairTarget(true, "none", true, DriftClass.ActionableDrift));
                // 读不到：不许盲写
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "none", true, DriftClass.ObservationUnknown));
                // 安全阻塞：不许动设备
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "none", true, DriftClass.SafetyBlocked));
                // 重跑也修不好：交给状态机重试与人工，不做「立即强制重跑」
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "none", true, DriftClass.UnfixableDrift));
                // 完全符合：一个字节都不写
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "none", true, DriftClass.Compliant));
                Eq("day", ReconcileVerifier.RepairTarget(true, "none", false, DriftClass.ActionableDrift));
            });

            Run("漂移分类：安全 &gt; 可修复 &gt; 需人工 &gt; 未知 &gt; 符合", delegate
            {
                Eq(DriftClass.Compliant, DriftClassifier.Classify(new List<DriftItem>()));
                Eq(DriftClass.ObservationUnknown, DriftClassifier.Classify(new List<DriftItem>
                    { new DriftItem("disk", DriftKind.Unknown, false, "", "读不到") }));
                Eq(DriftClass.UnfixableDrift, DriftClassifier.Classify(new List<DriftItem>
                    { new DriftItem("maintenance", DriftKind.Mismatch, false, "", "权限被拒") }));
                Eq(DriftClass.ActionableDrift, DriftClassifier.Classify(new List<DriftItem>
                    { new DriftItem("disk", DriftKind.Unknown, false, "", "读不到"),
                      new DriftItem("spindown", DriftKind.Mismatch, true, "", "不符") }));
                Eq(DriftClass.SafetyBlocked, DriftClassifier.Classify(new List<DriftItem>
                    { new DriftItem("spindown", DriftKind.Mismatch, true, "", "不符"),
                      new DriftItem("disk", DriftKind.Safety, false, "", "身份不明") }));
            });

            Run("漂移分类：维护任务被重新启用是可修复偏差而不是模糊的失败", delegate
            {
                QuietModeConfig cfg = FullConfig();
                SystemObservation obs = new SystemObservation();
                ObservedTask t = new ObservedTask();
                t.Path = @"\Microsoft\Windows\Defrag\";
                t.Name = "ScheduledDefrag";
                t.Presence = TaskPresence.Present;
                t.Enabled = true;                       // 夜里又被人打开了
                obs.Tasks.Add(t);

                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(), obs, null, null, null);
                Eq(ReconcileVerdict.Failed, v.Verdict);
                Eq(DriftClass.ActionableDrift, v.Classification);
                True(v.Problems.Exists(p => p.Contains("仍然处于启用状态")), "应指出任务被重新启用");
                Eq("night", ReconcileVerifier.RepairTarget(true, "none", true, v));
            });

            Run("漂移分类：身份不明的磁盘走安全阻塞，绝不自动脱机", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;
                cfg.Fan.Enabled = false;
                cfg.SpinDown.Enabled = false;

                List<DiskStateObservation> disks = new List<DiskStateObservation>();
                DiskStateObservation d = new DiskStateObservation();
                d.UniqueId = "SAMPLE-UNIQUE-9999";
                d.Matched = false;                      // 认不出是哪块盘
                d.Error = "序列号不匹配";
                disks.Add(d);

                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(), new SystemObservation(), disks, null, null);
                Eq(DriftClass.SafetyBlocked, v.Classification);
                Eq(ReconcileVerdict.Failed, v.Verdict);
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "none", true, v));
            });

            Run("漂移分类：全员读不到只算状态未知，不许当成功也不许盲写", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;
                cfg.Disks.Enabled = false;
                cfg.Fan.Enabled = true;
                cfg.SpinDown.Enabled = true;

                FanObservation fan = new FanObservation();
                fan.Available = false;
                fan.Error = "IPC 不可用";

                SpinDownObservation spin = new SpinDownObservation();
                spin.Enabled = true;
                spin.ExpectedSeconds = cfg.SpinDown.Seconds;
                spin.Captured = false;
                spin.Error = "powercfg 被拒绝";

                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(),
                    new SystemObservation(), null, fan, spin);
                Eq(ReconcileVerdict.Degraded, v.Verdict);
                Eq(DriftClass.ObservationUnknown, v.Classification);
                Eq<string>(null, ReconcileVerifier.RepairTarget(true, "none", true, v));
            });

            Run("计划：停转开启时带上目标秒数，关闭时不下发", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.SpinDown.Enabled = true;
                cfg.SpinDown.Minutes = 20;
                ReconcilePlan p = ReconcilePlanner.Build("night", "timer", "", cfg, NightMaintenance());
                ReconcileStep sd = p.Steps.Find(x => x.Module == "spindown");
                True(sd != null, "计划里应有 spindown 步骤");
                Eq("pending", sd.Status);
                Eq(true, sd.Critical);
                True(sd.Detail.Contains("1200"), "明细应写出 1200 秒，实际：" + sd.Detail);

                cfg.SpinDown.Enabled = false;
                ReconcilePlan p2 = ReconcilePlanner.Build("night", "timer", "", cfg, NightMaintenance());
                ReconcileStep sd2 = p2.Steps.Find(x => x.Module == "spindown");
                Eq("skipped", sd2.Status);
                Eq("spinDown.enabled=false", sd2.Error);
            });

            Run("验证：DISKIDLE 与配置不一致算失败", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;
                cfg.Disks.Enabled = false;
                cfg.Fan.Enabled = false;
                cfg.SpinDown.Enabled = true;
                cfg.SpinDown.Minutes = 20;

                SpinDownObservation s = new SpinDownObservation();
                s.Enabled = true;
                s.ExpectedSeconds = 1200;
                s.Captured = true;
                s.AcSeconds = 300;
                s.DcSeconds = 0;

                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(),
                    new SystemObservation(), null, null, s);
                Eq(ReconcileVerdict.Failed, v.Verdict);
                True(v.Problems.Exists(x => x.Contains("硬盘停转")), "应指出硬盘停转");
                True(v.Problems.Exists(x => x.Contains("1200")), "应写出期望值");
            });

            Run("验证：DISKIDLE 读不到算降级，读对才算 Ok", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;
                cfg.Disks.Enabled = false;
                cfg.Fan.Enabled = false;
                cfg.SpinDown.Enabled = true;
                cfg.SpinDown.Minutes = 20;

                SpinDownObservation s = new SpinDownObservation();
                s.Enabled = true;
                s.ExpectedSeconds = 1200;
                s.Captured = false;
                s.Error = "powercfg 不可用";

                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(),
                    new SystemObservation(), null, null, s);
                Eq(ReconcileVerdict.Degraded, v.Verdict);

                s.Captured = true;
                s.AcSeconds = 1200;
                s.DcSeconds = 1200;
                ReconcileVerification v2 = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(),
                    new SystemObservation(), null, null, s);
                Eq(ReconcileVerdict.Ok, v2.Verdict);
                Eq("NightActive", v2.VerdictText);
            });

            Run("验证：磁盘状态不明算降级不算失败", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;   // 只测磁盘这一项
                SystemObservation obs = new SystemObservation();
                List<DiskStateObservation> disks = new List<DiskStateObservation>();
                DiskStateObservation d = new DiskStateObservation();
                d.Matched = true;
                d.FriendlyName = "SAMPLE HDD";
                d.IsOffline = null;
                disks.Add(d);

                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(), obs, disks, null);
                Eq(ReconcileVerdict.Degraded, v.Verdict);
                True(v.Warnings.Exists(w => w.Contains("读不到脱机状态")), "应说明读不到");
            });

            Run("验证：风扇配置不符算失败", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;
                cfg.Disks.Enabled = false;
                FanObservation fan = new FanObservation();
                fan.Available = true;
                fan.CurrentProfile = "FanControl.json";
                fan.ExpectedProfile = "FanControl_Silent_Lite.json";
                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(), new SystemObservation(), null, fan);
                Eq(ReconcileVerdict.Failed, v.Verdict);
                True(v.Problems.Exists(x => x.Contains("风扇")), "应指出风扇");
            });

            Run("验证：全部组件都满足才算 Ok", delegate
            {
                QuietModeConfig cfg = FullConfig();
                cfg.Maintenance.Enabled = false;
                cfg.Disks.Enabled = false;
                FanObservation fan = new FanObservation();
                fan.Available = true;
                fan.CurrentProfile = "FanControl_Silent_Lite.json";
                fan.ExpectedProfile = "FanControl_Silent_Lite.json";
                ReconcileVerification v = ReconcileVerifier.Verify("night", cfg, new BaselineSnapshot(), new SystemObservation(), null, fan);
                Eq(ReconcileVerdict.Ok, v.Verdict);
                Eq("NightActive", v.VerdictText);
            });

            // 2026-11-15 00:15 实测发现：夜间链跑完后 state.json 里 controllerState 仍写着
            // EnteringNight（决策值），整夜与实机相位差一格。收尾必须落到稳定态。
            Run("状态：链跑完后控制器落到稳定态而不是过渡态", delegate
            {
                Eq(ControllerState.NightActive,
                   ControllerStates.Settle("night", true, ControllerState.EnteringNight));
                Eq(ControllerState.DayActive,
                   ControllerStates.Settle("day", true, ControllerState.EnteringDay));
                Eq(ControllerState.DayActive,
                   ControllerStates.Settle("restore", true, ControllerState.EnteringDay));
                Eq(ControllerState.NightActive,
                   ControllerStates.Settle("NIGHT", true, ControllerState.EnteringNight));
            });

            Run("状态：验证没过就保留过渡态或降级态", delegate
            {
                Eq(ControllerState.NightDegraded,
                   ControllerStates.Settle("night", false, ControllerState.EnteringNight));
                Eq(ControllerState.EnteringDay,
                   ControllerStates.Settle("day", false, ControllerState.EnteringDay));
                // none / verify 不改相位：决策说手动静音，就不许被收尾逻辑改成白天
                Eq(ControllerState.ManualQuiet,
                   ControllerStates.Settle("none", true, ControllerState.ManualQuiet));
                Eq(ControllerState.SchedulePaused,
                   ControllerStates.Settle("verify", true, ControllerState.SchedulePaused));
            });

            Run("状态：读完旧文件后 schemaVersion 抬到当前值", delegate
            {
                JsonObject o = new JsonObject();
                o.Set("schemaVersion", 3);
                o.Set("modeOn", true);
                o.Set("lastVerdict", "Ok");
                QuietModeState st = QuietModeState.FromJson(o);
                Eq(QuietModeState.CurrentSchemaVersion, st.SchemaVersion);
                True(st.SchemaVersion > 3, "旧文件里的 3 必须被抬到当前版本，否则排查会怀疑旧二进制还在跑");
                Eq(true, st.ModeOn);
                Eq("Ok", st.LastVerdict);
            });

            Run("状态：往返一圈版本号不低于当前值", delegate
            {
                QuietModeState st = new QuietModeState();
                st.ModeOn = false;
                st.ControllerState = "NightActive";
                QuietModeState back = QuietModeState.FromJson(st.ToJson());
                Eq(QuietModeState.CurrentSchemaVersion, back.SchemaVersion);
                Eq("NightActive", back.ControllerState);
            });
        }

        private static DecisionInput NightInput(DateTime now)
        {
            ScheduleSettings s = new ScheduleSettings();
            s.NightStart = "23:58";
            s.DayStart = "10:02";
            s.StartupOverrideEnabled = true;
            s.StartupDayStart = "06:00";
            s.StartupGraceMinutes = 15;

            DecisionInput i = new DecisionInput();
            i.Schedule = s;
            i.State = new QuietModeState();
            i.Window = ScheduleWindow.Evaluate(s, now, TimeSpan.FromHours(3));
            i.Trigger = "timer";
            i.Now = now;
            return i;
        }

        /// <summary>在指定时刻做一次决策（窗口与「现在」必须同一个时刻）。</summary>
        private static Decision DecideAt(QuietModeState st, ScheduleSettings s, DateTime now)
        {
            DecisionInput i = NightInput(now);
            i.Schedule = s;
            i.State = st;
            i.Window = ScheduleWindow.Evaluate(s, now, TimeSpan.FromHours(3));
            i.LastRunFailed = StateMachine.IsLastRunFailed(st);
            return StateMachine.Decide(i);
        }

        private static ScheduleSettings DefaultSchedule()
        {
            return NightInput(new DateTime(2026, 11, 12, 1, 0, 0)).Schedule;
        }

        private static QuietModeConfig FullConfig()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            cfg.Maintenance.Enabled = true;
            cfg.Disks.Enabled = true;
            cfg.Fan.Enabled = true;
            cfg.Fan.NormalProfile = @"X:\FanControl\Configurations\FanControl.json";
            cfg.Fan.SilentProfile = @"X:\FanControl\Configurations\FanControl_Silent_Lite.json";

            DiskTarget t = new DiskTarget();
            t.UniqueId = "SAMPLE-UNIQUE-0002";
            t.SerialNumber = "SAMPLE-SERIAL-0002";
            t.FriendlyName = "SAMPLE HDD 4TB";
            t.SizeBytes = 4000000000000;
            t.BusType = "SATA";
            t.Enabled = true;
            cfg.Disks.Targets.Add(t);
            return cfg;
        }

        private static MaintenancePlan NightMaintenance()
        {
            MaintenancePlan p = new MaintenancePlan();
            p.Phase = "night";
            p.AddTask(MaintenanceAction.DisableTask, @"\Microsoft\Windows\Defrag\", "ScheduledDefrag", "test");
            return p;
        }

        private static MaintenancePlan DayMaintenance()
        {
            MaintenancePlan p = new MaintenancePlan();
            p.Phase = "day";
            p.AddTask(MaintenanceAction.EnableTask, @"\Microsoft\Windows\Defrag\", "ScheduledDefrag", "test");
            return p;
        }
    }
}

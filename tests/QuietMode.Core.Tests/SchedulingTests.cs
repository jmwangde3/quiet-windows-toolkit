using System;
using System.Collections.Generic;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Configuration;
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

            Run("状态机：手动关闭保留到下一夜", delegate
            {
                DecisionInput i = NightInput(new DateTime(2026, 11, 12, 1, 0, 0));   // 夜间
                i.State.AutoSilentSuppressed = true;
                Decision d = StateMachine.Decide(i);
                Eq(ControllerState.ManualNormal, d.State);
                Eq("day", d.Action);
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

            Run("计划：夜间顺序 = 维护 → 磁盘 → 风扇 → 验证", delegate
            {
                ReconcilePlan p = ReconcilePlanner.Build("night", "timer", "", FullConfig(), NightMaintenance());
                Eq(4, p.Steps.Count);
                Eq("maintenance", p.Steps[0].Module);
                Eq("disk", p.Steps[1].Module);
                Eq("offline", p.Steps[1].Action);
                Eq("fan", p.Steps[2].Module);
                Eq("silent", p.Steps[2].Action);
                Eq("verify", p.Steps[3].Module);
            });

            Run("计划：白天顺序 = 磁盘 → 维护 → 风扇 → 验证", delegate
            {
                ReconcilePlan p = ReconcilePlanner.Build("day", "schedule", "", FullConfig(), DayMaintenance());
                Eq(4, p.Steps.Count);
                Eq("disk", p.Steps[0].Module);
                Eq("online", p.Steps[0].Action);
                Eq("maintenance", p.Steps[1].Module);
                Eq("fan", p.Steps[2].Module);
                Eq("normal", p.Steps[2].Action);
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
                ReconcilePlan p = ReconcilePlanner.Build("night", "timer", "", cfg, NightMaintenance());
                Eq(4, p.Steps.Count);
                Eq("skipped", p.Steps[1].Status);
                Eq("skipped", p.Steps[2].Status);
                True(p.Steps[1].Critical == false, "跳过的步骤不应是关键步骤");
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
            return i;
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

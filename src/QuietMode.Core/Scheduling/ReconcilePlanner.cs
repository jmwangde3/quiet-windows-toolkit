using System;
using System.Collections.Generic;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Models;

namespace QuietMode.Core.Scheduling
{
    /// <summary>
    /// 把「一个动作」展开成固定的步骤序列。这里是整条链唯一的顺序定义；
    /// worker 只能照着执行，C# 和脚本都不允许再各写一套。
    /// </summary>
    public static class ReconcilePlanner
    {
        public static ReconcilePlan Build(string action, string trigger, string reason,
                                          QuietModeConfig cfg, MaintenancePlan maintenance)
        {
            return Build(action, trigger, reason, cfg, maintenance, (bool?)null);
        }

        /// <summary>
        /// targetNight 只在 action=none / verify（即「只回读核对」）时有意义：
        /// 夜里核对必须按夜间的期望来，否则「维护任务应当启用」这种白天条件会把一个
        /// 完全正常的夜间状态判成 Failed。早期固定成 day，正是漂移修复每十分钟空转的原因。
        /// </summary>
        public static ReconcilePlan Build(string action, string trigger, string reason,
                                          QuietModeConfig cfg, MaintenancePlan maintenance, bool? targetNight)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            ReconcilePlan p = new ReconcilePlan();
            p.Action = NormalizeAction(action);
            p.Trigger = trigger ?? "";
            p.Reason = reason ?? "";
            p.CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

            bool night = targetNight.HasValue ? targetNight.Value : p.Action == "night";
            int enabledTargets = CountEnabledTargets(cfg);

            if (p.Action == "verify")
            {
                p.Add("verify", night ? "night" : "day", "只读核对当前状态", true);
                p.Notes.Add("verify 模式不做任何写入");
                return p;
            }

            if (night)
            {
                AddMaintenance(p, cfg, maintenance, "night");
                AddDisk(p, cfg, enabledTargets, "offline");
                AddSpinDown(p, cfg);
                AddFan(p, cfg, "silent");
                p.Add("verify", "night", "全面验证夜间状态", true);
            }
            else
            {
                // 白天顺序与夜间相反：先把盘放回来，才有条件恢复维护任务。
                AddDisk(p, cfg, enabledTargets, "online");
                AddMaintenance(p, cfg, maintenance, "day");
                AddSpinDown(p, cfg);
                AddFan(p, cfg, "normal");
                p.Add("verify", "day", "全面验证白天状态", true);
            }

            if (p.Action == "restore") p.Notes.Add("restore 与 day 使用同一条链，区别是忽略时间窗口、强制恢复到 baseline");
            return p;
        }

        private static void AddMaintenance(ReconcilePlan p, QuietModeConfig cfg, MaintenancePlan maintenance, string phase)
        {
            if (!cfg.Maintenance.Enabled)
            {
                ReconcileStep s = p.Add("maintenance", phase, "维护闸门已在配置中关闭", false);
                s.Status = "skipped";
                s.Error = "maintenance.enabled=false";
                return;
            }
            int count = maintenance == null ? 0 : maintenance.Steps.Count;
            // 必须把明细挂到计划上：worker 只认 request.maintenance，漏了这一步
            // 就会「日志写着 11 个维护步骤、实际一个都没执行」。
            p.Maintenance = maintenance;
            p.Add("maintenance", phase, count + " 个维护步骤", true);
            if (maintenance == null) p.Notes.Add("注意：维护计划为空，执行层不会做任何维护动作");
        }

        private static void AddDisk(ReconcilePlan p, QuietModeConfig cfg, int enabledTargets, string op)
        {
            if (!cfg.Disks.Enabled || enabledTargets == 0)
            {
                ReconcileStep s = p.Add("disk", op, "没有启用的目标磁盘", false);
                s.Status = "skipped";
                s.Error = cfg.Disks.Enabled ? "disks.targets 为空" : "disks.enabled=false";
                return;
            }
            p.Add("disk", op, enabledTargets + " 个目标磁盘", true);
            if (cfg.Disks.ForceBusy)
                p.Notes.Add("disks.forceBusy=true：占用检查只记录，不阻止脱机");
        }

        /// <summary>
        /// 硬盘空闲停转。它是用户独立开关（spinDown.enabled），但只要开着就是
        /// reconcile 的一部分：配置说 20 分钟，系统里就只能是 1200 秒。
        /// 关掉时标 skipped，一个字节都不写——不替用户清他手设的值。
        /// </summary>
        private static void AddSpinDown(ReconcilePlan p, QuietModeConfig cfg)
        {
            SpinDownSettings sd = cfg.SpinDown;
            if (sd == null || !sd.Enabled)
            {
                ReconcileStep s = p.Add("spindown", "apply", "硬盘停转已在配置中关闭", false);
                s.Status = "skipped";
                s.Error = "spinDown.enabled=false";
                return;
            }
            p.Add("spindown", "apply", sd.Minutes + " 分钟（" + sd.Seconds + " 秒）", true);
        }

        private static void AddFan(ReconcilePlan p, QuietModeConfig cfg, string action)
        {
            if (!cfg.Fan.Enabled)
            {
                ReconcileStep s = p.Add("fan", action, "风扇切换已在配置中关闭", false);
                s.Status = "skipped";
                s.Error = "fan.enabled=false";
                return;
            }
            string profile = action == "silent" ? cfg.Fan.SilentProfile : cfg.Fan.NormalProfile;
            p.Add("fan", action, profile ?? "", true);
        }

        public static int CountEnabledTargets(QuietModeConfig cfg)
        {
            int n = 0;
            if (cfg == null || cfg.Disks == null) return 0;
            foreach (DiskTarget t in cfg.Disks.Targets)
            {
                if (t != null && t.Enabled) n++;
            }
            return n;
        }

        private static string NormalizeAction(string action)
        {
            string a = (action ?? "").Trim().ToLowerInvariant();
            if (a == "enter-night" || a == "enternight") a = "night";
            if (a == "enter-day" || a == "enterday") a = "day";
            if (a != "night" && a != "day" && a != "restore" && a != "verify") a = "verify";
            return a;
        }
    }

    /// <summary>
    /// 全组件验证。夜间的每一项都来自规格 §7.4 的表格：风扇、磁盘、维护任务、
    /// Regular Maintenance、SysMain、MaintenanceBoundary、WakeUp。
    /// </summary>
    public static class ReconcileVerifier
    {
        /// <summary>
        /// 是否需要「漂移修复」：自动决策说不用动（action=none），但回读存在可修复偏差。
        /// 这正是首夜之后的形态 —— 状态机认为「已经在夜间」，可电源方案还是旧值，
        /// 于是每个周期都安静地判定失败，谁都不去修。
        ///
        /// 只认 ActionableDrift：Unknown 不许盲写，SafetyBlocked 不许动设备，
        /// UnfixableDrift 重跑也没用（交给状态机下个周期重试并累计失败次数）。
        /// </summary>
        public static string RepairTarget(bool apply, string action, bool targetNight, DriftClass classification)
        {
            if (!apply) return null;
            if (!string.Equals(action ?? "", "none", StringComparison.OrdinalIgnoreCase)) return null;
            if (!DriftClassifier.AllowsAutomaticRepair(classification)) return null;
            return targetNight ? "night" : "day";
        }

        /// <summary>兼容旧调用：按 verdict 推断（Degraded/Failed 都可能是可修复偏差）。</summary>
        public static string RepairTarget(bool apply, string action, bool targetNight, ReconcileVerdict verdict)
        {
            if (verdict != ReconcileVerdict.Failed && verdict != ReconcileVerdict.Degraded) return null;
            return RepairTarget(apply, action, targetNight, DriftClass.ActionableDrift);
        }

        /// <summary>
        /// 只需修复某个组件时用的「定点修复」判定。目前用于把「维护任务被重新启用」
        /// 这类偏差收敛到最小动作；返回空表示走完整相位。
        /// </summary>
        public static string RepairTarget(bool apply, string action, bool targetNight, ReconcileVerification v)
        {
            if (v == null) return null;
            return RepairTarget(apply, action, targetNight, v.Classification);
        }

        public static ReconcileVerification Verify(string action, QuietModeConfig cfg, BaselineSnapshot baseline,
                                                   SystemObservation obs, IList<DiskStateObservation> disks,
                                                   FanObservation fan, SpinDownObservation spinDown = null)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            ReconcileVerification v = new ReconcileVerification();
            v.Action = (action ?? "").Trim().ToLowerInvariant();
            bool night = v.Action == "night" || v.Action == "verify-night";

            if (cfg.Maintenance.Enabled)
            {
                v.Maintenance = night
                    ? MaintenanceVerifier.VerifyNight(cfg, obs)
                    : MaintenanceVerifier.VerifyDay(cfg, baseline, obs);
                // 没有可用基线就没有「原始状态」可谈，白天恢复不能报 Ok。
                if (!night && (baseline == null || baseline.ScheduledTasks.Count == 0))
                {
                    v.Unknown("maintenance", "没有可用的 baseline（缺失或损坏），无法确认维护任务已恢复到原始状态");
                }
                foreach (string p in v.Maintenance.Problems) v.Problem("maintenance", true, "", p);
                foreach (string w in v.Maintenance.Warnings)
                {
                    // 「读不到」不可修复；「任务/服务仍在运行」重跑相位还能再停一次，值得修。
                    bool unreadable = w.Contains("无法确认") || w.Contains("读取失败")
                                      || w.Contains("读不到") || w.Contains("未探测");
                    if (unreadable) v.Unknown("maintenance", w);
                    else v.Warning("maintenance", true, "", w);
                }
                v.Notes.AddRange(v.Maintenance.Notes);
            }
            else
            {
                v.Notes.Add("维护闸门已关闭，不参与验证");
            }

            bool wantOffline = night || v.Action == "verify-offline";
            if (cfg.Disks.Enabled)
            {
                List<DiskStateObservation> list = disks == null ? new List<DiskStateObservation>() : new List<DiskStateObservation>(disks);
                v.Disks.AddRange(list);
                foreach (DiskStateObservation d in list)
                {
                    if (!d.Matched)
                    {
                        // 身份不明 = 安全阻塞：不许为了「看起来对齐」去动一块认不出的盘。
                        v.Safety("disk", "磁盘 " + d.Display + "：身份未匹配（"
                                          + (string.IsNullOrEmpty(d.Error) ? "原因未知" : d.Error) + "），不做任何写入");
                        continue;
                    }
                    if (!d.IsOffline.HasValue)
                    {
                        v.Unknown("disk", "磁盘 " + d.Display + "：读不到脱机状态，无法确认");
                        continue;
                    }
                    if (d.IsOffline.Value != wantOffline)
                    {
                        v.Problem("disk", true, d.UniqueId, "磁盘 " + d.Display + "：期望 " + (wantOffline ? "Offline" : "Online")
                                   + "，实际 " + (d.IsOffline.Value ? "Offline" : "Online"));
                    }
                }
                if (list.Count == 0)
                    v.Unknown("disk", "配置启用了磁盘管理，但没有采集到任何目标磁盘状态");
            }
            else
            {
                v.Notes.Add("磁盘管理已关闭，不参与验证");
            }

            if (cfg.SpinDown != null && cfg.SpinDown.Enabled)
            {
                v.SpinDown = spinDown;
                int want = cfg.SpinDown.Seconds;
                if (spinDown == null || !spinDown.Captured)
                {
                    v.Unknown("spindown", "硬盘停转：读不到 DISKIDLE，无法确认（期望 " + want + "s）"
                                          + (spinDown == null || string.IsNullOrEmpty(spinDown.Error) ? "" : "：" + spinDown.Error));
                }
                else if (!spinDown.Matches)
                {
                    v.Problem("spindown", true, "DISKIDLE", "硬盘停转：期望 " + want + "s，实际 AC=" + spinDown.AcSeconds
                               + "s DC=" + spinDown.DcSeconds + "s");
                }
            }
            else
            {
                v.Notes.Add("硬盘停转已关闭，不参与验证");
            }

            v.Fan = fan;
            if (cfg.Fan.Enabled)
            {
                if (fan == null)
                {
                    v.Unknown("fan", "风扇：没有采集到状态");
                }
                else if (!fan.Available)
                {
                    v.Unknown("fan", "风扇：状态未知（" + (string.IsNullOrEmpty(fan.Error) ? "IPC 不可用" : fan.Error) + "）");
                }
                else if (!fan.Matches)
                {
                    v.Problem("fan", true, fan.ExpectedProfile, "风扇：当前配置 "
                               + (fan.CurrentProfile.Length == 0 ? "(未知)" : fan.CurrentProfile)
                               + "，期望 " + fan.ExpectedProfile);
                }
            }
            else
            {
                v.Notes.Add("风扇切换已关闭，不参与验证");
            }

            v.Settle();
            return v;
        }
    }
}

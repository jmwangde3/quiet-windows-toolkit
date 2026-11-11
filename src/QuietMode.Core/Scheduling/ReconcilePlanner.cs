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
            if (cfg == null) throw new ArgumentNullException("cfg");
            ReconcilePlan p = new ReconcilePlan();
            p.Action = NormalizeAction(action);
            p.Trigger = trigger ?? "";
            p.Reason = reason ?? "";
            p.CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

            bool night = p.Action == "night";
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
                AddFan(p, cfg, "silent");
                p.Add("verify", "night", "全面验证夜间状态", true);
            }
            else
            {
                // 白天顺序与夜间相反：先把盘放回来，才有条件恢复维护任务。
                AddDisk(p, cfg, enabledTargets, "online");
                AddMaintenance(p, cfg, maintenance, "day");
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
        public static ReconcileVerification Verify(string action, QuietModeConfig cfg, BaselineSnapshot baseline,
                                                   SystemObservation obs, IList<DiskStateObservation> disks,
                                                   FanObservation fan)
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
                v.Problems.AddRange(v.Maintenance.Problems);
                v.Warnings.AddRange(v.Maintenance.Warnings);
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
                        v.Problems.Add("磁盘 " + d.Display + "：身份未匹配（" + (string.IsNullOrEmpty(d.Error) ? "原因未知" : d.Error) + "）");
                        continue;
                    }
                    if (!d.IsOffline.HasValue)
                    {
                        v.Warnings.Add("磁盘 " + d.Display + "：读不到脱机状态，无法确认");
                        continue;
                    }
                    if (d.IsOffline.Value != wantOffline)
                    {
                        v.Problems.Add("磁盘 " + d.Display + "：期望 " + (wantOffline ? "Offline" : "Online")
                                       + "，实际 " + (d.IsOffline.Value ? "Offline" : "Online"));
                    }
                }
                if (list.Count == 0)
                    v.Warnings.Add("配置启用了磁盘管理，但没有采集到任何目标磁盘状态");
            }
            else
            {
                v.Notes.Add("磁盘管理已关闭，不参与验证");
            }

            v.Fan = fan;
            if (cfg.Fan.Enabled)
            {
                if (fan == null)
                {
                    v.Warnings.Add("风扇：没有采集到状态");
                }
                else if (!fan.Available)
                {
                    v.Warnings.Add("风扇：状态未知（" + (string.IsNullOrEmpty(fan.Error) ? "IPC 不可用" : fan.Error) + "）");
                }
                else if (!fan.Matches)
                {
                    v.Problems.Add("风扇：当前配置 " + (fan.CurrentProfile.Length == 0 ? "(未知)" : fan.CurrentProfile)
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

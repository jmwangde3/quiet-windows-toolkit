using System;
using System.Collections.Generic;
using System.Globalization;
using QuietMode.Core.Models;

namespace QuietMode.Core.Maintenance
{
    /// <summary>
    /// 纯计划器：把「配置 + baseline + 当前观测」算成一串明确的步骤。
    /// 不碰系统，不猜状态，因此可以完全离线单测（规格 §4.2 / §4.3 的顺序在这里固化）。
    /// </summary>
    public static class MaintenancePlanner
    {
        public static MaintenancePlan BuildNight(QuietModeConfig cfg, BaselineSnapshot baseline, SystemObservation obs)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            if (obs == null) obs = new SystemObservation();

            MaintenancePlan plan = new MaintenancePlan();
            plan.Phase = "night";
            MaintenanceSettings m = cfg.Maintenance;

            if (!m.Enabled)
            {
                plan.Notes.Add("maintenance.enabled=false：本次不产生任何维护步骤（不做禁用，也不做恢复）");
                return plan;
            }

            // ---- 1. 停止 + 禁用维护任务 ----
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                string forbidden = MaintenanceCatalog.ForbiddenPrefixOf(def.Path);
                if (forbidden.Length > 0)
                {
                    plan.Notes.Add(def.FullName + " 落在禁改范围 " + forbidden + "，已跳过（规格 §4.6）");
                    continue;
                }

                ObservedTask t = obs.FindTask(def.Path, def.Name);
                TaskPresence presence = t == null ? TaskPresence.Unknown : t.Presence;

                if (presence == TaskPresence.NotPresent)
                {
                    plan.Notes.Add(def.FullName + " 在本机不存在（NotPresent），跳过；不计为失败");
                    continue;
                }
                if (presence == TaskPresence.Unknown)
                {
                    plan.Notes.Add(def.FullName + " 状态读取失败（"
                                   + (t == null ? "未探测" : Trim(t.Error))
                                   + "），仍按计划尝试，但可能无法验证");
                }

                if (t != null && t.Running)
                {
                    MaintenanceStep stop = plan.AddTask(MaintenanceAction.StopTask, def.Path, def.Name,
                        "任务正在运行，先停止再禁用；只停这一个任务，不按进程名杀");
                    stop.Critical = false;   // 停止失败不阻断，但要记录
                }

                MaintenanceStep disable = plan.AddTask(MaintenanceAction.DisableTask, def.Path, def.Name,
                    def.Purpose + "；夜间禁用");
                disable.Critical = true;
            }

            // ---- 2. SysMain ----
            if (m.DisableSysMainAtNight)
            {
                ObservedService svc = obs.FindService(MaintenanceCatalog.SysMainService);
                if (svc == null || !svc.Exists)
                {
                    plan.Notes.Add("服务 " + MaintenanceCatalog.SysMainService + " 不存在，跳过");
                }
                else
                {
                    if (svc.Running)
                    {
                        MaintenanceStep stop = plan.AddService(MaintenanceAction.StopService,
                            MaintenanceCatalog.SysMainService, "", "夜间停止 SysMain（内存预读会唤醒磁盘）");
                        stop.Critical = false;
                    }
                    plan.AddService(MaintenanceAction.SetServiceStart, MaintenanceCatalog.SysMainService, "4",
                        "夜间把 SysMain 置为 Disabled");
                }
            }
            else
            {
                plan.Notes.Add("maintenance.disableSysMainAtNight=false：SysMain 保持原状");
            }

            // ---- 3. 自动维护注册表 ----
            plan.AddRegistry(MaintenanceAction.SetRegistryValue,
                MaintenanceCatalog.MaintenanceRegistryKey, MaintenanceCatalog.MaintenanceBoundaryValue,
                m.MaintenanceHour.ToString(CultureInfo.InvariantCulture), "DWord",
                "把自动维护窗口推到白天时段，夜间不触发");

            if (m.DisableWake)
            {
                plan.AddRegistry(MaintenanceAction.SetRegistryValue,
                    MaintenanceCatalog.MaintenanceRegistryKey, MaintenanceCatalog.MaintenanceWakeValue,
                    "0", "DWord", "禁止维护任务把机器从睡眠唤醒");
            }
            else
            {
                plan.Notes.Add("maintenance.disableWake=false：不修改 WakeUp");
            }

            return plan;
        }

        public static MaintenancePlan BuildDay(QuietModeConfig cfg, BaselineSnapshot baseline, SystemObservation obs)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            if (obs == null) obs = new SystemObservation();
            if (baseline == null) baseline = new BaselineSnapshot();

            MaintenancePlan plan = new MaintenancePlan();
            plan.Phase = "day";
            MaintenanceSettings m = cfg.Maintenance;

            if (!m.Enabled)
            {
                plan.Notes.Add("maintenance.enabled=false：不做恢复（也没在夜间改动过）");
                return plan;
            }

            // ---- 1. 计划任务恢复到 baseline ----
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                ObservedTask t = obs.FindTask(def.Path, def.Name);
                if (t == null || t.Presence == TaskPresence.NotPresent)
                {
                    plan.Notes.Add(def.FullName + " 当前不存在，无需恢复");
                    continue;
                }

                TaskSnapshot b = FindTask(baseline, def.Path, def.Name);
                if (b == null || !b.Enabled.HasValue)
                {
                    plan.Notes.Add(def.FullName + " 的 baseline 未记录启用状态，白天不擅自恢复");
                    continue;
                }
                if (b.OriginalUnknown)
                {
                    // baseline 自己说了它不知道。这时候动它就是在猜 —— 留在 Pending 并说明。
                    plan.Notes.Add(def.FullName + " 的原始启用状态未知（" + Trim(b.OriginalReason)
                                   + "），本步保留 Pending，不启用也不禁用；"
                                   + "确认后执行 qmctl maint baseline-enable 或 baseline-disable");
                    continue;
                }

                bool desired = b.Enabled.Value;
                if (t.Enabled.HasValue && t.Enabled.Value == desired)
                {
                    continue;   // 已经是原始状态，不产生步骤
                }

                MaintenanceStep s = plan.AddTask(
                    desired ? MaintenanceAction.EnableTask : MaintenanceAction.DisableTask,
                    def.Path, def.Name,
                    "恢复 baseline 原始状态（enabled=" + (desired ? "true" : "false") + "）");
                // 原本启用、白天恢复不回来 = 关键失败：用户的维护任务被我们关掉了却没还回去。
                // 原本禁用只是「对齐」，失败也要如实报告，但不该让整轮判成灾难。
                s.Critical = desired;
            }

            // ---- 2. SysMain 恢复 ----
            if (m.DisableSysMainAtNight)
            {
                ServiceSnapshot b = FindService(baseline, MaintenanceCatalog.SysMainService);
                ObservedService now = obs.FindService(MaintenanceCatalog.SysMainService);

                if (b == null)
                {
                    plan.Notes.Add("baseline 未记录 SysMain，跳过恢复");
                }
                else if (now == null || !now.Exists)
                {
                    plan.Notes.Add("SysMain 当前不存在，跳过恢复");
                }
                else
                {
                    string desiredStart = DesiredStartValue(b);
                    if (desiredStart.Length > 0 && now.StartValue.ToString(CultureInfo.InvariantCulture) != desiredStart)
                    {
                        plan.AddService(MaintenanceAction.SetServiceStart, MaintenanceCatalog.SysMainService,
                            desiredStart, "恢复 baseline 记录的服务启动类型（" + b.StartupType + "）");
                    }
                    if (string.Equals(b.Status, "Running", StringComparison.OrdinalIgnoreCase) && !now.Running)
                    {
                        plan.AddService(MaintenanceAction.StartService, MaintenanceCatalog.SysMainService, "",
                            "baseline 记录当时 SysMain 正在运行，恢复运行状态");
                    }
                }
            }

            // ---- 3. 注册表恢复 ----
            foreach (RegistrySnapshot b in BaselineRegistryFor(baseline, m))
            {
                string label = b.KeyPath + "\\" + b.ValueName;
                if (b.Exists)
                {
                    plan.AddRegistry(MaintenanceAction.SetRegistryValue, b.KeyPath, b.ValueName, b.Value, b.Kind,
                        "恢复原始值（存在，类型 " + b.Kind + "）");
                }
                else
                {
                    plan.AddRegistry(MaintenanceAction.DeleteRegistryValue, b.KeyPath, b.ValueName, "", b.Kind,
                        "原始状态不存在该值，恢复时删除" + (label.Length > 0 ? "：" + label : ""));
                }
            }

            foreach (string name in new string[]
                     {
                         MaintenanceCatalog.MaintenanceBoundaryValue,
                         m.DisableWake ? MaintenanceCatalog.MaintenanceWakeValue : ""
                     })
            {
                if (name.Length == 0) continue;
                if (FindRegistry(baseline, MaintenanceCatalog.MaintenanceRegistryKey, name) == null)
                {
                    plan.Notes.Add("baseline 未记录 " + name + "，白天不写回（宁可不改也不编造原始值）");
                }
            }

            return plan;
        }

        /// <summary>baseline 里与本次策略相关的注册表项：MaintenanceBoundary 总是恢复，WakeUp 只在改过时恢复。</summary>
        private static List<RegistrySnapshot> BaselineRegistryFor(BaselineSnapshot baseline, MaintenanceSettings m)
        {
            List<RegistrySnapshot> result = new List<RegistrySnapshot>();
            foreach (RegistrySnapshot r in baseline.Registry)
            {
                if (!string.Equals(r.KeyPath, MaintenanceCatalog.MaintenanceRegistryKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(r.ValueName, MaintenanceCatalog.MaintenanceBoundaryValue, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(r);
                }
                else if (string.Equals(r.ValueName, MaintenanceCatalog.MaintenanceWakeValue, StringComparison.OrdinalIgnoreCase) && m.DisableWake)
                {
                    result.Add(r);
                }
            }
            return result;
        }

        public static TaskSnapshot FindTask(BaselineSnapshot baseline, string path, string name)
        {
            if (baseline == null) return null;
            string p = (path ?? "").Trim().TrimEnd('\\');
            foreach (TaskSnapshot t in baseline.ScheduledTasks)
            {
                if (string.Equals((t.Path ?? "").Trim().TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }
            return null;
        }

        public static ServiceSnapshot FindService(BaselineSnapshot baseline, string serviceName)
        {
            if (baseline == null) return null;
            foreach (ServiceSnapshot s in baseline.Services)
            {
                if (string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        public static RegistrySnapshot FindRegistry(BaselineSnapshot baseline, string keyPath, string valueName)
        {
            if (baseline == null) return null;
            foreach (RegistrySnapshot r in baseline.Registry)
            {
                if (string.Equals(r.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.ValueName, valueName, StringComparison.OrdinalIgnoreCase))
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>
        /// 优先用 baseline 记录的数字 Start 值；没有时按 StartupType 反推。
        /// 两者都没有就返回空串 —— 表示「不知道原始值」，不写回。
        /// </summary>
        public static string DesiredStartValue(ServiceSnapshot b)
        {
            if (b == null) return "";
            if (b.StartValue >= 0) return b.StartValue.ToString(CultureInfo.InvariantCulture);
            switch ((b.StartupType ?? "").ToLowerInvariant())
            {
                case "boot": return "0";
                case "system": return "1";
                case "automatic": return "2";
                case "automatic-delayed": return "2";
                case "manual": return "3";
                case "disabled": return "4";
                default: return "";
            }
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 120 ? t.Substring(0, 120) + "..." : t;
        }
    }
}

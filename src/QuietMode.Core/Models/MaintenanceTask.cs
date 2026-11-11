using System;
using System.Collections.Generic;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// 唯一的维护任务清单（规格 §4.1）。任何脚本、Worker、托盘程序都从这里取任务，
    /// 禁止再各自定义 $MaintTasks 数组。
    /// </summary>
    public sealed class MaintenanceTaskDefinition
    {
        public string Path = "";
        public string Name = "";
        /// <summary>night-maintenance | gate | crash-recovery | support</summary>
        public string Module = "";
        /// <summary>夜间默认是否停用。</summary>
        public bool NightDisableDefault = true;
        /// <summary>true = 任何情况下都不停用（系统关键）。</summary>
        public bool Protected;
        public string Purpose = "";

        public string FullName { get { return Path.TrimEnd('\\') + "\\" + Name; } }

        public override string ToString() { return FullName; }
    }

    public static class MaintenanceCatalog
    {
        private static readonly List<MaintenanceTaskDefinition> _all = new List<MaintenanceTaskDefinition>
        {
            New(@"\Microsoft\Windows\Defrag\", "ScheduledDefrag", "night-maintenance", true, false,
                "机械盘碎片整理，夜间会唤醒磁盘"),
            New(@"\Microsoft\Windows\DiskCleanup\", "SilentCleanup", "night-maintenance", true, false,
                "磁盘清理，夜间扫描全盘"),
            New(@"\Microsoft\Windows\Data Integrity Scan\", "Data Integrity Check And Scan", "night-maintenance", true, false,
                "存储完整性校验"),
            New(@"\Microsoft\Windows\Data Integrity Scan\", "Data Integrity Scan", "night-maintenance", true, false,
                "存储完整性扫描（重复条目，v1 清单里出现两次）"),
            New(@"\Microsoft\Windows\Chkdsk\", "ProactiveScan", "night-maintenance", true, false,
                "计划内 CHKDSK 预检"),
            New(@"\Microsoft\Windows\Shell\", "IndexerAutomaticMaintenance", "night-maintenance", true, false,
                "搜索索引维护"),
            New(@"\Microsoft\Windows\TaskScheduler\", "Regular Maintenance", "night-maintenance", true, false,
                "自动维护总开关；v1 只禁不恢复，是本轮重构重点"),
            New(@"\Microsoft\Windows\Windows Error Reporting\", "QueueReporting", "crash-recovery", false, false,
                "崩溃报告队列上报；默认不参与夜间停用，Phase 3 由策略决定"),
            New(@"\QuietHDD\", "QuietHDD_NightGate", "gate", false, false,
                "v1 夜间闸门（23:58 / 10:02 / AtStartup+2min）"),
            New(@"\QuietHDD\", "QuietHDD_DiskGate", "gate", false, false,
                "v1 磁盘闸门（diskgate.ps1）")
        };

        private static MaintenanceTaskDefinition New(string path, string name, string module, bool nightDisable, bool protect, string purpose)
        {
            MaintenanceTaskDefinition t = new MaintenanceTaskDefinition();
            t.Path = path;
            t.Name = name;
            t.Module = module;
            t.NightDisableDefault = nightDisable;
            t.Protected = protect;
            t.Purpose = purpose;
            return t;
        }

        public static IList<MaintenanceTaskDefinition> All { get { return _all.AsReadOnly(); } }

        public static List<MaintenanceTaskDefinition> ForModule(string module)
        {
            List<MaintenanceTaskDefinition> r = new List<MaintenanceTaskDefinition>();
            foreach (MaintenanceTaskDefinition t in _all)
            {
                if (string.Equals(t.Module, module, StringComparison.OrdinalIgnoreCase)) r.Add(t);
            }
            return r;
        }

        public static List<MaintenanceTaskDefinition> NightDisableTargets
        {
            get
            {
                List<MaintenanceTaskDefinition> r = new List<MaintenanceTaskDefinition>();
                foreach (MaintenanceTaskDefinition t in _all)
                {
                    if (t.NightDisableDefault && !t.Protected) r.Add(t);
                }
                return r;
            }
        }

        public static List<MaintenanceTaskDefinition> GateTasks { get { return ForModule("gate"); } }

        /// <summary>SysMain（原 SuperFetch）——夜间停用、白天按 baseline 恢复。</summary>
        public const string SysMainService = "SysMain";

        /// <summary>自动维护注册表位置（MaintenanceBoundary / WakeUp）。</summary>
        public const string MaintenanceRegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
        public const string MaintenanceBoundaryValue = "MaintenanceBoundary";
        public const string MaintenanceWakeValue = "WakeUp";

        /// <summary>DISKIDLE 电源设置标识。</summary>
        public const string DiskIdleSubGroup = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        public const string DiskIdleSetting = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
    }
}

using System;
using System.Collections.Generic;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Models;
using QuietMode.Windows.Native;

namespace QuietMode.Windows
{
    /// <summary>
    /// 只读观测：把「系统现在是什么样」采成 SystemObservation。
    /// planner / verifier 只吃这份数据，执行层绝不自己判断状态。
    /// </summary>
    public static class MaintenanceProbe
    {
        public static SystemObservation Capture()
        {
            SystemObservation obs = new SystemObservation();
            obs.CapturedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            obs.Elevated = WindowsVersionInfo.IsElevated();

            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.All)
            {
                ObservedTask t = new ObservedTask();
                t.Path = def.Path;
                t.Name = def.Name;

                TaskQueryResult q = TaskSchedulerClient.Query(def.Path, def.Name);
                if (q.Exists.HasValue)
                {
                    t.Presence = q.Exists.Value ? TaskPresence.Present : TaskPresence.NotPresent;
                }
                else
                {
                    t.Presence = TaskPresence.Unknown;
                }
                t.Enabled = q.Enabled;
                t.State = q.State ?? "";
                t.LastRunTime = q.LastRunTime ?? "";
                t.LastResult = q.LastResult ?? "";
                t.Error = q.Error ?? "";
                obs.Tasks.Add(t);
            }

            ObservedService svc = new ObservedService();
            ServiceSnapshot s = ServiceInventory.Query(MaintenanceCatalog.SysMainService);
            svc.ServiceName = MaintenanceCatalog.SysMainService;
            svc.Exists = !string.Equals(s.StartupType, "missing", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(s.StartupType, "unknown", StringComparison.OrdinalIgnoreCase);
            svc.StartValue = s.StartValue;
            svc.StartupType = s.StartupType ?? "";
            svc.Status = s.Status ?? "";
            if (!svc.Exists) svc.Error = s.DisplayName ?? "";
            obs.Services.Add(svc);

            foreach (string name in new string[]
                     {
                         MaintenanceCatalog.MaintenanceBoundaryValue,
                         MaintenanceCatalog.MaintenanceWakeValue
                     })
            {
                RegistrySnapshot r = RegistryReader.Read(MaintenanceCatalog.MaintenanceRegistryKey, name, "maintenance");
                ObservedRegistryValue o = new ObservedRegistryValue();
                o.KeyPath = r.KeyPath;
                o.ValueName = r.ValueName;
                o.Exists = r.Exists;
                o.Kind = r.Kind;
                o.Value = r.Value;
                obs.Registry.Add(o);
            }

            if (!obs.Elevated)
            {
                obs.Notes.Add("当前进程未提权：任务状态读取可能受限，写操作需要管理员");
            }
            return obs;
        }
    }
}

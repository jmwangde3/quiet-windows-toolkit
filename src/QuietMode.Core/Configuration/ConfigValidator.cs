using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuietMode.Core.Models;

namespace QuietMode.Core.Configuration
{
    public sealed class ValidationReport
    {
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();

        public bool IsValid { get { return Errors.Count == 0; } }

        public void Error(string message) { Errors.Add(message); }
        public void Warn(string message) { Warnings.Add(message); }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(IsValid ? "VALID" : "INVALID");
            foreach (string e in Errors) sb.AppendLine("  ERROR   " + e);
            foreach (string w in Warnings) sb.AppendLine("  WARN    " + w);
            return sb.ToString().TrimEnd();
        }

        public string ToJsonText()
        {
            return ToJsonNode().ToJson(true);
        }

        public Json.JsonObject ToJsonNode()
        {
            Json.JsonObject o = new Json.JsonObject();
            o.Set("valid", IsValid);
            Json.JsonArray errs;
            o.SetArray("errors", out errs);
            foreach (string e in Errors) errs.Add(e);
            Json.JsonArray warns;
            o.SetArray("warnings", out warns);
            foreach (string w in Warnings) warns.Add(w);
            return o;
        }
    }

    /// <summary>
    /// 配置校验（规格 §2.3）。校验失败时调用方必须拒绝执行磁盘操作，不允许回退到 Disk 0。
    /// </summary>
    public static class ConfigValidator
    {
        public const int SpinDownMaxMinutes = 180;

        public static ValidationReport Validate(QuietModeConfig cfg, bool checkFileSystem)
        {
            ValidationReport r = new ValidationReport();
            if (cfg == null)
            {
                r.Error("配置为 null");
                return r;
            }

            if (cfg.SchemaVersion != QuietModeConfig.CurrentSchemaVersion)
            {
                r.Error("schemaVersion=" + cfg.SchemaVersion + "，当前支持的版本是 " + QuietModeConfig.CurrentSchemaVersion
                        + (cfg.SchemaVersion < QuietModeConfig.CurrentSchemaVersion ? "；请先执行 config migrate" : "；配置来自更新的版本，拒绝加载"));
                return r;
            }

            ValidateSchedule(cfg.Schedule, r);
            ValidateFan(cfg.Fan, r, checkFileSystem);
            ValidateDisks(cfg.Disks, r);
            ValidateMaintenance(cfg.Maintenance, r);
            ValidateSpinDown(cfg.SpinDown, r);

            if (cfg.Schedule.Enabled && !cfg.Schedule.StartupOverrideEnabled)
            {
                r.Warn("schedule.startupOverrideEnabled=false：开机时间落在夜间窗口内时不会自动恢复白天状态");
            }
            if (!cfg.Schedule.Enabled && cfg.Disks.Enabled)
            {
                r.Warn("schedule.enabled=false 但 disks.enabled=true：磁盘脱机只能手动触发");
            }

            foreach (string w in cfg.ParseWarnings) r.Warn(w);
            return r;
        }

        private static void ValidateSchedule(ScheduleSettings s, ValidationReport r)
        {
            if (s.Mode != "override" && s.Mode != "coexist")
                r.Error("schedule.mode 必须是 override 或 coexist，实际是 \"" + s.Mode + "\"");
            RequireTime(s.NightStart, "schedule.nightStart", r);
            RequireTime(s.DayStart, "schedule.dayStart", r);
            RequireTime(s.StartupDayStart, "schedule.startupDayStart", r);
            Range(s.StartupGraceMinutes, 0, 120, "schedule.startupGraceMinutes", r);
            Range(s.ReconcileIntervalMinutes, 1, 1440, "schedule.reconcileIntervalMinutes", r);
        }

        private static void ValidateFan(FanSettings f, ValidationReport r, bool checkFileSystem)
        {
            RequireTime(f.AutoSwitch.On, "fan.autoSwitch.on", r);
            RequireTime(f.AutoSwitch.Off, "fan.autoSwitch.off", r);

            if (!f.Enabled) return;

            RequireAbsolutePath(f.IpcPath, "fan.ipcPath", r);
            RequireAbsolutePath(f.ControlExe, "fan.controlExe", r);
            RequireAbsolutePath(f.NormalProfile, "fan.normalProfile", r);
            RequireAbsolutePath(f.SilentProfile, "fan.silentProfile", r);
            if (!string.IsNullOrWhiteSpace(f.FlagPath)) RequireAbsolutePath(f.FlagPath, "fan.flagPath", r);

            if (string.Equals(f.NormalProfile, f.SilentProfile, StringComparison.OrdinalIgnoreCase))
                r.Error("fan.normalProfile 与 fan.silentProfile 指向同一个文件，切换不会产生效果");

            if (checkFileSystem)
            {
                if (!string.IsNullOrWhiteSpace(f.ControlExe) && !File.Exists(f.ControlExe))
                    r.Error("fan.controlExe 不存在：" + f.ControlExe);
                if (!string.IsNullOrWhiteSpace(f.IpcPath) && !File.Exists(f.IpcPath))
                    r.Error("fan.ipcPath 不存在：" + f.IpcPath);
                if (!string.IsNullOrWhiteSpace(f.NormalProfile) && !File.Exists(f.NormalProfile))
                    r.Error("fan.normalProfile 不存在：" + f.NormalProfile);
                if (!string.IsNullOrWhiteSpace(f.SilentProfile) && !File.Exists(f.SilentProfile))
                    r.Error("fan.silentProfile 不存在：" + f.SilentProfile);
            }
        }

        private static void ValidateDisks(DiskSettings d, ValidationReport r)
        {
            if (!d.Enabled) return;

            if (d.Targets.Count == 0)
            {
                r.Error("disks.enabled=true 但没有配置任何磁盘目标；如不需要脱机请显式设置 disks.enabled=false");
                return;
            }

            Dictionary<string, int> uniqueIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> serials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int enabledCount = 0;

            for (int i = 0; i < d.Targets.Count; i++)
            {
                DiskTarget t = d.Targets[i];
                string label = "disks.targets[" + i + "]";

                if (!t.HasIdentity)
                {
                    r.Error(label + " 没有 uniqueId / serialNumber，无法唯一识别；禁止按磁盘编号回退");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(t.UniqueId))
                {
                    int prev;
                    if (uniqueIds.TryGetValue(t.UniqueId, out prev))
                        r.Error(label + " 与 disks.targets[" + prev + "] 的 uniqueId 重复：" + t.UniqueId);
                    else uniqueIds[t.UniqueId] = i;
                }

                if (!string.IsNullOrWhiteSpace(t.SerialNumber))
                {
                    int prev;
                    if (serials.TryGetValue(t.SerialNumber, out prev))
                        r.Error(label + " 与 disks.targets[" + prev + "] 的 serialNumber 重复：" + t.SerialNumber);
                    else serials[t.SerialNumber] = i;
                }

                if (t.SizeBytes <= 0) r.Warn(label + " sizeBytes 未记录，身份核对将只能依赖 uniqueId / 序列号");
                if (t.Enabled) enabledCount++;
            }

            if (enabledCount == 0) r.Warn("disks.enabled=true 但所有目标都是 enabled=false，实际不会脱机任何磁盘");
        }

        private static void ValidateMaintenance(MaintenanceSettings m, ValidationReport r)
        {
            Range(m.MaintenanceHour, 0, 23, "maintenance.maintenanceHour", r);
            if (!m.Enabled && m.StrictNightDisable)
                r.Warn("maintenance.enabled=false 时 strictNightDisable 不生效");
        }

        private static void ValidateSpinDown(SpinDownSettings s, ValidationReport r)
        {
            Range(s.Minutes, 0, SpinDownMaxMinutes, "spinDown.minutes", r);
            if (s.Enabled && s.Minutes <= 0)
                r.Error("spinDown.enabled=true 但 spinDown.minutes=0（0 表示永不），两者矛盾");
        }

        private static void RequireTime(string value, string label, ValidationReport r)
        {
            int h, m;
            if (!Models.TimeText.TryParseHhmm(value, out h, out m))
                r.Error(label + " 不是合法的 HH:mm 时间：" + (value ?? "<null>"));
        }

        private static void Range(int value, int min, int max, string label, ValidationReport r)
        {
            if (value < min || value > max)
                r.Error(label + " 超出范围 " + min + ".." + max + "，实际是 " + value);
        }

        private static void RequireAbsolutePath(string value, string label, ValidationReport r)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                r.Error(label + " 为空；fan.enabled=true 时必须给出绝对路径");
                return;
            }
            if (!Path.IsPathRooted(value))
                r.Error(label + " 不是绝对路径：" + value);
        }
    }
}

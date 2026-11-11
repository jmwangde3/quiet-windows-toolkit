using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuietMode.Core.Models;

namespace QuietMode.Core.Configuration
{
    /// <summary>磁盘编号 → 稳定身份的解析结果（由 Windows 层提供）。</summary>
    public sealed class DiskHint
    {
        public int Number = -1;
        public string UniqueId = "";
        public string SerialNumber = "";
        public string FriendlyName = "";
        public long SizeBytes;
        public string BusType = "";
        public bool Found;
    }

    public sealed class MigrationResult
    {
        public bool LegacyFound;
        public bool WroteConfig;
        public bool WroteState;
        public QuietModeConfig Config;
        public QuietModeState State;
        public string LegacySettingsPath = "";
        public string LegacyStatePath = "";
        public List<string> Notes = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Errors = new List<string>();

        public bool Ok { get { return Errors.Count == 0; } }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("旧配置：" + (LegacyFound ? "找到 " + LegacySettingsPath : "未找到"));
            foreach (string n in Notes) sb.AppendLine("  · " + n);
            foreach (string w in Warnings) sb.AppendLine("  ! " + w);
            foreach (string e in Errors) sb.AppendLine("  x " + e);
            sb.AppendLine(WroteConfig ? "已写入 " + (Config != null ? "config.json" : "") : "未写入 config.json");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// v1（%APPDATA%\QuietMode\settings.ini + state.ini）→ v2（config.json + state.json）。
    /// 只做结构转换，不改任何系统状态；磁盘编号只在能解析到稳定身份时才进目标列表。
    /// </summary>
    public static class ConfigMigrator
    {
        public static MigrationResult Apply(QuietModePaths paths, Func<int, DiskHint> diskLookup, bool overwriteExisting)
        {
            MigrationResult r = Inspect(paths, diskLookup);
            if (!r.Ok) return r;

            if (!r.LegacyFound)
            {
                r.Warnings.Add("没有可迁移的 v1 配置，请用 config init 生成默认策略");
                return r;
            }

            ConfigStore store = new ConfigStore(paths);
            if (File.Exists(paths.ConfigPath) && !overwriteExisting)
            {
                r.Errors.Add("目标已存在 " + paths.ConfigPath + "；迁移会覆盖它，需显式 --force");
                return r;
            }

            ValidationReport report = ConfigValidator.Validate(r.Config, false);
            foreach (string e in report.Errors) r.Errors.Add("迁移结果校验失败：" + e);
            if (!r.Ok) return r;

            store.Save(r.Config, false);
            r.WroteConfig = true;

            StateStore stateStore = new StateStore(paths);
            stateStore.Save(r.State);
            r.WroteState = true;
            return r;
        }

        public static MigrationResult Inspect(QuietModePaths paths, Func<int, DiskHint> diskLookup)
        {
            MigrationResult r = new MigrationResult();
            string settingsPath = Path.Combine(paths.LegacyAppDataDir, "settings.ini");
            string statePath = Path.Combine(paths.LegacyAppDataDir, "state.ini");
            r.LegacySettingsPath = settingsPath;
            r.LegacyStatePath = statePath;

            Dictionary<string, string> ini = ReadIni(settingsPath);
            Dictionary<string, string> st = ReadIni(statePath);
            r.LegacyFound = ini.Count > 0;
            if (!r.LegacyFound)
            {
                r.Config = ConfigStore.DefaultPolicy();
                r.State = LegacyState(st, r);
                return r;
            }

            QuietModeConfig c = ConfigStore.DefaultPolicy();

            // ---- 风扇 ----
            c.Fan.Enabled = GB(ini, "FanEnabled", true);
            c.Fan.IpcPath = SanitizePath(G(ini, "FanIpcCtl", ""), "FanIpcCtl", r);
            c.Fan.ControlExe = SanitizePath(G(ini, "FanControlExe", ""), "FanControlExe", r);
            c.Fan.NormalProfile = SanitizePath(G(ini, "FanNormal", ""), "FanNormal", r);
            c.Fan.SilentProfile = SanitizePath(G(ini, "FanSilent", ""), "FanSilent", r);
            c.Fan.FlagPath = SanitizePath(G(ini, "FanFlag", ""), "FanFlag", r);
            if (c.Fan.Enabled && (string.IsNullOrWhiteSpace(c.Fan.IpcPath) || string.IsNullOrWhiteSpace(c.Fan.SilentProfile)))
            {
                r.Warnings.Add("v1 中风扇功能开启但配置文件路径不完整，已把 fan.enabled 置为 false，需在设置里重新指定");
                c.Fan.Enabled = false;
            }

            // ---- 磁盘 ----
            c.Disks.Enabled = GB(ini, "HddEnabled", true);
            c.Disks.ForceBusy = GB(ini, "HddForce", false);
            string disksText = G(ini, "HddDisks", "0,1");
            foreach (string part in disksText.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                int number;
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    r.Warnings.Add("HddDisks 中的 \"" + trimmed + "\" 不是磁盘编号，已忽略");
                    continue;
                }

                DiskTarget target = new DiskTarget();
                target.LegacyNumber = number;
                DiskHint hint = diskLookup == null ? null : diskLookup(number);
                if (hint != null && hint.Found && (!string.IsNullOrWhiteSpace(hint.UniqueId) || !string.IsNullOrWhiteSpace(hint.SerialNumber)))
                {
                    target.UniqueId = hint.UniqueId ?? "";
                    target.SerialNumber = hint.SerialNumber ?? "";
                    target.FriendlyName = hint.FriendlyName ?? "";
                    target.SizeBytes = hint.SizeBytes;
                    target.BusType = hint.BusType ?? "";
                    target.Enabled = true;
                    r.Notes.Add("磁盘 #" + number + " → " + DescribeTarget(target));
                }
                else
                {
                    // 不把「猜的磁盘」写进配置：解析不出稳定身份就整条丢弃，只留告警。
                    r.Warnings.Add("磁盘 #" + number + " 无法解析到稳定身份，已从目标列表移除；"
                                   + "请在设置中重新选择（禁止按编号猜测）");
                    continue;
                }
                c.Disks.Targets.Add(target);
            }

            if (c.Disks.Enabled && c.Disks.Targets.Count == 0)
            {
                c.Disks.Enabled = false;
                r.Warnings.Add("v1 的磁盘目标一个都没解析成功，已把 disks.enabled 置为 false；"
                               + "在设置里重新选择之前不会对任何磁盘做脱机");
            }

            // ---- 维护 ----
            c.Maintenance.Enabled = GB(ini, "MaintenanceEnabled", true);

            // ---- 停转 ----
            c.SpinDown.Enabled = GB(ini, "SpinDownEnabled", false);
            int legacySeconds = GI(ini, "SpinDownSeconds", 1200);
            c.SpinDown.Minutes = GI(ini, "SpinDownMinutes", legacySeconds / 60);
            if (c.SpinDown.Minutes < 0) c.SpinDown.Minutes = 0;
            if (c.SpinDown.Minutes > ConfigValidator.SpinDownMaxMinutes) c.SpinDown.Minutes = ConfigValidator.SpinDownMaxMinutes;
            if (c.SpinDown.Enabled && c.SpinDown.Minutes == 0)
            {
                c.SpinDown.Enabled = false;
                r.Warnings.Add("v1 停转开启但分钟数为 0，已改为关闭（0 表示永不）");
            }

            // ---- 调度 ----
            string mode = G(ini, "ScheduleMode", "override");
            if (mode != "override" && mode != "coexist") mode = "override";
            c.Schedule.Mode = mode;
            c.Schedule.Enabled = GB(ini, "GateEnabled", true);
            c.Schedule.NightStart = G(ini, "AutoSilentOn", "23:58");
            c.Schedule.DayStart = G(ini, "AutoSilentOff", "10:02");
            c.Fan.AutoSwitch.Enabled = GB(ini, "AutoSilent", true);
            c.Fan.AutoSwitch.On = c.Schedule.NightStart;
            c.Fan.AutoSwitch.Off = c.Schedule.DayStart;

            // ---- 启动 ----
            c.Startup.AutoStart = GB(ini, "AutoStart", false);
            c.Startup.RestoreOnBoot = GB(ini, "RestoreOnBoot", false);

            r.Notes.Add("settings.ini 共 " + ini.Count + " 个键，已映射到 v2 结构");
            r.Config = c;
            r.State = LegacyState(st, r);
            return r;
        }

        private static QuietModeState LegacyState(Dictionary<string, string> st, MigrationResult r)
        {
            QuietModeState s = new QuietModeState();
            s.ModeOn = GB(st, "ModeOn", false);
            s.SchedulePaused = GB(st, "SchedulePaused", false);
            s.AutoSilentActive = GB(st, "AutoSilentActive", false);
            s.AutoSilentSuppressed = GB(st, "AutoSilentSuppressed", false);
            s.AutoOffDayStamp = G(st, "AutoOffDayStamp", "");
            if (st.Count > 0) r.Notes.Add("state.ini 共 " + st.Count + " 个键，已迁移到 state.json");
            return s;
        }

        private static string DescribeTarget(DiskTarget t)
        {
            return (string.IsNullOrWhiteSpace(t.FriendlyName) ? "(未命名)" : t.FriendlyName)
                   + " / " + (t.SizeBytes > 0 ? FormatSize(t.SizeBytes) : "大小未知")
                   + " / " + (string.IsNullOrWhiteSpace(t.BusType) ? "总线未知" : t.BusType)
                   + " / uniqueId=" + (string.IsNullOrWhiteSpace(t.UniqueId) ? "-" : t.UniqueId);
        }

        private static string FormatSize(long bytes)
        {
            double gb = bytes / 1024.0 / 1024.0 / 1024.0;
            return gb.ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        private static string SanitizePath(string value, string key, MigrationResult r)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (value.IndexOf('<') >= 0 || value.IndexOf('>') >= 0)
            {
                r.Warnings.Add(key + " 仍是占位符（" + value + "），已清空");
                return "";
            }
            return value.Trim();
        }

        private static Dictionary<string, string> ReadIni(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return d;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch { return d; }
            foreach (string raw in lines)
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#") || s.StartsWith(";")) continue;
                int i = s.IndexOf('=');
                if (i <= 0) continue;
                d[s.Substring(0, i).Trim()] = s.Substring(i + 1).Trim();
            }
            return d;
        }

        private static string G(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : def;
        }

        private static bool GB(Dictionary<string, string> d, string k, bool def)
        {
            string v;
            if (!d.TryGetValue(k, out v)) return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static int GI(Dictionary<string, string> d, string k, int def)
        {
            string v;
            if (!d.TryGetValue(k, out v)) return def;
            int n;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : def;
        }
    }
}

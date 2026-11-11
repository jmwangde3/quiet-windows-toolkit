using System;
using System.Collections.Generic;
using System.Globalization;
using QuietMode.Core.Json;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// v2 机器级配置（config.json）。字段含义见 docs/CONFIGURATION.md。
    /// 规格 §2.2 定义的字段全部保留；额外字段用于承接 v1 已有功能，避免重构期功能回退。
    /// </summary>
    public sealed class QuietModeConfig
    {
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion = CurrentSchemaVersion;
        public ScheduleSettings Schedule = new ScheduleSettings();
        public FanSettings Fan = new FanSettings();
        public DiskSettings Disks = new DiskSettings();
        public MaintenanceSettings Maintenance = new MaintenanceSettings();
        public SpinDownSettings SpinDown = new SpinDownSettings();
        public StartupSettings Startup = new StartupSettings();

        /// <summary>解析过程中收集的类型/取值问题（不抛异常，交由 ConfigValidator 汇总）。</summary>
        public List<string> ParseWarnings = new List<string>();

        public JsonObject ToJson()
        {
            JsonObject root = new JsonObject();
            root.Set("schemaVersion", SchemaVersion);
            root.Set("schedule", Schedule.ToJson());
            root.Set("fan", Fan.ToJson());
            root.Set("disks", Disks.ToJson());
            root.Set("maintenance", Maintenance.ToJson());
            root.Set("spinDown", SpinDown.ToJson());
            root.Set("startup", Startup.ToJson());
            return root;
        }

        public string ToJsonText() { return ToJson().ToJson(true); }

        public static QuietModeConfig FromJson(JsonObject root)
        {
            if (root == null) throw new ArgumentNullException("root");
            QuietModeConfig c = new QuietModeConfig();
            c.SchemaVersion = root.GetInt("schemaVersion", 1);
            JsonObject o;

            o = root.GetObject("schedule");
            if (o != null) c.Schedule = ScheduleSettings.FromJson(o, c.ParseWarnings);
            o = root.GetObject("fan");
            if (o != null) c.Fan = FanSettings.FromJson(o, c.ParseWarnings);
            o = root.GetObject("disks");
            if (o != null) c.Disks = DiskSettings.FromJson(o, c.ParseWarnings);
            o = root.GetObject("maintenance");
            if (o != null) c.Maintenance = MaintenanceSettings.FromJson(o, c.ParseWarnings);
            o = root.GetObject("spinDown");
            if (o != null) c.SpinDown = SpinDownSettings.FromJson(o, c.ParseWarnings);
            o = root.GetObject("startup");
            if (o != null) c.Startup = StartupSettings.FromJson(o, c.ParseWarnings);

            return c;
        }

        public QuietModeConfig Clone()
        {
            return FromJson(ToJson());
        }
    }

    public sealed class ScheduleSettings
    {
        public bool Enabled = true;
        public string Mode = "override";          // override | coexist
        public string NightStart = "23:58";
        public string DayStart = "10:02";
        public bool StartupOverrideEnabled = true;
        public string StartupDayStart = "06:00";
        public int StartupGraceMinutes = 15;
        public int ReconcileIntervalMinutes = 10;

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("mode", Mode);
            o.Set("nightStart", NightStart);
            o.Set("dayStart", DayStart);
            o.Set("startupOverrideEnabled", StartupOverrideEnabled);
            o.Set("startupDayStart", StartupDayStart);
            o.Set("startupGraceMinutes", StartupGraceMinutes);
            o.Set("reconcileIntervalMinutes", ReconcileIntervalMinutes);
            return o;
        }

        public static ScheduleSettings FromJson(JsonObject o, List<string> w)
        {
            ScheduleSettings s = new ScheduleSettings();
            s.Enabled = ReadBool(o, "enabled", s.Enabled, w, "schedule.enabled");
            s.Mode = ReadString(o, "mode", s.Mode, w, "schedule.mode");
            s.NightStart = ReadString(o, "nightStart", s.NightStart, w, "schedule.nightStart");
            s.DayStart = ReadString(o, "dayStart", s.DayStart, w, "schedule.dayStart");
            s.StartupOverrideEnabled = ReadBool(o, "startupOverrideEnabled", s.StartupOverrideEnabled, w, "schedule.startupOverrideEnabled");
            s.StartupDayStart = ReadString(o, "startupDayStart", s.StartupDayStart, w, "schedule.startupDayStart");
            s.StartupGraceMinutes = ReadInt(o, "startupGraceMinutes", s.StartupGraceMinutes, w, "schedule.startupGraceMinutes");
            s.ReconcileIntervalMinutes = ReadInt(o, "reconcileIntervalMinutes", s.ReconcileIntervalMinutes, w, "schedule.reconcileIntervalMinutes");
            return s;
        }

        internal static string ReadString(JsonObject o, string name, string fallback, List<string> w, string label)
        {
            JsonNode n = o.Get(name);
            if (n == null || n.IsNull) return fallback;
            try { return n.AsString(); }
            catch (Exception ex) { w.Add(label + " 类型错误（" + ex.Message + "），已用默认值 " + fallback); return fallback; }
        }

        internal static bool ReadBool(JsonObject o, string name, bool fallback, List<string> w, string label)
        {
            JsonNode n = o.Get(name);
            if (n == null || n.IsNull) return fallback;
            try { return n.AsBool(); }
            catch (Exception ex) { w.Add(label + " 类型错误（" + ex.Message + "），已用默认值 " + fallback); return fallback; }
        }

        internal static int ReadInt(JsonObject o, string name, int fallback, List<string> w, string label)
        {
            JsonNode n = o.Get(name);
            if (n == null || n.IsNull) return fallback;
            try { return n.AsInt(); }
            catch (Exception ex) { w.Add(label + " 类型错误（" + ex.Message + "），已用默认值 " + fallback); return fallback; }
        }
    }

    public sealed class FanSettings
    {
        public bool Enabled = false;
        public string IpcPath = "";
        public string ControlExe = "";
        public string NormalProfile = "";
        public string SilentProfile = "";
        public string FlagPath = "";
        public AutoSwitchSettings AutoSwitch = new AutoSwitchSettings();

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("ipcPath", IpcPath ?? "");
            o.Set("controlExe", ControlExe ?? "");
            o.Set("normalProfile", NormalProfile ?? "");
            o.Set("silentProfile", SilentProfile ?? "");
            o.Set("flagPath", FlagPath ?? "");
            o.Set("autoSwitch", AutoSwitch.ToJson());
            return o;
        }

        public static FanSettings FromJson(JsonObject o, List<string> w)
        {
            FanSettings f = new FanSettings();
            f.Enabled = ScheduleSettings.ReadBool(o, "enabled", f.Enabled, w, "fan.enabled");
            f.IpcPath = ScheduleSettings.ReadString(o, "ipcPath", f.IpcPath, w, "fan.ipcPath");
            f.ControlExe = ScheduleSettings.ReadString(o, "controlExe", f.ControlExe, w, "fan.controlExe");
            f.NormalProfile = ScheduleSettings.ReadString(o, "normalProfile", f.NormalProfile, w, "fan.normalProfile");
            f.SilentProfile = ScheduleSettings.ReadString(o, "silentProfile", f.SilentProfile, w, "fan.silentProfile");
            f.FlagPath = ScheduleSettings.ReadString(o, "flagPath", f.FlagPath, w, "fan.flagPath");
            JsonObject a = o.GetObject("autoSwitch");
            if (a != null) f.AutoSwitch = AutoSwitchSettings.FromJson(a, w);
            return f;
        }
    }

    public sealed class AutoSwitchSettings
    {
        public bool Enabled = true;
        public string On = "23:58";
        public string Off = "10:02";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("on", On);
            o.Set("off", Off);
            return o;
        }

        public static AutoSwitchSettings FromJson(JsonObject o, List<string> w)
        {
            AutoSwitchSettings a = new AutoSwitchSettings();
            a.Enabled = ScheduleSettings.ReadBool(o, "enabled", a.Enabled, w, "fan.autoSwitch.enabled");
            a.On = ScheduleSettings.ReadString(o, "on", a.On, w, "fan.autoSwitch.on");
            a.Off = ScheduleSettings.ReadString(o, "off", a.Off, w, "fan.autoSwitch.off");
            return a;
        }
    }

    public sealed class DiskSettings
    {
        public bool Enabled = true;
        public bool ForceBusy = false;
        public List<DiskTarget> Targets = new List<DiskTarget>();

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("forceBusy", ForceBusy);
            JsonArray arr;
            o.SetArray("targets", out arr);
            foreach (DiskTarget t in Targets) arr.Add(t.ToJson());
            return o;
        }

        public static DiskSettings FromJson(JsonObject o, List<string> w)
        {
            DiskSettings d = new DiskSettings();
            d.Enabled = ScheduleSettings.ReadBool(o, "enabled", d.Enabled, w, "disks.enabled");
            d.ForceBusy = ScheduleSettings.ReadBool(o, "forceBusy", d.ForceBusy, w, "disks.forceBusy");
            JsonArray arr = o.GetArray("targets");
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    JsonObject item = arr[i].TryObject();
                    if (item == null) { w.Add("disks.targets[" + i + "] 不是对象，已跳过"); continue; }
                    d.Targets.Add(DiskTarget.FromJson(item, w, i));
                }
            }
            return d;
        }
    }

    /// <summary>
    /// 磁盘目标。唯一身份 = uniqueId（首选）或 serialNumber；磁盘编号只作运行时线索，不参与匹配。
    /// </summary>
    public sealed class DiskTarget
    {
        public string UniqueId = "";
        public string SerialNumber = "";
        public string FriendlyName = "";
        public long SizeBytes = 0;
        public string BusType = "";
        public bool Enabled = true;
        /// <summary>v1 迁移时保留的磁盘编号线索，仅用于日志和人眼核对。</summary>
        public int LegacyNumber = -1;

        public bool HasIdentity
        {
            get { return !string.IsNullOrWhiteSpace(UniqueId) || !string.IsNullOrWhiteSpace(SerialNumber); }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("uniqueId", UniqueId ?? "");
            o.Set("serialNumber", SerialNumber ?? "");
            o.Set("friendlyName", FriendlyName ?? "");
            o.Set("sizeBytes", SizeBytes);
            o.Set("busType", BusType ?? "");
            o.Set("enabled", Enabled);
            if (LegacyNumber >= 0) o.Set("legacyNumber", LegacyNumber);
            return o;
        }

        public static DiskTarget FromJson(JsonObject o, List<string> w, int index)
        {
            DiskTarget t = new DiskTarget();
            string label = "disks.targets[" + index + "]";
            t.UniqueId = ScheduleSettings.ReadString(o, "uniqueId", "", w, label + ".uniqueId");
            t.SerialNumber = ScheduleSettings.ReadString(o, "serialNumber", "", w, label + ".serialNumber");
            t.FriendlyName = ScheduleSettings.ReadString(o, "friendlyName", "", w, label + ".friendlyName");
            JsonNode size = o.Get("sizeBytes");
            if (size != null && !size.IsNull)
            {
                try { t.SizeBytes = size.AsLong(); }
                catch (Exception ex) { w.Add(label + ".sizeBytes 类型错误（" + ex.Message + "）"); }
            }
            t.BusType = ScheduleSettings.ReadString(o, "busType", "", w, label + ".busType");
            t.Enabled = ScheduleSettings.ReadBool(o, "enabled", true, w, label + ".enabled");
            JsonNode ln = o.Get("legacyNumber");
            if (ln != null && !ln.IsNull)
            {
                try { t.LegacyNumber = ln.AsInt(); }
                catch { }
            }
            return t;
        }
    }

    public sealed class MaintenanceSettings
    {
        public bool Enabled = true;
        public bool StrictNightDisable = true;
        public bool DisableSysMainAtNight = true;
        public int MaintenanceHour = 10;
        public bool DisableWake = true;

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("strictNightDisable", StrictNightDisable);
            o.Set("disableSysMainAtNight", DisableSysMainAtNight);
            o.Set("maintenanceHour", MaintenanceHour);
            o.Set("disableWake", DisableWake);
            return o;
        }

        public static MaintenanceSettings FromJson(JsonObject o, List<string> w)
        {
            MaintenanceSettings m = new MaintenanceSettings();
            m.Enabled = ScheduleSettings.ReadBool(o, "enabled", m.Enabled, w, "maintenance.enabled");
            m.StrictNightDisable = ScheduleSettings.ReadBool(o, "strictNightDisable", m.StrictNightDisable, w, "maintenance.strictNightDisable");
            m.DisableSysMainAtNight = ScheduleSettings.ReadBool(o, "disableSysMainAtNight", m.DisableSysMainAtNight, w, "maintenance.disableSysMainAtNight");
            m.MaintenanceHour = ScheduleSettings.ReadInt(o, "maintenanceHour", m.MaintenanceHour, w, "maintenance.maintenanceHour");
            m.DisableWake = ScheduleSettings.ReadBool(o, "disableWake", m.DisableWake, w, "maintenance.disableWake");
            return m;
        }
    }

    public sealed class SpinDownSettings
    {
        public bool Enabled = false;
        public int Minutes = 20;

        public int Seconds { get { return Minutes * 60; } }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("minutes", Minutes);
            return o;
        }

        public static SpinDownSettings FromJson(JsonObject o, List<string> w)
        {
            SpinDownSettings s = new SpinDownSettings();
            s.Enabled = ScheduleSettings.ReadBool(o, "enabled", s.Enabled, w, "spinDown.enabled");
            s.Minutes = ScheduleSettings.ReadInt(o, "minutes", s.Minutes, w, "spinDown.minutes");
            return s;
        }
    }

    public sealed class StartupSettings
    {
        public bool AutoStart = false;
        public bool RestoreOnBoot = false;

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("autoStart", AutoStart);
            o.Set("restoreOnBoot", RestoreOnBoot);
            return o;
        }

        public static StartupSettings FromJson(JsonObject o, List<string> w)
        {
            StartupSettings s = new StartupSettings();
            s.AutoStart = ScheduleSettings.ReadBool(o, "autoStart", s.AutoStart, w, "startup.autoStart");
            s.RestoreOnBoot = ScheduleSettings.ReadBool(o, "restoreOnBoot", s.RestoreOnBoot, w, "startup.restoreOnBoot");
            return s;
        }
    }

    /// <summary>工具版本常量，写入 baseline 元数据。</summary>
    public static class ProductInfo
    {
        public const string Version = "2.0.0-phase4";
        public const string Name = "QuietMode";
    }

    public static class TimeText
    {
        public static bool TryParseHhmm(string s, out int hour, out int minute)
        {
            hour = minute = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            int colon = t.IndexOf(':');
            if (colon <= 0 || colon == t.Length - 1) return false;
            string hs = t.Substring(0, colon);
            string ms = t.Substring(colon + 1);
            if (hs.Length < 1 || hs.Length > 2 || ms.Length != 2) return false;
            if (!int.TryParse(hs, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
            if (!int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
            return hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59;
        }

        public static int ToMinutes(string s)
        {
            int h, m;
            if (!TryParseHhmm(s, out h, out m)) return -1;
            return h * 60 + m;
        }
    }
}

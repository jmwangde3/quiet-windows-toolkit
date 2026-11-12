using System;
using System.Collections.Generic;
using QuietMode.Core.Json;
using QuietMode.Core.Scheduling;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// 给托盘 / 外部程序看的「一次观测」快照。全部字段都是观测事实或已存状态，
    /// 不含任何期望值比对后的「成功」结论 —— 那是 verifier 的职责。
    /// 托盘只读这个快照渲染界面，绝不自己判断该开还是该关。
    /// </summary>
    public sealed class StatusSnapshot
    {
        public string GeneratedUtc = "";
        public bool Elevated;

        public bool ConfigOk = true;
        public bool ConfigExists;
        public string ConfigError = "";
        public string ConfigPath = "";
        public List<string> ConfigWarnings = new List<string>();

        public ScheduleSettings Schedule = new ScheduleSettings();
        public StartupSettings Startup = new StartupSettings();
        public WindowEvaluation Window;
        public Decision Decision;
        public QuietModeState State = new QuietModeState();

        public FanStatus Fan = new FanStatus();
        public List<DiskStatus> Disks = new List<DiskStatus>();
        public SpinDownStatus SpinDown = new SpinDownStatus();
        public TaskStatus Task = new TaskStatus();
        public bool BaselineExists;

        public bool AnyDiskOffline
        {
            get
            {
                foreach (DiskStatus d in Disks) if (d.Offline.HasValue && d.Offline.Value) return true;
                return false;
            }
        }

        public int UnknownDisks
        {
            get
            {
                int n = 0;
                foreach (DiskStatus d in Disks) if (!d.Offline.HasValue) n++;
                return n;
            }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("ok", true);
            o.Set("generatedUtc", GeneratedUtc ?? "");
            o.Set("elevated", Elevated);

            JsonObject cfg = o.SetObject("config");
            cfg.Set("path", ConfigPath ?? "");
            cfg.Set("exists", ConfigExists);
            cfg.Set("ok", ConfigOk);
            cfg.Set("error", ConfigError ?? "");
            JsonArray warns;
            cfg.SetArray("warnings", out warns);
            foreach (string w in ConfigWarnings) warns.Add(w);

            o.Set("schedule", Schedule == null ? new ScheduleSettings().ToJson() : Schedule.ToJson());
            o.Set("startup", Startup == null ? new StartupSettings().ToJson() : Startup.ToJson());

            JsonObject win = o.SetObject("window");
            if (Window == null)
            {
                win.Set("isNight", false);
                win.Set("band", "Day");
                win.Set("reason", "未计算");
            }
            else
            {
                win.Set("isNight", Window.IsNight);
                win.Set("band", Window.Band.ToString());
                win.Set("nowMinutes", Window.NowMinutes);
                win.Set("nowText", TimeOfDay.Format(Window.NowMinutes));
                win.Set("nightStartMinutes", Window.NightStartMinutes);
                win.Set("dayStartMinutes", Window.DayStartMinutes);
                win.Set("effectiveDayStartMinutes", Window.EffectiveDayStartMinutes);
                win.Set("startupOverrideApplied", Window.StartupOverrideApplied);
                win.Set("reason", Window.Reason ?? "");
            }

            JsonObject dec = o.SetObject("controller");
            if (Decision == null)
            {
                dec.Set("state", "Error");
                dec.Set("text", "未计算");
                dec.Set("action", "none");
                dec.Set("targetNight", false);
                dec.Set("reason", "未计算");
            }
            else
            {
                dec.Set("state", Decision.State.ToString());
                dec.Set("text", ControllerStates.Text(Decision.State));
                dec.Set("action", Decision.Action ?? "none");
                dec.Set("targetNight", Decision.TargetNight);
                dec.Set("reason", Decision.Reason ?? "");
                JsonArray notes;
                dec.SetArray("notes", out notes);
                foreach (string n in Decision.Notes) notes.Add(n);
            }

            JsonObject st = o.SetObject("state");
            QuietModeState s = State ?? new QuietModeState();
            st.Set("modeOn", s.ModeOn);
            st.Set("schedulePaused", s.SchedulePaused);
            st.Set("autoSilentActive", s.AutoSilentActive);
            st.Set("autoSilentSuppressed", s.AutoSilentSuppressed);
            st.Set("autoSilentSuppressedWindow", s.AutoSilentSuppressedWindow ?? "");
            st.Set("lastVerdict", s.LastVerdict ?? "");
            st.Set("lastErrorCode", s.LastErrorCode ?? "");
            st.Set("lastReconcileAction", s.LastReconcileAction ?? "");
            st.Set("consecutiveFailures", s.ConsecutiveFailures);
            st.Set("lastReconcileUtc", s.LastReconcileUtc ?? "");
            st.Set("lastNightRunUtc", s.LastNightRunUtc ?? "");
            st.Set("lastDayRunUtc", s.LastDayRunUtc ?? "");
            st.Set("autoOffDayStamp", s.AutoOffDayStamp ?? "");
            st.Set("recoveryActive", s.Recovery != null && s.Recovery.Active);
            if (s.Recovery != null && s.Recovery.Active) st.Set("recoveryReason", s.Recovery.Reason ?? "");

            o.Set("fan", (Fan ?? new FanStatus()).ToJson());

            JsonObject disks = o.SetObject("disks");
            disks.Set("anyOffline", AnyDiskOffline);
            disks.Set("unknown", UnknownDisks);
            JsonArray arr;
            disks.SetArray("targets", out arr);
            foreach (DiskStatus d in Disks) arr.Add(d.ToJson());

            o.Set("spinDown", (SpinDown ?? new SpinDownStatus()).ToJson());
            o.Set("task", (Task ?? new TaskStatus()).ToJson());
            o.Set("baselineExists", BaselineExists);
            return o;
        }
    }

    public sealed class FanStatus
    {
        public bool Enabled;
        public bool Available;
        public string CurrentProfile = "";
        public string NormalProfile = "";
        public string SilentProfile = "";
        /// <summary>silent | normal | other | unknown</summary>
        public string Match = "unknown";
        public string Error = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("available", Available);
            o.Set("currentProfile", CurrentProfile ?? "");
            o.Set("normalProfile", NormalProfile ?? "");
            o.Set("silentProfile", SilentProfile ?? "");
            o.Set("match", Match ?? "unknown");
            o.Set("error", Error ?? "");
            return o;
        }
    }

    public sealed class DiskStatus
    {
        /// <summary>运行时编号，只用于显示；身份仍是 uniqueId / serial。</summary>
        public int Number = -1;
        public bool Matched;
        public string MatchStatus = "";
        public string MatchReason = "";
        public string UniqueId = "";
        public string SerialNumber = "";
        public string FriendlyName = "";
        public long SizeBytes;
        public bool? Offline;
        public List<string> Letters = new List<string>();

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("number", Number);
            o.Set("matched", Matched);
            o.Set("matchStatus", MatchStatus ?? "");
            o.Set("matchReason", MatchReason ?? "");
            o.Set("uniqueId", UniqueId ?? "");
            o.Set("serialNumber", SerialNumber ?? "");
            o.Set("friendlyName", FriendlyName ?? "");
            o.Set("sizeBytes", SizeBytes);
            o.Set("offline", Offline.HasValue ? (JsonNode)new JsonBool(Offline.Value) : JsonNull.Instance);
            JsonArray arr;
            o.SetArray("letters", out arr);
            foreach (string l in Letters) arr.Add(l);
            return o;
        }
    }

    public sealed class SpinDownStatus
    {
        public int AcSeconds = -1;
        public int DcSeconds = -1;
        public int ConfiguredMinutes;
        public bool ConfiguredEnabled;
        public string Error = "";

        /// <summary>以 AC 为准（台式机睡眠场景），两者不一致时在 text 里点出来。</summary>
        public bool EffectiveOn { get { return AcSeconds > 0; } }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("acSeconds", AcSeconds);
            o.Set("dcSeconds", DcSeconds);
            o.Set("effectiveOn", EffectiveOn);
            o.Set("configuredMinutes", ConfiguredMinutes);
            o.Set("configuredEnabled", ConfiguredEnabled);
            o.Set("error", Error ?? "");
            return o;
        }
    }

    public sealed class TaskStatus
    {
        public string Path = "";
        public string Name = "";
        public bool? Exists;
        public string State = "";
        public string LastRunTime = "";
        public string LastResult = "";
        public bool Conflict;
        public string Error = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("path", Path ?? "");
            o.Set("name", Name ?? "");
            o.Set("exists", Exists.HasValue ? (JsonNode)new JsonBool(Exists.Value) : JsonNull.Instance);
            o.Set("state", State ?? "");
            o.Set("lastRunTime", LastRunTime ?? "");
            o.Set("lastResult", LastResult ?? "");
            o.Set("conflict", Conflict);
            o.Set("error", Error ?? "");
            return o;
        }
    }
}

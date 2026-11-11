using System;
using System.Collections.Generic;
using QuietMode.Core.Json;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// v2 运行状态（state.json）。只存运行期事实，不存策略。
    /// 恢复事务进度也持久化在这里（规格 §5.3「恢复进度记录」）。
    /// </summary>
    public sealed class QuietModeState
    {
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion = CurrentSchemaVersion;
        public bool ModeOn;
        public bool SchedulePaused;
        public bool AutoSilentActive;
        public bool AutoSilentSuppressed;
        /// <summary>最近一次 reconcile 的结论：Ok | Degraded | Failed | NotRun。</summary>
        public string LastVerdict = "";
        public string LastErrorCode = "";
        /// <summary>最近一次 reconcile 执行的动作：night | day | restore | none。</summary>
        public string LastReconcileAction = "";
        /// <summary>连续失败次数；成功清零。用于 UI 区分「正在重试」和「一直失败」。</summary>
        public int ConsecutiveFailures;
        /// <summary>最近一次决策出的控制器状态，仅作展示与排查用。</summary>
        public string ControllerState = "";
        /// <summary>当日已执行过一次「完整关闭静音模式」的日期戳（yyyy-MM-dd）。</summary>
        public string AutoOffDayStamp = "";
        public string LastNightRunUtc = "";
        public string LastDayRunUtc = "";
        public string LastReconcileUtc = "";
        public string LastBootReconcileUtc = "";
        public RecoveryProgress Recovery = new RecoveryProgress();

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("schemaVersion", SchemaVersion);
            o.Set("modeOn", ModeOn);
            o.Set("schedulePaused", SchedulePaused);
            o.Set("autoSilentActive", AutoSilentActive);
            o.Set("autoSilentSuppressed", AutoSilentSuppressed);
            o.Set("lastVerdict", LastVerdict ?? "");
            o.Set("lastErrorCode", LastErrorCode ?? "");
            o.Set("lastReconcileAction", LastReconcileAction ?? "");
            o.Set("consecutiveFailures", ConsecutiveFailures);
            o.Set("controllerState", ControllerState ?? "");
            o.Set("autoOffDayStamp", AutoOffDayStamp ?? "");
            o.Set("lastNightRunUtc", LastNightRunUtc ?? "");
            o.Set("lastDayRunUtc", LastDayRunUtc ?? "");
            o.Set("lastReconcileUtc", LastReconcileUtc ?? "");
            o.Set("lastBootReconcileUtc", LastBootReconcileUtc ?? "");
            o.Set("recovery", Recovery.ToJson());
            return o;
        }

        public static QuietModeState FromJson(JsonObject o)
        {
            QuietModeState s = new QuietModeState();
            if (o == null) return s;
            s.SchemaVersion = o.GetInt("schemaVersion", 1);
            s.ModeOn = o.GetBool("modeOn", false);
            s.SchedulePaused = o.GetBool("schedulePaused", false);
            s.AutoSilentActive = o.GetBool("autoSilentActive", false);
            s.AutoSilentSuppressed = o.GetBool("autoSilentSuppressed", false);
            s.LastVerdict = o.GetString("lastVerdict", "");
            s.LastErrorCode = o.GetString("lastErrorCode", "");
            s.LastReconcileAction = o.GetString("lastReconcileAction", "");
            s.ConsecutiveFailures = o.GetInt("consecutiveFailures", 0);
            s.ControllerState = o.GetString("controllerState", "");
            s.AutoOffDayStamp = o.GetString("autoOffDayStamp", "");
            s.LastNightRunUtc = o.GetString("lastNightRunUtc", "");
            s.LastDayRunUtc = o.GetString("lastDayRunUtc", "");
            s.LastReconcileUtc = o.GetString("lastReconcileUtc", "");
            s.LastBootReconcileUtc = o.GetString("lastBootReconcileUtc", "");
            JsonObject r = o.GetObject("recovery");
            if (r != null) s.Recovery = RecoveryProgress.FromJson(r);
            return s;
        }
    }

    public sealed class RecoveryProgress
    {
        public bool Active;
        public string StartedUtc = "";
        public string Reason = "";
        public int Attempts;
        public List<RecoveryStep> Steps = new List<RecoveryStep>();

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("active", Active);
            o.Set("startedUtc", StartedUtc ?? "");
            o.Set("reason", Reason ?? "");
            o.Set("attempts", Attempts);
            JsonArray arr;
            o.SetArray("steps", out arr);
            foreach (RecoveryStep s in Steps) arr.Add(s.ToJson());
            return o;
        }

        public static RecoveryProgress FromJson(JsonObject o)
        {
            RecoveryProgress p = new RecoveryProgress();
            p.Active = o.GetBool("active", false);
            p.StartedUtc = o.GetString("startedUtc", "");
            p.Reason = o.GetString("reason", "");
            p.Attempts = o.GetInt("attempts", 0);
            JsonArray arr = o.GetArray("steps");
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    JsonObject item = arr[i].TryObject();
                    if (item != null) p.Steps.Add(RecoveryStep.FromJson(item));
                }
            }
            return p;
        }

        public RecoveryStep Step(string name)
        {
            foreach (RecoveryStep s in Steps)
            {
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            }
            RecoveryStep created = new RecoveryStep();
            created.Name = name;
            Steps.Add(created);
            return created;
        }
    }

    public sealed class RecoveryStep
    {
        public string Name = "";
        public string Status = "pending";     // pending | running | ok | failed | skipped
        public string Detail = "";
        public string UpdatedUtc = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("name", Name ?? "");
            o.Set("status", Status ?? "pending");
            o.Set("detail", Detail ?? "");
            o.Set("updatedUtc", UpdatedUtc ?? "");
            return o;
        }

        public static RecoveryStep FromJson(JsonObject o)
        {
            RecoveryStep s = new RecoveryStep();
            s.Name = o.GetString("name", "");
            s.Status = o.GetString("status", "pending");
            s.Detail = o.GetString("detail", "");
            s.UpdatedUtc = o.GetString("updatedUtc", "");
            return s;
        }
    }
}

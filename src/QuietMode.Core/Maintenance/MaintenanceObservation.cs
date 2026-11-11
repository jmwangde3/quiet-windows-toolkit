using System;
using System.Collections.Generic;
using QuietMode.Core.Json;

namespace QuietMode.Core.Maintenance
{
    /// <summary>任务是否存在。Unknown 表示读不到（多半是权限），绝不当成不存在。</summary>
    public enum TaskPresence
    {
        Unknown = 0,
        Present = 1,
        NotPresent = 2
    }

    /// <summary>
    /// 一次系统观测结果。这是「事实」，不是「期望」。
    /// 探测层（QuietMode.Windows）负责填充，planner / verifier 只读它，因此两者都能离线单测。
    /// </summary>
    public sealed class SystemObservation
    {
        public string CapturedAtUtc = "";
        public bool Elevated;
        public List<ObservedTask> Tasks = new List<ObservedTask>();
        public List<ObservedService> Services = new List<ObservedService>();
        public List<ObservedRegistryValue> Registry = new List<ObservedRegistryValue>();
        public List<string> Notes = new List<string>();

        public ObservedTask FindTask(string path, string name)
        {
            foreach (ObservedTask t in Tasks)
            {
                if (string.Equals(Norm(t.Path), Norm(path), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }
            return null;
        }

        public ObservedService FindService(string serviceName)
        {
            foreach (ObservedService s in Services)
            {
                if (string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        public ObservedRegistryValue FindRegistry(string keyPath, string valueName)
        {
            foreach (ObservedRegistryValue r in Registry)
            {
                if (string.Equals(r.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.ValueName, valueName, StringComparison.OrdinalIgnoreCase))
                {
                    return r;
                }
            }
            return null;
        }

        private static string Norm(string path)
        {
            string p = (path ?? "").Trim();
            return p.Length == 0 ? "" : p.TrimEnd('\\');
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("capturedAtUtc", CapturedAtUtc ?? "");
            o.Set("elevated", Elevated);

            JsonArray tasks;
            o.SetArray("tasks", out tasks);
            foreach (ObservedTask t in Tasks) tasks.Add(t.ToJson());

            JsonArray svc;
            o.SetArray("services", out svc);
            foreach (ObservedService s in Services) svc.Add(s.ToJson());

            JsonArray reg;
            o.SetArray("registry", out reg);
            foreach (ObservedRegistryValue r in Registry) reg.Add(r.ToJson());

            JsonArray notes;
            o.SetArray("notes", out notes);
            foreach (string n in Notes) notes.Add(n);
            return o;
        }
    }

    public sealed class ObservedTask
    {
        public string Path = "";
        public string Name = "";
        public TaskPresence Presence = TaskPresence.Unknown;
        /// <summary>null = 读不到 Enabled。</summary>
        public bool? Enabled;
        /// <summary>Readiness 原文：Ready / Running / Disabled / Queued / Unknown ...</summary>
        public string State = "";
        public string LastRunTime = "";
        public string LastResult = "";
        public string Error = "";

        public string FullName { get { return (Path ?? "").TrimEnd('\\') + "\\" + Name; } }

        public bool Running
        {
            get { return string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase); }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("path", Path ?? "");
            o.Set("name", Name ?? "");
            o.Set("presence", Presence.ToString());
            o.Set("enabled", Enabled.HasValue ? (JsonNode)new JsonBool(Enabled.Value) : JsonNull.Instance);
            o.Set("state", State ?? "");
            o.Set("lastRunTime", LastRunTime ?? "");
            o.Set("lastResult", LastResult ?? "");
            o.Set("error", Error ?? "");
            return o;
        }

        public static ObservedTask FromJson(JsonObject o)
        {
            ObservedTask t = new ObservedTask();
            t.Path = o.GetString("path", "");
            t.Name = o.GetString("name", "");
            string p = o.GetString("presence", "Unknown");
            t.Presence = string.Equals(p, "Present", StringComparison.OrdinalIgnoreCase) ? TaskPresence.Present
                       : string.Equals(p, "NotPresent", StringComparison.OrdinalIgnoreCase) ? TaskPresence.NotPresent
                       : TaskPresence.Unknown;
            JsonNode e = o.Get("enabled");
            t.Enabled = e == null || e.IsNull ? (bool?)null : e.AsBool();
            t.State = o.GetString("state", "");
            t.LastRunTime = o.GetString("lastRunTime", "");
            t.LastResult = o.GetString("lastResult", "");
            t.Error = o.GetString("error", "");
            return t;
        }
    }

    public sealed class ObservedService
    {
        public string ServiceName = "";
        public bool Exists;
        /// <summary>HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\Start，-1 = 读不到。</summary>
        public int StartValue = -1;
        public string StartupType = "";
        public string Status = "";
        public string Error = "";

        public bool Running
        {
            get { return string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase); }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("serviceName", ServiceName ?? "");
            o.Set("exists", Exists);
            o.Set("startValue", StartValue);
            o.Set("startupType", StartupType ?? "");
            o.Set("status", Status ?? "");
            o.Set("error", Error ?? "");
            return o;
        }

        public static ObservedService FromJson(JsonObject o)
        {
            ObservedService s = new ObservedService();
            s.ServiceName = o.GetString("serviceName", "");
            s.Exists = o.GetBool("exists", false);
            s.StartValue = o.GetInt("startValue", -1);
            s.StartupType = o.GetString("startupType", "");
            s.Status = o.GetString("status", "");
            s.Error = o.GetString("error", "");
            return s;
        }
    }

    public sealed class ObservedRegistryValue
    {
        public string KeyPath = "";
        public string ValueName = "";
        public bool Exists;
        public string Kind = "";
        public string Value = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("keyPath", KeyPath ?? "");
            o.Set("valueName", ValueName ?? "");
            o.Set("exists", Exists);
            o.Set("kind", Kind ?? "");
            o.Set("value", Value ?? "");
            return o;
        }

        public static ObservedRegistryValue FromJson(JsonObject o)
        {
            ObservedRegistryValue r = new ObservedRegistryValue();
            r.KeyPath = o.GetString("keyPath", "");
            r.ValueName = o.GetString("valueName", "");
            r.Exists = o.GetBool("exists", false);
            r.Kind = o.GetString("kind", "");
            r.Value = o.GetString("value", "");
            return r;
        }
    }
}

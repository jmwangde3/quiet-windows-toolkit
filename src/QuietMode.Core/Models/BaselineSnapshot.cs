using System;
using System.Collections.Generic;
using QuietMode.Core.Json;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// 安装前系统原始状态快照（baseline.json）。日常切换静音绝不覆盖本文件。
    /// </summary>
    public sealed class BaselineSnapshot
    {
        /// <summary>
        /// 3：TaskSnapshot 增加 originalUnknown / originalReason —— 允许 baseline 明确说
        /// 「原始启用状态无法确定」，而不是把夜间被本工具禁用的状态当成原始状态。
        /// </summary>
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion = CurrentSchemaVersion;
        public SnapshotMetadata Metadata = new SnapshotMetadata();
        public List<TaskSnapshot> ScheduledTasks = new List<TaskSnapshot>();
        public List<ServiceSnapshot> Services = new List<ServiceSnapshot>();
        public List<RegistrySnapshot> Registry = new List<RegistrySnapshot>();
        public PowerPlanSnapshot PowerPlan = new PowerPlanSnapshot();
        public List<DiskSnapshot> Disks = new List<DiskSnapshot>();
        public FanSnapshot FanControl = new FanSnapshot();

        /// <summary>采集期间的非致命问题（例如某任务需要提权才能读取 XML）。</summary>
        public List<string> CaptureNotes = new List<string>();

        public JsonObject ToJson()
        {
            JsonObject root = new JsonObject();
            root.Set("schemaVersion", SchemaVersion);

            JsonObject meta = Metadata.ToJson();
            root.Set("metadata", meta);

            JsonArray tasks;
            root.SetArray("scheduledTasks", out tasks);
            foreach (TaskSnapshot t in ScheduledTasks) tasks.Add(t.ToJson());

            JsonArray services;
            root.SetArray("services", out services);
            foreach (ServiceSnapshot s in Services) services.Add(s.ToJson());

            JsonArray reg;
            root.SetArray("registry", out reg);
            foreach (RegistrySnapshot r in Registry) reg.Add(r.ToJson());

            root.Set("powerPlan", PowerPlan.ToJson());

            JsonArray disks;
            root.SetArray("disks", out disks);
            foreach (DiskSnapshot d in Disks) disks.Add(d.ToJson());

            root.Set("fanControl", FanControl.ToJson());

            if (CaptureNotes.Count > 0)
            {
                JsonArray notes;
                root.SetArray("captureNotes", out notes);
                foreach (string n in CaptureNotes) notes.Add(n);
            }
            return root;
        }

        public static BaselineSnapshot FromJson(JsonObject root)
        {
            if (root == null) throw new ArgumentNullException("root");
            BaselineSnapshot b = new BaselineSnapshot();
            b.SchemaVersion = root.GetInt("schemaVersion", 1);
            JsonObject meta = root.GetObject("metadata");
            if (meta != null) b.Metadata = SnapshotMetadata.FromJson(meta);

            JsonArray arr = root.GetArray("scheduledTasks");
            if (arr != null) for (int i = 0; i < arr.Count; i++) { JsonObject o = arr[i].TryObject(); if (o != null) b.ScheduledTasks.Add(TaskSnapshot.FromJson(o)); }
            arr = root.GetArray("services");
            if (arr != null) for (int i = 0; i < arr.Count; i++) { JsonObject o = arr[i].TryObject(); if (o != null) b.Services.Add(ServiceSnapshot.FromJson(o)); }
            arr = root.GetArray("registry");
            if (arr != null) for (int i = 0; i < arr.Count; i++) { JsonObject o = arr[i].TryObject(); if (o != null) b.Registry.Add(RegistrySnapshot.FromJson(o)); }
            JsonObject pp = root.GetObject("powerPlan");
            if (pp != null) b.PowerPlan = PowerPlanSnapshot.FromJson(pp);
            arr = root.GetArray("disks");
            if (arr != null) for (int i = 0; i < arr.Count; i++) { JsonObject o = arr[i].TryObject(); if (o != null) b.Disks.Add(DiskSnapshot.FromJson(o)); }
            JsonObject fan = root.GetObject("fanControl");
            if (fan != null) b.FanControl = FanSnapshot.FromJson(fan);
            arr = root.GetArray("captureNotes");
            if (arr != null) for (int i = 0; i < arr.Count; i++) b.CaptureNotes.Add(arr[i].AsString());

            return b;
        }
    }

    public sealed class SnapshotMetadata
    {
        public int SchemaVersion = BaselineSnapshot.CurrentSchemaVersion;
        public string WindowsBuild = "";
        public string WindowsProductName = "";
        public string WindowsDisplayVersion = "";
        public string CreatedAtUtc = "";
        public string CreatedBy = "";
        public string ToolVersion = "";
        public bool Complete = true;
        public string IntegritySha256 = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("schemaVersion", SchemaVersion);
            o.Set("windowsBuild", WindowsBuild ?? "");
            o.Set("windowsProductName", WindowsProductName ?? "");
            o.Set("windowsDisplayVersion", WindowsDisplayVersion ?? "");
            o.Set("createdAtUtc", CreatedAtUtc ?? "");
            o.Set("createdBy", CreatedBy ?? "");
            o.Set("toolVersion", ToolVersion ?? "");
            o.Set("complete", Complete);
            o.Set("integritySha256", IntegritySha256 ?? "");
            return o;
        }

        public static SnapshotMetadata FromJson(JsonObject o)
        {
            SnapshotMetadata m = new SnapshotMetadata();
            m.SchemaVersion = o.GetInt("schemaVersion", BaselineSnapshot.CurrentSchemaVersion);
            m.WindowsBuild = o.GetString("windowsBuild", "");
            m.WindowsProductName = o.GetString("windowsProductName", "");
            m.WindowsDisplayVersion = o.GetString("windowsDisplayVersion", "");
            m.CreatedAtUtc = o.GetString("createdAtUtc", "");
            m.CreatedBy = o.GetString("createdBy", "");
            m.ToolVersion = o.GetString("toolVersion", "");
            m.Complete = o.GetBool("complete", true);
            m.IntegritySha256 = o.GetString("integritySha256", "");
            return m;
        }
    }

    public sealed class TaskSnapshot
    {
        public string Path = "";
        public string Name = "";
        public string Module = "";
        /// <summary>null = 无法确定（需要提权或查询失败）。</summary>
        public bool? Exists;
        public bool? Enabled;
        public string XmlSha256 = "";
        /// <summary>任务 XML 备份相对 recovery 目录的路径。</summary>
        public string XmlBackup = "";
        public string Error = "";

        /// <summary>
        /// Enabled 是「拍摄那一刻观测到的值」，不一定等于「原始值」。
        /// 拍摄时正处在夜间窗口（或其它可证明的干扰条件下）且任务处于禁用状态时，
        /// 两者无法区分 —— 此时置 true，白天恢复不得据此猜 Enabled。
        /// </summary>
        public bool OriginalUnknown;
        public string OriginalReason = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("path", Path ?? "");
            o.Set("name", Name ?? "");
            o.Set("module", Module ?? "");
            o.Set("exists", Exists.HasValue ? (JsonNode)new JsonBool(Exists.Value) : JsonNull.Instance);
            o.Set("enabled", Enabled.HasValue ? (JsonNode)new JsonBool(Enabled.Value) : JsonNull.Instance);
            o.Set("xmlSha256", XmlSha256 ?? "");
            o.Set("xmlBackup", XmlBackup ?? "");
            o.Set("error", Error ?? "");
            // 只在「确实不知道」时写：默认值不落盘，老 baseline 的规范化文本才不会变，
            // 否则一次字段扩展就会让所有历史基线的完整性校验值全部失配。
            if (OriginalUnknown)
            {
                o.Set("originalUnknown", true);
                o.Set("originalReason", OriginalReason ?? "");
            }
            return o;
        }

        public static TaskSnapshot FromJson(JsonObject o)
        {
            TaskSnapshot t = new TaskSnapshot();
            t.Path = o.GetString("path", "");
            t.Name = o.GetString("name", "");
            t.Module = o.GetString("module", "");
            JsonNode e = o.Get("exists");
            t.Exists = e == null || e.IsNull ? (bool?)null : e.AsBool();
            e = o.Get("enabled");
            t.Enabled = e == null || e.IsNull ? (bool?)null : e.AsBool();
            t.XmlSha256 = o.GetString("xmlSha256", "");
            t.XmlBackup = o.GetString("xmlBackup", "");
            t.Error = o.GetString("error", "");
            t.OriginalUnknown = o.GetBool("originalUnknown", false);
            t.OriginalReason = o.GetString("originalReason", "");
            return t;
        }

        public string FullName { get { return (Path ?? "").TrimEnd('\\') + "\\" + Name; } }
    }

    public sealed class ServiceSnapshot
    {
        public string ServiceName = "";
        public string DisplayName = "";
        public string StartupType = "";     // boot | system | automatic | manual | disabled | unknown
        public int StartValue = -1;         // HKLM\SYSTEM\CurrentControlSet\Services\<name>\Start
        public string Status = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("serviceName", ServiceName ?? "");
            o.Set("displayName", DisplayName ?? "");
            o.Set("startupType", StartupType ?? "");
            o.Set("startValue", StartValue);
            o.Set("status", Status ?? "");
            return o;
        }

        public static ServiceSnapshot FromJson(JsonObject o)
        {
            ServiceSnapshot s = new ServiceSnapshot();
            s.ServiceName = o.GetString("serviceName", "");
            s.DisplayName = o.GetString("displayName", "");
            s.StartupType = o.GetString("startupType", "");
            s.StartValue = o.GetInt("startValue", -1);
            s.Status = o.GetString("status", "");
            return s;
        }
    }

    public sealed class RegistrySnapshot
    {
        public string KeyPath = "";
        public string ValueName = "";
        public bool Exists;
        public string Kind = "";            // String | DWord | QWord | MultiString | Binary | ExpandString | None
        public string Value = "";           // 统一按字符串存，Kind 负责解释
        public string Module = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("keyPath", KeyPath ?? "");
            o.Set("valueName", ValueName ?? "");
            o.Set("exists", Exists);
            o.Set("kind", Kind ?? "");
            o.Set("value", Value ?? "");
            o.Set("module", Module ?? "");
            return o;
        }

        public static RegistrySnapshot FromJson(JsonObject o)
        {
            RegistrySnapshot r = new RegistrySnapshot();
            r.KeyPath = o.GetString("keyPath", "");
            r.ValueName = o.GetString("valueName", "");
            r.Exists = o.GetBool("exists", false);
            r.Kind = o.GetString("kind", "");
            r.Value = o.GetString("value", "");
            r.Module = o.GetString("module", "");
            return r;
        }
    }

    public sealed class PowerPlanSnapshot
    {
        public string ActiveSchemeGuid = "";
        public bool Captured;
        public int DiskIdleAcSeconds = -1;
        public int DiskIdleDcSeconds = -1;
        public string Error = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("activeSchemeGuid", ActiveSchemeGuid ?? "");
            o.Set("captured", Captured);
            o.Set("diskIdleAcSeconds", DiskIdleAcSeconds);
            o.Set("diskIdleDcSeconds", DiskIdleDcSeconds);
            o.Set("error", Error ?? "");
            return o;
        }

        public static PowerPlanSnapshot FromJson(JsonObject o)
        {
            PowerPlanSnapshot p = new PowerPlanSnapshot();
            p.ActiveSchemeGuid = o.GetString("activeSchemeGuid", "");
            p.Captured = o.GetBool("captured", false);
            p.DiskIdleAcSeconds = o.GetInt("diskIdleAcSeconds", -1);
            p.DiskIdleDcSeconds = o.GetInt("diskIdleDcSeconds", -1);
            p.Error = o.GetString("error", "");
            return p;
        }
    }

    public sealed class DiskSnapshot
    {
        public int Number = -1;             // 运行时信息，不作为身份
        public string UniqueId = "";
        public string SerialNumber = "";
        public string FriendlyName = "";
        public long SizeBytes;
        public string BusType = "";
        public bool? IsOffline;
        public bool? IsReadOnly;
        public bool? IsSystem;
        public bool? IsBoot;
        public string HealthStatus = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("number", Number);
            o.Set("uniqueId", UniqueId ?? "");
            o.Set("serialNumber", SerialNumber ?? "");
            o.Set("friendlyName", FriendlyName ?? "");
            o.Set("sizeBytes", SizeBytes);
            o.Set("busType", BusType ?? "");
            o.Set("isOffline", IsOffline.HasValue ? (JsonNode)new JsonBool(IsOffline.Value) : JsonNull.Instance);
            o.Set("isReadOnly", IsReadOnly.HasValue ? (JsonNode)new JsonBool(IsReadOnly.Value) : JsonNull.Instance);
            o.Set("isSystem", IsSystem.HasValue ? (JsonNode)new JsonBool(IsSystem.Value) : JsonNull.Instance);
            o.Set("isBoot", IsBoot.HasValue ? (JsonNode)new JsonBool(IsBoot.Value) : JsonNull.Instance);
            o.Set("healthStatus", HealthStatus ?? "");
            return o;
        }

        public static DiskSnapshot FromJson(JsonObject o)
        {
            DiskSnapshot d = new DiskSnapshot();
            d.Number = o.GetInt("number", -1);
            d.UniqueId = o.GetString("uniqueId", "");
            d.SerialNumber = o.GetString("serialNumber", "");
            d.FriendlyName = o.GetString("friendlyName", "");
            d.SizeBytes = o.GetLong("sizeBytes", 0);
            d.BusType = o.GetString("busType", "");
            JsonNode n = o.Get("isOffline"); d.IsOffline = n == null || n.IsNull ? (bool?)null : n.AsBool();
            n = o.Get("isReadOnly"); d.IsReadOnly = n == null || n.IsNull ? (bool?)null : n.AsBool();
            n = o.Get("isSystem"); d.IsSystem = n == null || n.IsNull ? (bool?)null : n.AsBool();
            n = o.Get("isBoot"); d.IsBoot = n == null || n.IsNull ? (bool?)null : n.AsBool();
            d.HealthStatus = o.GetString("healthStatus", "");
            return d;
        }
    }

    public sealed class FanSnapshot
    {
        public bool ProcessRunning;
        public string ControlExe = "";
        public string ControlExeSha256 = "";
        public string IpcPath = "";
        public string NormalProfile = "";
        public string NormalProfileSha256 = "";
        public string SilentProfile = "";
        public string SilentProfileSha256 = "";
        public string FlagPath = "";
        public bool? FlagExists;
        /// <summary>FanControl 自身记录的当前配置（读不到时留空，不猜）。</summary>
        public string CurrentProfile = "";
        public string Note = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("processRunning", ProcessRunning);
            o.Set("controlExe", ControlExe ?? "");
            o.Set("controlExeSha256", ControlExeSha256 ?? "");
            o.Set("ipcPath", IpcPath ?? "");
            o.Set("normalProfile", NormalProfile ?? "");
            o.Set("normalProfileSha256", NormalProfileSha256 ?? "");
            o.Set("silentProfile", SilentProfile ?? "");
            o.Set("silentProfileSha256", SilentProfileSha256 ?? "");
            o.Set("flagPath", FlagPath ?? "");
            o.Set("flagExists", FlagExists.HasValue ? (JsonNode)new JsonBool(FlagExists.Value) : JsonNull.Instance);
            o.Set("currentProfile", CurrentProfile ?? "");
            o.Set("note", Note ?? "");
            return o;
        }

        public static FanSnapshot FromJson(JsonObject o)
        {
            FanSnapshot f = new FanSnapshot();
            f.ProcessRunning = o.GetBool("processRunning", false);
            f.ControlExe = o.GetString("controlExe", "");
            f.ControlExeSha256 = o.GetString("controlExeSha256", "");
            f.IpcPath = o.GetString("ipcPath", "");
            f.NormalProfile = o.GetString("normalProfile", "");
            f.NormalProfileSha256 = o.GetString("normalProfileSha256", "");
            f.SilentProfile = o.GetString("silentProfile", "");
            f.SilentProfileSha256 = o.GetString("silentProfileSha256", "");
            f.FlagPath = o.GetString("flagPath", "");
            JsonNode n = o.Get("flagExists"); f.FlagExists = n == null || n.IsNull ? (bool?)null : n.AsBool();
            f.CurrentProfile = o.GetString("currentProfile", "");
            f.Note = o.GetString("note", "");
            return f;
        }
    }
}

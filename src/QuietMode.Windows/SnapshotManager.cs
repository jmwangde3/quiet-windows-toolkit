using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuietMode.Core.Configuration;
using QuietMode.Core.Json;
using QuietMode.Core.Models;
using QuietMode.Core.Scheduling;
using QuietMode.Windows.Native;

namespace QuietMode.Windows
{
    public sealed class SnapshotVerifyResult
    {
        public bool Ok = true;
        public bool Complete = true;
        public BaselineState State = BaselineState.Missing;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();

        public void AddErrors(IEnumerable<string> items) { foreach (string s in items) Errors.Add(s); }
        public void AddWarnings(IEnumerable<string> items) { foreach (string s in items) Warnings.Add(s); }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Ok ? "SNAPSHOT OK" : "SNAPSHOT INVALID");
            sb.AppendLine("state=" + State + "  complete=" + Complete);
            foreach (string e in Errors) sb.AppendLine("  ERROR   " + e);
            foreach (string w in Warnings) sb.AppendLine("  WARN    " + w);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 原始状态快照（baseline.json）。只采集，不改系统状态。
    /// 原则（规格 §5.2）：日常切换不覆盖；升级不覆盖；只有用户主动重置才重建。
    /// </summary>
    public sealed class SnapshotManager
    {
        public readonly QuietModePaths Paths;
        private readonly QuietModeConfig _config;

        public SnapshotManager(QuietModePaths paths, QuietModeConfig config)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            Paths = paths;
            _config = config ?? ConfigStore.DefaultPolicy();
        }

        public string BaselinePath { get { return Paths.BaselinePath; } }

        public BaselineSnapshot Capture()
        {
            BaselineSnapshot s = new BaselineSnapshot();
            s.Metadata.WindowsBuild = WindowsVersionInfo.Build();
            s.Metadata.WindowsProductName = WindowsVersionInfo.ProductName();
            s.Metadata.WindowsDisplayVersion = WindowsVersionInfo.DisplayVersion();
            s.Metadata.CreatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            s.Metadata.CreatedBy = WindowsVersionInfo.UserName();
            s.Metadata.ToolVersion = ProductInfo.Version;
            s.Metadata.SchemaVersion = BaselineSnapshot.CurrentSchemaVersion;
            s.Metadata.Complete = true;

            Paths.EnsureLayout();
            Directory.CreateDirectory(Paths.TaskBackupDir);

            // 拍快照的时机本身会影响可信度：夜间窗口内维护任务是被本工具/旧闸门禁用的，
            // 这时观测到的 Enabled=false 不能当成「原始状态」写进 baseline。
            bool nightContext = false;
            string nightReason = "";
            try
            {
                WindowEvaluation w = ScheduleWindow.Evaluate(_config.Schedule, DateTime.Now, null);
                if (w.IsNight)
                {
                    nightContext = true;
                    nightReason = "快照拍摄于夜间窗口内（" + w.ToLine() + "），该任务此刻的禁用状态可能来自夜间闸门";
                }
            }
            catch { }

            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.All)
            {
                TaskSnapshot t = new TaskSnapshot();
                t.Path = def.Path;
                t.Name = def.Name;
                t.Module = def.Module;

                TaskQueryResult q = TaskSchedulerClient.Query(def.Path, def.Name);
                t.Exists = q.Exists;
                t.Enabled = q.Enabled;
                t.Error = q.Error ?? "";

                if (nightContext && q.Enabled == false && def.NightDisableDefault)
                {
                    // 无法区分「本来就没开」与「今晚被关掉了」—— 明确记成未知，白天不据此做决定。
                    t.OriginalUnknown = true;
                    t.OriginalReason = nightReason;
                }

                if (q.Exists == true && !string.IsNullOrEmpty(q.Xml))
                {
                    string fileName = SafeFileName(def.FullName) + ".xml";
                    string full = Path.Combine(Paths.TaskBackupDir, fileName);
                    try
                    {
                        AtomicFile.WriteText(full, q.Xml, null, null);
                        t.XmlBackup = "tasks/" + fileName;
                        t.XmlSha256 = AtomicFile.Sha256OfText(q.Xml);
                    }
                    catch (Exception ex)
                    {
                        t.Error = Append(t.Error, "写入任务 XML 备份失败：" + ex.Message);
                        s.Metadata.Complete = false;
                    }
                }

                if (t.Exists == null)
                {
                    s.Metadata.Complete = false;
                    s.CaptureNotes.Add(def.FullName + "：无法确定是否存在（" + t.Error + "）"
                                       + (t.Error != null && t.Error.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0
                                          ? "；以管理员身份运行可补全" : ""));
                }
                else if (t.Exists == true && string.IsNullOrEmpty(t.XmlBackup))
                {
                    s.Metadata.Complete = false;
                    s.CaptureNotes.Add(def.FullName + "：存在但未取到 XML 备份（" + t.Error + "）");
                }
                else if (t.Exists == true && t.Enabled == false && def.NightDisableDefault)
                {
                    // 拍快照时就是禁用：白天只会「恢复到禁用」，等于永远不开。
                    // 这多半是旧版本禁用后没恢复留下的，必须让人知道而不是悄悄接受。
                    if (t.OriginalUnknown)
                    {
                        s.CaptureNotes.Add(def.FullName + "：原始启用状态未知（" + t.OriginalReason
                                           + "）。白天不会擅自启用或禁用；确认它应当保持启用后，"
                                           + "执行 qmctl maint baseline-enable 再重建快照");
                    }
                    else
                    {
                        s.CaptureNotes.Add(def.FullName + "：快照记录为「禁用」，白天不会恢复启用。"
                                           + "若这不是你的本意，说明快照拍在旧版本已禁用之后；"
                                           + "确认后可用 qmctl maint baseline-enable 修正");
                    }
                }

                s.ScheduledTasks.Add(t);
            }

            // 服务
            ServiceSnapshot sysMain = ServiceInventory.Query(MaintenanceCatalog.SysMainService);
            s.Services.Add(sysMain);
            if (string.Equals(sysMain.StartupType, "missing", StringComparison.OrdinalIgnoreCase))
                s.CaptureNotes.Add("服务 " + MaintenanceCatalog.SysMainService + " 不存在");

            // 注册表：自动维护边界与唤醒
            s.Registry.Add(RegistryReader.Read(MaintenanceCatalog.MaintenanceRegistryKey,
                MaintenanceCatalog.MaintenanceBoundaryValue, "maintenance"));
            s.Registry.Add(RegistryReader.Read(MaintenanceCatalog.MaintenanceRegistryKey,
                MaintenanceCatalog.MaintenanceWakeValue, "maintenance"));

            // 电源
            s.PowerPlan = PowerPlanReader.Capture();
            if (!s.PowerPlan.Captured)
            {
                s.Metadata.Complete = false;
                s.CaptureNotes.Add("DISKIDLE 读取失败：" + s.PowerPlan.Error);
            }

            // 磁盘
            s.Disks = DiskInventory.Query();
            if (s.Disks.Count == 0)
            {
                s.Metadata.Complete = false;
                s.CaptureNotes.Add("磁盘清单为空：WMI 查询失败或无权限");
            }

            // FanControl
            s.FanControl = FanInventory.Capture(_config.Fan);

            // 完整性校验值
            s.Metadata.IntegritySha256 = ComputeIntegrity(s);
            return s;
        }

        public void Save(BaselineSnapshot snapshot, bool overwrite)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (File.Exists(Paths.BaselinePath) && !overwrite)
                throw new InvalidOperationException("baseline.json 已存在；重建基线必须显式 --reset（规格 §5.2）");

            snapshot.Metadata.IntegritySha256 = ComputeIntegrity(snapshot);
            string text = snapshot.ToJson().ToJson(true) + "\n";

            // baseline.previous.json 只在「现有基线可信」时才轮换。
            // 否则一次重建会把唯一一份还能用的旧基线覆盖成坏文件，等于把退路烧掉。
            string previous = Path.Combine(Paths.RecoveryDir, "baseline.previous.json");
            bool rotate = false;
            if (File.Exists(Paths.BaselinePath))
            {
                BaselineHealth health = Inspect();
                rotate = health.Usable;
                if (!rotate)
                    snapshot.CaptureNotes.Add("原 baseline 不可用（" + health.State + "），未覆盖 baseline.previous.json");
            }

            AtomicFile.WriteText(Paths.BaselinePath, text, rotate ? previous : null, delegate(string reread)
            {
                JsonNode node;
                string err;
                if (!JsonNode.TryParse(reread, out node, out err))
                    throw new InvalidDataException("baseline 写入后复读失败：" + err);
            });
        }

        /// <summary>
        /// 读取并体检。任何情况下都不抛异常 —— 调用方要根据状态决定「能不能恢复」，
        /// 而不是被一个异常打断在半路。
        /// </summary>
        public BaselineSnapshot TryLoad(out BaselineHealth health)
        {
            health = new BaselineHealth();
            if (!File.Exists(Paths.BaselinePath))
            {
                health.State = BaselineState.Missing;
                health.Errors.Add("baseline.json 不存在");
                return null;
            }

            BaselineSnapshot s;
            try
            {
                string text = File.ReadAllText(Paths.BaselinePath, Encoding.UTF8);
                JsonNode node;
                string err;
                if (!JsonNode.TryParse(text, out node, out err))
                {
                    health.State = BaselineState.Corrupt;
                    health.Errors.Add("baseline.json 解析失败：" + err);
                    return null;
                }
                JsonObject root = node.TryObject();
                if (root == null)
                {
                    health.State = BaselineState.Corrupt;
                    health.Errors.Add("baseline.json 根节点不是对象");
                    return null;
                }
                s = BaselineSnapshot.FromJson(root);
            }
            catch (Exception ex)
            {
                health.State = BaselineState.Corrupt;
                health.Errors.Add("读取 baseline.json 失败：" + ex.Message);
                return null;
            }

            health = Evaluate(s);
            return s;
        }

        /// <summary>只体检，不返回数据。用于「要不要拿它去恢复」的判断。</summary>
        public BaselineHealth Inspect()
        {
            BaselineHealth h;
            BaselineSnapshot s = TryLoad(out h);
            if (s == null) return h;
            return h;
        }

        private BaselineHealth Evaluate(BaselineSnapshot s)
        {
            return BaselineHealth.Evaluate(
                s,
                delegate(string relative)
                {
                    string full = Path.Combine(Paths.RecoveryDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(full);
                },
                delegate(string relative)
                {
                    string full = Path.Combine(Paths.RecoveryDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    try { return AtomicFile.Sha256OfFile(full); }
                    catch { return ""; }
                });
        }

        public BaselineSnapshot Load()
        {
            if (!File.Exists(Paths.BaselinePath)) return null;
            string text = File.ReadAllText(Paths.BaselinePath, Encoding.UTF8);
            JsonNode node;
            string err;
            if (!JsonNode.TryParse(text, out node, out err))
                throw new InvalidDataException("baseline.json 解析失败：" + err);
            JsonObject root = node.TryObject();
            if (root == null) throw new InvalidDataException("baseline.json 根节点不是对象");
            return BaselineSnapshot.FromJson(root);
        }

        public SnapshotVerifyResult Verify(BaselineSnapshot s)
        {
            SnapshotVerifyResult r = new SnapshotVerifyResult();
            BaselineHealth h = s == null ? BaselineHealth.Evaluate(null, null, null) : Evaluate(s);

            r.State = h.State;
            r.AddErrors(h.Errors);
            r.AddWarnings(h.Warnings);
            r.Ok = h.Usable;
            r.Complete = h.State == BaselineState.Complete;
            if (h.State == BaselineState.Partial)
                r.Warnings.Add("快照不完整；恢复时缺失项会被跳过并记录，不会假装成功");
            return r;
        }

        public static string ComputeIntegrity(BaselineSnapshot s)
        {
            return SnapshotIntegrity.Compute(s);
        }

        private static string SafeFileName(string fullName)
        {
            StringBuilder sb = new StringBuilder(fullName.Length);
            foreach (char c in fullName)
            {
                if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    sb.Append('_');
                else if (char.IsWhiteSpace(c))
                    sb.Append('_');
                else sb.Append(c);
            }
            string name = sb.ToString().Trim('_');
            return name.Length > 150 ? name.Substring(0, 150) : name;
        }

        private static string Append(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? b : a + "；" + b;
        }

        private static string Short(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "<空>";
            return hash.Length <= 12 ? hash : hash.Substring(0, 12) + "…";
        }
    }
}

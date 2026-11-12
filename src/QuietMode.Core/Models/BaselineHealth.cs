using System;
using System.Collections.Generic;
using System.Text;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// 基线的可用性。四种状态必须严格区分，因为它们允许的后续动作完全不同：
    ///   Missing  —— 没有基线：可以采集，但不许拿它恢复。
    ///   Corrupt  —— 解析不了 / schema 不符 / 完整性校验不过：一个字节都不能信。
    ///   Partial  —— 结构完整但采集时有缺口：已记录的项可以恢复，没记录的不许猜。
    ///   Complete —— 完整：可以恢复，也可以作为「已恢复」的判据。
    /// </summary>
    public enum BaselineState
    {
        Missing = 0,
        Corrupt,
        Partial,
        Complete
    }

    public sealed class BaselineHealth
    {
        public BaselineState State = BaselineState.Missing;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public int TaskCount;
        public int XmlBackups;
        public int MissingXmlBackups;

        /// <summary>能不能拿它去恢复已知项（Partial 可以，只是缺的部分不碰）。</summary>
        public bool Usable { get { return State == BaselineState.Complete || State == BaselineState.Partial; } }

        /// <summary>能不能据此宣称「系统已完整恢复」。只有完整基线可以。</summary>
        public bool Authoritative { get { return State == BaselineState.Complete; } }

        public static BaselineHealth Evaluate(BaselineSnapshot s,
                                              Func<string, bool> fileExists,
                                              Func<string, string> fileHash)
        {
            BaselineHealth h = new BaselineHealth();
            if (s == null)
            {
                h.State = BaselineState.Missing;
                h.Errors.Add("baseline.json 不存在");
                return h;
            }

            h.TaskCount = s.ScheduledTasks == null ? 0 : s.ScheduledTasks.Count;

            if (s.SchemaVersion != BaselineSnapshot.CurrentSchemaVersion)
            {
                h.State = BaselineState.Corrupt;
                h.Errors.Add("schemaVersion=" + s.SchemaVersion + "，本版本期望 " + BaselineSnapshot.CurrentSchemaVersion);
                return h;
            }

            string expected = s.Metadata == null ? "" : (s.Metadata.IntegritySha256 ?? "");
            if (expected.Length == 0)
            {
                h.State = BaselineState.Corrupt;
                h.Errors.Add("缺少 integritySha256：无法证明这份基线没有被改过");
                return h;
            }
            string actual = SnapshotIntegrity.Compute(s);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                h.State = BaselineState.Corrupt;
                h.Errors.Add("完整性校验值不匹配（记录 " + Short(expected) + "，实算 " + Short(actual) + "）");
                return h;
            }

            // 结构没问题，开始看内容是否齐全。
            if (s.Metadata == null || !s.Metadata.Complete) h.Warnings.Add("采集时标记为不完整（metadata.complete=false）");
            if (h.TaskCount == 0)
            {
                h.Warnings.Add("基线里没有任何计划任务记录");
                if (s.Metadata != null && !s.Metadata.Complete)
                    h.Warnings.Add("因此无法恢复维护任务：需要重新采集（qmctl snapshot create --reset）");
            }

            foreach (TaskSnapshot t in s.ScheduledTasks)
            {
                if (string.IsNullOrEmpty(t.XmlBackup)) continue;
                h.XmlBackups++;
                string path = t.XmlBackup;
                if (fileExists != null && !fileExists(path))
                {
                    h.MissingXmlBackups++;
                    h.Warnings.Add(t.FullName + "：任务 XML 备份缺失 " + path);
                    continue;
                }
                if (fileHash != null)
                {
                    string hash = fileHash(path);
                    if (!string.Equals(hash, t.XmlSha256, StringComparison.OrdinalIgnoreCase))
                        h.Warnings.Add(t.FullName + "：任务 XML 备份与记录不一致（可能被改动）");
                }
            }

            foreach (RegistrySnapshot r in s.Registry)
            {
                if (string.Equals(r.Kind, "Error", StringComparison.Ordinal))
                    h.Warnings.Add("注册表读取失败：" + r.KeyPath + "\\" + r.ValueName + " — " + r.Value);
            }

            if (s.Disks != null && s.Disks.Count == 0)
                h.Warnings.Add("基线里没有磁盘清单：无法按稳定身份恢复磁盘");

            h.State = h.Warnings.Count == 0 ? BaselineState.Complete : BaselineState.Partial;
            return h;
        }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("baseline: " + State + (Usable ? "（可用于恢复）" : "（不可用于恢复）"));
            sb.AppendLine("任务记录 " + TaskCount + " 项，XML 备份 " + XmlBackups + " 个，缺失 " + MissingXmlBackups + " 个");
            foreach (string e in Errors) sb.AppendLine("  ERROR   " + e);
            foreach (string w in Warnings) sb.AppendLine("  WARN    " + w);
            return sb.ToString().TrimEnd();
        }

        private static string Short(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "<空>";
            return hash.Length <= 12 ? hash : hash.Substring(0, 12) + "…";
        }
    }
}

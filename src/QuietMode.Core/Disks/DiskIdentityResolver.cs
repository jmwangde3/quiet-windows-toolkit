using System;
using System.Collections.Generic;
using System.Text;
using QuietMode.Core.Models;

namespace QuietMode.Core.Disks
{
    public enum DiskMatchStatus
    {
        /// <summary>唯一命中，且校验字段一致。</summary>
        Matched,
        /// <summary>没有任何磁盘与身份匹配。</summary>
        NotFound,
        /// <summary>多个磁盘同时命中，禁止操作。</summary>
        Ambiguous,
        /// <summary>配置里没有可用的稳定身份（既无 UniqueId 也无 SerialNumber）。</summary>
        InsufficientIdentity,
        /// <summary>身份命中了，但辅助字段（序列号 / 容量 / 型号 / 总线）对不上，视为不同磁盘。</summary>
        Conflict
    }

    /// <summary>单个磁盘目标的解析结果。只有 Matched 才允许继续往下走。</summary>
    public sealed class DiskMatch
    {
        public DiskTarget Target;
        public DiskMatchStatus Status = DiskMatchStatus.NotFound;
        public DiskSnapshot Disk;
        public string Reason = "";
        /// <summary>参与比较的候选磁盘编号，便于人眼排查，不用于操作。</summary>
        public List<int> CandidateNumbers = new List<int>();
        public List<string> Notes = new List<string>();

        public bool Unique { get { return Status == DiskMatchStatus.Matched && Disk != null; } }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case DiskMatchStatus.Matched: return "matched";
                    case DiskMatchStatus.NotFound: return "not-found";
                    case DiskMatchStatus.Ambiguous: return "ambiguous";
                    case DiskMatchStatus.InsufficientIdentity: return "no-identity";
                    case DiskMatchStatus.Conflict: return "conflict";
                    default: return "unknown";
                }
            }
        }
    }

    /// <summary>
    /// 稳定身份匹配（规格 §3.1）。规则：
    ///   · UniqueId 优先；UniqueId 命中后再用 SerialNumber / Size / BusType 复核。
    ///   · UniqueId 缺失或未命中时才用 SerialNumber，且必须唯一命中。
    ///   · 磁盘编号（Number / LegacyNumber）永远不参与匹配，只用于日志。
    ///   · 任何「多命中 / 无身份 / 字段冲突」都返回不可操作状态，由调用方拒绝执行，
    ///     绝不退回到「大概就是 0 号盘」。
    /// </summary>
    public static class DiskIdentityResolver
    {
        /// <summary>容量允许的误差（字节）。同一 API 读取应完全相等，留 1 MiB 容忍固件换算差异。</summary>
        public const long SizeTolerance = 1024L * 1024L;

        public static DiskMatch Resolve(DiskTarget target, IList<DiskSnapshot> disks)
        {
            DiskMatch m = new DiskMatch();
            m.Target = target;
            if (target == null)
            {
                m.Status = DiskMatchStatus.InsufficientIdentity;
                m.Reason = "未提供磁盘目标";
                return m;
            }
            if (disks == null || disks.Count == 0)
            {
                m.Status = DiskMatchStatus.NotFound;
                m.Reason = "当前无法枚举磁盘（WMI 查询失败或返回空）";
                return m;
            }

            string wantUnique = NormalizeId(target.UniqueId);
            string wantSerial = NormalizeSerial(target.SerialNumber);
            if (wantUnique.Length == 0 && wantSerial.Length == 0)
            {
                m.Status = DiskMatchStatus.InsufficientIdentity;
                m.Reason = "配置中没有 uniqueId / serialNumber，无法唯一确认身份。请在设置中重新选择磁盘。";
                return m;
            }

            List<DiskSnapshot> hits = new List<DiskSnapshot>();
            bool byUnique = false;

            if (wantUnique.Length > 0)
            {
                foreach (DiskSnapshot d in disks)
                {
                    if (NormalizeId(d.UniqueId) == wantUnique) hits.Add(d);
                }
                byUnique = hits.Count > 0;
            }

            if (hits.Count == 0 && wantSerial.Length > 0)
            {
                foreach (DiskSnapshot d in disks)
                {
                    if (NormalizeSerial(d.SerialNumber) == wantSerial) hits.Add(d);
                }
                if (hits.Count > 0)
                    m.Notes.Add("uniqueId 未命中，改用序列号匹配（较弱身份）");
            }

            if (hits.Count == 0)
            {
                m.Status = DiskMatchStatus.NotFound;
                m.Reason = "未找到匹配磁盘（uniqueId=" + Show(target.UniqueId) + " serial=" + Show(target.SerialNumber) + "）";
                return m;
            }

            foreach (DiskSnapshot d in hits) m.CandidateNumbers.Add(d.Number);

            if (hits.Count > 1)
            {
                m.Status = DiskMatchStatus.Ambiguous;
                m.Reason = "有 " + hits.Count + " 块磁盘同时匹配该身份（编号 " + JoinNumbers(hits) + "），拒绝操作";
                return m;
            }

            DiskSnapshot disk = hits[0];
            m.Disk = disk;

            // ---- 辅助字段复核 ----
            string haveSerial = NormalizeSerial(disk.SerialNumber);
            if (wantSerial.Length > 0 && haveSerial.Length > 0 && haveSerial != wantSerial)
            {
                m.Status = DiskMatchStatus.Conflict;
                m.Reason = "序列号不一致（配置 " + Show(target.SerialNumber) + "，实际 " + Show(disk.SerialNumber) + "），判定为不同磁盘";
                return m;
            }
            if (wantSerial.Length > 0 && haveSerial.Length == 0)
                m.Notes.Add("实际磁盘读不到序列号，无法复核");

            if (target.SizeBytes > 0 && disk.SizeBytes > 0)
            {
                long diff = Math.Abs(target.SizeBytes - disk.SizeBytes);
                if (diff > SizeTolerance)
                {
                    m.Status = DiskMatchStatus.Conflict;
                    m.Reason = "容量不一致（配置 " + Fmt(target.SizeBytes) + "，实际 " + Fmt(disk.SizeBytes) + "），判定为不同磁盘";
                    return m;
                }
            }

            if (!string.IsNullOrWhiteSpace(target.BusType) && !string.IsNullOrWhiteSpace(disk.BusType)
                && !string.Equals(target.BusType.Trim(), disk.BusType.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                m.Status = DiskMatchStatus.Conflict;
                m.Reason = "总线类型不一致（配置 " + target.BusType + "，实际 " + disk.BusType + "），判定为不同磁盘";
                return m;
            }

            if (!string.IsNullOrWhiteSpace(target.FriendlyName) && !string.IsNullOrWhiteSpace(disk.FriendlyName)
                && !string.Equals(NormalizeName(target.FriendlyName), NormalizeName(disk.FriendlyName), StringComparison.OrdinalIgnoreCase))
            {
                // 型号串在不同命名空间里可能被截断或加了后缀，仅告警不阻断。
                m.Notes.Add("型号串不一致（配置 " + target.FriendlyName.Trim() + "，实际 " + disk.FriendlyName.Trim() + "），已按身份字段判定为同一磁盘");
            }

            if (disk.Number < 0)
            {
                m.Status = DiskMatchStatus.Conflict;
                m.Reason = "匹配到的磁盘没有有效编号，无法执行操作";
                return m;
            }

            if (!byUnique) m.Notes.Add("本次匹配基于序列号，建议在设置中补写 uniqueId");

            m.Status = DiskMatchStatus.Matched;
            m.Reason = "唯一命中磁盘 #" + disk.Number + (byUnique ? "（uniqueId）" : "（serialNumber）");
            return m;
        }

        public static List<DiskMatch> ResolveAll(DiskSettings settings, IList<DiskSnapshot> disks)
        {
            List<DiskMatch> list = new List<DiskMatch>();
            if (settings == null) return list;
            foreach (DiskTarget t in settings.Targets)
            {
                if (!t.Enabled) continue;
                list.Add(Resolve(t, disks));
            }
            return list;
        }

        public static string NormalizeId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Trim().ToUpperInvariant();
        }

        /// <summary>序列号：去空白、去尾部 NUL、统一大写。WMI 常带填充空格。</summary>
        public static string NormalizeSerial(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || c == '\0') continue;
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        public static string NormalizeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string s = value.Replace('\0', ' ').Trim();
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            return s;
        }

        private static string JoinNumbers(List<DiskSnapshot> list)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DiskSnapshot d in list)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(d.Number);
            }
            return sb.ToString();
        }

        private static string Show(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "(空)" : s.Trim();
        }

        public static string Fmt(long bytes)
        {
            if (bytes <= 0) return "0";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[i];
        }
    }
}

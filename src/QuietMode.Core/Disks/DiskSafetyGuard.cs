using System;
using System.Collections.Generic;
using System.Text;
using QuietMode.Core.Json;
using QuietMode.Core.Models;

namespace QuietMode.Core.Disks
{
    /// <summary>磁盘上的一个卷（分区）。用于安全检查与占用诊断。</summary>
    public sealed class DiskVolumeInfo
    {
        public string DriveLetter = "";       // "D:" 或空
        public string Label = "";
        public string FileSystem = "";
        public string UniqueId = "";          // 卷 GUID 路径
        public long SizeBytes;
        public long FreeBytes;
        public bool IsSystem;
        public bool IsBoot;
        public string PageFilePath = "";      // 该卷上的页面文件（若有）
        public string DumpFilePath = "";       // 该卷上的转储文件（若有）

        public string Display
        {
            get
            {
                string name = string.IsNullOrEmpty(DriveLetter) ? "(无盘符)" : DriveLetter;
                if (!string.IsNullOrEmpty(Label)) name += " " + Label;
                if (!string.IsNullOrEmpty(FileSystem)) name += " [" + FileSystem + "]";
                return name;
            }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("driveLetter", DriveLetter ?? "");
            o.Set("label", Label ?? "");
            o.Set("fileSystem", FileSystem ?? "");
            o.Set("uniqueId", UniqueId ?? "");
            o.Set("sizeBytes", SizeBytes);
            o.Set("freeBytes", FreeBytes);
            o.Set("isSystem", IsSystem);
            o.Set("isBoot", IsBoot);
            if (!string.IsNullOrEmpty(PageFilePath)) o.Set("pageFile", PageFilePath);
            if (!string.IsNullOrEmpty(DumpFilePath)) o.Set("dumpFile", DumpFilePath);
            return o;
        }
    }

    /// <summary>安全判定所需的全部输入。全部由 Windows 层只读探测填充。</summary>
    public sealed class DiskSafetyContext
    {
        public DiskSnapshot Disk;
        public List<DiskVolumeInfo> Volumes = new List<DiskVolumeInfo>();
        public bool? AutomaticManagedPagefile;
        public List<string> PageFiles = new List<string>();
        public string DumpFile = "";
        public bool CrashDumpEnabled = true;
        public bool InStoragePool;
        public string StoragePoolName = "";
        /// <summary>存储池探测的告警（例如探测失败、无法排除成员身份），只告警不阻断。</summary>
        public string StoragePoolNote = "";
        /// <summary>可选：占用诊断结论（不影响系统盘保护判定）。</summary>
        public DiskOccupancyReport Occupancy;
    }

    public sealed class SafetyVerdict
    {
        /// <summary>true 表示允许执行脱机。</summary>
        public bool Allowed;
        public List<string> Blocks = new List<string>();
        public List<string> Warnings = new List<string>();

        public string ToText()
        {
            if (Blocks.Count == 0 && Warnings.Count == 0) return "允许脱机（无告警）";
            StringBuilder sb = new StringBuilder();
            sb.Append(Allowed ? "允许脱机" : "禁止脱机");
            foreach (string b in Blocks) sb.Append("\n  阻止：").Append(b);
            foreach (string w in Warnings) sb.Append("\n  告警：").Append(w);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 脱机安全闸门（规格 §3.2）。这里是硬约束：
    ///   · 系统盘 / 启动盘 / 页面文件所在盘 / 存储池成员 / 身份或状态不确定 → 一律拒绝；
    ///   · Force 只能越过「普通占用」类告警，永远越不过上面任何一条；
    ///   · 拒绝时给出具体原因，绝不「换个盘号再试」。
    /// </summary>
    public static class DiskSafetyGuard
    {
        public static SafetyVerdict Evaluate(DiskSafetyContext ctx, bool forceBusy)
        {
            SafetyVerdict v = new SafetyVerdict();
            if (ctx == null || ctx.Disk == null)
            {
                v.Allowed = false;
                v.Blocks.Add("没有可判定的磁盘对象");
                return v;
            }

            DiskSnapshot d = ctx.Disk;

            if (d.Number < 0)
                v.Blocks.Add("磁盘编号无效");

            // 关键标志必须为「明确 false」才放行，null（读不到）同样按危险处理。
            if (d.IsBoot == null) v.Blocks.Add("无法确定是否为启动盘（IsBoot 读取失败）");
            else if (d.IsBoot.Value) v.Blocks.Add("这是启动盘（IsBoot=True）");

            if (d.IsSystem == null) v.Blocks.Add("无法确定是否为系统盘（IsSystem 读取失败）");
            else if (d.IsSystem.Value) v.Blocks.Add("这是系统盘（IsSystem=True）");

            if (ctx.InStoragePool)
                v.Blocks.Add("磁盘属于存储池" + (string.IsNullOrEmpty(ctx.StoragePoolName) ? "" : "「" + ctx.StoragePoolName + "」") + "，不支持脱机");
            if (!string.IsNullOrWhiteSpace(ctx.StoragePoolNote))
                v.Warnings.Add(ctx.StoragePoolNote);

            // 存储空间 / 虚拟盘 / 不支持脱机的总线类型
            if (!string.IsNullOrEmpty(d.BusType))
            {
                if (string.Equals(d.BusType, "Spaces", StringComparison.OrdinalIgnoreCase))
                    v.Blocks.Add("磁盘类型为存储空间（Spaces），不支持脱机");
                if (string.Equals(d.BusType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase))
                    v.Blocks.Add("磁盘为文件支持的虚拟盘，不支持脱机");
            }

            foreach (DiskVolumeInfo vol in ctx.Volumes)
            {
                if (vol.IsSystem) v.Blocks.Add("磁盘上的卷 " + vol.Display + " 是系统卷");
                if (vol.IsBoot) v.Blocks.Add("磁盘上的卷 " + vol.Display + " 是启动卷");
                if (!string.IsNullOrEmpty(vol.PageFilePath))
                    v.Blocks.Add("磁盘上的卷 " + vol.Display + " 承载页面文件 " + vol.PageFilePath);
            }

            if (ctx.PageFiles.Count > 0)
            {
                foreach (string pf in ctx.PageFiles)
                {
                    if (PathOnThisDisk(pf, ctx.Volumes))
                        v.Blocks.Add("页面文件位于该磁盘：" + pf);
                }
            }
            if (ctx.AutomaticManagedPagefile == true)
                v.Warnings.Add("系统自动管理页面文件，无法静态排除该盘被用作页面文件；请先确认或关闭自动管理");

            if (!string.IsNullOrEmpty(ctx.DumpFile) && PathOnThisDisk(ctx.DumpFile, ctx.Volumes))
            {
                if (ctx.CrashDumpEnabled)
                    v.Warnings.Add("崩溃转储文件位于该磁盘（" + ctx.DumpFile + "），脱机期间蓝屏将无法写入转储");
            }

            if (ctx.Occupancy != null && ctx.Occupancy.Busy)
            {
                string detail = ctx.Occupancy.BusySummary;
                if (forceBusy) v.Warnings.Add("检测到占用（" + detail + "），已按 forceBusy 忽略");
                else v.Blocks.Add("检测到占用（" + detail + "）；确认可中断后可用 --force 越过此项");
            }
            else if (ctx.Occupancy != null && ctx.Occupancy.Warnings.Count > 0)
            {
                foreach (string w in ctx.Occupancy.Warnings) v.Warnings.Add(w);
            }

            if (!string.IsNullOrEmpty(d.HealthStatus)
                && !string.Equals(d.HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase))
                v.Warnings.Add("磁盘健康状态为 " + d.HealthStatus);

            if (d.IsReadOnly == true)
                v.Warnings.Add("磁盘当前为只读");

            if (!string.IsNullOrEmpty(d.BusType) && string.Equals(d.BusType, "USB", StringComparison.OrdinalIgnoreCase))
                v.Warnings.Add("磁盘通过 USB 连接，脱机后可能被重新枚举");

            if (ctx.Volumes.Count == 0)
                v.Warnings.Add("该磁盘上没有可识别的卷（可能是裸盘或分区表异常）");

            v.Allowed = v.Blocks.Count == 0;
            return v;
        }

        /// <summary>判断路径是否落在给定卷集合上（按盘符前缀，保守判断）。</summary>
        public static bool PathOnThisDisk(string path, List<DiskVolumeInfo> volumes)
        {
            if (string.IsNullOrWhiteSpace(path) || volumes == null) return false;
            string p = path.Trim();
            if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p.Substring(4);
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
            if (p.Length < 2 || p[1] != ':') return false;
            string letter = char.ToUpperInvariant(p[0]) + ":";
            foreach (DiskVolumeInfo v in volumes)
            {
                if (!string.IsNullOrEmpty(v.DriveLetter)
                    && string.Equals(v.DriveLetter.Trim().ToUpperInvariant(), letter, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}

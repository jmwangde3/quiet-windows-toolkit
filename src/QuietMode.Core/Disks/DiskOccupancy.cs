using System;
using System.Collections.Generic;
using System.Text;
using QuietMode.Core.Json;

namespace QuietMode.Core.Disks
{
    /// <summary>一个疑似占用磁盘的进程。仅作诊断，绝不据此静默强杀。</summary>
    public sealed class DiskOccupant
    {
        public int Pid;
        public string Name = "";
        public string ExecutablePath = "";
        public string CommandLine = "";
        public string Hit = "";            // 命中原因：路径 / 命令行 / 工作目录

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("pid", Pid);
            o.Set("name", Name ?? "");
            o.Set("executablePath", ExecutablePath ?? "");
            o.Set("commandLine", CommandLine ?? "");
            o.Set("hit", Hit ?? "");
            return o;
        }

        public string ToLine()
        {
            return "PID " + Pid + "  " + Name + "  （" + Hit + "）"
                   + (string.IsNullOrEmpty(ExecutablePath) ? "" : "\n      " + ExecutablePath);
        }
    }

    /// <summary>
    /// 磁盘占用诊断报告（规格 §3.3）。真实信号以计数器为主，进程路径只作辅助线索：
    ///   · 两次采样的物理磁盘 I/O 计数（字节/秒、磁盘时间占比、队列长度）；
    ///   · 卷级可用空间变化；
    ///   · 进程可执行路径 / 命令行命中盘符；
    ///   · 可选 handle.exe（存在才用，不作为硬依赖）。
    /// </summary>
    public sealed class DiskOccupancyReport
    {
        public int DiskNumber = -1;
        public double SampleSeconds;
        public double BytesPerSecond = -1;
        public double PercentDiskTime = -1;
        public double QueueLength = -1;
        public bool CountersAvailable;
        public List<DiskOccupant> Occupants = new List<DiskOccupant>();
        public List<string> VolumesActive = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Notes = new List<string>();
        /// <summary>是否判定为「正在被使用」。force 只能覆盖这一项。</summary>
        public bool Busy;

        public string BusySummary
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                if (BytesPerSecond >= 0)
                    sb.Append(FormatRate(BytesPerSecond)).Append(" 读写");
                if (PercentDiskTime >= 0)
                    sb.Append(sb.Length > 0 ? "，" : "").Append(PercentDiskTime.ToString("0.#")).Append("% 磁盘时间");
                if (Occupants.Count > 0)
                {
                    string shown = Occupants.Count > 10 ? "10+" : Occupants.Count.ToString();
                    sb.Append(sb.Length > 0 ? "，" : "").Append(shown).Append(" 个进程可能占用");
                }
                if (sb.Length == 0) return CountersAvailable ? "有活动" : "无法判定（性能计数器不可用）";
                return sb.ToString();
            }
        }

        public static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) return "未知";
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            double v = bytesPerSecond;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[i];
        }

        private static JsonNumber Num(double value)
        {
            return new JsonNumber(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("diskNumber", DiskNumber);
            o.Set("busy", Busy);
            o.Set("sampleSeconds", Num(Math.Round(SampleSeconds, 3)));
            o.Set("countersAvailable", CountersAvailable);
            if (BytesPerSecond >= 0) o.Set("bytesPerSecond", Num(Math.Round(BytesPerSecond, 0)));
            if (PercentDiskTime >= 0) o.Set("percentDiskTime", Num(Math.Round(PercentDiskTime, 1)));
            if (QueueLength >= 0) o.Set("queueLength", Num(Math.Round(QueueLength, 2)));

            JsonArray occ;
            o.SetArray("occupants", out occ);
            foreach (DiskOccupant x in Occupants) occ.Add(x.ToJson());

            JsonArray vols;
            o.SetArray("volumesActive", out vols);
            foreach (string s in VolumesActive) vols.Add(s);

            JsonArray warns;
            o.SetArray("warnings", out warns);
            foreach (string s in Warnings) warns.Add(s);

            JsonArray notes;
            o.SetArray("notes", out notes);
            foreach (string s in Notes) notes.Add(s);
            return o;
        }
    }
}

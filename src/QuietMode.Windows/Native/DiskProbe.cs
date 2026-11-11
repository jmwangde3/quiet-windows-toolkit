using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using QuietMode.Core.Disks;
using QuietMode.Core.Models;

namespace QuietMode.Windows.Native
{
    /// <summary>
    /// 磁盘相关只读探测：卷、页面文件、转储文件、存储池、I/O 计数、疑似占用进程。
    /// 这里只做「展示 + 预检」；真正动手前的权威复检在 Invoke-DiskOperation.ps1 里再跑一遍（防 TOCTOU）。
    /// </summary>
    public static class DiskProbe
    {
        private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

        /// <summary>在 Storage 命名空间建立查询。ManagementScope 不可 Dispose，这里只负责返回 searcher。</summary>
        private static ManagementObjectSearcher NewSearcher(string query)
        {
            ManagementScope scope = new ManagementScope(StorageScope);
            scope.Connect();
            return new ManagementObjectSearcher(scope, new ObjectQuery(query));
        }

        // ---------------- 卷 ----------------

        public static List<DiskVolumeInfo> QueryVolumes(int diskNumber)
        {
            List<DiskVolumeInfo> list = new List<DiskVolumeInfo>();
            try
            {
                using (ManagementObjectSearcher searcher = NewSearcher("SELECT DiskNumber, DriveLetter, Size, IsBoot, IsSystem FROM MSFT_Partition"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            int dn = DiskInventory.ToInt(item["DiskNumber"], -1);
                            if (dn != diskNumber) continue;
                            DiskVolumeInfo v = new DiskVolumeInfo();
                            v.DriveLetter = LetterOf(item["DriveLetter"]);
                            v.SizeBytes = DiskInventory.ToLong(item["Size"]);
                            v.IsBoot = DiskInventory.ToBool(item["IsBoot"]) == true;
                            v.IsSystem = DiskInventory.ToBool(item["IsSystem"]) == true;
                            list.Add(v);
                        }
                    }
                }
            }
            catch
            {
                return list;
            }

            Dictionary<string, string[]> meta = QueryVolumeMeta();
            foreach (DiskVolumeInfo v in list)
            {
                string[] m;
                if (!string.IsNullOrEmpty(v.DriveLetter) && meta.TryGetValue(v.DriveLetter, out m))
                {
                    v.Label = m[0];
                    v.FileSystem = m[1];
                    long free;
                    if (long.TryParse(m[2], out free)) v.FreeBytes = free;
                    v.UniqueId = m[3];
                }
                if ((v.IsBoot || v.IsSystem) && string.IsNullOrEmpty(v.DriveLetter))
                {
                    // 系统/启动分区可能没有盘符（ESP、恢复分区），保留空盘符，用标记体现，
                    // 不要伪造盘符，否则路径归属判断会错。
                    v.Label = v.IsSystem && v.IsBoot ? "系统/启动分区" : (v.IsSystem ? "系统分区" : "启动分区");
                }
            }
            return list;
        }

        private static Dictionary<string, string[]> QueryVolumeMeta()
        {
            Dictionary<string, string[]> map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = NewSearcher("SELECT DriveLetter, FileSystemLabel, FileSystem, SizeRemaining, Path FROM MSFT_Volume"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            string letter = LetterOf(item["DriveLetter"]);
                            if (string.IsNullOrEmpty(letter)) continue;
                            map[letter] = new string[]
                            {
                                DiskInventory.ToStr(item["FileSystemLabel"]).Trim(),
                                DiskInventory.ToStr(item["FileSystem"]).Trim(),
                                DiskInventory.ToLong(item["SizeRemaining"]).ToString(),
                                DiskInventory.ToStr(item["Path"])
                            };
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        public static string SystemDrive()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windir) && windir.Length >= 2 && windir[1] == ':')
                return char.ToUpperInvariant(windir[0]) + ":";
            return "C:";
        }

        private static string LetterOf(object value)
        {
            if (value == null) return "";
            try
            {
                if (value is char)
                {
                    char cc = char.ToUpperInvariant((char)value);
                    return (cc >= 'A' && cc <= 'Z') ? cc + ":" : "";
                }
                if (value is ushort) return char.ToUpperInvariant((char)(ushort)value) + ":";
                if (value is uint) return char.ToUpperInvariant((char)(uint)value) + ":";
                string s = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture).Trim();
                if (s.Length == 0) return "";
                char c = s[0];
                if (c >= 'a' && c <= 'z') c = char.ToUpperInvariant(c);
                if (c < 'A' || c > 'Z') return "";
                return c + ":";
            }
            catch { return ""; }
        }

        // ---------------- 页面文件 / 转储 ----------------

        public sealed class PageFileInfo
        {
            public bool? AutomaticManaged;
            public List<string> Paths = new List<string>();
        }

        public static PageFileInfo QueryPageFiles()
        {
            PageFileInfo info = new PageFileInfo();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item) { info.AutomaticManaged = DiskInventory.ToBool(item["AutomaticManagedPagefile"]); }
                        break;
                    }
                }
            }
            catch { }

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PageFileSetting"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            string name = DiskInventory.ToStr(item["Name"]).Trim();
                            if (!string.IsNullOrEmpty(name)) info.Paths.Add(name);
                        }
                    }
                }
            }
            catch { }
            return info;
        }

        public sealed class CrashDumpInfo
        {
            public string DumpFile = "";
            public bool Enabled;
            public int CrashDumpEnabled = -1;
        }

        public static CrashDumpInfo QueryCrashDump()
        {
            CrashDumpInfo info = new CrashDumpInfo();
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl"))
                {
                    if (key == null) return info;
                    object dump = key.GetValue("DumpFile");
                    object enabled = key.GetValue("CrashDumpEnabled");
                    info.DumpFile = DumpExpand(dump == null ? "" : Convert.ToString(dump));
                    if (enabled != null)
                    {
                        int v = Convert.ToInt32(enabled);
                        info.CrashDumpEnabled = v;
                        info.Enabled = v != 0;
                    }
                }
            }
            catch { }
            return info;
        }

        private static string DumpExpand(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            s = s.Replace("%SystemRoot%", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            s = s.Replace("%SystemDrive%", SystemDrive());
            return s;
        }

        // ---------------- 存储池 ----------------

        public sealed class PoolInfo
        {
            /// <summary>true 表示确认是存储池成员。</summary>
            public bool IsMember;
            public string PoolName = "";
            /// <summary>非空表示判定为成员的原因，或探测失败的原因。</summary>
            public string Reason = "";
            /// <summary>探测是否成功执行。false 表示「不知道」，不能当成「不是成员」。</summary>
            public bool Detected;
        }

        /// <summary>
        /// 判定磁盘是否为存储池成员：枚举非原始存储池 → 取成员物理盘 → 按序列号比对。
        /// 探测失败时 Detected=false，调用方应把它当成不确定而不是安全。
        /// </summary>
        public static PoolInfo QueryStoragePool(DiskSnapshot disk)
        {
            PoolInfo info = new PoolInfo();
            if (disk == null) return info;

            List<string> memberSerials = new List<string>();
            string matchedPool = "";
            try
            {
                using (ManagementObjectSearcher searcher = NewSearcher("SELECT FriendlyName FROM MSFT_StoragePool WHERE IsPrimordial = FALSE"))
                using (ManagementObjectCollection pools = searcher.Get())
                {
                    foreach (ManagementBaseObject pool in pools)
                    {
                        using (pool)
                        {
                            string name = DiskInventory.ToStr(pool["FriendlyName"]);
                            ManagementBaseObject outParams = null;
                            ManagementObject mo = pool as ManagementObject;
                            if (mo != null)
                            {
                                try
                                {
                                    ManagementBaseObject inParams = mo.GetMethodParameters("GetPhysicalDisks");
                                    outParams = mo.InvokeMethod("GetPhysicalDisks", inParams, null);
                                }
                                catch { outParams = null; }
                            }
                            if (outParams != null)
                            {
                                using (outParams)
                                {
                                    object[] members = outParams["PhysicalDisks"] as object[];
                                    if (members != null)
                                    {
                                        foreach (object m in members)
                                        {
                                            ManagementBaseObject member = m as ManagementBaseObject;
                                            if (member == null) continue;
                                            using (member)
                                            {
                                                string sn = DiskInventory.ToStr(member["SerialNumber"]);
                                                if (string.IsNullOrWhiteSpace(sn)) continue;
                                                memberSerials.Add(sn);
                                                if (SameSerial(sn, disk.SerialNumber) && matchedPool.Length == 0)
                                                    matchedPool = name;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                info.Detected = true;
            }
            catch (Exception ex)
            {
                info.Detected = false;
                info.Reason = "存储池信息读取失败：" + ProcessRunnerFlatten(ex.Message);
                return info;
            }

            if (matchedPool.Length > 0)
            {
                info.IsMember = true;
                info.PoolName = matchedPool;
                info.Reason = "磁盘属于存储池「" + matchedPool + "」";
                return info;
            }
            if (memberSerials.Count > 0 && string.IsNullOrWhiteSpace(disk.SerialNumber))
                info.Reason = "本机存在存储池，但该磁盘读不到序列号，无法排除池成员身份";
            return info;
        }

        private static bool SameSerial(string a, string b)
        {
            string x = DiskIdentityResolver.NormalizeSerial(a);
            string y = DiskIdentityResolver.NormalizeSerial(b);
            return x.Length > 0 && x == y;
        }

        private static string ProcessRunnerFlatten(string s)
        {
            return QuietMode.Core.Processes.ProcessRunner.Flatten(s);
        }

        // ---------------- I/O 计数 ----------------

        private sealed class DiskCounters
        {
            public double BytesPerSec = -1;
            public double PercentTime = -1;
            public double Queue = -1;
            public bool Available;
        }

        private static DiskCounters SampleCounters(int diskNumber)
        {
            DiskCounters c = new DiskCounters();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, DiskBytesPersec, PercentDiskTime, CurrentDiskQueueLength FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    string num = diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            // 实例名形如 "0"、"1 D:"、"2 C: G:"；没有挂载卷的磁盘只有编号。
                            string name = DiskInventory.ToStr(item["Name"]).Trim();
                            if (!string.Equals(name, num, StringComparison.Ordinal)
                                && !name.StartsWith(num + " ", StringComparison.Ordinal)) continue;
                            c.BytesPerSec = ToDouble(item["DiskBytesPersec"]);
                            c.PercentTime = ToDouble(item["PercentDiskTime"]);
                            c.Queue = ToDouble(item["CurrentDiskQueueLength"]);
                            c.Available = true;
                            break;
                        }
                    }
                }
            }
            catch { }
            return c;
        }

        private static double ToDouble(object v)
        {
            if (v == null) return -1;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return -1; }
        }

        // ---------------- 进程（辅助线索） ----------------

        private static readonly Regex PathToken = new Regex(@"(?:^|[^A-Za-z0-9])([A-Za-z]):\\", RegexOptions.Compiled);

        public static List<DiskOccupant> QueryOccupants(List<DiskVolumeInfo> volumes, int diskNumber)
        {
            List<DiskOccupant> list = new List<DiskOccupant>();
            if (volumes == null || volumes.Count == 0) return list;
            HashSet<string> letters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DiskVolumeInfo v in volumes)
                if (!string.IsNullOrEmpty(v.DriveLetter)) letters.Add(v.DriveLetter.Trim().ToUpperInvariant());
            if (letters.Count == 0) return list;

            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            int pid = DiskInventory.ToInt(item["ProcessId"], 0);
                            if (pid <= 4 || pid == self) continue;
                            string exe = DiskInventory.ToStr(item["ExecutablePath"]);
                            string cmd = DiskInventory.ToStr(item["CommandLine"]);
                            string hit = "";
                            if (ReferencesDisk(exe, letters)) hit = "可执行文件在该盘";
                            else if (ReferencesDisk(cmd, letters)) hit = "命令行参数指向该盘";
                            if (hit.Length == 0) continue;

                            DiskOccupant o = new DiskOccupant();
                            o.Pid = pid;
                            o.Name = DiskInventory.ToStr(item["Name"]);
                            o.ExecutablePath = exe;
                            o.CommandLine = Truncate(cmd, 320);
                            o.Hit = hit;
                            list.Add(o);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static bool ReferencesDisk(string text, HashSet<string> letters)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (Match m in PathToken.Matches(text))
            {
                if (letters.Contains(m.Groups[1].Value.ToUpperInvariant() + ":")) return true;
            }
            return false;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ---------------- 汇总 ----------------

        public static DiskOccupancyReport Diagnose(DiskSnapshot disk, List<DiskVolumeInfo> volumes, bool withProcesses)
        {
            DiskOccupancyReport r = new DiskOccupancyReport();
            if (disk == null) return r;
            r.DiskNumber = disk.Number;

            DiskCounters first = SampleCounters(disk.Number);
            DateTime t0 = DateTime.UtcNow;
            System.Threading.Thread.Sleep(700);
            DiskCounters second = SampleCounters(disk.Number);
            double seconds = (DateTime.UtcNow - t0).TotalSeconds;
            if (seconds <= 0.05) seconds = 0.7;

            r.SampleSeconds = seconds;
            r.CountersAvailable = first.Available && second.Available;
            if (r.CountersAvailable)
            {
                r.BytesPerSecond = (first.BytesPerSec + second.BytesPerSec) / 2.0;
                r.PercentDiskTime = (first.PercentTime + second.PercentTime) / 2.0;
                r.QueueLength = Math.Max(first.Queue, second.Queue);
            }
            else
            {
                r.Warnings.Add("性能计数器不可用，占用判定只能依赖进程线索");
            }

            if (withProcesses)
            {
                r.Occupants = QueryOccupants(volumes, disk.Number);
                if (r.Occupants.Count > 0)
                    r.Notes.Add("进程线索只是辅助判断，不代表真的持有句柄");
            }

            bool ioBusy = r.CountersAvailable && (r.BytesPerSecond > 262144 || r.PercentDiskTime > 15);
            bool procBusy = r.Occupants.Count > 0;
            r.Busy = ioBusy || procBusy;
            if (ioBusy && procBusy) r.Notes.Add("I/O 与进程线索同时命中");
            return r;
        }
    }
}

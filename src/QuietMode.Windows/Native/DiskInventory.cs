using System;
using System.Collections.Generic;
using System.Management;
using QuietMode.Core.Configuration;
using QuietMode.Core.Models;

namespace QuietMode.Windows.Native
{
    /// <summary>
    /// 磁盘清单。只读；身份以 UniqueId / SerialNumber 为准，Number 仅作运行时线索。
    /// </summary>
    public static class DiskInventory
    {
        private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

        public static List<DiskSnapshot> Query()
        {
            List<DiskSnapshot> list = QueryMsftDisk();
            if (list.Count == 0) list = QueryWin32DiskDrive();
            return list;
        }

        public static DiskHint ResolveNumber(int number)
        {
            foreach (DiskSnapshot d in Query())
            {
                if (d.Number == number)
                {
                    DiskHint h = new DiskHint();
                    h.Number = d.Number;
                    h.UniqueId = d.UniqueId;
                    h.SerialNumber = d.SerialNumber;
                    h.FriendlyName = d.FriendlyName;
                    h.SizeBytes = d.SizeBytes;
                    h.BusType = d.BusType;
                    h.Found = true;
                    return h;
                }
            }
            return new DiskHint { Number = number, Found = false };
        }

        private static List<DiskSnapshot> QueryMsftDisk()
        {
            List<DiskSnapshot> list = new List<DiskSnapshot>();
            try
            {
                ManagementScope scope = new ManagementScope(StorageScope);
                scope.Connect();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Number, UniqueId, SerialNumber, FriendlyName, Size, BusType, IsOffline, IsReadOnly, IsSystem, IsBoot, HealthStatus FROM MSFT_Disk")))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            DiskSnapshot d = new DiskSnapshot();
                            d.Number = ToInt(item["Number"], -1);
                            d.UniqueId = ToStr(item["UniqueId"]);
                            d.SerialNumber = ToStr(item["SerialNumber"]).Trim();
                            d.FriendlyName = ToStr(item["FriendlyName"]);
                            d.SizeBytes = ToLong(item["Size"]);
                            d.BusType = BusTypeName(ToInt(item["BusType"], -1));
                            d.IsOffline = ToBool(item["IsOffline"]);
                            d.IsReadOnly = ToBool(item["IsReadOnly"]);
                            d.IsSystem = ToBool(item["IsSystem"]);
                            d.IsBoot = ToBool(item["IsBoot"]);
                            d.HealthStatus = ToStr(item["HealthStatus"]);
                            list.Add(d);
                        }
                    }
                }
            }
            catch
            {
                list.Clear();
            }
            return list;
        }

        private static List<DiskSnapshot> QueryWin32DiskDrive()
        {
            List<DiskSnapshot> list = new List<DiskSnapshot>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Index, SerialNumber, Model, Size, InterfaceType FROM Win32_DiskDrive"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            DiskSnapshot d = new DiskSnapshot();
                            d.Number = ToInt(item["Index"], -1);
                            d.SerialNumber = ToStr(item["SerialNumber"]).Trim();
                            d.FriendlyName = ToStr(item["Model"]).Trim();
                            d.SizeBytes = ToLong(item["Size"]);
                            d.BusType = ToStr(item["InterfaceType"]);
                            list.Add(d);
                        }
                    }
                }
            }
            catch
            {
                list.Clear();
            }
            return list;
        }

        public static string BusTypeName(int code)
        {
            switch (code)
            {
                case 0: return "Unknown";
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 4: return "1394";
                case 5: return "SSA";
                case 6: return "Fibre";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Virtual";
                case 15: return "FileBackedVirtual";
                case 16: return "Spaces";
                case 17: return "NVMe";
                case 18: return "SCM";
                default: return code < 0 ? "" : "BusType" + code;
            }
        }

        internal static string ToStr(object v) { return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
        internal static long ToLong(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
        internal static int ToInt(object v, int fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
        internal static bool? ToBool(object v)
        {
            if (v == null) return null;
            try { return Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}

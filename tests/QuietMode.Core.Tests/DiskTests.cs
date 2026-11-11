using System;
using System.Collections.Generic;
using QuietMode.Core.Disks;
using QuietMode.Core.Models;

namespace QuietMode.Core.Tests
{
    /// <summary>
    /// Phase 2 单测：磁盘身份解析、安全闸门、操作结果不变式。
    /// 全部基于合成数据，不接触真实磁盘。
    /// </summary>
    public static partial class Program
    {
        internal static void RunDiskTests()
        {
            Run("磁盘身份：uniqueId 唯一命中", TestResolveUnique);
            Run("磁盘身份：uniqueId 未命中时按序列号匹配", TestResolveBySerial);
            Run("磁盘身份：序列号命中多块盘 → 拒绝", TestResolveAmbiguous);
            Run("磁盘身份：无身份字段 → 拒绝", TestResolveNoIdentity);
            Run("磁盘身份：序列号冲突 → 判定为不同盘", TestResolveSerialConflict);
            Run("磁盘身份：容量冲突 → 判定为不同盘", TestResolveSizeConflict);
            Run("磁盘身份：总线类型冲突 → 判定为不同盘", TestResolveBusConflict);
            Run("磁盘身份：型号串不一致仅告警", TestResolveNameWarning);
            Run("磁盘身份：绝不回退到 Disk 0", TestResolveNeverFallsBackToZero);
            Run("磁盘身份：序列号归一化（空格 / 大小写）", TestResolveSerialNormalization);

            Run("安全闸门：系统盘 / 启动盘禁止脱机", TestSafetySystemBoot);
            Run("安全闸门：IsBoot 读取失败同样禁止", TestSafetyUnknownFlags);
            Run("安全闸门：页面文件所在盘禁止脱机", TestSafetyPagefile);
            Run("安全闸门：页面文件在别的盘不影响", TestSafetyPagefileOtherDisk);
            Run("安全闸门：存储池成员禁止脱机", TestSafetyStoragePool);
            Run("安全闸门：占用时 force 可越过", TestSafetyBusyForce);
            Run("安全闸门：占用时不给 force 则阻止", TestSafetyBusyBlocked);
            Run("安全闸门：转储文件只告警", TestSafetyDumpWarning);
            Run("安全闸门：Spaces 总线禁止脱机", TestSafetySpaces);
            Run("安全闸门：路径归属判断（盘符前缀）", TestPathOnDisk);

            Run("操作结果：请求与回读不一致不算成功", TestResultInvariant);
            Run("操作结果：JSON 往返保留字段", TestResultJsonRoundTrip);
        }

        // ---------------- 合成数据 ----------------

        private static DiskSnapshot Disk(int number, string uniqueId, string serial, string name, long size, string bus)
        {
            DiskSnapshot d = new DiskSnapshot();
            d.Number = number;
            d.UniqueId = uniqueId;
            d.SerialNumber = serial;
            d.FriendlyName = name;
            d.SizeBytes = size;
            d.BusType = bus;
            d.IsOffline = false;
            d.IsReadOnly = false;
            d.IsSystem = false;
            d.IsBoot = false;
            d.HealthStatus = "Healthy";
            return d;
        }

        private static DiskTarget Target(string uniqueId, string serial, long size, string bus)
        {
            DiskTarget t = new DiskTarget();
            t.UniqueId = uniqueId;
            t.SerialNumber = serial;
            t.SizeBytes = size;
            t.BusType = bus;
            t.Enabled = true;
            return t;
        }

        private static List<DiskSnapshot> TwoDisks()
        {
            List<DiskSnapshot> list = new List<DiskSnapshot>();
            list.Add(Disk(0, "ID-DISK0", "SN-0000", "SAMPLE HDD 14TB", 14000000000000L, "SATA"));
            list.Add(Disk(1, "ID-DISK1", "SN-1111", "SAMPLE HDD 4TB", 4000000000000L, "SATA"));
            return list;
        }

        // ---------------- 身份解析 ----------------

        private static void TestResolveUnique()
        {
            DiskMatch m = DiskIdentityResolver.Resolve(Target("ID-DISK1", "SN-1111", 4000000000000L, "SATA"), TwoDisks());
            Eq(DiskMatchStatus.Matched, m.Status);
            Eq(1, m.Disk.Number);
            True(m.Unique, "应唯一命中");
        }

        private static void TestResolveBySerial()
        {
            DiskMatch m = DiskIdentityResolver.Resolve(Target("", "SN-1111", 4000000000000L, "SATA"), TwoDisks());
            Eq(DiskMatchStatus.Matched, m.Status);
            Eq(1, m.Disk.Number);
            True(m.Notes.Count > 0, "应提示使用较弱身份");
        }

        private static void TestResolveAmbiguous()
        {
            List<DiskSnapshot> disks = TwoDisks();
            disks[1].SerialNumber = "SN-0000";      // 两块盘序列号相同
            DiskMatch m = DiskIdentityResolver.Resolve(Target("", "SN-0000", 0, ""), disks);
            Eq(DiskMatchStatus.Ambiguous, m.Status);
            Eq(null, m.Disk);
            True(m.Reason.Contains("同时匹配"), "原因应说明多命中：" + m.Reason);
        }

        private static void TestResolveNoIdentity()
        {
            DiskTarget t = new DiskTarget();
            t.LegacyNumber = 0;
            DiskMatch m = DiskIdentityResolver.Resolve(t, TwoDisks());
            Eq(DiskMatchStatus.InsufficientIdentity, m.Status);
            Eq(null, m.Disk);
        }

        private static void TestResolveSerialConflict()
        {
            // uniqueId 命中，但序列号不符 → 视为不同磁盘（可能是换盘 / 固件变化）
            DiskMatch m = DiskIdentityResolver.Resolve(Target("ID-DISK1", "SN-9999", 0, ""), TwoDisks());
            Eq(DiskMatchStatus.Conflict, m.Status);
            True(m.Reason.Contains("序列号"), m.Reason);
        }

        private static void TestResolveSizeConflict()
        {
            DiskMatch m = DiskIdentityResolver.Resolve(Target("ID-DISK1", "SN-1111", 4000000000000L + 10L * 1024 * 1024, "SATA"), TwoDisks());
            Eq(DiskMatchStatus.Conflict, m.Status);
            True(m.Reason.Contains("容量"), m.Reason);
        }

        private static void TestResolveBusConflict()
        {
            DiskMatch m = DiskIdentityResolver.Resolve(Target("ID-DISK1", "SN-1111", 4000000000000L, "USB"), TwoDisks());
            Eq(DiskMatchStatus.Conflict, m.Status);
            True(m.Reason.Contains("总线"), m.Reason);
        }

        private static void TestResolveNameWarning()
        {
            DiskTarget t = Target("ID-DISK1", "SN-1111", 4000000000000L, "SATA");
            t.FriendlyName = "OTHER MODEL";
            DiskMatch m = DiskIdentityResolver.Resolve(t, TwoDisks());
            Eq(DiskMatchStatus.Matched, m.Status);
            True(m.Notes.Exists(n => n.Contains("型号串")), "应有型号告警");
        }

        private static void TestResolveNeverFallsBackToZero()
        {
            // 身份完全不存在，但 legacyNumber=0：必须拒绝，不能操作 0 号盘
            DiskTarget t = Target("ID-NOT-EXIST", "", 0, "");
            t.LegacyNumber = 0;
            DiskMatch m = DiskIdentityResolver.Resolve(t, TwoDisks());
            Eq(DiskMatchStatus.NotFound, m.Status);
            Eq(null, m.Disk);
        }

        private static void TestResolveSerialNormalization()
        {
            List<DiskSnapshot> disks = TwoDisks();
            disks[1].SerialNumber = "  sn-1111  ";
            DiskMatch m = DiskIdentityResolver.Resolve(Target("", "SN-1111", 0, ""), disks);
            Eq(DiskMatchStatus.Matched, m.Status);
            Eq(1, m.Disk.Number);
        }

        // ---------------- 安全闸门 ----------------

        private static DiskSafetyContext Context(DiskSnapshot d)
        {
            DiskSafetyContext ctx = new DiskSafetyContext();
            ctx.Disk = d;
            return ctx;
        }

        private static void TestSafetySystemBoot()
        {
            DiskSnapshot d = Disk(2, "ID-SYS", "SN-SYS", "SAMPLE SSD", 1000000000000L, "NVMe");
            d.IsSystem = true;
            d.IsBoot = true;
            SafetyVerdict v = DiskSafetyGuard.Evaluate(Context(d), false);
            Eq(false, v.Allowed);
            True(v.Blocks.Exists(b => b.Contains("系统盘")), v.ToText());
            True(v.Blocks.Exists(b => b.Contains("启动盘")), v.ToText());

            // force 也不能越过
            SafetyVerdict forced = DiskSafetyGuard.Evaluate(Context(d), true);
            Eq(false, forced.Allowed);
        }

        private static void TestSafetyUnknownFlags()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "SATA");
            d.IsBoot = null;
            d.IsSystem = null;
            SafetyVerdict v = DiskSafetyGuard.Evaluate(Context(d), false);
            Eq(false, v.Allowed);
            True(v.Blocks.Count >= 2, v.ToText());
        }

        private static void TestSafetyPagefile()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "SATA");
            DiskSafetyContext ctx = Context(d);
            ctx.PageFiles.Add(@"D:\pagefile.sys");
            ctx.Volumes.Add(Volume("D:", false, false));
            SafetyVerdict v = DiskSafetyGuard.Evaluate(ctx, false);
            Eq(false, v.Allowed);
            True(v.Blocks.Exists(b => b.Contains("页面文件")), v.ToText());
        }

        private static void TestSafetyPagefileOtherDisk()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "SATA");
            DiskSafetyContext ctx = Context(d);
            ctx.PageFiles.Add(@"G:\pagefile.sys");
            ctx.Volumes.Add(Volume("D:", false, false));
            SafetyVerdict v = DiskSafetyGuard.Evaluate(ctx, false);
            Eq(true, v.Allowed);
        }

        private static void TestSafetyStoragePool()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "SATA");
            DiskSafetyContext ctx = Context(d);
            ctx.InStoragePool = true;
            ctx.StoragePoolName = "Pool1";
            SafetyVerdict v = DiskSafetyGuard.Evaluate(ctx, false);
            Eq(false, v.Allowed);
            True(v.Blocks.Exists(b => b.Contains("存储池")), v.ToText());
        }

        private static void TestSafetyBusyForce()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "SATA");
            DiskSafetyContext ctx = Context(d);
            ctx.Volumes.Add(Volume("D:", false, false));
            DiskOccupancyReport occ = new DiskOccupancyReport();
            occ.Busy = true;
            occ.CountersAvailable = true;
            occ.BytesPerSecond = 1024 * 1024;
            ctx.Occupancy = occ;

            Eq(false, DiskSafetyGuard.Evaluate(ctx, false).Allowed);
            SafetyVerdict forced = DiskSafetyGuard.Evaluate(ctx, true);
            Eq(true, forced.Allowed);
            True(forced.Warnings.Exists(w => w.Contains("forceBusy")), forced.ToText());
        }

        private static void TestSafetyBusyBlocked()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "SATA");
            DiskSafetyContext ctx = Context(d);
            DiskOccupancyReport occ = new DiskOccupancyReport();
            occ.Busy = true;
            ctx.Occupancy = occ;
            SafetyVerdict v = DiskSafetyGuard.Evaluate(ctx, false);
            Eq(false, v.Allowed);
            True(v.Blocks.Exists(b => b.Contains("占用")), v.ToText());
        }

        private static void TestSafetyDumpWarning()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "SATA");
            DiskSafetyContext ctx = Context(d);
            ctx.Volumes.Add(Volume("D:", false, false));
            ctx.DumpFile = @"D:\MEMORY.DMP";
            ctx.CrashDumpEnabled = true;
            SafetyVerdict v = DiskSafetyGuard.Evaluate(ctx, false);
            Eq(true, v.Allowed);
            True(v.Warnings.Exists(w => w.Contains("转储")), v.ToText());
        }

        private static void TestSafetySpaces()
        {
            DiskSnapshot d = Disk(1, "ID-1", "SN-1", "SAMPLE", 1000, "Spaces");
            SafetyVerdict v = DiskSafetyGuard.Evaluate(Context(d), false);
            Eq(false, v.Allowed);
        }

        private static void TestPathOnDisk()
        {
            List<DiskVolumeInfo> vols = new List<DiskVolumeInfo>();
            vols.Add(Volume("D:", false, false));

            True(DiskSafetyGuard.PathOnThisDisk(@"D:\pagefile.sys", vols), "普通路径");
            True(DiskSafetyGuard.PathOnThisDisk(@"\??\D:\pagefile.sys", vols), "\\??\\ 前缀");
            True(DiskSafetyGuard.PathOnThisDisk(@"\\?\D:\pagefile.sys", vols), "\\\\?\\ 前缀");
            True(DiskSafetyGuard.PathOnThisDisk(@"d:\pagefile.sys", vols), "小写盘符");
            Eq(false, DiskSafetyGuard.PathOnThisDisk(@"G:\pagefile.sys", vols));
            Eq(false, DiskSafetyGuard.PathOnThisDisk(@"", vols));
        }

        private static DiskVolumeInfo Volume(string letter, bool system, bool boot)
        {
            DiskVolumeInfo v = new DiskVolumeInfo();
            v.DriveLetter = letter;
            v.IsSystem = system;
            v.IsBoot = boot;
            v.FileSystem = "NTFS";
            v.SizeBytes = 1000;
            return v;
        }

        // ---------------- 操作结果 ----------------

        private static void TestResultInvariant()
        {
            DiskOperationResult r = DiskOperationResult.Make("offline");
            r.PreviousState = "online";
            r.RequestedState = "offline";
            r.ActualState = "online";          // 回读仍是 online
            r.Complete();
            Eq(false, r.Success);
            Eq(false, r.Verified);
            Eq(DiskErrorCode.VerifyFailed, r.ErrorCode);

            DiskOperationResult ok = DiskOperationResult.Make("offline");
            ok.PreviousState = "online";
            ok.RequestedState = "offline";
            ok.ActualState = "offline";
            ok.Complete();
            Eq(true, ok.Success);
            Eq(true, ok.Verified);
            Eq(DiskErrorCode.Ok, ok.ErrorCode);

            // 已带错误码时，即使状态一致也不能算成功
            DiskOperationResult bad = DiskOperationResult.Make("offline");
            bad.RequestedState = "offline";
            bad.ActualState = "offline";
            bad.ErrorCode = DiskErrorCode.SetFailed;
            bad.Complete();
            Eq(false, bad.Success);
        }

        private static void TestResultJsonRoundTrip()
        {
            DiskOperationResult r = DiskOperationResult.Make("online");
            r.UniqueId = "SAMPLE-UNIQUE-0001";
            r.SerialNumber = "SAMPLE-SERIAL-0001";
            r.FriendlyName = "SAMPLE HDD 4TB";
            r.CurrentDiskNumber = 3;
            r.PreviousState = "offline";
            r.RequestedState = "online";
            r.ActualState = "online";
            r.Steps.Add("step-1");
            r.Warnings.Add("warn-1");
            r.Complete();

            string json = r.ToJson().ToJson(true);
            DiskOperationResult back = DiskOperationResult.FromJson((QuietMode.Core.Json.JsonObject)QuietMode.Core.Json.JsonNode.Parse(json));
            Eq(r.UniqueId, back.UniqueId);
            Eq(r.CurrentDiskNumber, back.CurrentDiskNumber);
            Eq(r.ActualState, back.ActualState);
            Eq(r.Success, back.Success);
            Eq(1, back.Steps.Count);
            Eq(1, back.Warnings.Count);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuietMode.Core.Configuration;
using QuietMode.Core.Disks;
using QuietMode.Core.Json;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Models;
using QuietMode.Core.Processes;
using QuietMode.Windows;
using QuietMode.Windows.Native;

namespace QuietMode.Cli
{
    /// <summary>
    /// qmctl —— v2 的机器侧入口（Phase 1 只读 + 配置/快照）。
    /// 约定：--quiet 时不输出任何内容，只以退出码表达结果，供 PowerShell 模块调用，避开控制台编码耦合。
    /// </summary>
    public static class Program
    {
        private static bool _quiet;
        private static bool _json;
        private static string _root;
        private static string _outPath;

        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }

            List<string> positional = new List<string>();
            bool force = false, reset = false, apply = false, withProcess = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--quiet": _quiet = true; break;
                    case "--json": _json = true; break;
                    case "--force": force = true; break;
                    case "--reset": reset = true; break;
                    case "--apply": apply = true; break;
                    case "--processes": withProcess = true; break;
                    case "--out":
                        if (i + 1 < args.Length) { _outPath = args[++i]; }
                        else return Fail("--out 需要一个路径参数");
                        break;
                    case "--root":
                        if (i + 1 < args.Length) { _root = args[++i]; }
                        else return Fail("--root 需要一个路径参数");
                        break;
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;
                    default:
                        if (a.StartsWith("--root=", StringComparison.Ordinal)) _root = a.Substring(7);
                        else if (a.StartsWith("--out=", StringComparison.Ordinal)) _outPath = a.Substring(6);
                        else if (a.StartsWith("-", StringComparison.Ordinal)) return Fail("未知开关：" + a);
                        else positional.Add(a);
                        break;
                }
            }

            if (positional.Count == 0) { PrintUsage(); return 1; }

            QuietModePaths paths = QuietModePaths.ForRoot(_root);

            string cmd = positional[0].ToLowerInvariant();
            string sub = positional.Count > 1 ? positional[1].ToLowerInvariant() : "";
            List<string> rest = positional.Count > 2 ? positional.GetRange(2, positional.Count - 2) : new List<string>();

            switch (cmd)
            {
                case "config":
                    return ConfigCommand(paths, sub, force);
                case "snapshot":
                    return SnapshotCommand(paths, sub, reset);
                case "state":
                    return StateCommand(paths, sub);
                case "disks":
                    return DisksCommand(paths, sub, rest, force, apply, withProcess);
                case "maint":
                    return MaintCommand(paths, sub, rest, apply);
                case "paths":
                    return PathsCommand(paths);
                default:
                    return Fail("未知命令：" + cmd);
            }
        }

        // ---------------- config ----------------

        private static int ConfigCommand(QuietModePaths paths, string sub, bool force)
        {
            ConfigStore store = new ConfigStore(paths);

            switch (sub)
            {
                case "show":
                {
                    ConfigLoadResult r = store.Load(false);
                    if (!r.Loaded)
                    {
                        if (_json) Emit(new JsonObject().Set("loaded", false).Set("error", r.Error ?? ""));
                        return Fail(r.Error);
                    }
                    if (!_quiet)
                    {
                        if (_json) Emit(r.Config.ToJson());
                        else
                        {
                            Console.WriteLine(r.Config.ToJsonText());
                            foreach (string w in r.Report.Warnings) Console.Error.WriteLine("WARN " + w);
                        }
                    }
                    return r.Report.IsValid ? 0 : 2;
                }

                case "validate":
                {
                    ConfigLoadResult r = store.Load(true);
                    if (_json) { Emit(r.Report.ToJsonNode()); return r.Usable ? 0 : 2; }
                    if (!_quiet)
                    {
                        if (!r.Loaded) { Console.Error.WriteLine(r.Error); return 2; }
                        Console.WriteLine(r.SourcePath + (r.UsedBackup ? "（回退到备份）" : ""));
                        Console.WriteLine(r.Report.ToText());
                    }
                    return r.Usable ? 0 : 2;
                }

                case "init":
                {
                    if (File.Exists(paths.ConfigPath) && !force)
                        return Fail("config.json 已存在：" + paths.ConfigPath + "（--force 可覆盖）");
                    paths.EnsureLayout();
                    QuietModeConfig cfg = ConfigStore.DefaultPolicy();
                    store.Save(cfg, false);
                    Info("已写入默认策略：" + paths.ConfigPath);
                    return 0;
                }

                case "migrate":
                {
                    MigrationResult r = ConfigMigrator.Apply(paths, delegate(int number)
                    {
                        try { return DiskInventory.ResolveNumber(number); }
                        catch { return new DiskHint { Number = number, Found = false }; }
                    }, force);

                    if (!_quiet) Console.WriteLine(r.ToText());
                    if (_json)
                    {
                        JsonObject o = new JsonObject();
                        o.Set("ok", r.Ok);
                        o.Set("legacyFound", r.LegacyFound);
                        o.Set("wroteConfig", r.WroteConfig);
                        JsonArray notes; o.SetArray("notes", out notes); foreach (string n in r.Notes) notes.Add(n);
                        JsonArray warns; o.SetArray("warnings", out warns); foreach (string w in r.Warnings) warns.Add(w);
                        JsonArray errs; o.SetArray("errors", out errs); foreach (string e in r.Errors) errs.Add(e);
                        Emit(o);
                    }
                    return r.Ok && r.WroteConfig ? 0 : (r.Ok ? 0 : 1);
                }

                case "path":
                    PathsCommand(paths);
                    return 0;

                default:
                    return Fail("config 子命令：show | validate | init | migrate | path");
            }
        }

        // ---------------- snapshot ----------------

        private static int SnapshotCommand(QuietModePaths paths, string sub, bool reset)
        {
            ConfigStore store = new ConfigStore(paths);
            ConfigLoadResult cfg = store.Load(false);
            QuietModeConfig config = cfg.Usable ? cfg.Config : ConfigStore.DefaultPolicy();

            SnapshotManager mgr = new SnapshotManager(paths, config);

            switch (sub)
            {
                case "create":
                {
                    if (File.Exists(paths.BaselinePath) && !reset)
                        return Fail("baseline.json 已存在，拒绝覆盖（规格 §5.2）；确需重建请加 --reset");

                    BaselineSnapshot s = mgr.Capture();
                    mgr.Save(s, true);

                    SnapshotVerifyResult v = mgr.Verify(s);
                    if (!_quiet)
                    {
                        Console.WriteLine("已生成 " + paths.BaselinePath);
                        Console.WriteLine(" 任务 " + s.ScheduledTasks.Count + " 项 / 服务 " + s.Services.Count
                                          + " 项 / 注册表 " + s.Registry.Count + " 项 / 磁盘 " + s.Disks.Count + " 块");
                        Console.WriteLine(" complete=" + s.Metadata.Complete + "  sha256=" + Short(s.Metadata.IntegritySha256));
                        foreach (string n in s.CaptureNotes) Console.WriteLine("  note  " + n);
                        if (!s.Metadata.Complete && !WindowsVersionInfo.IsElevated())
                            Console.WriteLine("  提示  当前会话未提权；以管理员身份重跑可读取受保护任务并生成完整快照");
                        Console.WriteLine(v.ToText());
                    }
                    if (_json)
                    {
                        JsonObject o = new JsonObject();
                        o.Set("path", paths.BaselinePath);
                        o.Set("complete", s.Metadata.Complete);
                        o.Set("integritySha256", s.Metadata.IntegritySha256 ?? "");
                        o.Set("valid", v.Ok);
                        Emit(o);
                    }
                    return v.Ok ? 0 : 2;
                }

                case "verify":
                {
                    BaselineSnapshot s;
                    try { s = mgr.Load(); }
                    catch (Exception ex) { return Fail(ex.Message); }
                    if (s == null) return Fail("未找到 baseline.json：" + paths.BaselinePath);

                    SnapshotVerifyResult v = mgr.Verify(s);
                    if (_json)
                    {
                        JsonObject o = new JsonObject();
                        o.Set("valid", v.Ok);
                        o.Set("complete", v.Complete);
                        JsonArray errs; o.SetArray("errors", out errs); foreach (string e in v.Errors) errs.Add(e);
                        JsonArray warns; o.SetArray("warnings", out warns); foreach (string w in v.Warnings) warns.Add(w);
                        Emit(o);
                    }
                    else if (!_quiet)
                    {
                        Console.WriteLine(v.ToText());
                    }
                    return v.Ok ? 0 : 2;
                }

                case "show":
                {
                    BaselineSnapshot s;
                    try { s = mgr.Load(); }
                    catch (Exception ex) { return Fail(ex.Message); }
                    if (s == null) return Fail("未找到 baseline.json：" + paths.BaselinePath);

                    if (_json) { Emit(s.ToJson()); return 0; }
                    if (_quiet) return 0;

                    Console.WriteLine("baseline ：" + paths.BaselinePath);
                    Console.WriteLine("创建     ：" + s.Metadata.CreatedAtUtc + " by " + s.Metadata.CreatedBy);
                    Console.WriteLine("Windows  ：" + s.Metadata.WindowsProductName + " " + s.Metadata.WindowsDisplayVersion
                                      + " (" + s.Metadata.WindowsBuild + ")");
                    Console.WriteLine("完整性   ：" + (string.IsNullOrEmpty(s.Metadata.IntegritySha256) ? "-" : s.Metadata.IntegritySha256));
                    Console.WriteLine();
                    Console.WriteLine("计划任务：");
                    foreach (TaskSnapshot t in s.ScheduledTasks)
                    {
                        Console.WriteLine("  " + Pad(t.FullName, 62) + " exists=" + Tri(t.Exists) + " enabled=" + Tri(t.Enabled)
                                          + (string.IsNullOrEmpty(t.Error) ? "" : "  ! " + t.Error));
                    }
                    Console.WriteLine();
                    Console.WriteLine("服务：");
                    foreach (ServiceSnapshot svc in s.Services)
                        Console.WriteLine("  " + Pad(svc.ServiceName, 20) + svc.StartupType + " / " + svc.Status);
                    Console.WriteLine();
                    Console.WriteLine("注册表：");
                    foreach (RegistrySnapshot reg in s.Registry)
                        Console.WriteLine("  " + Pad(reg.ValueName, 24) + (reg.Exists ? reg.Kind + "=" + reg.Value : "<不存在>"));
                    Console.WriteLine();
                    Console.WriteLine("电源：scheme=" + s.PowerPlan.ActiveSchemeGuid
                                      + "  DISKIDLE AC=" + s.PowerPlan.DiskIdleAcSeconds + "s DC=" + s.PowerPlan.DiskIdleDcSeconds + "s"
                                      + (string.IsNullOrEmpty(s.PowerPlan.Error) ? "" : "  ! " + s.PowerPlan.Error));
                    Console.WriteLine();
                    Console.WriteLine("磁盘：");
                    foreach (DiskSnapshot d in s.Disks)
                    {
                        Console.WriteLine("  #" + d.Number + " " + Pad(d.FriendlyName, 26)
                                          + Pad(FormatSize(d.SizeBytes), 10) + Pad(d.BusType, 8)
                                          + "offline=" + Tri(d.IsOffline) + " system=" + Tri(d.IsSystem) + " boot=" + Tri(d.IsBoot)
                                          + " uid=" + Short(d.UniqueId));
                    }
                    Console.WriteLine();
                    Console.WriteLine("FanControl：running=" + s.FanControl.ProcessRunning
                                      + " silentSha=" + Short(s.FanControl.SilentProfileSha256));
                    return 0;
                }

                default:
                    return Fail("snapshot 子命令：create | verify | show");
            }
        }

        private static int StateCommand(QuietModePaths paths, string sub)
        {
            StateStore store = new StateStore(paths);
            QuietModeState s = store.Load();
            if (sub == "show" || sub == "")
            {
                if (_json) { Emit(s.ToJson()); return 0; }
                if (!_quiet) Console.WriteLine(s.ToJson().ToJson(true));
                return 0;
            }
            return Fail("state 子命令：show");
        }

        private static int DisksCommand(QuietModePaths paths, string sub, List<string> rest, bool force, bool apply, bool withProcess)
        {
            switch (sub)
            {
                case "":
                case "list": return DisksList();
                case "resolve": return DisksResolve(paths);
                case "inspect": return DisksInspect(paths, rest, force, withProcess);
                case "offline": return DisksChange(paths, rest, "offline", apply, force);
                case "online": return DisksChange(paths, rest, "online", apply, force);
                case "verify": return DisksChange(paths, rest, "verify", false, force);
                default: return Fail("disks 子命令：list | resolve | inspect | offline | online | verify");
            }
        }

        /// <summary>列出本机全部磁盘（只读，不需要配置文件）。</summary>
        private static int DisksList()
        {
            List<DiskSnapshot> disks = DiskInventory.Query();
            if (_json)
            {
                JsonArray arr = new JsonArray();
                foreach (DiskSnapshot d in disks) arr.Add(d.ToJson());
                JsonObject o = new JsonObject();
                o.Set("disks", arr);
                Emit(o);
                return disks.Count > 0 ? 0 : 2;
            }
            if (_quiet) return disks.Count > 0 ? 0 : 2;
            if (disks.Count == 0) return Fail("磁盘清单为空（WMI 查询失败）");

            Console.WriteLine("NUM  FRIENDLY                     SIZE        BUS     OFFLINE SYSTEM BOOT  UNIQUEID");
            foreach (DiskSnapshot d in disks)
            {
                Console.WriteLine(Pad(d.Number.ToString(CultureInfo.InvariantCulture), 5)
                                  + Pad(d.FriendlyName, 30)
                                  + Pad(FormatSize(d.SizeBytes), 12)
                                  + Pad(d.BusType, 8)
                                  + Pad(Tri(d.IsOffline), 8)
                                  + Pad(Tri(d.IsSystem), 7)
                                  + Pad(Tri(d.IsBoot), 6)
                                  + Short(d.UniqueId));
            }
            return 0;
        }

        /// <summary>把配置里的磁盘目标逐一与当前磁盘匹配，输出唯一命中情况。</summary>
        private static int DisksResolve(QuietModePaths paths)
        {
            QuietModeConfig cfg;
            int loadCode = LoadConfig(paths, out cfg);
            if (loadCode != 0) return loadCode;

            List<DiskSnapshot> disks = DiskInventory.Query();
            List<DiskMatch> matches = DiskIdentityResolver.ResolveAll(cfg.Disks, disks);
            bool allMatched = matches.Count > 0;
            foreach (DiskMatch m in matches) if (!m.Unique) allMatched = false;

            if (_json)
            {
                JsonArray arr = new JsonArray();
                foreach (DiskMatch m in matches)
                {
                    JsonObject item;
                    arr.AddObject(out item);
                    item.Set("status", m.StatusText);
                    item.Set("reason", m.Reason ?? "");
                    item.Set("uniqueId", m.Target == null ? "" : m.Target.UniqueId);
                    item.Set("serialNumber", m.Target == null ? "" : m.Target.SerialNumber);
                    item.Set("friendlyName", m.Target == null ? "" : m.Target.FriendlyName);
                    item.Set("diskNumber", m.Disk == null ? -1 : m.Disk.Number);
                    JsonArray notes;
                    item.SetArray("notes", out notes);
                    foreach (string n in m.Notes) notes.Add(n);
                }
                JsonObject root = new JsonObject();
                root.Set("disksEnabled", cfg.Disks.Enabled);
                root.Set("targets", arr);
                Emit(root);
                return allMatched ? 0 : 2;
            }
            if (_quiet) return allMatched ? 0 : 2;

            if (matches.Count == 0)
                return Fail("配置中没有启用的磁盘目标（disks.enabled=" + cfg.Disks.Enabled + "）");

            Console.WriteLine("IDX  STATUS           DISK  NAME                           UNIQUEID");
            for (int i = 0; i < matches.Count; i++)
            {
                DiskMatch m = matches[i];
                Console.WriteLine(Pad(i.ToString(CultureInfo.InvariantCulture), 5)
                                  + Pad(m.StatusText, 17)
                                  + Pad(m.Disk == null ? "-" : m.Disk.Number.ToString(CultureInfo.InvariantCulture), 6)
                                  + Pad(m.Target == null ? "" : m.Target.FriendlyName, 31)
                                  + Short(m.Target == null ? "" : m.Target.UniqueId));
                if (m.Status != DiskMatchStatus.Matched || m.Notes.Count > 0)
                    Console.WriteLine("       " + m.Reason + (m.Notes.Count > 0 ? "  [" + string.Join("；", m.Notes.ToArray()) + "]" : ""));
            }
            return allMatched ? 0 : 2;
        }

        /// <summary>身份 + 卷 + 页面文件 + 存储池 + 占用 + 安全结论，全部只读。</summary>
        private static int DisksInspect(QuietModePaths paths, List<string> rest, bool force, bool withProcess)
        {
            QuietModeConfig cfg;
            int loadCode = LoadConfig(paths, out cfg);
            if (loadCode != 0) return loadCode;

            List<DiskTarget> targets;
            string selError;
            if (!SelectTargets(cfg, rest, out targets, out selError)) return Fail(selError);

            List<DiskSnapshot> disks = DiskInventory.Query();
            List<JsonObject> jsonItems = new List<JsonObject>();
            int problems = 0;

            foreach (DiskTarget t in targets)
            {
                DiskMatch m = DiskIdentityResolver.Resolve(t, disks);
                JsonObject item = new JsonObject();
                item.Set("uniqueId", t.UniqueId ?? "");
                item.Set("friendlyName", t.FriendlyName ?? "");
                item.Set("matchStatus", m.StatusText);
                item.Set("matchReason", m.Reason ?? "");
                item.Set("diskNumber", m.Disk == null ? -1 : m.Disk.Number);

                if (!m.Unique)
                {
                    problems++;
                    jsonItems.Add(item);
                    if (!_quiet && !_json)
                        Console.WriteLine("× " + (string.IsNullOrEmpty(t.FriendlyName) ? Short(t.UniqueId) : t.FriendlyName)
                                          + " → " + m.StatusText + "：" + m.Reason);
                    continue;
                }

                DiskSafetyContext ctx = BuildSafetyContext(m.Disk, withProcess);
                SafetyVerdict verdict = DiskSafetyGuard.Evaluate(ctx, force);
                if (!verdict.Allowed) problems++;

                JsonArray vols;
                item.SetArray("volumes", out vols);
                foreach (DiskVolumeInfo v in ctx.Volumes) vols.Add(v.ToJson());
                item.Set("safetyAllowed", verdict.Allowed);
                JsonArray blocks;
                item.SetArray("blocks", out blocks);
                foreach (string b in verdict.Blocks) blocks.Add(b);
                JsonArray warns;
                item.SetArray("warnings", out warns);
                foreach (string w in verdict.Warnings) warns.Add(w);
                if (ctx.Occupancy != null) item.Set("occupancy", ctx.Occupancy.ToJson());
                jsonItems.Add(item);

                if (_quiet || _json) continue;

                Console.WriteLine("─ " + (string.IsNullOrEmpty(m.Disk.FriendlyName) ? "磁盘 #" + m.Disk.Number : m.Disk.FriendlyName)
                                  + "   #" + m.Disk.Number + "   " + FormatSize(m.Disk.SizeBytes) + "   " + m.Disk.BusType
                                  + "   " + (m.Disk.IsOffline == true ? "OFFLINE" : "ONLINE"));
                Console.WriteLine("  uniqueId  " + m.Disk.UniqueId);
                Console.WriteLine("  serial    " + m.Disk.SerialNumber + "   health=" + m.Disk.HealthStatus);
                Console.WriteLine("  match     " + m.Reason);
                foreach (string n in m.Notes) Console.WriteLine("  note      " + n);
                foreach (DiskVolumeInfo v in ctx.Volumes)
                    Console.WriteLine("  volume    " + Pad(v.Display, 24) + Pad(v.FileSystem, 8) + Pad(FormatSize(v.SizeBytes), 12)
                                      + (v.IsSystem ? " [系统]" : "") + (v.IsBoot ? " [启动]" : ""));
                if (ctx.PageFiles.Count > 0)
                    Console.WriteLine("  pagefile  " + string.Join(", ", ctx.PageFiles.ToArray())
                                      + (ctx.AutomaticManagedPagefile == true ? "  [自动管理]" : ""));
                if (!string.IsNullOrEmpty(ctx.DumpFile))
                    Console.WriteLine("  dumpfile  " + ctx.DumpFile + (ctx.CrashDumpEnabled ? "" : "  [已禁用]"));
                if (ctx.Occupancy != null)
                {
                    Console.WriteLine("  io        " + ctx.Occupancy.BusySummary
                                      + (ctx.Occupancy.CountersAvailable ? "" : "（计数器不可用）"));
                    int shown = 0;
                    foreach (DiskOccupant occ in ctx.Occupancy.Occupants)
                    {
                        if (shown++ >= 10)
                        {
                            Console.WriteLine("  occupant  …… 另有 " + (ctx.Occupancy.Occupants.Count - 10) + " 个进程命中（--json 可看全部）");
                            break;
                        }
                        Console.WriteLine("  occupant  " + occ.ToLine().Replace("\n", "\n            "));
                    }
                }
                Console.WriteLine("  safety    " + verdict.ToText().Replace("\n", "\n            "));
            }

            if (_json)
            {
                JsonArray arr = new JsonArray();
                foreach (JsonObject o in jsonItems) arr.Add(o);
                JsonObject root = new JsonObject();
                root.Set("disksEnabled", cfg.Disks.Enabled);
                root.Set("targets", arr);
                Emit(root);
            }
            return problems == 0 ? 0 : 2;
        }

        /// <summary>执行脱机/联机/校验。真正动手必须带 --apply，否则只演练。</summary>
        private static int DisksChange(QuietModePaths paths, List<string> rest, string operation, bool apply, bool force)
        {
            QuietModeConfig cfg;
            int loadCode = LoadConfig(paths, out cfg);
            if (loadCode != 0) return loadCode;

            List<DiskTarget> targets;
            string selError;
            if (!SelectTargets(cfg, rest, out targets, out selError)) return Fail(selError);
            if (targets.Count == 0) return Fail("没有可操作的磁盘目标");

            DiskOperationBatch batch = DiskController.Run(paths, operation, targets, apply, force, 0);

            if (_json)
            {
                JsonObject root = new JsonObject();
                root.Set("ok", batch.Ok);
                root.Set("error", batch.Error ?? "");
                root.Set("elevated", batch.Elevated);
                root.Set("requestFile", batch.RequestFile ?? "");
                root.Set("resultFile", batch.ResultFile ?? "");
                JsonArray arr;
                root.SetArray("results", out arr);
                foreach (DiskOperationResult r in batch.Results) arr.Add(r.ToJson());
                Emit(root);
            }
            else if (!_quiet)
            {
                if (!string.IsNullOrEmpty(batch.Error)) Console.Error.WriteLine("错误：" + batch.Error);
                foreach (DiskOperationResult r in batch.Results)
                    Console.WriteLine(r.ToText() + "\n");
                if (!string.IsNullOrEmpty(batch.StdErr))
                    Console.WriteLine("stderr: " + ProcessRunner.Flatten(batch.StdErr));
                if (!apply && operation != "verify")
                    Console.WriteLine("（演练模式：加 --apply 才会真正执行）");
            }

            if (!string.IsNullOrEmpty(batch.Error)) return 2;
            return batch.Ok ? 0 : 2;
        }

        // ---------------- disks helpers ----------------

        private static int LoadConfig(QuietModePaths paths, out QuietModeConfig cfg)
        {
            cfg = null;
            ConfigLoadResult lr = new ConfigStore(paths).Load(false);
            if (!lr.Loaded)
            {
                Fail("读取配置失败：" + (lr.Error ?? "未知错误"));
                return 1;
            }
            if (!lr.Usable)
            {
                Fail("配置校验未通过：" + lr.Report.ToText());
                return 2;
            }
            cfg = lr.Config;
            return 0;
        }

        /// <summary>选择磁盘目标：无参数=全部启用；数字=索引；all=全部启用。</summary>
        private static bool SelectTargets(QuietModeConfig cfg, List<string> rest, out List<DiskTarget> targets, out string error)
        {
            targets = new List<DiskTarget>();
            error = "";
            List<DiskTarget> all = new List<DiskTarget>();
            foreach (DiskTarget t in cfg.Disks.Targets) if (t.Enabled) all.Add(t);

            string selector = (rest != null && rest.Count > 0) ? rest[0] : "";
            if (selector == "" || selector.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                targets.AddRange(all);
                return true;
            }

            int index;
            if (int.TryParse(selector, out index))
            {
                if (index < 0 || index >= all.Count)
                {
                    error = "索引 " + index + " 超出范围（共 " + all.Count + " 个启用的磁盘目标）";
                    return false;
                }
                targets.Add(all[index]);
                return true;
            }

            foreach (DiskTarget t in all)
            {
                if (string.Equals(DiskIdentityResolver.NormalizeId(t.UniqueId), DiskIdentityResolver.NormalizeId(selector), StringComparison.Ordinal)
                    || string.Equals(DiskIdentityResolver.NormalizeSerial(t.SerialNumber), DiskIdentityResolver.NormalizeSerial(selector), StringComparison.Ordinal))
                {
                    targets.Add(t);
                    return true;
                }
            }
            error = "找不到磁盘目标：" + selector;
            return false;
        }

        private static DiskSafetyContext BuildSafetyContext(DiskSnapshot disk, bool withProcess)
        {
            DiskSafetyContext ctx = new DiskSafetyContext();
            ctx.Disk = disk;
            ctx.Volumes = DiskProbe.QueryVolumes(disk.Number);

            DiskProbe.PageFileInfo pf = DiskProbe.QueryPageFiles();
            ctx.AutomaticManagedPagefile = pf.AutomaticManaged;
            ctx.PageFiles = pf.Paths;

            DiskProbe.CrashDumpInfo dump = DiskProbe.QueryCrashDump();
            ctx.DumpFile = dump.DumpFile;
            ctx.CrashDumpEnabled = dump.Enabled;

            DiskProbe.PoolInfo pool = DiskProbe.QueryStoragePool(disk);
            ctx.InStoragePool = pool.IsMember;
            ctx.StoragePoolName = pool.PoolName;
            if (!pool.IsMember && !string.IsNullOrEmpty(pool.Reason)) ctx.StoragePoolNote = pool.Reason;

            ctx.Occupancy = DiskProbe.Diagnose(disk, ctx.Volumes, withProcess);
            return ctx;
        }

        // ---------------- maint ----------------

        private static int MaintCommand(QuietModePaths paths, string sub, List<string> rest, bool apply)
        {
            switch (sub)
            {
                case "list": return MaintList();
                case "status": return MaintStatus(paths);
                case "plan": return MaintPlan(paths, rest);
                case "night":
                case "day": return MaintSwitch(paths, sub, apply);
                case "verify": return MaintVerify(paths, rest);
                case "baseline-enable":
                case "baseline-disable":
                    return MaintBaselineSet(paths, rest, sub == "baseline-enable", apply);
                default:
                    Console.WriteLine("maint 子命令：list | status | plan <night|day> | night [--apply] | day [--apply] |");
                    Console.WriteLine("              verify <night|day> | baseline-enable <序号|all> --apply");
                    Console.WriteLine("              baseline-disable <序号|all> --apply");
                    return 1;
            }
        }

        private static int MaintList()
        {
            SystemObservation obs = MaintenanceProbe.Capture();
            if (_json)
            {
                JsonObject o = new JsonObject();
                JsonArray arr;
                o.SetArray("tasks", out arr);
                foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.All)
                {
                    ObservedTask t = obs.FindTask(def.Path, def.Name);
                    JsonObject item;
                    arr.AddObject(out item);
                    item.Set("module", def.Module);
                    item.Set("fullName", def.FullName);
                    item.Set("nightDisable", def.NightDisableDefault);
                    item.Set("purpose", def.Purpose);
                    item.Set("presence", t == null ? "Unknown" : t.Presence.ToString());
                    item.Set("enabled", t != null && t.Enabled.HasValue ? (JsonNode)new JsonBool(t.Enabled.Value) : JsonNull.Instance);
                    item.Set("state", t == null ? "" : (t.State ?? ""));
                    item.Set("error", t == null ? "" : (t.Error ?? ""));
                }
                o.Set("elevated", obs.Elevated);
                Emit(o);
                return 0;
            }
            if (_quiet) return 0;

            Console.WriteLine("MODULE           NIGHT TASK                                                        PRESENCE   ENABLED STATE");
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.All)
            {
                ObservedTask t = obs.FindTask(def.Path, def.Name);
                string presence = t == null ? "Unknown" : t.Presence.ToString();
                string enabled = t != null && t.Enabled.HasValue ? (t.Enabled.Value ? "on" : "off") : "?";
                Console.WriteLine(Pad(def.Module, 17) + Pad(def.NightDisableDefault ? "y" : "-", 6)
                                  + Pad(ShortTaskName(def.FullName), 60) + Pad(presence, 11) + Pad(enabled, 8)
                                  + (t == null ? "" : t.State));
            }
            Console.WriteLine();
            Console.WriteLine("禁改范围（规格 §4.6）：" + string.Join(" ", MaintenanceCatalog.ForbiddenPathPrefixes));
            Console.WriteLine("elevated=" + obs.Elevated);
            return 0;
        }

        /// <summary>表格里省掉公共前缀，避免长任务名被截断；JSON 里仍是全名。</summary>
        private static string ShortTaskName(string fullName)
        {
            const string prefix = @"\Microsoft\Windows\";
            string n = fullName ?? "";
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return n.Substring(prefix.Length);
            return n;
        }

        private static int MaintStatus(QuietModePaths paths)
        {
            QuietModeConfig cfg;
            int rc = LoadConfig(paths, out cfg);
            if (rc != 0) return rc;

            BaselineSnapshot baseline = new SnapshotManager(paths, cfg).Load();
            SystemObservation obs = MaintenanceProbe.Capture();

            if (_json)
            {
                JsonObject o = new JsonObject();
                o.Set("observation", obs.ToJson());
                o.Set("config", cfg.Maintenance.ToJson());
                if (baseline != null) o.Set("baseline", baseline.ToJson());
                JsonArray anomalies;
                o.SetArray("baselineAnomalies", out anomalies);
                foreach (string s in BaselineAnomalies(baseline)) anomalies.Add(s);
                Emit(o);
                return 0;
            }
            if (_quiet) return 0;

            Console.WriteLine("maintenance.enabled        = " + cfg.Maintenance.Enabled);
            Console.WriteLine("maintenanceHour            = " + cfg.Maintenance.MaintenanceHour);
            Console.WriteLine("disableWake                = " + cfg.Maintenance.DisableWake);
            Console.WriteLine("disableSysMainAtNight      = " + cfg.Maintenance.DisableSysMainAtNight);
            Console.WriteLine("baseline.json              = " + (baseline == null ? "(未建立)" : paths.BaselinePath));
            Console.WriteLine("elevated                   = " + obs.Elevated);
            Console.WriteLine();

            ObservedService svc = obs.FindService(MaintenanceCatalog.SysMainService);
            Console.WriteLine("SysMain " + (svc == null || !svc.Exists
                ? "不存在"
                : "Start=" + svc.StartValue + " (" + svc.StartupType + ") Status=" + svc.Status));
            foreach (ObservedRegistryValue r in obs.Registry)
            {
                Console.WriteLine(r.ValueName + " " + (r.Exists ? "= " + r.Value + " (" + r.Kind + ")" : "不存在"));
            }
            Console.WriteLine();

            Console.WriteLine("TASK                                                        BASELINE NOW");
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                TaskSnapshot b = MaintenancePlanner.FindTask(baseline, def.Path, def.Name);
                ObservedTask t = obs.FindTask(def.Path, def.Name);
                string bText = b == null ? "?" : (!b.Exists.HasValue ? "?" : (b.Exists.Value ? (b.Enabled.HasValue ? (b.Enabled.Value ? "启用" : "禁用") : "?") : "不存在"));
                string nText = t == null ? "?" : (t.Presence == TaskPresence.NotPresent ? "不存在"
                                                 : t.Presence == TaskPresence.Unknown ? "读取失败"
                                                 : (t.Enabled.HasValue ? (t.Enabled.Value ? "启用" : "禁用") : "?"));
                Console.WriteLine(Pad(ShortTaskName(def.FullName), 60) + Pad(bText, 9) + nText);
            }

            List<string> pending = BaselineAnomalies(baseline);
            if (pending.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("需要你决定的 baseline 异常：");
                foreach (string s in pending) Console.WriteLine("  - " + s);
                Console.WriteLine("  修正方式：qmctl maint baseline-enable <序号> --apply");
            }
            return 0;
        }

        /// <summary>baseline 里记为「禁用」的任务，白天永远不会被启用 —— 大概率是早期版本遗留。</summary>
        private static List<string> BaselineAnomalies(BaselineSnapshot baseline)
        {
            List<string> list = new List<string>();
            if (baseline == null) return list;
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                TaskSnapshot b = MaintenancePlanner.FindTask(baseline, def.Path, def.Name);
                if (b == null || b.Exists != true || !b.Enabled.HasValue || b.Enabled.Value) continue;
                list.Add(def.FullName + " 在 baseline 里就是「禁用」，白天不会恢复启用"
                         + "（若这不是你的本意，说明快照拍在旧版本已禁用之后）");
            }
            return list;
        }

        private static int MaintPlan(QuietModePaths paths, List<string> rest)
        {
            string phase = rest.Count > 0 ? rest[0].ToLowerInvariant() : "";
            if (phase != "night" && phase != "day") return Fail("用法：qmctl maint plan <night|day>");

            QuietModeConfig cfg;
            int rc = LoadConfig(paths, out cfg);
            if (rc != 0) return rc;
            BaselineSnapshot baseline = new SnapshotManager(paths, cfg).Load();
            SystemObservation obs = MaintenanceProbe.Capture();

            MaintenancePlan plan = phase == "night"
                ? MaintenancePlanner.BuildNight(cfg, baseline, obs)
                : MaintenancePlanner.BuildDay(cfg, baseline, obs);

            if (_json) { Emit(plan.ToJson()); return 0; }
            if (!_quiet) PrintPlan(plan);
            return 0;
        }

        private static void PrintPlan(MaintenancePlan plan)
        {
            Console.WriteLine("计划阶段 " + plan.Phase + "，共 " + plan.Steps.Count + " 步：");
            foreach (MaintenanceStep s in plan.Steps)
            {
                Console.WriteLine("  " + s.Order.ToString(CultureInfo.InvariantCulture).PadLeft(2) + ". "
                                  + Pad(s.ActionText, 14) + s.Target
                                  + (string.IsNullOrEmpty(s.DesiredValue) ? "" : " = " + s.DesiredValue)
                                  + (s.Critical ? "" : "   (非关键)"));
            }
            if (plan.Notes.Count > 0)
            {
                Console.WriteLine("说明：");
                foreach (string n in plan.Notes) Console.WriteLine("  - " + n);
            }
        }

        private static int MaintSwitch(QuietModePaths paths, string phase, bool apply)
        {
            QuietModeConfig cfg;
            int rc = LoadConfig(paths, out cfg);
            if (rc != 0) return rc;
            BaselineSnapshot baseline = new SnapshotManager(paths, cfg).Load();
            if (baseline == null)
            {
                if (_json) Emit(new JsonObject().Set("ok", false).Set("error", "baseline.json 不存在，先运行 qmctl snapshot create"));
                else Console.Error.WriteLine("baseline.json 不存在：先运行 qmctl snapshot create（白天恢复需要原始状态）");
                return 2;
            }

            MaintenanceRun run = MaintenanceController.Run(paths, cfg, baseline, phase, apply, 240000);

            if (_json)
            {
                JsonObject o = run.ToJson();
                Emit(o);
                if (!apply) return 0;
                return run.Ok ? 0 : 2;
            }
            if (_quiet) return !apply ? 0 : (run.Ok ? 0 : 2);

            PrintPlan(run.Plan);
            Console.WriteLine();
            if (!string.IsNullOrEmpty(run.Error)) Console.WriteLine("错误：" + run.Error);
            if (!apply)
            {
                Console.WriteLine("（演练：未改动任何状态。加 --apply 才真正执行）");
                return 0;
            }

            Console.WriteLine("执行结果：");
            foreach (MaintenanceStep s in run.Plan.Steps) Console.WriteLine("  " + s.ToLine());
            Console.WriteLine();
            Console.WriteLine(run.Verification.ToText());
            return run.Ok ? 0 : 2;
        }

        private static int MaintVerify(QuietModePaths paths, List<string> rest)
        {
            string phase = rest.Count > 0 ? rest[0].ToLowerInvariant() : "night";
            if (phase != "night" && phase != "day") return Fail("用法：qmctl maint verify <night|day>");

            QuietModeConfig cfg;
            int rc = LoadConfig(paths, out cfg);
            if (rc != 0) return rc;
            BaselineSnapshot baseline = new SnapshotManager(paths, cfg).Load();
            SystemObservation obs = MaintenanceProbe.Capture();

            MaintenanceVerification v = phase == "night"
                ? MaintenanceVerifier.VerifyNight(cfg, obs)
                : MaintenanceVerifier.VerifyDay(cfg, baseline, obs);

            if (_json) { JsonObject o = v.ToJson(); o.Set("elevated", obs.Elevated); Emit(o); return v.Verdict == MaintenanceVerdict.Ok ? 0 : 2; }
            if (!_quiet) Console.WriteLine(v.ToText());
            return v.Verdict == MaintenanceVerdict.Ok ? 0 : 2;
        }

        private static int MaintBaselineSet(QuietModePaths paths, List<string> rest, bool enable, bool apply)
        {
            if (rest.Count == 0) return Fail("用法：qmctl maint baseline-" + (enable ? "enable" : "disable") + " <序号|all> --apply");

            QuietModeConfig cfg;
            int rc = LoadConfig(paths, out cfg);
            if (rc != 0) return rc;

            SnapshotManager mgr = new SnapshotManager(paths, cfg);
            BaselineSnapshot baseline = mgr.Load();
            if (baseline == null) return Fail("baseline.json 不存在，先运行 qmctl snapshot create");

            List<MaintenanceTaskDefinition> targets = MaintenanceCatalog.NightDisableTargets;
            List<TaskSnapshot> changed = new List<TaskSnapshot>();

            if (string.Equals(rest[0], "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (MaintenanceTaskDefinition def in targets)
                {
                    TaskSnapshot b = MaintenancePlanner.FindTask(baseline, def.Path, def.Name);
                    if (b != null && b.Exists == true) { b.Enabled = enable; changed.Add(b); }
                }
            }
            else
            {
                int index;
                if (!int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                    return Fail("序号必须是整数或 all");
                if (index < 0 || index >= targets.Count) return Fail("序号超出范围（0-" + (targets.Count - 1) + "）");
                MaintenanceTaskDefinition def = targets[index];
                TaskSnapshot b = MaintenancePlanner.FindTask(baseline, def.Path, def.Name);
                if (b == null) return Fail("baseline 里没有 " + def.FullName);
                b.Enabled = enable;
                changed.Add(b);
            }

            if (changed.Count == 0) return Fail("没有可修改的条目");

            if (!_json) foreach (TaskSnapshot b in changed)
                Console.WriteLine("baseline " + b.FullName + " -> enabled=" + (enable ? "true" : "false"));
            if (!_json && !apply)
            {
                Console.WriteLine("（演练：未写入。加 --apply 才修改 baseline.json）");
                return 0;
            }

            mgr.Save(baseline, true);
            if (_json) Emit(new JsonObject().Set("ok", true).Set("changed", changed.Count).Set("enabled", enable));
            else if (!_quiet) Console.WriteLine("已写回 baseline.json（完整性哈希已重算）");
            return 0;
        }

        private static int PathsCommand(QuietModePaths paths)
        {
            if (_json)
            {
                JsonObject o = new JsonObject();
                o.Set("root", paths.Root);
                o.Set("config", paths.ConfigPath);
                o.Set("backup", paths.BackupPath);
                o.Set("state", paths.StatePath);
                o.Set("baseline", paths.BaselinePath);
                o.Set("logs", paths.LogDir);
                o.Set("recovery", paths.RecoveryDir);
                o.Set("legacyAppData", paths.LegacyAppDataDir);
                Emit(o);
                return 0;
            }
            if (!_quiet)
            {
                Console.WriteLine("root     " + paths.Root);
                Console.WriteLine("config   " + paths.ConfigPath);
                Console.WriteLine("backup   " + paths.BackupPath);
                Console.WriteLine("state    " + paths.StatePath);
                Console.WriteLine("baseline " + paths.BaselinePath);
                Console.WriteLine("logs     " + paths.LogDir);
                Console.WriteLine("recovery " + paths.RecoveryDir);
                Console.WriteLine("legacy   " + paths.LegacyAppDataDir);
            }
            return 0;
        }

        // ---------------- helpers ----------------

        private static void PrintUsage()
        {
            Console.WriteLine("qmctl " + ProductInfo.Version);
            Console.WriteLine("用法: qmctl [--root PATH] [--quiet] [--json] [--out FILE] <命令> [参数]");
            Console.WriteLine();
            Console.WriteLine("  config show                 打印当前配置 JSON");
            Console.WriteLine("  config validate             校验 config.json（含文件存在性）");
            Console.WriteLine("  config init [--force]       写默认策略");
            Console.WriteLine("  config migrate [--force]    从 v1 settings.ini 迁移");
            Console.WriteLine("  config path                 打印路径布局");
            Console.WriteLine("  snapshot create [--reset]   采集 baseline（已存在需 --reset）");
            Console.WriteLine("  snapshot verify             校验 baseline 完整性");
            Console.WriteLine("  snapshot show               打印 baseline 摘要");
            Console.WriteLine("  state show                  打印 state.json");
            Console.WriteLine("  disks list                  列出本机磁盘与稳定身份");
            Console.WriteLine("  disks resolve               配置目标 → 当前磁盘的唯一匹配结果");
            Console.WriteLine("  disks inspect [索引|all]     身份/卷/页面文件/存储池/占用/安全结论（只读）");
            Console.WriteLine("  disks offline [索引|all]     脱机（默认演练，--apply 才执行，--force 越过普通占用）");
            Console.WriteLine("  disks online  [索引|all]     联机（默认演练，--apply 才执行）");
            Console.WriteLine("  disks verify  [索引|all]     只回读当前状态");
            Console.WriteLine("  maint list                  维护任务清单与当前存在/启用状态");
            Console.WriteLine("  maint status                配置 + baseline 对照 + 需要决定的异常");
            Console.WriteLine("  maint plan night|day        只算计划，不动系统");
            Console.WriteLine("  maint night|day [--apply]   夜间禁用 / 白天恢复（默认演练）");
            Console.WriteLine("  maint verify night|day      只读验证当前是否达成目标状态");
            Console.WriteLine("  maint baseline-enable  <序号|all> --apply  把 baseline 纠正为「启用」");
            Console.WriteLine("  maint baseline-disable <序号|all> --apply  把 baseline 纠正为「禁用」");
            Console.WriteLine();
            Console.WriteLine("退出码: 0 成功 / 1 一般失败 / 2 校验失败或操作未达成");
        }

        private static int Fail(string message)
        {
            if (!_quiet && !string.IsNullOrEmpty(message)) Console.Error.WriteLine("错误：" + message);
            return 1;
        }

        private static void Info(string message)
        {
            if (!_quiet) Console.WriteLine(message);
        }

        private static void Emit(JsonNode node)
        {
            string text = node.ToJson(true);
            if (!string.IsNullOrEmpty(_outPath))
            {
                // 写到文件，供 PowerShell 以 UTF-8 读取，绕开控制台代码页
                string dir = Path.GetDirectoryName(_outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                AtomicFile.WriteText(_outPath, text + "\n", null, null);
                return;
            }
            if (_quiet) return;
            Console.WriteLine(text);
        }

        private static string Tri(bool? value)
        {
            if (!value.HasValue) return "?";
            return value.Value ? "yes" : "no";
        }

        private static string Pad(string value, int width)
        {
            string s = value ?? "";
            if (s.Length >= width) return s.Substring(0, Math.Max(0, width - 1)) + " ";
            return s.PadRight(width);
        }

        private static string Short(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= 16 ? value : value.Substring(0, 16) + "…";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "-";
            double gb = bytes / 1024.0 / 1024.0 / 1024.0;
            if (gb >= 1000) return (gb / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " TB";
            return gb.ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }
    }
}

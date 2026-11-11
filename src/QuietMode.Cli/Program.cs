using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuietMode.Core.Configuration;
using QuietMode.Core.Json;
using QuietMode.Core.Models;
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
            bool force = false, reset = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--quiet": _quiet = true; break;
                    case "--json": _json = true; break;
                    case "--force": force = true; break;
                    case "--reset": reset = true; break;
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

            switch (cmd)
            {
                case "config":
                    return ConfigCommand(paths, sub, force);
                case "snapshot":
                    return SnapshotCommand(paths, sub, reset);
                case "state":
                    return StateCommand(paths, sub);
                case "disks":
                    return DisksCommand(sub);
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

        private static int DisksCommand(string sub)
        {
            if (sub != "" && sub != "list") return Fail("disks 子命令：list");

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

            Console.WriteLine("NUM  FRIENDLY                   SIZE        BUS     OFFLINE SYSTEM BOOT  UNIQUEID");
            foreach (DiskSnapshot d in disks)
            {
                Console.WriteLine(Pad(d.Number.ToString(CultureInfo.InvariantCulture), 5)
                                  + Pad(d.FriendlyName, 28)
                                  + Pad(FormatSize(d.SizeBytes), 12)
                                  + Pad(d.BusType, 8)
                                  + Pad(Tri(d.IsOffline), 8)
                                  + Pad(Tri(d.IsSystem), 7)
                                  + Pad(Tri(d.IsBoot), 6)
                                  + Short(d.UniqueId));
            }
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
            Console.WriteLine("  disks list                  列出磁盘与稳定身份");
            Console.WriteLine();
            Console.WriteLine("退出码: 0 成功 / 1 一般失败 / 2 校验失败");
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

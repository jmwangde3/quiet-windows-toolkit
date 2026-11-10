using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuietMode
{
    // ============================================================
    //  QuietMode - 统一静音托盘总控
    //  风扇: FanControl IpcCtl load (用户态, 无需管理员)
    //  硬盘: C:\ProgramData\QuietHDD\diskgate.ps1 -ForceNight / -ForceDay (需管理员)
    //  维护任务 / 停转 / 定时闸门: 管理员 worker
    //  编译: .NET Framework 4.x csc.exe, /target:winexe
    // ============================================================

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Cfg.Load();
            Sys.Log("Main entered" + (args.Length > 0 ? " arg=" + args[0] : " (no args)"));

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Sys.Log("ThreadException: " + e.Exception);
                try { MessageBox.Show("QuietMode 出错：\n" + e.Exception.Message + "\n\n详情见 " + Cfg.LogPath,
                    "QuietMode", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Sys.Log("UnhandledException: " + (e.ExceptionObject == null ? "null" : e.ExceptionObject.ToString()));
            };

            // --diag 不参与单实例互斥，托盘在跑时也能自检
            if (args.Length > 0 && args[0] == "--diag") { Diag.Run(); return; }

            // --fixhwinfo：一次性把 \HWiNFO 登录任务指向迁移后的目录（需管理员）
            if (args.Length > 0 && args[0] == "--fixhwinfo")
            {
                string fr = Sys.FixHwinfoTask();
                try { File.WriteAllText(Path.Combine(Cfg.Dir, "fixhwinfo_last.txt"), fr, new UTF8Encoding(false)); }
                catch { }
                return;
            }

            // --fixgate：把已安装的 diskgate.ps1 补上「开机补跑」判定（需管理员）
            if (args.Length > 0 && args[0] == "--fixgate")
            {
                string gr = Sys.FixDiskGateScript();
                try { File.WriteAllText(Path.Combine(Cfg.Dir, "fixgate_last.txt"), gr, new UTF8Encoding(false)); }
                catch { }
                return;
            }

            bool created;
            using (Mutex m = new Mutex(true, "Local\\QuietMode_SingleInstance_v1", out created))
            {
                if (!created)
                {
                    // 不再静默退出：给已有实例发信号，并弹一个看得见的提示
                    try { File.WriteAllText(Path.Combine(Cfg.Dir, "activate.signal"), DateTime.Now.ToString("o"), new UTF8Encoding(false)); }
                    catch { }
                    try
                    {
                        MessageBox.Show(
                            "QuietMode 已经在运行了。\n\n" +
                            "托盘图标如果没看到，是被 Windows 折叠进任务栏的 ^ 溢出面板了 —— 点 ^ 展开，" +
                            "或把图标拖到常显区。\n\n" +
                            "双击托盘图标 = 开/关静音；右键 = 菜单 / 设置。",
                            "QuietMode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch { }
                    return;
                }
                Application.Run(new TrayApp());
            }
        }
    }

    // ------------------------------------------------------------
    //  配置 (INI, UTF-8 no BOM)
    // ------------------------------------------------------------
    internal sealed class Cfg
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuietMode");
        public static readonly string IniPath = Path.Combine(Dir, "settings.ini");
        public static readonly string StatePath = Path.Combine(Dir, "state.ini");
        public static readonly string LogPath = Path.Combine(Dir, "QuietMode.log");
        public static readonly string WorkerPath = Path.Combine(Dir, "worker.ps1");
        public static readonly string ResultPath = Path.Combine(Dir, "worker_result.txt");

        public static bool FanEnabled = true;
        public static string FanIpcCtl = @"<FAN_ROOT>\ipc\IpcCtl.exe";
        public static string FanNormal = @"<FAN_ROOT>\Configurations\FanControl.json";
        public static string FanSilent = @"<FAN_ROOT>\Configurations\FanControl_Silent_Lite.json";
        public static string FanControlExe = @"<FAN_ROOT>\FanControl.exe";
        public static string FanFlag = @"<FAN_ROOT>\silent_active.flag";

        public static bool HddEnabled = true;
        public static string HddDisks = "0,1";
        public static bool HddForce = false;
        public static string HddConf = @"C:\ProgramData\QuietHDD\diskgate.conf";
        public static string HddGate = @"C:\ProgramData\QuietHDD\diskgate.ps1";

        public static bool MaintenanceEnabled = true;

        // 硬盘空闲停转（DISKIDLE）：独立开关，与静音模式完全解耦
        public static bool SpinDownEnabled = false;
        public static int SpinDownMinutes = 20;          // 0 = 永不（关闭）
        public static int SpinDownSeconds { get { return SpinDownMinutes * 60; } }
        public static readonly string SpinScriptPath = Path.Combine(Dir, "spindown.ps1");
        public static readonly string SpinResultPath = Path.Combine(Dir, "spindown_result.txt");

        public static string ScheduleMode = "override";   // override | coexist
        public static bool GateEnabled = true;             // 用户对定时闸门的长期意愿（独立开关）
        public static bool AutoSilent = true;              // 到点自动切风扇档（只动风扇，无需管理员）
        public static string AutoSilentOn = "23:58";
        public static string AutoSilentOff = "10:02";
        public static bool AutoStart = false;
        public static bool RestoreOnBoot = false;

        public static bool ModeOn = false;
        public static bool SchedulePaused = false;
        public static bool AutoSilentActive = false;       // 自动静音当前在夜间窗口内已生效
        public static bool AutoSilentSuppressed = false;   // 本时段被用户手动关掉，别再自动切回
        public static string AutoOffDayStamp = "";         // 当日已自动做过一次「完整关闭静音模式」

        public static readonly string GateScriptPath = Path.Combine(Dir, "gate.ps1");
        public static readonly string GateResultPath = Path.Combine(Dir, "gate_result.txt");

        public static readonly string[] MaintPaths = new string[]
        {
            @"\Microsoft\Windows\Defrag\",
            @"\Microsoft\Windows\DiskCleanup\",
            @"\Microsoft\Windows\Data Integrity Scan\",
            @"\Microsoft\Windows\Data Integrity Scan\",
            @"\Microsoft\Windows\Chkdsk\",
            @"\Microsoft\Windows\Shell\"
        };
        public static readonly string[] MaintNames = new string[]
        {
            "ScheduledDefrag", "SilentCleanup", "Data Integrity Check And Scan",
            "Data Integrity Scan", "ProactiveScan", "IndexerAutomaticMaintenance"
        };
        public static readonly string[] GateTasks = new string[]
        {
            "QuietHDD_NightGate", "QuietHDD_DiskGate"
        };

        private static Dictionary<string, string> ReadIni(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch { return d; }
            foreach (string raw in lines)
            {
                string s = raw.Trim();
                if (s.Length == 0 || s.StartsWith("#") || s.StartsWith(";")) continue;
                int i = s.IndexOf('=');
                if (i <= 0) continue;
                d[s.Substring(0, i).Trim()] = s.Substring(i + 1).Trim();
            }
            return d;
        }

        private static string G(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : def;
        }
        private static bool GB(Dictionary<string, string> d, string k, bool def)
        {
            string v;
            if (!d.TryGetValue(k, out v)) return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        private static int GI(Dictionary<string, string> d, string k, int def)
        {
            string v;
            if (!d.TryGetValue(k, out v)) return def;
            int n;
            return int.TryParse(v, out n) ? n : def;
        }

        public static void Load()
        {
            try { if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir); }
            catch { }

            Dictionary<string, string> d = ReadIni(IniPath);
            FanEnabled = GB(d, "FanEnabled", true);
            FanIpcCtl = G(d, "FanIpcCtl", FanIpcCtl);
            FanNormal = G(d, "FanNormal", FanNormal);
            FanSilent = G(d, "FanSilent", FanSilent);
            FanControlExe = G(d, "FanControlExe", FanControlExe);
            FanFlag = G(d, "FanFlag", FanFlag);
            HddEnabled = GB(d, "HddEnabled", true);
            HddDisks = G(d, "HddDisks", HddDisks);
            HddForce = GB(d, "HddForce", false);
            HddConf = G(d, "HddConf", HddConf);
            HddGate = G(d, "HddGate", HddGate);
            MaintenanceEnabled = GB(d, "MaintenanceEnabled", true);
            SpinDownEnabled = GB(d, "SpinDownEnabled", false);
            // 兼容旧版本：老 ini 只存 SpinDownSeconds（默认 300），按分钟换算
            int legacySec = GI(d, "SpinDownSeconds", 1200);
            SpinDownMinutes = GI(d, "SpinDownMinutes", legacySec / 60);
            if (SpinDownMinutes < 0) SpinDownMinutes = 0;
            if (SpinDownMinutes > 180) SpinDownMinutes = 180;
            ScheduleMode = G(d, "ScheduleMode", "override");
            if (ScheduleMode != "override" && ScheduleMode != "coexist") ScheduleMode = "override";
            GateEnabled = GB(d, "GateEnabled", true);
            AutoStart = GB(d, "AutoStart", false);
            RestoreOnBoot = GB(d, "RestoreOnBoot", false);
            AutoSilent = GB(d, "AutoSilent", true);
            AutoSilentOn = G(d, "AutoSilentOn", "23:58");
            AutoSilentOff = G(d, "AutoSilentOff", "10:02");

            Dictionary<string, string> s = ReadIni(StatePath);
            ModeOn = GB(s, "ModeOn", false);
            SchedulePaused = GB(s, "SchedulePaused", false);
            AutoSilentActive = GB(s, "AutoSilentActive", false);
            AutoSilentSuppressed = GB(s, "AutoSilentSuppressed", false);
            AutoOffDayStamp = G(s, "AutoOffDayStamp", "");

            // 停转开关以系统实际值为准，避免“配置说开、系统其实关”的错位
            int ac = Sys.DiskIdleSeconds();
            if (ac >= 0) SpinDownEnabled = ac > 0;
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                StringBuilder b = new StringBuilder();
                b.AppendLine("# QuietMode settings - edit in the tray Settings dialog");
                b.AppendLine("FanEnabled=" + (FanEnabled ? 1 : 0));
                b.AppendLine("FanIpcCtl=" + FanIpcCtl);
                b.AppendLine("FanNormal=" + FanNormal);
                b.AppendLine("FanSilent=" + FanSilent);
                b.AppendLine("FanControlExe=" + FanControlExe);
                b.AppendLine("FanFlag=" + FanFlag);
                b.AppendLine("HddEnabled=" + (HddEnabled ? 1 : 0));
                b.AppendLine("HddDisks=" + HddDisks);
                b.AppendLine("HddForce=" + (HddForce ? 1 : 0));
                b.AppendLine("HddConf=" + HddConf);
                b.AppendLine("HddGate=" + HddGate);
                b.AppendLine("MaintenanceEnabled=" + (MaintenanceEnabled ? 1 : 0));
                b.AppendLine("SpinDownEnabled=" + (SpinDownEnabled ? 1 : 0));
                b.AppendLine("SpinDownMinutes=" + SpinDownMinutes);
                b.AppendLine("SpinDownSeconds=" + SpinDownSeconds);
                b.AppendLine("ScheduleMode=" + ScheduleMode);
                b.AppendLine("GateEnabled=" + (GateEnabled ? 1 : 0));
                b.AppendLine("AutoSilent=" + (AutoSilent ? 1 : 0));
                b.AppendLine("AutoSilentOn=" + AutoSilentOn);
                b.AppendLine("AutoSilentOff=" + AutoSilentOff);
                b.AppendLine("AutoStart=" + (AutoStart ? 1 : 0));
                b.AppendLine("RestoreOnBoot=" + (RestoreOnBoot ? 1 : 0));
                File.WriteAllText(IniPath, b.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Sys.Log("Save settings failed: " + ex.Message); }
        }

        public static void SaveState()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                StringBuilder b = new StringBuilder();
                b.AppendLine("ModeOn=" + (ModeOn ? 1 : 0));
                b.AppendLine("SchedulePaused=" + (SchedulePaused ? 1 : 0));
                b.AppendLine("AutoSilentActive=" + (AutoSilentActive ? 1 : 0));
                b.AppendLine("AutoSilentSuppressed=" + (AutoSilentSuppressed ? 1 : 0));
                b.AppendLine("AutoOffDayStamp=" + AutoOffDayStamp);
                File.WriteAllText(StatePath, b.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Sys.Log("Save state failed: " + ex.Message); }
        }

        public static int[] Disks()
        {
            List<int> r = new List<int>();
            foreach (string p in HddDisks.Split(','))
            {
                int n;
                if (int.TryParse(p.Trim(), out n)) r.Add(n);
            }
            if (r.Count == 0) r.Add(0);
            return r.ToArray();
        }
    }

    // ------------------------------------------------------------
    //  系统交互
    // ------------------------------------------------------------
    internal static class Sys
    {
        public static void Log(string msg)
        {
            try
            {
                if (!Directory.Exists(Cfg.Dir)) Directory.CreateDirectory(Cfg.Dir);
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + Environment.NewLine;
                File.AppendAllText(Cfg.LogPath, line, new UTF8Encoding(false));
            }
            catch { }
        }

        public static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "TIMEOUT"; }
                return (o + e).Trim();
            }
            catch (Exception ex) { return "ERR:" + ex.Message; }
        }

        public static string FanCurrentConfig()
        {
            string o = Run(Cfg.FanIpcCtl, "list", 8000);
            if (o.StartsWith("ERR:") || o == "TIMEOUT") return null;
            foreach (string line in o.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = line.Trim();
                if (s.StartsWith("current="))
                {
                    // "current=FanControl_Silent_Lite.json folder=<FAN_ROOT>\..." -> 只取文件名
                    string v = s.Substring(8).Trim();
                    int sp = v.IndexOfAny(new char[] { ' ', '\t' });
                    if (sp > 0) v = v.Substring(0, sp);
                    return v.Length > 0 ? v : null;
                }
            }
            return null;
        }

        // 自动静音专用：只切风扇，不碰硬盘/维护任务，所以无需管理员、不弹 UAC
        public static bool SetFanSilent(bool silent)
        {
            try
            {
                string cfg = silent ? Cfg.FanSilent : Cfg.FanNormal;
                if (!File.Exists(cfg)) { Log("SetFanSilent: missing " + cfg); return false; }
                EnsureFanControl();
                string o = Run(Cfg.FanIpcCtl, "load \"" + cfg + "\"", 15000);
                if (o == "TIMEOUT" || o.StartsWith("ERR:")) { Log("SetFanSilent failed: " + o); return false; }
                try
                {
                    if (silent) File.WriteAllText(Cfg.FanFlag, "(active)", Encoding.ASCII);
                    else if (File.Exists(Cfg.FanFlag)) File.Delete(Cfg.FanFlag);
                }
                catch (Exception ex) { Log("SetFanSilent flag: " + ex.Message); }
                Log("SetFanSilent -> " + Path.GetFileName(cfg) + "  out=" + o.Trim());
                return true;
            }
            catch (Exception ex) { Log("SetFanSilent exception: " + ex.Message); return false; }
        }

        public static Dictionary<int, bool> DiskStates(int[] disks)
        {
            Dictionary<int, bool> r = new Dictionary<int, bool>();
            try
            {
                ManagementObjectSearcher s = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT Number,IsOffline FROM MSFT_Disk");
                foreach (ManagementObject o in s.Get())
                {
                    int n = Convert.ToInt32(o["Number"]);
                    bool off = Convert.ToBoolean(o["IsOffline"]);
                    r[n] = off;
                }
            }
            catch (Exception ex) { Log("DiskStates WMI failed: " + ex.Message); return null; }
            return r;
        }

        public static Dictionary<int, string> DriveLetters()
        {
            Dictionary<int, string> r = new Dictionary<int, string>();
            try
            {
                ManagementObjectSearcher s = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT DiskNumber,DriveLetter FROM MSFT_Partition");
                foreach (ManagementObject o in s.Get())
                {
                    object dl = o["DriveLetter"];
                    if (dl == null) continue;
                    int c = Convert.ToInt32(dl);
                    if (c < 65 || c > 90) continue;
                    int n = Convert.ToInt32(o["DiskNumber"]);
                    if (!r.ContainsKey(n)) r[n] = "";
                    if (r[n].Length > 0) r[n] += "/";
                    r[n] += ((char)c) + ":";
                }
            }
            catch (Exception ex) { Log("DriveLetters WMI failed: " + ex.Message); }
            return r;
        }

        public static bool FanControlRunning()
        {
            try { return Process.GetProcessesByName("FanControl").Length > 0; }
            catch { return false; }
        }

        public static void EnsureFanControl()
        {
            if (FanControlRunning()) return;
            try
            {
                if (File.Exists(Cfg.FanControlExe))
                {
                    Process.Start(Cfg.FanControlExe);
                    Log("FanControl.exe started");
                    Thread.Sleep(4500);
                }
                else Log("FanControl.exe not found: " + Cfg.FanControlExe);
            }
            catch (Exception ex) { Log("Start FanControl failed: " + ex.Message); }
        }

        // 生成管理员 worker 脚本 (纯 ASCII, UTF-8 无 BOM)
        public static void BuildWorker(bool on)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("$ErrorActionPreference = 'Continue'");
            b.AppendLine("$r = @()");
            b.AppendLine("$res = '" + Cfg.ResultPath.Replace("'", "''") + "'");

            if (Cfg.HddEnabled)
            {
                b.AppendLine("$conf = '" + Cfg.HddConf.Replace("'", "''") + "'");
                b.AppendLine("$gate = '" + Cfg.HddGate.Replace("'", "''") + "'");
                b.AppendLine("[System.IO.File]::WriteAllLines($conf, @('Disks=" + Cfg.HddDisks +
                             "','Force=" + (Cfg.HddForce ? 1 : 0) + "'), (New-Object System.Text.UTF8Encoding($false)))");
                b.AppendLine("$confBack = (Get-Content -LiteralPath $conf -Encoding UTF8 | Where-Object { $_ -match '^Disks=' } | Select-Object -First 1)");
                b.AppendLine("$r += ('HDD_CONF=' + $confBack)");
                string sw = on ? "-ForceNight" : "-ForceDay";
                b.AppendLine("try { & $gate " + sw + "; $r += 'HDD=" + (on ? "OFFLINE_REQUESTED" : "ONLINE_REQUESTED") +
                             "' } catch { $r += ('HDD=ERR:' + $_.Exception.Message) }");
            }

            if (Cfg.MaintenanceEnabled)
            {
                b.AppendLine("$tasks = @(");
                for (int i = 0; i < Cfg.MaintPaths.Length; i++)
                {
                    string comma = (i < Cfg.MaintPaths.Length - 1) ? "," : "";
                    b.AppendLine("  @('" + Cfg.MaintPaths[i] + "','" + Cfg.MaintNames[i] + "')" + comma);
                }
                b.AppendLine(")");
                if (on)
                {
                    b.AppendLine("foreach($p in $tasks){ try { Stop-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue; Disable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction Stop | Out-Null; $r += ('MT=OFF:' + $p[1]) } catch { $r += ('MT=ERR:' + $p[1] + ':' + $_.Exception.Message) } }");
                }
                else
                {
                    b.AppendLine("foreach($p in $tasks){ try { Enable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction Stop | Out-Null; $r += ('MT=ON:' + $p[1]) } catch { $r += ('MT=ERR:' + $p[1] + ':' + $_.Exception.Message) } }");
                }
            }

            if (Cfg.ScheduleMode == "override")
            {
                // 最终要不要跑 = 用户长期意愿(Cfg.GateEnabled) 且 当前不是静音模式接管
                bool gateWant = Cfg.GateEnabled && !on;
                string verb = gateWant ? "Enable" : "Disable";
                string tag = gateWant ? "RESUMED" : "PAUSED";
                b.AppendLine("foreach($tn in @('" + Cfg.GateTasks[0] + "','" + Cfg.GateTasks[1] + "')){");
                b.AppendLine("  try { " + verb + "-ScheduledTask -TaskPath '\\QuietHDD\\' -TaskName $tn -ErrorAction Stop | Out-Null; $r += ('GATE=" + tag + ":' + $tn) }");
                b.AppendLine("  catch { $r += ('GATE=ERR:' + $tn + ':' + $_.Exception.Message) }");
                b.AppendLine("}");
            }

            // 硬盘空闲停转不在这里做：它是独立开关，走 Sys.ApplyDiskIdle 自己的提权脚本，
            // 开关静音模式绝不改动 DISKIDLE 值。

            b.AppendLine("[System.IO.File]::WriteAllText($res, ($r -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))");
            try { File.WriteAllText(Cfg.WorkerPath, b.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Log("BuildWorker write failed: " + ex.Message); }
        }

        public static string RunWorker()
        {
            try { if (File.Exists(Cfg.ResultPath)) File.Delete(Cfg.ResultPath); }
            catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Cfg.WorkerPath + "\"";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(180000))
                {
                    try { p.Kill(); } catch { }
                    return "WORKER_TIMEOUT";
                }
            }
            catch (Exception ex)
            {
                Log("RunWorker failed: " + ex.Message);
                return "UAC_CANCELLED_OR_ERR:" + ex.Message;
            }
            try
            {
                if (!File.Exists(Cfg.ResultPath)) return "NO_RESULT_FILE";
                return File.ReadAllText(Cfg.ResultPath, Encoding.UTF8).Trim();
            }
            catch (Exception ex) { return "READ_RESULT_ERR:" + ex.Message; }
        }

        // ------------------------------------------------------------
        //  硬盘空闲停转（DISKIDLE）——独立模块
        //  读取：直接查当前活动电源方案的注册表值（无需管理员、不用起子进程）
        //  写入：powercfg + 一次性 UAC
        // ------------------------------------------------------------
        private const string SubDiskGuid = @"0012ee47-9041-4b5d-9b77-535fba8b1442";
        private const string DiskIdleGuid = @"6738e2c4-e8a5-4a42-b16a-e040e769756e";

        public static int DiskIdleSeconds()
        {
            try
            {
                using (RegistryKey ps = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", false))
                {
                    if (ps == null) return -1;
                    string active = ps.GetValue("ActivePowerScheme") as string;
                    if (string.IsNullOrEmpty(active)) return -1;
                    using (RegistryKey k = ps.OpenSubKey(active + "\\" + SubDiskGuid + "\\" + DiskIdleGuid, false))
                    {
                        if (k == null) return -1;
                        object v = k.GetValue("ACSettingIndex");
                        if (v == null) return -1;
                        return Convert.ToInt32(v);
                    }
                }
            }
            catch (Exception ex) { Log("DiskIdleSeconds failed: " + ex.Message); return -1; }
        }

        // 纯 ASCII 的提权脚本：设 AC/DC 的 DISKIDLE，然后回读校验
        public static void BuildSpinScript()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("param([int]$Minutes = 20)");
            b.AppendLine("$ErrorActionPreference = 'Continue'");
            b.AppendLine("$res = '" + Cfg.SpinResultPath.Replace("'", "''") + "'");
            b.AppendLine("$r = @()");
            b.AppendLine("$sec = [int]$Minutes * 60");
            b.AppendLine("if ($sec -lt 0) { $sec = 0 }");
            b.AppendLine("$r += ('SPIN_WANT=' + $sec)");
            b.AppendLine("try {");
            b.AppendLine("  & powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec | Out-Null");
            b.AppendLine("  & powercfg /setdcvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec | Out-Null");
            b.AppendLine("  & powercfg /s SCHEME_CURRENT | Out-Null");
            b.AppendLine("  Start-Sleep -Milliseconds 400");
            b.AppendLine("} catch { $r += ('SPIN=ERR:' + $_.Exception.Message) }");
            b.AppendLine("try {");
            b.AppendLine("  $ps = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Power\\User\\PowerSchemes'");
            b.AppendLine("  $active = (Get-ItemProperty -Path $ps -ErrorAction Stop).ActivePowerScheme");
            b.AppendLine("  $k = $ps + '\\' + $active + '\\" + SubDiskGuid + "\\" + DiskIdleGuid + "'");
            b.AppendLine("  $ac = [int](Get-ItemProperty -Path $k -Name ACSettingIndex -ErrorAction Stop).ACSettingIndex");
            b.AppendLine("  $dc = [int](Get-ItemProperty -Path $k -Name DCSettingIndex -ErrorAction Stop).DCSettingIndex");
            b.AppendLine("  $r += ('SPIN_AC=' + $ac)");
            b.AppendLine("  $r += ('SPIN_DC=' + $dc)");
            b.AppendLine("  if ($ac -eq $sec) { $r += 'SPIN=OK' } else { $r += 'SPIN=MISMATCH' }");
            b.AppendLine("} catch { $r += ('SPIN_READ_ERR=' + $_.Exception.Message) }");
            b.AppendLine("[System.IO.File]::WriteAllText($res, ($r -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))");
            try { File.WriteAllText(Cfg.SpinScriptPath, b.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Log("BuildSpinScript write failed: " + ex.Message); }
        }

        // minutes: 0 = 关闭（永不）；>0 = 空闲这么多分钟后停转
        public static string ApplyDiskIdle(int minutes)
        {
            try
            {
                BuildSpinScript();
                try { if (File.Exists(Cfg.SpinResultPath)) File.Delete(Cfg.SpinResultPath); }
                catch { }
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                                Cfg.SpinScriptPath + "\" -Minutes " + minutes;
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(90000))
                {
                    try { p.Kill(); } catch { }
                    return "TIMEOUT";
                }
                if (!File.Exists(Cfg.SpinResultPath)) return "NO_RESULT";
                string r = File.ReadAllText(Cfg.SpinResultPath, Encoding.UTF8).Trim();
                Log("ApplyDiskIdle(" + minutes + "m) => " + r.Replace("\r", " ").Replace("\n", " | "));
                return r;
            }
            catch (Exception ex)
            {
                Log("ApplyDiskIdle failed: " + ex.Message);
                return "UAC_CANCELLED_OR_ERR:" + ex.Message;
            }
        }

        // ------------------------------------------------------------
        //  定时闸门启停（独立开关，需管理员）
        // ------------------------------------------------------------
        public static void BuildGateScript()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("param([int]$Enable = 1, [int]$Online = 0, [string]$Disks = '')");
            b.AppendLine("$ErrorActionPreference = 'Continue'");
            b.AppendLine("$res = '" + Cfg.GateResultPath.Replace("'", "''") + "'");
            b.AppendLine("$r = @()");
            b.AppendLine("$r += ('GATE_WANT=' + $Enable)");
            b.AppendLine("foreach ($tn in @('" + Cfg.GateTasks[0] + "','" + Cfg.GateTasks[1] + "')) {");
            b.AppendLine("  try {");
            b.AppendLine("    $t = Get-ScheduledTask -TaskPath '\\QuietHDD\\' -TaskName $tn -ErrorAction Stop");
            b.AppendLine("    if ($Enable -eq 1) { Enable-ScheduledTask -InputObject $t -ErrorAction Stop | Out-Null }");
            b.AppendLine("    else { Disable-ScheduledTask -InputObject $t -ErrorAction Stop | Out-Null }");
            b.AppendLine("    $t2 = Get-ScheduledTask -TaskPath '\\QuietHDD\\' -TaskName $tn -ErrorAction Stop");
            b.AppendLine("    $r += ('GATE=' + $tn + ':' + $t2.State)");
            b.AppendLine("  } catch { $r += ('GATE_ERR=' + $tn + ':' + $_.Exception.Message) }");
            b.AppendLine("}");
            b.AppendLine("if ($Online -eq 1 -and $Disks -ne '') {");
            b.AppendLine("  foreach ($d in $Disks.Split(',')) {");
            b.AppendLine("    $n = 0");
            b.AppendLine("    if ([int]::TryParse($d.Trim(), [ref]$n)) {");
            b.AppendLine("      try {");
            b.AppendLine("        $dk = Get-Disk -Number $n -ErrorAction Stop");
            b.AppendLine("        if ($dk.IsOffline) { Set-Disk -Number $n -IsOffline $false -ErrorAction Stop }");
            b.AppendLine("        $r += ('DISK_ONLINE=' + $n)");
            b.AppendLine("      } catch { $r += ('DISK_ERR=' + $n + ':' + $_.Exception.Message) }");
            b.AppendLine("    }");
            b.AppendLine("  }");
            b.AppendLine("}");
            b.AppendLine("[System.IO.File]::WriteAllText($res, ($r -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))");
            try { File.WriteAllText(Cfg.GateScriptPath, b.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Log("BuildGateScript write failed: " + ex.Message); }
        }

        // enable=false 时顺便把 conf 里的盘联机（否则关了闸门盘还是脱机的，反直觉）
        public static string ApplyGate(bool enable, bool bringOnline)
        {
            try
            {
                BuildGateScript();
                try { if (File.Exists(Cfg.GateResultPath)) File.Delete(Cfg.GateResultPath); }
                catch { }
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                                Cfg.GateScriptPath + "\" -Enable " + (enable ? 1 : 0) +
                                " -Online " + ((!enable && bringOnline) ? 1 : 0) +
                                " -Disks \"" + Cfg.HddDisks + "\"";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(180000))
                {
                    try { p.Kill(); } catch { }
                    return "TIMEOUT";
                }
                if (!File.Exists(Cfg.GateResultPath)) return "NO_RESULT";
                string r = File.ReadAllText(Cfg.GateResultPath, Encoding.UTF8).Trim();
                Log("ApplyGate(" + (enable ? "on" : "off") + ",online=" + bringOnline + ") => " +
                    r.Replace("\r", " ").Replace("\n", " | "));
                return r;
            }
            catch (Exception ex)
            {
                Log("ApplyGate failed: " + ex.Message);
                return "UAC_CANCELLED_OR_ERR:" + ex.Message;
            }
        }

        public static bool GateResultOk(string r, bool enable)
        {
            if (string.IsNullOrEmpty(r)) return false;
            if (r.IndexOf("GATE_ERR=", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            int n = 0;
            foreach (string raw in r.Split('\n'))
            {
                string s = raw.Trim();
                if (!s.StartsWith("GATE=", StringComparison.OrdinalIgnoreCase)) continue;
                n++;
                bool disabled = s.EndsWith(":Disabled", StringComparison.OrdinalIgnoreCase);
                if (enable && disabled) return false;
                if (!enable && !disabled) return false;
            }
            return n >= 2;
        }

        // 一次性维护：把 \HWiNFO 登录任务指向迁移后的 <TOOLS>\HWiNFO64（需要管理员）
        public static string FixHwinfoTask()
        {
            string script = Path.Combine(Cfg.Dir, "fixhwinfo.ps1");
            string result = Path.Combine(Cfg.Dir, "fixhwinfo_result.txt");
            StringBuilder b = new StringBuilder();
            b.AppendLine("$ErrorActionPreference = 'Stop'");
            b.AppendLine("$out = '" + result.Replace("'", "''") + "'");
            b.AppendLine("$r = @()");
            b.AppendLine("try {");
            b.AppendLine("  $exe = '<TOOLS>\\HWiNFO64\\HWiNFO64Launcher.exe'");
            b.AppendLine("  $wd = '<TOOLS>\\HWiNFO64'");
            b.AppendLine("  if (-not (Test-Path -LiteralPath $exe)) { throw 'target exe missing' }");
            b.AppendLine("  $a = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $wd");
            b.AppendLine("  $t = Set-ScheduledTask -TaskPath '\\' -TaskName 'HWiNFO' -Action $a -ErrorAction Stop");
            b.AppendLine("  $r += ('SET=OK state=' + $t.State)");
            b.AppendLine("  $v = Get-ScheduledTask -TaskPath '\\' -TaskName 'HWiNFO' -ErrorAction Stop");
            b.AppendLine("  foreach ($x in $v.Actions) { $r += ('EXEC=' + $x.Execute); $r += ('WORKDIR=' + $x.WorkingDirectory) }");
            b.AppendLine("} catch { $r += ('ERR=' + $_.Exception.Message) }");
            b.AppendLine("[System.IO.File]::WriteAllText($out, ($r -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))");
            try { File.WriteAllText(script, b.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { return "SCRIPT_WRITE_ERR:" + ex.Message; }
            try { if (File.Exists(result)) File.Delete(result); } catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return "TIMEOUT"; }
                if (!File.Exists(result)) return "NO_RESULT";
                string r = File.ReadAllText(result, Encoding.UTF8).Trim();
                Log("FixHwinfoTask => " + r.Replace("\r", " ").Replace("\n", " | "));
                return r;
            }
            catch (Exception ex) { Log("FixHwinfoTask failed: " + ex.Message); return "UAC_CANCELLED_OR_ERR:" + ex.Message; }
        }

        // 一次性维护：给已安装的 diskgate.ps1 补上「开机补跑」判定（需要管理员）
        public static string FixDiskGateScript()
        {
            string script = Path.Combine(Cfg.Dir, "fixgate.ps1");
            string result = Path.Combine(Cfg.Dir, "fixgate_result.txt");
            StringBuilder b = new StringBuilder();
            b.AppendLine("$ErrorActionPreference = 'Stop'");
            b.AppendLine("$out = '" + result.Replace("'", "''") + "'");
            b.AppendLine("$f = 'C:\\ProgramData\\QuietHDD\\diskgate.ps1'");
            b.AppendLine("$bak = 'C:\\ProgramData\\QuietHDD\\diskgate.ps1.bak'");
            b.AppendLine("$enc = New-Object System.Text.UTF8Encoding($false)");
            b.AppendLine("$r = @()");
            b.AppendLine("try {");
            b.AppendLine("  $txt = [System.IO.File]::ReadAllText($f, $enc).Replace(\"`r`n\", \"`n\")");
            b.AppendLine("  if (-not (Test-Path -LiteralPath $bak)) { [System.IO.File]::WriteAllText($bak, $txt, $enc); $r += 'BACKUP=OK' }");
            b.AppendLine("  else { $r += 'BACKUP=EXISTS' }");
            b.AppendLine("  $old = @'");
            b.AppendLine("$h = (Get-Date).Hour");
            b.AppendLine("$night = if ($ForceNight) { $true } elseif ($ForceDay) { $false } else { ($h -ge 23 -or $h -lt 10) }");
            b.AppendLine("$msg = @('mode=' + $(if ($night) { 'NIGHT' } else { 'DAY' }))");
            b.AppendLine("'@");
            b.AppendLine("  $new = @'");
            b.AppendLine("$h = (Get-Date).Hour");
            b.AppendLine("# Startup re-run: booted <15 min ago -> 06:00-10:01 counts as DAY (online).");
            b.AppendLine("# Unattended reboot 00:00-05:59 still NIGHT, disks stay quiet.");
            b.AppendLine("$dayStartHour = 10");
            b.AppendLine("try {");
            b.AppendLine("  $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop");
            b.AppendLine("  if ($os -and $os.LastBootUpTime) {");
            b.AppendLine("    $ageMin = ((Get-Date) - $os.LastBootUpTime).TotalMinutes");
            b.AppendLine("    if ($ageMin -ge 0 -and $ageMin -lt 15) { $dayStartHour = 6 }");
            b.AppendLine("  }");
            b.AppendLine("} catch { }");
            b.AppendLine("$night = if ($ForceNight) { $true } elseif ($ForceDay) { $false } else { ($h -ge 23 -or $h -lt $dayStartHour) }");
            b.AppendLine("$msg = @('mode=' + $(if ($night) { 'NIGHT' } else { 'DAY' }) + $(if ($dayStartHour -eq 6) { '(startup)' } else { '' }))");
            b.AppendLine("'@");
            b.AppendLine("  $oldN = $old.Replace(\"`r`n\", \"`n\").TrimEnd(\"`n\")");
            b.AppendLine("  $newN = $new.Replace(\"`r`n\", \"`n\").TrimEnd(\"`n\")");
            b.AppendLine("  if ($txt.Contains($oldN)) { $txt = $txt.Replace($oldN, $newN); $r += 'MATCH=YES WRITTEN' }");
            b.AppendLine("  else { $r += ('MATCH=' + $(if ($txt.Contains('dayStartHour')) { 'ALREADY_PATCHED' } else { 'NO' })) }");
            // 保持 $msg 行与 foreach 之间有换行，避免 ))foreach 粘连
            b.AppendLine("  $txt = $txt.Replace('))foreach', \"))`n`nforeach\")");
            b.AppendLine("  [System.IO.File]::WriteAllText($f, $txt, $enc)");
            b.AppendLine("  $err = $null");
            b.AppendLine("  [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$err) | Out-Null");
            b.AppendLine("  $r += ('PARSE_ERRORS=' + $(if ($err) { $err.Count } else { 0 }))");
            b.AppendLine("  $r += ('HAS_STARTUP_LOGIC=' + [System.IO.File]::ReadAllText($f, $enc).Contains('dayStartHour'))");
            b.AppendLine("} catch { $r += ('ERR=' + $_.Exception.Message) }");
            b.AppendLine("[System.IO.File]::WriteAllText($out, ($r -join [Environment]::NewLine), $enc)");
            try { File.WriteAllText(script, b.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { return "SCRIPT_WRITE_ERR:" + ex.Message; }
            try { if (File.Exists(result)) File.Delete(result); } catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return "TIMEOUT"; }
                if (!File.Exists(result)) return "NO_RESULT";
                string r = File.ReadAllText(result, Encoding.UTF8).Trim();
                Log("FixDiskGateScript => " + r.Replace("\r", " ").Replace("\n", " | "));
                return r;
            }
            catch (Exception ex) { Log("FixDiskGateScript failed: " + ex.Message); return "UAC_CANCELLED_OR_ERR:" + ex.Message; }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue("QuietMode", "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else k.DeleteValue("QuietMode", false);
                }
            }
            catch (Exception ex) { Log("SetAutoStart failed: " + ex.Message); }
        }

        // Win11 默认把新注册的托盘图标折叠进 ^ 溢出面板，用户就“看不到”。
        // explorer 会为本程序在 NotifyIconSettings 下建一条记录，把 IsPromoted 置 1 即常显（HKCU 无需管理员）。
        public static void EnsureTrayPromoted()
        {
            try
            {
                string self = Application.ExecutablePath;
                using (RegistryKey b = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (b == null) return;
                    foreach (string sub in b.GetSubKeyNames())
                    {
                        using (RegistryKey k = b.OpenSubKey(sub, true))
                        {
                            if (k == null) continue;
                            object p = k.GetValue("ExecutablePath");
                            if (p == null) continue;
                            if (!string.Equals(p.ToString(), self, StringComparison.OrdinalIgnoreCase)) continue;
                            object v = k.GetValue("IsPromoted");
                            bool ok = false;
                            try { ok = (v != null && Convert.ToInt32(v) == 1); } catch { ok = false; }
                            if (!ok)
                            {
                                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                Log("IsPromoted=1 set on " + sub);
                            }
                            return;
                        }
                    }
                    Log("NotifyIconSettings entry for self not found yet");
                }
            }
            catch (Exception ex) { Log("EnsureTrayPromoted failed: " + ex.Message); }
        }

        public static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (k == null) return false;
                    object v = k.GetValue("QuietMode");
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------
    //  --diag 自检：不改动系统，只读取状态并验证提权脚本语法
    // ------------------------------------------------------------
    internal static class Diag
    {
        public static void Run()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("QuietMode diagnostics " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            b.AppendLine("Exe       : " + Application.ExecutablePath);
            b.AppendLine("DataDir   : " + Cfg.Dir);
            b.AppendLine("IniExists : " + File.Exists(Cfg.IniPath));
            b.AppendLine("Modules   : Fan=" + Cfg.FanEnabled + " Hdd=" + Cfg.HddEnabled +
                         " Maint=" + Cfg.MaintenanceEnabled + " Spin=" + Cfg.SpinDownEnabled +
                         " (" + Cfg.SpinDownMinutes + " min)");
            b.AppendLine("DiskIdle  : system=" + Sys.DiskIdleSeconds() + " sec  conf=" + Cfg.SpinDownSeconds + " sec");
            b.AppendLine("Schedule  : " + Cfg.ScheduleMode + "  Disks=" + Cfg.HddDisks + "  Force=" + Cfg.HddForce);
            b.AppendLine("State     : ModeOn=" + Cfg.ModeOn + " GateEnabled=" + Cfg.GateEnabled +
                         " AutoActive=" + Cfg.AutoSilentActive + " AutoSuppressed=" + Cfg.AutoSilentSuppressed +
                         " SchedulePaused=" + Cfg.SchedulePaused + " AutoOffDayStamp=" + Cfg.AutoOffDayStamp);
            DateTime bn = DateTime.Now;
            int bm = bn.Hour * 60 + bn.Minute;
            int bon = 1438;
            try { string[] bp = Cfg.AutoSilentOn.Split(':'); bon = int.Parse(bp[0]) * 60 + int.Parse(bp[1]); }
            catch { }
            bool bNight = (((bm - bon) % 1440) + 1440) % 1440 < 45;
            Dictionary<int, bool> bds = Sys.DiskStates(Cfg.Disks());
            bool bOff = (bds == null);
            if (!bOff)
                foreach (int n in Cfg.Disks()) { bool v; if (!bds.TryGetValue(n, out v) || v) { bOff = true; break; } }
            b.AppendLine("BootReconcile : now=" + bn.ToString("HH:mm") + " inNightWin=" + bNight +
                         " ModeOn=" + Cfg.ModeOn + " anyDiskOffline=" + bOff +
                         " wouldRun=" + (!Cfg.RestoreOnBoot && !bNight && (Cfg.ModeOn || bOff)));
            b.AppendLine("AutoStart : registry=" + Sys.IsAutoStart() + " config=" + Cfg.AutoStart);
            b.AppendLine("FanCtlRun : " + Sys.FanControlRunning());
            b.AppendLine("FanConfig : " + (Sys.FanCurrentConfig() ?? "NULL"));
            Dictionary<int, bool> ds = Sys.DiskStates(Cfg.Disks());
            if (ds == null) b.AppendLine("DiskStates: WMI FAILED");
            else
            {
                foreach (int n in Cfg.Disks())
                {
                    bool v;
                    b.AppendLine("Disk " + n + " IsOffline=" + (ds.TryGetValue(n, out v) ? v.ToString() : "NOT_FOUND"));
                }
            }
            Dictionary<int, string> ls = Sys.DriveLetters();
            foreach (int n in Cfg.Disks())
                b.AppendLine("Disk " + n + " letters=" + (ls.ContainsKey(n) ? ls[n] : "?"));

            string onPath = Path.Combine(Cfg.Dir, "diag_on.ps1");
            string offPath = Path.Combine(Cfg.Dir, "diag_off.ps1");
            string spinPath = Path.Combine(Cfg.Dir, "diag_spin.ps1");
            string gatePath = Path.Combine(Cfg.Dir, "diag_gate.ps1");
            string synPath = Path.Combine(Cfg.Dir, "diag_syntax.txt");
            string checkPath = Path.Combine(Cfg.Dir, "diag_check.ps1");

            Sys.BuildSpinScript();
            string spin = "";
            try { spin = File.ReadAllText(Cfg.SpinScriptPath, Encoding.UTF8); }
            catch (Exception ex) { spin = "READ_FAIL: " + ex.Message; }
            File.WriteAllText(spinPath, spin, new UTF8Encoding(false));

            Sys.BuildGateScript();
            string gscr = "";
            try { gscr = File.ReadAllText(Cfg.GateScriptPath, Encoding.UTF8); }
            catch (Exception ex) { gscr = "READ_FAIL: " + ex.Message; }
            File.WriteAllText(gatePath, gscr, new UTF8Encoding(false));

            Sys.BuildWorker(true);
            string on = File.ReadAllText(Cfg.WorkerPath, Encoding.UTF8);
            File.WriteAllText(onPath, on, new UTF8Encoding(false));
            Sys.BuildWorker(false);
            string off = File.ReadAllText(Cfg.WorkerPath, Encoding.UTF8);
            File.WriteAllText(offPath, off, new UTF8Encoding(false));
            Sys.BuildWorker(true);   // leave a consistent worker in place

            StringBuilder c = new StringBuilder();
            c.AppendLine("$ErrorActionPreference = 'Continue'");
            c.AppendLine("$r = @()");
            c.AppendLine("$files = @('" + onPath.Replace("'", "''") + "','" + offPath.Replace("'", "''") +
                         "','" + spinPath.Replace("'", "''") + "','" + gatePath.Replace("'", "''") + "')");
            c.AppendLine("foreach($f in $files){");
            c.AppendLine("  $t=$null;$e=$null");
            c.AppendLine("  [System.Management.Automation.Language.Parser]::ParseFile($f,[ref]$t,[ref]$e) | Out-Null");
            c.AppendLine("  $r += ((Split-Path $f -Leaf) + ' ParseErrors=' + $e.Count)");
            c.AppendLine("  foreach($x in $e){ $r += ('  line ' + $x.Extent.StartLineNumber + ': ' + $x.Message) }");
            c.AppendLine("}");
            c.AppendLine("[System.IO.File]::WriteAllLines('" + synPath.Replace("'", "''") +
                         "', $r, (New-Object System.Text.UTF8Encoding($false)))");
            File.WriteAllText(checkPath, c.ToString(), new UTF8Encoding(false));

            string po = Sys.Run("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + checkPath + "\"", 30000);
            b.AppendLine("SyntaxChecker stdout: " + po);
            if (File.Exists(synPath))
                b.AppendLine("=== worker syntax ===\n" + File.ReadAllText(synPath, Encoding.UTF8));
            else b.AppendLine("=== worker syntax === NO RESULT FILE");

            b.AppendLine("=== worker(ON) ===\n" + on);
            b.AppendLine("=== worker(OFF) ===\n" + off);
            b.AppendLine("=== spindown.ps1 ===\n" + spin);
            b.AppendLine("=== gate.ps1 ===\n" + gscr);

            string outPath = Path.Combine(Cfg.Dir, "diag.txt");
            File.WriteAllText(outPath, b.ToString(), new UTF8Encoding(false));
            Sys.Log("diag written to " + outPath);
        }
    }

    // ------------------------------------------------------------
    //  托盘图标绘制
    // ------------------------------------------------------------
    internal static class Icons
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public enum Kind { Quiet, Normal, Partial, Error }

        public static Icon Make(Kind k)
        {
            Bitmap bmp = new Bitmap(32, 32);
            Color bg;
            switch (k)
            {
                case Kind.Quiet: bg = Color.FromArgb(38, 148, 96); break;
                case Kind.Normal: bg = Color.FromArgb(70, 105, 150); break;
                case Kind.Partial: bg = Color.FromArgb(214, 145, 40); break;
                default: bg = Color.FromArgb(190, 60, 55); break;
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush br = new SolidBrush(bg)) g.FillEllipse(br, 1, 1, 29, 29);
                using (SolidBrush wh = new SolidBrush(Color.White))
                {
                    if (k == Kind.Quiet)
                    {
                        // crescent moon
                        g.FillEllipse(wh, 8, 6, 19, 19);
                        using (SolidBrush cut = new SolidBrush(bg)) g.FillEllipse(cut, 14, 3, 19, 19);
                    }
                    else if (k == Kind.Normal)
                    {
                        // three-blade fan
                        GraphicsState st = g.Save();
                        g.TranslateTransform(16, 16);
                        for (int i = 0; i < 3; i++)
                        {
                            g.RotateTransform(120);
                            g.FillEllipse(wh, 1, -3.2f, 12, 6.4f);
                        }
                        g.Restore(st);
                        g.FillEllipse(wh, 13.5f, 13.5f, 5, 5);
                    }
                    else
                    {
                        // exclamation
                        g.FillRectangle(wh, 14, 8, 4, 12);
                        g.FillEllipse(wh, 14, 22, 4, 4);
                    }
                }
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            bmp.Dispose();
            return ic;
        }
    }

    // ------------------------------------------------------------
    //  极简数字输入框（WinForms 无内置 InputBox）
    // ------------------------------------------------------------
    internal static class Prompt
    {
        public static int Number(string title, string text, int value, int min, int max, string suffix)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = true;
                f.TopMost = true;
                f.ClientSize = new Size(396, 156);
                f.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

                Label l = new Label();
                l.Text = text;
                l.Location = new Point(14, 14);
                l.Size = new Size(366, 40);
                f.Controls.Add(l);

                NumericUpDown n = new NumericUpDown();
                n.Minimum = min;
                n.Maximum = max;
                n.Value = Math.Max(min, Math.Min(max, value));
                n.Location = new Point(14, 62);
                n.Size = new Size(92, 23);
                f.Controls.Add(n);

                Label s = new Label();
                s.Text = suffix;
                s.Location = new Point(112, 64);
                s.Size = new Size(70, 20);
                f.Controls.Add(s);

                Label hint = new Label();
                hint.Text = "范围 " + min + " - " + max;
                hint.ForeColor = Color.Gray;
                hint.Location = new Point(190, 64);
                hint.Size = new Size(120, 20);
                f.Controls.Add(hint);

                Button ok = new Button();
                ok.Text = "确定";
                ok.Location = new Point(202, 108);
                ok.Size = new Size(84, 28);
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.Location = new Point(294, 108);
                cancel.Size = new Size(84, 28);
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(cancel);
                f.CancelButton = cancel;

                if (f.ShowDialog() != DialogResult.OK) return -1;
                return (int)n.Value;
            }
        }
    }

    // ------------------------------------------------------------
    //  托盘应用
    // ------------------------------------------------------------
    internal sealed class TrayApp : ApplicationContext
    {
        private NotifyIcon ni;
        private ContextMenuStrip menu;
        private ToolStripMenuItem[] st = new ToolStripMenuItem[4];
        private ToolStripMenuItem toggle, reapply;
        private ToolStripMenuItem spinToggle, spinMinutesItem;
        private ToolStripMenuItem gateToggle;
        private System.Windows.Forms.Timer timer;
        private System.Windows.Forms.Timer signalTimer;
        private System.Windows.Forms.Timer promoteTimer;
        // 专用作跨线程 Invoke 的靶子；主线程构造时强制建句柄
        private Control uiHost = new Control();
        private bool busy = false;
        private string fanName = null;
        private int spinSec = -1;                 // 系统当前 DISKIDLE（秒），-1 = 读取失败，0 = 永不
        private Dictionary<int, bool> diskStates = null;
        private Dictionary<int, string> letters = null;
        private string lastError = "";
        // 必须可为 null：若初值恰好等于首帧算出的状态，RefreshAll 会跳过赋值，
        // NotifyIcon 一直没有 Icon 时根本不向任务栏注册，托盘与溢出区都看不到。
        private Icons.Kind? lastIcon = null;

        public TrayApp()
        {
            // 访问 Handle 会强制创建窗口句柄，之后 BeginInvoke 才能跨线程投递
            IntPtr force = uiHost.Handle;
            GC.KeepAlive(force);
            BuildMenu();
            ni = new NotifyIcon();
            ni.Text = "QuietMode";
            ni.ContextMenuStrip = menu;
            // 顺序要紧：先给 Icon 再 Visible，否则图标可能不注册
            try { ni.Icon = Icons.Make(Icons.Kind.Normal); }
            catch (Exception ex)
            {
                Sys.Log("initial icon failed: " + ex.Message);
                try { ni.Icon = (Icon)SystemIcons.Application.Clone(); } catch { }
            }
            ni.Visible = true;
            ni.DoubleClick += delegate(object s, EventArgs e) { Toggle(); };
            ni.BalloonTipClicked += delegate(object s, EventArgs e) { };

            ApplyAutoStartFromConfig();
            RefreshAll(true);

            // 启动可见反馈：托盘图标常被 Windows 折叠进 ^ 溢出区，不提示就像“没反应”
            Ui(delegate()
            {
                ni.ShowBalloonTip(7000, "QuietMode 已启动",
                    "托盘图标若没看到，是被折叠进任务栏的 ^ 溢出面板了 —— 点 ^ 展开，或把图标拖到常显区。\n" +
                    "双击图标 = 开/关静音，右键 = 菜单 / 设置。",
                    ToolTipIcon.Info);
            });

            if (Cfg.RestoreOnBoot && Cfg.ModeOn)
            {
                Thread t = new Thread(delegate() { Thread.Sleep(2500); Apply(Cfg.ModeOn, true); });
                t.IsBackground = true;
                t.Start();
            }
            else
            {
                // 开机对账：静音模式残留 -> 完整关闭；静音模式已关但盘还脱机 -> 完整恢复白天状态
                BootReconcile();
            }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 60000;
            timer.Tick += delegate(object s, EventArgs e) { CheckAutoSilent(); RefreshAll(false); };
            timer.Start();

            // 启动时若正处在时段窗口内（如刚过 23:58 开机），立即并行一次
            Thread bootAuto = new Thread(delegate()
            {
                Thread.Sleep(4000);
                try { CheckAutoSilent(); } catch (Exception ex) { Sys.Log("boot AutoSilent: " + ex.Message); }
            });
            bootAuto.IsBackground = true;
            bootAuto.Start();

            // 第二个实例双击时写 activate.signal，这里立刻弹气泡回应
            signalTimer = new System.Windows.Forms.Timer();
            signalTimer.Interval = 1200;
            signalTimer.Tick += delegate(object s, EventArgs e)
            {
                string sig = Path.Combine(Cfg.Dir, "activate.signal");
                if (!File.Exists(sig)) return;
                try { File.Delete(sig); } catch { }
                try { ni.ShowBalloonTip(4000, "QuietMode", "我已经在运行了，托盘图标在这里（可能被折叠进 ^）。", ToolTipIcon.Info); }
                catch { }
            };
            signalTimer.Start();

            // 图标注册后 explorer 才会建条目，延时几秒再尝试“常显”
            promoteTimer = new System.Windows.Forms.Timer();
            promoteTimer.Interval = 3000;
            promoteTimer.Tick += delegate(object s, EventArgs e)
            {
                promoteTimer.Stop();
                Sys.EnsureTrayPromoted();
            };
            promoteTimer.Start();

            // 首次运行弹一次可见提示：气泡会被「专注助手」吞掉，托盘图标又常被折叠，
            // 不弹这一下就会像“双击没反应”。
            string seen = Path.Combine(Cfg.Dir, "tip.done");
            if (!File.Exists(seen))
            {
                try { File.WriteAllText(seen, DateTime.Now.ToString("o")); } catch { }
                Ui(delegate()
                {
                    try
                    {
                        MessageBox.Show(
                            "QuietMode 已在托盘运行。\n\n" +
                            "· 图标位置：任务栏右下角。没看到就点 ^ 展开溢出面板，\n" +
                            "  或用右键菜单里的「托盘图标被隐藏？打开任务栏设置…」把它拖出来。\n" +
                            "· 双击图标 = 开 / 关静音\n" +
                            "· 右键图标 = 菜单（实时状态 / 设置 / 开机自启 / 退出）\n\n" +
                            "本提示只出现一次。",
                            "QuietMode 已启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex) { Sys.Log("first-run tip failed: " + ex.Message); }
                });
            }
            Sys.Log("TrayApp ready, icon set = " + (ni.Icon != null) + ", visible = " + ni.Visible);
        }

        // 把 UI 操作切回 UI 线程
        private void Ui(Action a)
        {
            try
            {
                if (uiHost.InvokeRequired)
                    uiHost.BeginInvoke(new MethodInvoker(delegate()
                    {
                        try { a(); } catch (Exception ex) { Sys.Log("UI action failed: " + ex.Message); }
                    }));
                else a();
            }
            catch (Exception ex) { Sys.Log("Ui dispatch failed: " + ex.Message); }
        }

        private void ApplyAutoStartFromConfig()
        {
            // 首次运行（settings.ini 不存在）时不动注册表，避免误删已有的自启项
            if (!File.Exists(Cfg.IniPath)) return;
            bool actual = Sys.IsAutoStart();
            if (actual != Cfg.AutoStart)
            {
                Sys.SetAutoStart(Cfg.AutoStart);
                Sys.Log("AutoStart reconciled to " + Cfg.AutoStart);
            }
        }

        private void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            for (int i = 0; i < st.Length; i++)
            {
                st[i] = new ToolStripMenuItem();
                st[i].Enabled = false;
                st[i].ForeColor = Color.FromArgb(70, 70, 70);
                menu.Items.Add(st[i]);
            }
            menu.Items.Add(new ToolStripSeparator());

            // 定时闸门：独立开关，控制 \QuietHDD\ 两个计划任务的启用状态
            gateToggle = new ToolStripMenuItem("计划闸门");
            gateToggle.CheckOnClick = false;
            gateToggle.Click += delegate(object s, EventArgs e) { ToggleGate(); };
            menu.Items.Add(gateToggle);

            // 硬盘空闲停转：独立开关，不随静音模式变化
            spinToggle = new ToolStripMenuItem("硬盘空闲停转");
            spinToggle.CheckOnClick = false;      // 状态由系统实测值决定，自己翻转
            spinToggle.Click += delegate(object s, EventArgs e) { ToggleSpinDown(); };
            menu.Items.Add(spinToggle);

            spinMinutesItem = new ToolStripMenuItem("设置停转时长…");
            spinMinutesItem.Click += delegate(object s, EventArgs e) { SetSpinMinutes(); };
            menu.Items.Add(spinMinutesItem);

            menu.Items.Add(new ToolStripSeparator());

            toggle = new ToolStripMenuItem("开启静音模式");
            toggle.Font = new Font(menu.Font, FontStyle.Bold);
            toggle.Click += delegate(object s, EventArgs e) { Toggle(); };
            menu.Items.Add(toggle);

            reapply = new ToolStripMenuItem("重新应用当前模式");
            reapply.Click += delegate(object s, EventArgs e) { StartApply(Cfg.ModeOn || Cfg.AutoSilentActive, false); };
            menu.Items.Add(reapply);

            // 明确的手动出口：静音模式已关但盘还脱机时，一键联机 / 恢复维护任务 / 恢复闸门
            ToolStripMenuItem bringOnline = new ToolStripMenuItem("立即联机硬盘（恢复白天状态）");
            bringOnline.Click += delegate(object s, EventArgs e) { StartApply(false, false); };
            menu.Items.Add(bringOnline);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new ToolStripMenuItem("设置…");
            settings.Click += delegate(object s, EventArgs e) { OpenSettings(); };
            menu.Items.Add(settings);

            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新状态");
            refresh.Click += delegate(object s, EventArgs e) { RefreshAll(true); };
            menu.Items.Add(refresh);

            ToolStripMenuItem trayHelp = new ToolStripMenuItem("托盘图标被隐藏？打开任务栏设置…");
            trayHelp.Click += delegate(object s, EventArgs e)
            {
                try { Process.Start("ms-settings:taskbar"); }
                catch
                {
                    try { Process.Start("control.exe", "/name Microsoft.NotificationAreaIcons"); }
                    catch (Exception ex) { Sys.Log("open tray settings failed: " + ex.Message); }
                }
            };
            menu.Items.Add(trayHelp);

            ToolStripMenuItem log = new ToolStripMenuItem("查看日志");
            log.Click += delegate(object s, EventArgs e)
            {
                try { if (!File.Exists(Cfg.LogPath)) File.WriteAllText(Cfg.LogPath, "", new UTF8Encoding(false)); Process.Start("notepad.exe", Cfg.LogPath); }
                catch (Exception ex) { Sys.Log("open log failed: " + ex.Message); }
            };
            menu.Items.Add(log);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate(object s, EventArgs e) { ExitThread(); };
            menu.Items.Add(exit);

            menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e) { RefreshAll(false); };
        }

        protected override void ExitThreadCore()
        {
            try { if (timer != null) timer.Stop(); } catch { }
            try { if (signalTimer != null) signalTimer.Stop(); } catch { }
            try { if (ni != null) { ni.Visible = false; ni.Dispose(); } } catch { }
            try { if (menu != null) menu.Dispose(); } catch { }
            base.ExitThreadCore();
        }

        private void Toggle()
        {
            if (busy) return;
            if (Cfg.ModeOn) { StartApply(false, false); return; }
            if (Cfg.AutoSilentActive) { StopAutoSilent(); return; }
            StartApply(true, false);
        }

        // 自动静音生效中再点“关闭静音模式”：只还原风扇（免 UAC），并抑制本时段
        private void StopAutoSilent()
        {
            Thread t = new Thread(delegate()
            {
                busy = true;
                try
                {
                    Sys.Log("=== StopAutoSilent (user) ===");
                    bool ok = Sys.SetFanSilent(false);
                    Cfg.AutoSilentActive = false;
                    Cfg.AutoSilentSuppressed = true;
                    Cfg.SaveState();
                    lastError = ok ? "" : "风扇还原失败";
                    Ui(delegate()
                    {
                        ShowBalloon("已退出夜间自动静音",
                            ok ? "风扇已回常态。今晚这个时段内不再自动切回，明晚照常。"
                               : "风扇配置切换失败，看日志确认 FanControl 是否在跑。",
                            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                    });
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Sys.Log("StopAutoSilent failed: " + ex);
                }
                finally
                {
                    busy = false;
                    Ui(delegate() { RefreshAll(true); });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------
        //  定时闸门：独立开关（\QuietHDD\ 两个计划任务的启用状态）
        // ------------------------------------------------------------
        private void ToggleGate()
        {
            if (busy) return;
            bool want = !Cfg.GateEnabled;
            Thread t = new Thread(delegate() { ApplyGateNow(want); });
            t.IsBackground = true;
            t.Start();
        }

        private void ApplyGateNow(bool enable)
        {
            if (busy) return;
            busy = true;
            try
            {
                Sys.Log("=== Gate apply " + (enable ? "ENABLE" : "DISABLE") + " ===");
                string r = Sys.ApplyGate(enable, true);
                if (r.StartsWith("UAC_CANCELLED"))
                {
                    lastError = "闸门开关被取消（需要管理员）";
                    Ui(delegate() { ShowBalloon("闸门未改动", "管理员权限被取消。", ToolTipIcon.Warning); });
                }
                else if (Sys.GateResultOk(r, enable))
                {
                    Cfg.GateEnabled = enable;
                    Cfg.SchedulePaused = (Cfg.ModeOn && Cfg.ScheduleMode == "override");
                    Cfg.Save();
                    lastError = "";
                    Ui(delegate()
                    {
                        ShowBalloon(enable ? "定时闸门已启用" : "定时闸门已关闭",
                            enable ? ("两个任务已 Enable：" + Cfg.AutoSilentOn + " 脱机 / " + Cfg.AutoSilentOff + " 联机")
                                   : ("两个任务已 Disable，今晚不会自动脱机。" +
                                      (r.IndexOf("DISK_ONLINE", StringComparison.OrdinalIgnoreCase) >= 0
                                          ? "已顺手把 Disk" + Cfg.HddDisks + " 联机。"
                                          : "")));
                    });
                }
                else
                {
                    lastError = "闸门开关失败";
                    Ui(delegate() { ShowBalloon("闸门开关失败", r, ToolTipIcon.Error); });
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Sys.Log("ApplyGateNow failed: " + ex);
            }
            finally
            {
                busy = false;
                Ui(delegate() { RefreshAll(true); });
            }
        }

        // ------------------------------------------------------------
        //  硬盘空闲停转：独立开关（只改 DISKIDLE，绝不牵动静音模式的其它模块）
        // ------------------------------------------------------------
        private void ToggleSpinDown()
        {
            if (busy) return;
            int cur = spinSec >= 0 ? spinSec : Sys.DiskIdleSeconds();
            int want;
            if (cur > 0) want = 0;                       // 当前开着 → 关
            else
            {
                want = Cfg.SpinDownMinutes;
                if (want <= 0) want = 20;                // 配置里是 0 时给个有意义的默认
            }
            ApplySpinAsync(want);
        }

        private void SetSpinMinutes()
        {
            if (busy) return;
            int cur = Cfg.SpinDownMinutes > 0 ? Cfg.SpinDownMinutes : 20;
            int v = Prompt.Number("硬盘空闲停转时长",
                "空闲多少分钟后让机械硬盘停转：\n0 = 永不（等同于关闭）", cur, 0, 180, " 分钟");
            if (v < 0) return;                           // 取消

            Cfg.SpinDownMinutes = v;
            Cfg.SpinDownEnabled = v > 0;
            Cfg.Save();
            Sys.Log("SpinDown minutes -> " + v);

            if (v == 0)
            {
                if (spinSec > 0) ApplySpinAsync(0);
                else
                {
                    Ui(delegate() { ShowBalloon("停转已关闭", "DISKIDLE 本来就是 0，电源方案不用改。"); });
                    RefreshAll(true);
                }
            }
            else if (spinSec > 0)
            {
                ApplySpinAsync(v);                       // 原本就开着，直接套用新时长
            }
            else
            {
                Ui(delegate()
                {
                    ShowBalloon("停转时长已保存",
                        "当前停转是关闭状态。点菜单里的「硬盘空闲停转」启用后按 " + v + " 分钟生效。");
                });
                RefreshAll(true);
            }
        }

        private void ApplySpinAsync(int minutes)
        {
            Thread t = new Thread(delegate() { ApplySpin(minutes); });
            t.IsBackground = true;
            t.Start();
        }

        private void ApplySpin(int minutes)
        {
            if (busy) return;
            busy = true;
            try
            {
                Sys.Log("=== SpinDown apply " + minutes + " min ===");
                string r = Sys.ApplyDiskIdle(minutes);
                bool ok = r.IndexOf("SPIN=OK", StringComparison.OrdinalIgnoreCase) >= 0;

                if (r.StartsWith("UAC_CANCELLED"))
                {
                    lastError = "停转设置被取消（需要管理员）";
                    Ui(delegate() { ShowBalloon("停转未应用", "管理员权限被取消，电源方案没改。", ToolTipIcon.Warning); });
                }
                else if (ok)
                {
                    lastError = "";
                    if (minutes > 0) { Cfg.SpinDownEnabled = true; Cfg.SpinDownMinutes = minutes; }
                    else Cfg.SpinDownEnabled = false;
                    Cfg.Save();
                    string body = minutes > 0
                        ? ("空闲 " + minutes + " 分钟后停转（交流/直流都生效，重启后保留）")
                        : "已关闭空闲停转（DISKIDLE = 0，永不停转）";
                    Ui(delegate() { ShowBalloon("硬盘停转已更新", body); });
                }
                else
                {
                    lastError = "停转设置失败";
                    Ui(delegate() { ShowBalloon("停转设置失败", r, ToolTipIcon.Error); });
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Sys.Log("ApplySpin failed: " + ex);
            }
            finally
            {
                busy = false;
                Ui(delegate() { RefreshAll(true); });
            }
        }

        private void ShowBalloon(string title, string body)
        {
            ShowBalloon(title, body, ToolTipIcon.Info);
        }

        private void ShowBalloon(string title, string body, ToolTipIcon icon)
        {
            try { ni.ShowBalloonTip(5000, title, body, icon); } catch { }
        }

        // 提权 worker 会等 UAC 最长 180s，放后台线程跑，避免托盘卡死像“没反应”
        private void StartApply(bool on, bool silent)
        {
            if (busy) return;
            Thread t = new Thread(delegate() { Apply(on, silent); });
            t.IsBackground = true;
            t.Start();
        }

        // 开机对账：不管静音模式是开是关，只要「人在用电脑」就应该是白天状态。
        // 两条历史坑：
        //  1) override 模式下「开启静音」会 Disable 两个定时闸门，10:02 的自动联机不触发；
        //  2) 夜间闸门 23:58 把盘脱机后，早上 10:02 之前开机会被 AtStartup 任务按
        //     mode=NIGHT 维持脱机 —— 静音模式已经是「关闭」，盘却起不来。
        // 所以启动时按「实际磁盘状态」对账，而不只看持久化的 ModeOn。
        private void BootReconcile()
        {
            DateTime now = DateTime.Now;
            int mins = now.Hour * 60 + now.Minute;
            bool inNight = InWindow(mins, ParseHm(Cfg.AutoSilentOn, 23 * 60 + 58), 45);
            bool anyOffline = false;
            try
            {
                Dictionary<int, bool> ds = Sys.DiskStates(Cfg.Disks());
                if (ds == null) { anyOffline = true; Sys.Log("BootReconcile: disk WMI failed, assume needs repair"); }
                else
                {
                    foreach (int n in Cfg.Disks())
                    {
                        bool v;
                        if (!ds.TryGetValue(n, out v) || v) { anyOffline = true; break; }
                    }
                }
            }
            catch (Exception ex) { anyOffline = true; Sys.Log("BootReconcile: " + ex.Message); }

            Sys.Log("BootReconcile: now=" + now.ToString("HH:mm") + " ModeOn=" + Cfg.ModeOn +
                    " inNightWin=" + inNight + " anyDiskOffline=" + anyOffline +
                    " stamp=" + Cfg.AutoOffDayStamp);

            if (inNight)
            {
                Sys.Log("BootReconcile: inside 23:58 night window, keeping night state");
                return;
            }
            if (!Cfg.ModeOn && !anyOffline)
            {
                Sys.Log("BootReconcile: mode off and disks already online, nothing to do");
                return;
            }
            // 只有「静音模式残留」才写日期戳；盘脱机的分支不写，UAC 被取消后下次开机还能重试。
            if (Cfg.ModeOn)
            {
                Cfg.AutoOffDayStamp = now.ToString("yyyy-MM-dd");
                Cfg.SaveState();
            }
            Thread t = new Thread(delegate()
            {
                // 静音模式残留：立刻处理。盘脱机：等 2.5 分钟，让 SYSTEM 的 AtStartup
                // 闸门先跑（PT2M，无需 UAC）；只有它没把盘联机时才提权兜底。
                Thread.Sleep(Cfg.ModeOn ? 3000 : 150000);
                if (!Cfg.ModeOn)
                {
                    bool stillOff = false;
                    try
                    {
                        Dictionary<int, bool> ds2 = Sys.DiskStates(Cfg.Disks());
                        if (ds2 == null) stillOff = true;
                        else
                            foreach (int n in Cfg.Disks())
                            { bool v; if (!ds2.TryGetValue(n, out v) || v) { stillOff = true; break; } }
                    }
                    catch { stillOff = true; }
                    if (!stillOff)
                    {
                        Sys.Log("BootReconcile: startup gate already onlined disks, skip UAC");
                        return;
                    }
                }
                Sys.Log(Cfg.ModeOn
                    ? "BootReconcile: leftover ModeOn=1 -> full OFF"
                    : "BootReconcile: disks still offline -> full OFF (online disks / maintenance / gate)");
                Apply(false, true);
            });
            t.IsBackground = true;
            t.Start();
        }

        // 自动静音：只在时段边界后的窗口内动手，窗口外一律让位给手动操作
        private string lastAutoStamp = "";

        private static int ParseHm(string s, int def)
        {
            try
            {
                string[] p = (s ?? "").Split(':');
                if (p.Length == 2) return int.Parse(p[0].Trim()) * 60 + int.Parse(p[1].Trim());
            }
            catch { }
            return def;
        }

        private static bool InWindow(int mins, int startM, int win)
        {
            int d = ((mins - startM) % 1440 + 1440) % 1440;
            return d < win;
        }

        private static string FmtDur(int sec)
        {
            if (sec <= 0) return "0 秒";
            if (sec % 60 == 0) return (sec / 60) + " 分钟";
            return sec + " 秒";
        }

        private void CheckAutoSilent()
        {
            if (!Cfg.AutoSilent)
            {
                if (Cfg.AutoSilentActive || Cfg.AutoSilentSuppressed)
                {
                    Cfg.AutoSilentActive = false;
                    Cfg.AutoSilentSuppressed = false;
                    Cfg.SaveState();
                }
                return;
            }
            if (!Cfg.FanEnabled) return;
            DateTime now = DateTime.Now;
            int mins = now.Hour * 60 + now.Minute;
            const int WIN = 45;
            bool nightWin = InWindow(mins, ParseHm(Cfg.AutoSilentOn, 23 * 60 + 58), WIN);
            bool dayWin = InWindow(mins, ParseHm(Cfg.AutoSilentOff, 10 * 60 + 2), WIN);

            if (!nightWin && !dayWin)
            {
                // 出了窗口：抑制标记与服务时段标记一起复位，明天照常
                bool hadAuto = Cfg.AutoSilentActive;
                if (Cfg.AutoSilentSuppressed || Cfg.AutoSilentActive)
                {
                    Cfg.AutoSilentSuppressed = false;
                    Cfg.AutoSilentActive = false;
                    Cfg.SaveState();
                }
                // 开机晚于 10:47（已错过日间 45 分钟窗口）时，昨夜的静音档可能还挂着：自愈一次。
                // 只对「自动静音残留」生效，用户白天自己在 FanControl 里选静音档不会被改回去。
                if (hadAuto && !Cfg.ModeOn && Cfg.FanEnabled)
                {
                    string c2 = Sys.FanCurrentConfig();
                    if (c2 != null && c2.Equals(Path.GetFileName(Cfg.FanSilent), StringComparison.OrdinalIgnoreCase))
                    {
                        Sys.Log("AutoSilent: outside window, cleaning leftover silent fan profile");
                        if (Sys.SetFanSilent(false)) lastError = "";
                        else lastError = "静音档自愈失败";
                    }
                }
                lastAutoStamp = "";
                return;
            }
            if (Cfg.AutoSilentSuppressed) return;

            // 日间窗口：残留的「整体静音模式」在这里做一次完整关闭（联机 / 维护 / 闸门），
            // 而不是只把风扇切回常态。同一天只做一次；白天手动开启静音会把标记写成今天，避免被立刻关掉。
            if (dayWin && Cfg.ModeOn)
            {
                string today = now.ToString("yyyy-MM-dd");
                if (Cfg.AutoOffDayStamp != today)
                {
                    Cfg.AutoOffDayStamp = today;
                    Cfg.SaveState();
                    Sys.Log("AutoSilent: day window -> full OFF (disk online / tasks / gate)");
                    StartApply(false, true);
                    return;
                }
            }

            string stamp = now.ToString("yyyy-MM-dd HH:mm");
            if (stamp == lastAutoStamp) return;
            lastAutoStamp = stamp;

            string cur = Sys.FanCurrentConfig();
            if (cur == null) return;
            bool isSilent = cur.Equals(Path.GetFileName(Cfg.FanSilent), StringComparison.OrdinalIgnoreCase);
            bool wantSilent = nightWin;

            // 风扇已在静音档且不是手动开的静音模式 → 认为自动静音生效中（菜单据此显示“开”）
            bool nowActive = !Cfg.ModeOn && nightWin && isSilent;
            if (nowActive != Cfg.AutoSilentActive)
            {
                Cfg.AutoSilentActive = nowActive;
                Cfg.SaveState();
                Sys.Log("AutoSilentActive -> " + nowActive);
            }

            if (isSilent == wantSilent) return;

            Sys.Log("AutoSilent: now=" + now.ToString("HH:mm") + " want=" + (wantSilent ? "silent" : "normal"));
            if (Sys.SetFanSilent(wantSilent))
            {
                lastError = "";
                if (wantSilent && !Cfg.ModeOn)
                {
                    Cfg.AutoSilentActive = true;
                    Cfg.SaveState();
                }
                Ui(delegate()
                {
                    try
                    {
                        ni.ShowBalloonTip(4000, "QuietMode 自动静音",
                            wantSilent ? "到点，风扇已切到静音档（硬盘由闸门脱机）" : "到点，风扇已恢复常态，硬盘已联机",
                            ToolTipIcon.Info);
                    }
                    catch { }
                });
            }
            else { lastError = "自动静音切换失败"; }
        }

        private void Apply(bool on, bool silent)
        {
            if (busy) return;
            busy = true;
            List<string> notes = new List<string>();
            bool elevCancelled = false;
            try
            {
                Sys.Log("=== Apply " + (on ? "ON" : "OFF") + " ===");

                if (Cfg.FanEnabled)
                {
                    Sys.EnsureFanControl();
                    string cfg = on ? Cfg.FanSilent : Cfg.FanNormal;
                    string o = Sys.Run(Cfg.FanIpcCtl, "load \"" + cfg + "\"", 15000);
                    notes.Add("风扇→" + Path.GetFileName(cfg) + (o.Contains("ERR:") ? " [" + o + "]" : ""));
                    Sys.Log("Fan load " + cfg + " => " + o);
                    try
                    {
                        if (on) File.WriteAllText(Cfg.FanFlag, "(active)", Encoding.ASCII);
                        else if (File.Exists(Cfg.FanFlag)) File.Delete(Cfg.FanFlag);
                    }
                    catch (Exception ex) { Sys.Log("flag write failed: " + ex.Message); }
                }

                // 停转是独立模块，不参与静音模式的提权批次
                bool needElev = Cfg.HddEnabled || Cfg.MaintenanceEnabled ||
                                Cfg.ScheduleMode == "override";
                if (needElev)
                {
                    Sys.BuildWorker(on);
                    string r = Sys.RunWorker();
                    Sys.Log("Worker result: " + r.Replace("\r", " ").Replace("\n", " | "));
                    if (r.StartsWith("UAC_CANCELLED") || r.StartsWith("WORKER_TIMEOUT") ||
                        r.StartsWith("NO_RESULT") || r.StartsWith("TIMEOUT") ||
                        r.StartsWith("READ_RESULT_ERR"))
                    {
                        elevCancelled = true;
                        lastError = "提权动作未完成，硬盘/维护任务/闸门未应用";
                        notes.Add("管理员动作：未完成（" + r.Split('\n')[0] + "）");
                        Ui(delegate()
                        {
                            ShowBalloon("自动恢复未完成",
                                "需要管理员权限才能联机硬盘 / 恢复定时闸门。下次开机或点「关闭静音模式」可重试。",
                                ToolTipIcon.Warning);
                        });
                    }
                    else
                    {
                        string flat = r.Replace("\r", " ").Replace("\n", " | ");
                        notes.Add("管理员动作：" + (flat.Length > 90 ? flat.Substring(0, 90) + "…" : flat));
                        lastError = "";
                    }
                }

                if (elevCancelled)
                {
                    // 提权失败：保持上次状态，下次开机兜底还能重试，绝不假装已经切换
                    Sys.Log("Apply: elevation cancelled, keeping ModeOn=" + Cfg.ModeOn);
                }
                else
                {
                    Cfg.ModeOn = on;
                    Cfg.SchedulePaused = (on && Cfg.ScheduleMode == "override");
                    Cfg.AutoSilentActive = false;
                    if (on)
                    {
                        Cfg.AutoSilentSuppressed = false;   // 手动开 = 用户当前就要静音
                        Cfg.AutoOffDayStamp = DateTime.Now.ToString("yyyy-MM-dd");  // 今天别被日间自动关掉
                    }
                    else
                    {
                        // 夜间自动静音时段内手动关闭 → 今晚别再自动切回去
                        DateTime tnow = DateTime.Now;
                        int tmins = tnow.Hour * 60 + tnow.Minute;
                        if (InWindow(tmins, ParseHm(Cfg.AutoSilentOn, 23 * 60 + 58), 45))
                            Cfg.AutoSilentSuppressed = true;
                    }
                    Cfg.SaveState();
                }

                if (!silent)
                {
                    string body = (elevCancelled ? "状态未改变（管理员权限被取消）" :
                                   (on ? "静音模式已开启" : "静音模式已关闭")) + "\n" + string.Join("\n", notes.ToArray());
                    Ui(delegate() { ni.ShowBalloonTip(5000, "QuietMode", body, ToolTipIcon.Info); });
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Sys.Log("Apply failed: " + ex);
                if (!silent) Ui(delegate() { ni.ShowBalloonTip(6000, "QuietMode 出错", ex.Message, ToolTipIcon.Error); });
            }
            finally
            {
                busy = false;
                Ui(delegate() { RefreshAll(true); });
            }
        }

        private void RefreshAll(bool full)
        {
            if (busy) return;
            try
            {
                fanName = Sys.FanCurrentConfig();
                diskStates = Sys.DiskStates(Cfg.Disks());
                letters = Sys.DriveLetters();
                spinSec = Sys.DiskIdleSeconds();
                if (full) Sys.Log("Refresh: fan=" + (fanName == null ? "?" : fanName) +
                                  " spin=" + spinSec + "s");
            }
            catch (Exception ex) { Sys.Log("Refresh failed: " + ex.Message); }
            UpdateUi();
        }

        private void UpdateUi()
        {
            string silentFile = Path.GetFileName(Cfg.FanSilent);
            string normalFile = Path.GetFileName(Cfg.FanNormal);
            bool fanSilent = !string.IsNullOrEmpty(fanName) &&
                             fanName.Equals(silentFile, StringComparison.OrdinalIgnoreCase);
            bool fanUnknown = string.IsNullOrEmpty(fanName);

            int offCount = 0, onCount = 0, unknown = 0;
            List<string> diskParts = new List<string>();
            int[] disks = Cfg.Disks();
            if (diskStates == null) { unknown = disks.Length; }
            else
            {
                foreach (int n in disks)
                {
                    bool v;
                    if (!diskStates.TryGetValue(n, out v)) { unknown++; diskParts.Add("Disk" + n + "=?"); continue; }
                    string ls = "";
                    if (letters != null && letters.ContainsKey(n)) ls = "(" + letters[n] + ")";
                    if (v) { offCount++; diskParts.Add("Disk" + n + ls + " 脱机"); }
                    else { onCount++; diskParts.Add("Disk" + n + ls + " 在线"); }
                }
            }

            // 状态行
            string modeDesc = Cfg.ModeOn ? "开启"
                             : (Cfg.AutoSilentActive ? "开启（自动·夜间风扇）" : "关闭");
            st[0].Text = "静音模式：" + modeDesc +
                         (Cfg.SchedulePaused ? "（定时闸门被接管）" : "");
            st[1].Text = "风扇：" + (fanUnknown ? "FanControl 未响应" :
                         (fanSilent ? "静音档 " + silentFile : "常态 " + (fanName ?? normalFile)));
            st[2].Text = "硬盘：" + (diskParts.Count == 0 ? "未配置" : string.Join(" / ", diskParts.ToArray())) +
                         (!Cfg.ModeOn && offCount > 0 ? "  ← 点「立即联机硬盘」" : "");
            st[3].Text = "自动静音：" + (Cfg.AutoSilent
                         ? ("开启，风扇 " + Cfg.AutoSilentOn + " 静音 → " + Cfg.AutoSilentOff + " 常速" +
                            (Cfg.AutoSilentSuppressed ? "（今晚已手动退出）" : ""))
                         : "关闭（只手动切换风扇）");

            // 计划闸门：可点开关
            string gateMode = Cfg.ScheduleMode == "override"
                ? (Cfg.AutoSilentOn + " 脱机 → " + Cfg.AutoSilentOff + " 联机")
                : "共存模式（静音时不接管）";
            if (!Cfg.GateEnabled) gateToggle.Text = "计划闸门：已关闭 · " + gateMode;
            else if (Cfg.SchedulePaused) gateToggle.Text = "计划闸门：静音模式期间暂停 · " + gateMode;
            else gateToggle.Text = "计划闸门：运行中 · " + gateMode;
            gateToggle.Checked = Cfg.GateEnabled;
            gateToggle.Enabled = !busy;

            // 硬盘空闲停转（独立项，不随静音模式变化）
            string spinDesc;
            if (spinSec < 0) spinDesc = "读取失败";
            else if (spinSec == 0) spinDesc = "关闭";
            else spinDesc = "开启 · 空闲 " + FmtDur(spinSec) + "后停转";
            spinToggle.Text = "硬盘空闲停转：" + spinDesc;
            spinToggle.Checked = spinSec > 0;
            spinToggle.Enabled = !busy;
            spinMinutesItem.Text = "设置停转时长…（配置 " +
                (Cfg.SpinDownMinutes <= 0 ? "关闭" : Cfg.SpinDownMinutes + " 分钟") + "）";
            spinMinutesItem.Enabled = !busy;

            toggle.Text = (Cfg.ModeOn || Cfg.AutoSilentActive) ? "关闭静音模式" : "开启静音模式";
            reapply.Enabled = !busy;

            // 生效判定
            bool wantQuiet = Cfg.ModeOn || Cfg.AutoSilentActive;
            bool expectFan = Cfg.FanEnabled ? fanSilent : true;
            bool expectHdd = Cfg.HddEnabled ? (offCount == disks.Length && unknown == 0) : true;
            bool allAsExpected;
            if (wantQuiet) allAsExpected = expectFan && expectHdd;
            else allAsExpected = (Cfg.FanEnabled ? !fanSilent : true) &&
                                 (Cfg.HddEnabled ? (onCount == disks.Length && unknown == 0) : true);

            Icons.Kind k;
            if (lastError.Length > 0 || (fanUnknown && Cfg.FanEnabled)) k = Icons.Kind.Error;
            else if (!allAsExpected) k = Icons.Kind.Partial;
            else k = wantQuiet ? Icons.Kind.Quiet : Icons.Kind.Normal;

            string tip = "静音:" + (wantQuiet ? "开" : "关") +
                         " 风扇:" + (fanUnknown ? "?" : (fanSilent ? "静音档" : "常态")) +
                         " 硬盘:" + (unknown > 0 ? "状态未知" :
                            (offCount == disks.Length ? "全脱机" : (onCount == disks.Length ? "全在线" : "混合"))) +
                         " 停转:" + (spinSec <= 0 ? "关" : FmtDur(spinSec));
            if (lastError.Length > 0) tip += " [!]";
            ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

            if (lastIcon == null || k != lastIcon.Value)
            {
                Icon old = ni.Icon;
                try { ni.Icon = Icons.Make(k); }
                catch (Exception ex)
                {
                    Sys.Log("Icon draw failed, fallback: " + ex.Message);
                    try { ni.Icon = (Icon)SystemIcons.Application.Clone(); } catch { }
                }
                if (old != null) { try { old.Dispose(); } catch { } }
                lastIcon = k;
            }
            // 兜底：任何路径导致 Icon 丢失时补一个，避免托盘彻底不可见
            if (ni.Icon == null)
            {
                try { ni.Icon = Icons.Make(k); lastIcon = k; }
                catch { try { ni.Icon = (Icon)SystemIcons.Application.Clone(); } catch { } }
            }
        }

        private void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm())
            {
                DialogResult r = f.ShowDialog();
                if (r == DialogResult.OK) RefreshAll(true);
            }
        }
    }

    // ------------------------------------------------------------
    //  设置窗口
    // ------------------------------------------------------------
    internal sealed class SettingsForm : Form
    {
        private CheckBox cbFan, cbHdd, cbMaint, cbSpin, cbForce, cbAuto, cbRestore, cbAutoSilent;
        private TextBox tbIpc, tbSilent, tbNormal, tbFcExe, tbDisks, tbConf, tbGate, tbAutoOn, tbAutoOff;
        private ComboBox cmbSchedule;
        private NumericUpDown nudSpin;
        private Button btnSave, btnCancel, btnDefaults, btnApplySpin;
        private Font uiFont;

        public SettingsForm()
        {
            uiFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            this.Font = uiFont;
            this.Text = "QuietMode 设置";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(566, 680);
            this.BackColor = SystemColors.Control;

            BuildModules();
            BuildFan();
            BuildHdd();
            BuildSpin();
            BuildPlan();
            BuildButtons();

            LoadFromCfg();
        }

        private GroupBox Group(string title, int y, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = title;
            g.Location = new Point(12, y);
            g.Size = new Size(542, h);
            g.Font = uiFont;
            this.Controls.Add(g);
            return g;
        }

        private Label Lbl(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 20);
            l.TextAlign = ContentAlignment.MiddleLeft;
            parent.Controls.Add(l);
            return l;
        }

        private void BuildModules()
        {
            GroupBox g = Group("静音模式包含的模块（开启/关闭静音时执行哪些动作）", 10, 96);
            cbFan = Cb(g, "风扇：切换 FanControl 配置到静音档", 16, 24, 250);
            cbHdd = Cb(g, "硬盘：设备层脱机（零 I/O，最彻底的静音）", 278, 24, 255);
            cbMaint = Cb(g, "系统维护任务：禁用 6 项自动整理/扫描", 16, 50, 250);
            Lbl(g, "未勾选的模块在切换时完全不动。硬盘空闲停转在下方独立控制，不受此处影响。", 16, 72, 520).ForeColor = Color.Gray;
        }

        private CheckBox Cb(Control parent, string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Location = new Point(x, y);
            c.Size = new Size(w, 22);
            parent.Controls.Add(c);
            return c;
        }

        private TextBox Tb(Control parent, int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Size = new Size(w, 23);
            parent.Controls.Add(t);
            return t;
        }

        private Button Browse(Control parent, TextBox target, int x, int y)
        {
            Button b = new Button();
            b.Text = "…";
            b.Location = new Point(x, y);
            b.Size = new Size(28, 23);
            b.Click += delegate(object s, EventArgs e)
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "所有文件|*.*";
                    try { if (File.Exists(target.Text)) d.FileName = target.Text; }
                    catch { }
                    if (d.ShowDialog() == DialogResult.OK) target.Text = d.FileName;
                }
            };
            parent.Controls.Add(b);
            return b;
        }

        private void BuildFan()
        {
            GroupBox g = Group("风扇（无需管理员，走 FanControl 命名管道）", 112, 138);
            Lbl(g, "IpcCtl.exe", 14, 24, 80);
            tbIpc = Tb(g, 96, 24, 400); Browse(g, tbIpc, 502, 24);
            Lbl(g, "静音配置", 14, 52, 80);
            tbSilent = Tb(g, 96, 52, 400); Browse(g, tbSilent, 502, 52);
            Lbl(g, "常态配置", 14, 80, 80);
            tbNormal = Tb(g, 96, 80, 400); Browse(g, tbNormal, 502, 80);
            Lbl(g, "FanControl.exe", 14, 108, 80);
            tbFcExe = Tb(g, 96, 108, 400); Browse(g, tbFcExe, 502, 108);
        }

        private void BuildHdd()
        {
            GroupBox g = Group("硬盘脱机（需管理员：Set-Disk / 计划任务）", 256, 112);
            Lbl(g, "磁盘号", 14, 24, 60);
            tbDisks = Tb(g, 76, 24, 90);
            cbForce = Cb(g, "Force：忽略进程占用强制脱机", 180, 24, 220);
            Lbl(g, "conf", 14, 52, 60);
            tbConf = Tb(g, 76, 52, 400); Browse(g, tbConf, 502, 52);
            Lbl(g, "闸门脚本", 14, 80, 60);
            tbGate = Tb(g, 76, 80, 400); Browse(g, tbGate, 502, 80);
        }

        // 硬盘空闲停转：独立于静音模式，开关/时长都不随静音模式变化
        private void BuildSpin()
        {
            GroupBox g = Group("硬盘空闲停转 DISKIDLE（独立开关，不随静音模式变化）", 374, 104);
            cbSpin = Cb(g, "启用空闲停转（机械盘无 I/O 后电机停转，彻底无噪声）", 16, 22, 420);
            Lbl(g, "停转时长", 16, 50, 60);
            nudSpin = new NumericUpDown();
            nudSpin.Location = new Point(82, 48);
            nudSpin.Size = new Size(80, 23);
            nudSpin.Minimum = 0;
            nudSpin.Maximum = 180;
            nudSpin.Increment = 5;
            g.Controls.Add(nudSpin);
            Lbl(g, "分钟（0 = 永不）", 168, 50, 120).ForeColor = Color.Gray;

            btnApplySpin = new Button();
            btnApplySpin.Text = "立即应用到系统（需管理员）";
            btnApplySpin.Location = new Point(304, 46);
            btnApplySpin.Size = new Size(220, 27);
            btnApplySpin.Click += delegate(object s, EventArgs e)
            {
                int min = cbSpin.Checked ? (int)nudSpin.Value : 0;
                if (cbSpin.Checked && min == 0)
                {
                    MessageBox.Show("已勾选启用但时长为 0（永不）。要真正启用请把时长设成大于 0 的分钟数。",
                        "QuietMode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                btnApplySpin.Enabled = false;
                btnApplySpin.Text = "正在应用（可能在等你确认 UAC）…";
                Application.DoEvents();
                string r = Sys.ApplyDiskIdle(min);
                btnApplySpin.Enabled = true;
                btnApplySpin.Text = "立即应用到系统（需管理员）";
                if (r.StartsWith("UAC_CANCELLED"))
                    MessageBox.Show("管理员权限被取消，电源方案没有改动。", "QuietMode",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (r.IndexOf("SPIN=OK", StringComparison.OrdinalIgnoreCase) >= 0)
                    MessageBox.Show(min > 0 ? ("已设置：空闲 " + min + " 分钟后停转（交流/直流）。") :
                                              "已关闭空闲停转（DISKIDLE = 0）。",
                        "QuietMode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show("应用失败，返回：\n" + r, "QuietMode",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            g.Controls.Add(btnApplySpin);

            Lbl(g, "计时从最后一次磁盘 I/O 结束后开始；磁盘已脱机时该项无意义（本来就没有 I/O）。",
                16, 78, 520).ForeColor = Color.Gray;
        }

        private void BuildPlan()
        {
            GroupBox g = Group("定时闸门 / 自动静音 / 开机", 484, 140);
            Lbl(g, "手动静音时", 14, 26, 80);
            cmbSchedule = new ComboBox();
            cmbSchedule.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSchedule.Location = new Point(96, 24);
            cmbSchedule.Size = new Size(250, 23);
            cmbSchedule.Items.Add("暂停定时闸门（推荐，手动优先）");
            cmbSchedule.Items.Add("与定时闸门共存（23:58/10:02 照常运行）");
            g.Controls.Add(cmbSchedule);

            cbAuto = Cb(g, "开机自启（当前用户，无需管理员）", 14, 56, 250);
            cbRestore = Cb(g, "启动时恢复上次静音模式", 278, 56, 240);

            cbAutoSilent = Cb(g, "到点自动切风扇静音档", 14, 82, 250);
            Lbl(g, "静音时段", 278, 84, 60);
            tbAutoOn = Tb(g, 338, 82, 52);
            Lbl(g, "→", 394, 84, 16);
            tbAutoOff = Tb(g, 412, 82, 52);

            Lbl(g, "只切风扇（免管理员、不弹 UAC）；硬盘脱机走两个定时闸门，空闲停转走上方的独立开关。",
                14, 110, 520).ForeColor = Color.Gray;
        }

        private void BuildButtons()
        {
            btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Location = new Point(326, 640);
            btnSave.Size = new Size(100, 30);
            btnSave.Click += delegate(object s, EventArgs e) { Save(); };
            this.Controls.Add(btnSave);

            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(434, 640);
            btnCancel.Size = new Size(100, 30);
            btnCancel.Click += delegate(object s, EventArgs e) { this.DialogResult = DialogResult.Cancel; };
            this.Controls.Add(btnCancel);
            this.CancelButton = btnCancel;

            btnDefaults = new Button();
            btnDefaults.Text = "恢复默认";
            btnDefaults.Location = new Point(12, 640);
            btnDefaults.Size = new Size(100, 30);
            btnDefaults.Click += delegate(object s, EventArgs e) { LoadDefaults(); };
            this.Controls.Add(btnDefaults);
        }

        private void LoadDefaults()
        {
            cbFan.Checked = true; cbHdd.Checked = true; cbMaint.Checked = true; cbSpin.Checked = false;
            tbIpc.Text = @"<FAN_ROOT>\ipc\IpcCtl.exe";
            tbSilent.Text = @"<FAN_ROOT>\Configurations\FanControl_Silent_Lite.json";
            tbNormal.Text = @"<FAN_ROOT>\Configurations\FanControl.json";
            tbFcExe.Text = @"<FAN_ROOT>\FanControl.exe";
            tbDisks.Text = "0,1"; cbForce.Checked = false;
            tbConf.Text = @"C:\ProgramData\QuietHDD\diskgate.conf";
            tbGate.Text = @"C:\ProgramData\QuietHDD\diskgate.ps1";
            cmbSchedule.SelectedIndex = 0;
            nudSpin.Value = 20;
            cbAutoSilent.Checked = true;
            tbAutoOn.Text = "23:58";
            tbAutoOff.Text = "10:02";
            cbAuto.Checked = Sys.IsAutoStart();
            cbRestore.Checked = false;
        }

        private void LoadFromCfg()
        {
            cbFan.Checked = Cfg.FanEnabled;
            cbHdd.Checked = Cfg.HddEnabled;
            cbMaint.Checked = Cfg.MaintenanceEnabled;
            cbSpin.Checked = Cfg.SpinDownEnabled;
            tbIpc.Text = Cfg.FanIpcCtl;
            tbSilent.Text = Cfg.FanSilent;
            tbNormal.Text = Cfg.FanNormal;
            tbFcExe.Text = Cfg.FanControlExe;
            tbDisks.Text = Cfg.HddDisks;
            cbForce.Checked = Cfg.HddForce;
            tbConf.Text = Cfg.HddConf;
            tbGate.Text = Cfg.HddGate;
            cmbSchedule.SelectedIndex = (Cfg.ScheduleMode == "override") ? 0 : 1;
            int v = Cfg.SpinDownMinutes;
            if (v < 0) v = 0;
            if (v > 180) v = 180;
            nudSpin.Value = v;
            cbAuto.Checked = Sys.IsAutoStart();
            cbRestore.Checked = Cfg.RestoreOnBoot;
            cbAutoSilent.Checked = Cfg.AutoSilent;
            tbAutoOn.Text = Cfg.AutoSilentOn;
            tbAutoOff.Text = Cfg.AutoSilentOff;
        }

        private void Save()
        {
            string d = tbDisks.Text.Trim();
            if (d.Length == 0) d = "0,1";
            foreach (string p in d.Split(','))
            {
                int n;
                if (!int.TryParse(p.Trim(), out n))
                {
                    MessageBox.Show("磁盘号格式不对，应形如 0,1", "QuietMode",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            Cfg.FanEnabled = cbFan.Checked;
            Cfg.HddEnabled = cbHdd.Checked;
            Cfg.MaintenanceEnabled = cbMaint.Checked;
            Cfg.SpinDownEnabled = cbSpin.Checked;
            Cfg.FanIpcCtl = tbIpc.Text.Trim();
            Cfg.FanSilent = tbSilent.Text.Trim();
            Cfg.FanNormal = tbNormal.Text.Trim();
            Cfg.FanControlExe = tbFcExe.Text.Trim();
            Cfg.HddDisks = d;
            Cfg.HddForce = cbForce.Checked;
            Cfg.HddConf = tbConf.Text.Trim();
            Cfg.HddGate = tbGate.Text.Trim();
            Cfg.ScheduleMode = (cmbSchedule.SelectedIndex == 0) ? "override" : "coexist";
            Cfg.SpinDownMinutes = (int)nudSpin.Value;
            Cfg.AutoStart = cbAuto.Checked;
            Cfg.RestoreOnBoot = cbRestore.Checked;
            Cfg.AutoSilent = cbAutoSilent.Checked;
            string onS = NormalizeHm(tbAutoOn.Text, "23:58");
            string offS = NormalizeHm(tbAutoOff.Text, "10:02");
            Cfg.AutoSilentOn = onS;
            Cfg.AutoSilentOff = offS;
            tbAutoOn.Text = onS; tbAutoOff.Text = offS;

            Cfg.Save();
            Sys.SetAutoStart(Cfg.AutoStart);
            Sys.Log("Settings saved");

            // 停转是独立项：配置改了不等于系统改了，提醒去点「立即应用」
            int sysSec = Sys.DiskIdleSeconds();
            int wantSec = Cfg.SpinDownEnabled ? Cfg.SpinDownSeconds : 0;
            if (sysSec >= 0 && sysSec != wantSec)
            {
                MessageBox.Show(
                    "配置已保存。\n\n" +
                    "系统当前 DISKIDLE = " + sysSec + " 秒，与这里的配置（" + wantSec + " 秒）不一致。\n" +
                    "要让新设置真正写进电源方案，请点「立即应用到系统（需管理员）」，那一步会弹一次 UAC。\n\n" +
                    "也可以直接从托盘菜单点「硬盘空闲停转」开关，效果一样。",
                    "QuietMode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            this.DialogResult = DialogResult.OK;
        }

        private static string NormalizeHm(string s, string def)
        {
            try
            {
                string[] p = (s ?? "").Replace("：", ":").Split(':');
                if (p.Length == 2)
                {
                    int h = int.Parse(p[0].Trim());
                    int mi = int.Parse(p[1].Trim());
                    if (h >= 0 && h < 24 && mi >= 0 && mi < 60) return h.ToString("00") + ":" + mi.ToString("00");
                }
            }
            catch { }
            return def;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && uiFont != null) uiFont.Dispose();
            base.Dispose(disposing);
        }
    }
}

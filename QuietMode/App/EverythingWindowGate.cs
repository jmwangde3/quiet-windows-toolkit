using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using QuietMode.Core;

namespace QuietMode.App
{
    /// <summary>
    /// Everything 静音窗口闸门。由计划任务以 `QuietMode.exe --everything-window` 每分钟调用一次。
    ///
    /// 为什么不用 PowerShell：任务每分钟启动 `powershell.exe` 会创建控制台；本机默认终端是
    /// Windows Terminal，conhost 移交终端时会新建可见窗口，`-WindowStyle Hidden` 拦不住
    /// （隐藏是在进程起来之后才 ShowWindow）。`QuietMode.exe` 是 WinExe（GUI 子系统），
    /// 从任务启动不创建任何控制台，从根上消除闪窗。
    ///
    /// 判定语义与旧 `powershell/Worker/Invoke-EverythingWindow.ps1` 等价：
    ///   - state.json 的 everythingQuietCycle 非空 = 本次静音激活，空 = 常态；
    ///   - stamp 相同就直接返回（绝大多数分钟走这条，无副作用）；
    ///   - 静音期间若 Everything 晚于本次激活才启动，视为用户手动打开，不强关；
    ///   - 只在动作成功后写 stamp，失败留给下一次重试。
    /// 不写 TrayLog，避免每分钟一行把日志刷爆。
    /// </summary>
    internal static class EverythingWindowGate
    {
        private static readonly Regex CyclePattern = new Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        // ---------------------------------------------------------------- 状态快照

        private struct GateState
        {
            public string Key;        // "quiet/<cycle>" 或 "normal"
            public bool Quiet;
            public DateTime? Since;   // 本次静音激活时刻（UTC）；legacy 或常态为 null
        }

        public static int Run(string[] args)
        {
            string root = GetArg(args, "-Root");
            string everythingExe = GetArg(args, "-EverythingExe");
            bool preview = HasFlag(args, "-Preview");
            string previewFile = GetArg(args, "-PreviewFile");
            if (string.IsNullOrWhiteSpace(root)) return Fail("缺少 -Root", 2);
            if (string.IsNullOrWhiteSpace(everythingExe)) return Fail("缺少 -EverythingExe", 2);

            GateState st;
            try
            {
                st = ReadState(root);
            }
            catch (Exception ex)
            {
                return Fail("读取 state.json 失败：" + ex.Message, 3);
            }

            if (preview)
            {
                // 本程序是 WinExe（GUI 子系统），Console.Out 没有接收方；
                // 预览结果必须走文件，供诊断与测试读取。
                if (!string.IsNullOrWhiteSpace(previewFile))
                {
                    try { File.WriteAllText(previewFile, st.Key, new UTF8Encoding(false)); }
                    catch (Exception ex) { return Fail("写预览文件失败：" + ex.Message, 11); }
                }
                try { Console.Out.WriteLine(st.Key); } catch { }
                return 0;
            }

            int session = Process.GetCurrentProcess().SessionId;
            if (session == 0) return Fail("Everything window must run in the interactive session.", 4);

            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietMode");
            string stamp = Path.Combine(stateDir, "everything-window.txt");
            if (StampMatches(stamp, st.Key)) return 0;   // 绝大多数分钟的出口

            if (!File.Exists(everythingExe)) return Fail("Everything executable missing: " + everythingExe, 5);

            string exeDir = Path.GetDirectoryName(everythingExe);
            List<Process> running = GetInteractive(everythingExe, session);

            // 静音期间，晚于本次激活才启动的实例算用户手动打开，不强关。
            bool manualLaunch = st.Quiet && st.Since.HasValue
                                && CountStartedAfter(running, st.Since.Value) > 0;

            if (st.Quiet && running.Count > 0 && !manualLaunch)
            {
                int[] before = Ids(running);
                Process stop = StartHidden(everythingExe, exeDir, "-exit -wait -no-first-instance");
                if (stop == null) return Fail("Everything exit command could not start.", 6);
                if (!stop.WaitForExit(30000)) return Fail("Everything exit command timed out.", 7);
                if (SafeExitCode(stop) != 0 || Intersects(before, Ids(GetInteractive(everythingExe, session))))
                    return Fail("Everything did not exit gracefully.", 8);
            }
            else if (!st.Quiet && running.Count == 0)
            {
                Process start = StartHidden(everythingExe, exeDir, "-first-instance -startup");
                if (start == null) return Fail("Everything start command could not start.", 9);
                if (start.WaitForExit(1000) && GetInteractive(everythingExe, session).Count == 0)
                    return Fail("Everything did not start (exit " + SafeExitCode(start) + ").", 10);
            }

            WriteStamp(stamp, st.Key);
            return 0;
        }

        // ---------------------------------------------------------------- 状态读取

        private static GateState ReadState(string root)
        {
            string statePath = Path.Combine(root, "state.json");
            if (!File.Exists(statePath)) throw new FileNotFoundException("state.json not found", statePath);
            JNode state = MiniJson.Parse(File.ReadAllText(statePath, Encoding.UTF8));

            GateState st = new GateState();
            string cycle;
            if (state.Has("everythingQuietCycle"))
            {
                JNode node = state["everythingQuietCycle"];
                cycle = node.AsString("");
                // 非法值直接报错：宁可不动作，也不要把脏 cycle 写进 stamp。
                if (node.Kind != JKind.String || (cycle.Length > 0 && !CyclePattern.IsMatch(cycle)))
                    throw new InvalidOperationException("Invalid Everything quiet cycle in state.json.");
            }
            else
            {
                // 升级后、首次 reconcile 写入新字段之前走这里：退回旧口径。
                if (!state.Has("modeOn") || !state.Has("autoSilentActive"))
                    throw new InvalidOperationException("Invalid QuietMode state.");
                bool modeOn = state["modeOn"].AsBool(false);
                bool autoSilent = state["autoSilentActive"].AsBool(false);
                bool suppressed = state["autoSilentSuppressed"].AsBool(false);
                cycle = (modeOn || (autoSilent && !suppressed)) ? "legacy" : "";
            }

            st.Quiet = cycle.Length > 0;
            st.Key = st.Quiet ? "quiet/" + cycle : "normal";
            if (st.Quiet && cycle != "legacy")
            {
                string since = state["everythingQuietSinceUtc"].AsString("");
                if (!string.IsNullOrEmpty(since))
                {
                    DateTimeOffset dto;
                    if (DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out dto))
                        st.Since = dto.UtcDateTime;
                }
            }
            return st;
        }

        // ---------------------------------------------------------------- 进程

        private static List<Process> GetInteractive(string everythingExe, int session)
        {
            List<Process> result = new List<Process>();
            string name = Path.GetFileNameWithoutExtension(everythingExe);
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.SessionId != session) continue;
                    string path = p.MainModule == null ? null : p.MainModule.FileName;
                    if (!string.Equals(path, everythingExe, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(p);
                }
                catch { /* 权限不足或已退出：跳过 */ }
            }
            return result;
        }

        private static int CountStartedAfter(List<Process> procs, DateTime utcCutoff)
        {
            int n = 0;
            foreach (Process p in procs)
            {
                try { if (p.StartTime.ToUniversalTime() > utcCutoff) n++; }
                catch { }
            }
            return n;
        }

        private static int[] Ids(List<Process> procs)
        {
            int[] ids = new int[procs.Count];
            for (int i = 0; i < procs.Count; i++) ids[i] = procs[i].Id;
            return ids;
        }

        private static bool Intersects(int[] a, int[] b)
        {
            foreach (int x in a)
                foreach (int y in b)
                    if (x == y) return true;
            return false;
        }

        private static Process StartHidden(string exe, string workingDir, string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
                psi.WorkingDirectory = workingDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                return Process.Start(psi);
            }
            catch { return null; }
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; } catch { return -1; }
        }

        // ---------------------------------------------------------------- stamp

        private static bool StampMatches(string stampPath, string key)
        {
            try
            {
                return File.Exists(stampPath) && File.ReadAllText(stampPath, Encoding.UTF8) == key;
            }
            catch { return false; }
        }

        private static void WriteStamp(string stampPath, string key)
        {
            try
            {
                string dir = Path.GetDirectoryName(stampPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = stampPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, key, new UTF8Encoding(false));
                if (File.Exists(stampPath)) File.Replace(tmp, stampPath, null);
                else File.Move(tmp, stampPath);
            }
            catch { /* 写不进去只意味着下一次重跑一遍，不影响正确性 */ }
        }

        // ---------------------------------------------------------------- 杂项

        private static void WriteErr(string message)
        {
            // WinExe 没有控制台，stderr 写到哪里都看不到；只保留退出码语义。
            try { Console.Error.WriteLine(message); } catch { }
        }

        /// <summary>记录原因后返回退出码。WinExe 下原因只能落到诊断文件，调用方靠退出码判断。</summary>
        private static int Fail(string message, int code)
        {
            WriteErr(message);
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietMode");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "everything-window.err"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] exit=" + code + " " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
            return code;
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using QuietMode.App;

namespace QuietMode.Core
{
    /// <summary>一次 qmctl 调用的结果。成功与否只看退出码 + 能否解析出 JSON，不看输出文案。</summary>
    internal sealed class QmctlCall
    {
        public string CommandLine = "";
        public bool Started;
        public bool TimedOut;
        public bool ElevationCancelled;
        public int ExitCode = -1;
        public string StdErr = "";
        public string Error = "";
        public string RawJson = "";
        public JNode Json = JNode.Null;

        public bool Ok { get { return Started && !TimedOut && !ElevationCancelled && ExitCode == 0; } }

        /// <summary>用于界面的简短失败原因。</summary>
        public string FailureText
        {
            get
            {
                if (!Started) return string.IsNullOrEmpty(Error) ? "无法启动 qmctl" : Error;
                if (ElevationCancelled) return "管理员权限被取消";
                if (TimedOut) return "qmctl 超时未返回";
                if (!string.IsNullOrEmpty(StdErr)) return StdErr.Trim();
                if (!string.IsNullOrEmpty(Error)) return Error;
                return "退出码 " + ExitCode;
            }
        }
    }

    /// <summary>
    /// 托盘与 v2 引擎之间唯一的通道。托盘不再自己算该开还是该关，也不自己写注册表/电源方案：
    /// 所有动作都变成一次 qmctl 调用，结论只认它返回的 JSON（那是它重新观测过的结果）。
    ///
    /// 两条硬规则：
    ///   1. qmctl 找不到就报「不可用」，绝不退回 v1 自带逻辑假装成功；
    ///   2. --apply 一律走 UAC 提权启动，取消就按取消处理，不写任何状态。
    /// </summary>
    internal static class QmctlClient
    {
        public static bool Available { get { return !string.IsNullOrEmpty(AppPaths.QmctlPath); } }

        public static string UnavailableReason
        {
            get
            {
                return "找不到 qmctl.exe。\n\n"
                       + "请把 qmctl.exe 放在 QuietMode.exe 同目录，或在 "
                       + AppPaths.TrayIniPath + " 里写 QmctlPath=<完整路径>。";
            }
        }

        public static QmctlCall Status(int timeoutMs = 120000)
        {
            return Run(new string[] { "status" }, timeoutMs, false);
        }

        public static QmctlCall Reconcile(bool apply, string trigger, int timeoutMs = 360000)
        {
            return Run(new string[] { "reconcile", "auto", "--trigger", trigger, apply ? "--apply" : "" },
                       timeoutMs, apply);
        }

        public static QmctlCall ReconcileAction(string action, bool apply, string trigger, int timeoutMs = 360000)
        {
            return Run(new string[] { "reconcile", action, "--trigger", trigger, apply ? "--apply" : "" },
                       timeoutMs, apply);
        }

        public static QmctlCall State(string sub, int timeoutMs = 60000)
        {
            return Run(new string[] { "state", sub }, timeoutMs, false);
        }

        public static QmctlCall ConfigSet(string key, string value, int timeoutMs = 60000)
        {
            return Run(new string[] { "config", "set", key, value }, timeoutMs, false);
        }

        /// <summary>
        /// 先按普通权限写；被拒绝（ProgramData 下 ACL 常见）再提权重试一次。
        /// 用户取消 UAC 就如实返回 ElevationCancelled，绝不假装写成功。
        /// </summary>
        public static QmctlCall ConfigSetAuto(string key, string value)
        {
            QmctlCall c = ConfigSet(key, value);
            if (c.Ok) return c;
            TrayLog.Write("config set " + key + " 未成功（" + c.FailureText + "），改提权重试");
            return Run(new string[] { "config", "set", key, value }, 120000, true);
        }

        public static QmctlCall ConfigShow(int timeoutMs = 60000)
        {
            return Run(new string[] { "config", "show" }, timeoutMs, false);
        }

        public static QmctlCall SpinSet(int minutes, bool apply, int timeoutMs = 180000)
        {
            return Run(new string[] { "spin", "set", minutes.ToString(), apply ? "--apply" : "" },
                       timeoutMs, apply);
        }

        public static QmctlCall SpinShow(int timeoutMs = 60000)
        {
            return Run(new string[] { "spin", "show" }, timeoutMs, false);
        }

        public static QmctlCall TaskStatus(int timeoutMs = 60000)
        {
            return Run(new string[] { "task", "status" }, timeoutMs, false);
        }

        /// <summary>把参数拼成一次调用。空字符串参数自动丢弃。</summary>
        public static QmctlCall Run(string[] args, int timeoutMs, bool elevate)
        {
            QmctlCall r = new QmctlCall();
            string exe = AppPaths.QmctlPath;
            if (string.IsNullOrEmpty(exe))
            {
                r.Error = UnavailableReason;
                return r;
            }

            string outFile = Path.Combine(Path.GetTempPath(),
                "QuietMode-qmctl-" + Guid.NewGuid().ToString("N") + ".json");

            StringBuilder cl = new StringBuilder();
            string root = AppPaths.Root;
            if (!string.IsNullOrEmpty(root)) cl.Append("--root \"").Append(root).Append("\" ");
            foreach (string a in args)
            {
                if (string.IsNullOrEmpty(a)) continue;
                cl.Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a).Append(' ');
            }
            cl.Append("--json --out \"").Append(outFile).Append('"');
            r.CommandLine = exe + " " + cl;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, cl.ToString());
                psi.UseShellExecute = elevate;
                psi.CreateNoWindow = true;
                if (elevate)
                {
                    // 提权只能用 ShellExecute；输出走 --out 文件，所以不需要重定向
                    psi.Verb = "runas";
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }
                else
                {
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = new UTF8Encoding(false);
                    psi.StandardErrorEncoding = new UTF8Encoding(false);
                    psi.WorkingDirectory = Path.GetDirectoryName(exe);
                }

                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    try
                    {
                        if (!p.Start()) { r.Error = "进程未能启动"; return r; }
                    }
                    catch (Win32Exception ex)
                    {
                        // 1223 = 用户在 UAC 对话框点了「否」
                        if (ex.NativeErrorCode == 1223) { r.ElevationCancelled = true; r.Started = true; }
                        else r.Error = "启动失败：" + ex.Message;
                        TrayLog.Write("qmctl start failed: " + ex.Message + " [" + r.CommandLine + "]");
                        return r;
                    }
                    r.Started = true;

                    if (!elevate)
                    {
                        // 先抽干输出再等，避免管道写满死锁
                        string so = p.StandardOutput.ReadToEnd();
                        string se = p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(timeoutMs))
                        {
                            r.TimedOut = true;
                            try { p.Kill(); } catch { }
                        }
                        else
                        {
                            r.ExitCode = p.ExitCode;
                        }
                        r.StdErr = se;
                        GC.KeepAlive(so);
                    }
                    else
                    {
                        if (!p.WaitForExit(timeoutMs)) { r.TimedOut = true; try { p.Kill(); } catch { } }
                        else r.ExitCode = p.ExitCode;
                    }
                }

                // JSON 走文件：彻底绕开控制台代码页（936）与管道编码
                if (File.Exists(outFile))
                {
                    try
                    {
                        string text = File.ReadAllText(outFile, new UTF8Encoding(false));
                        r.RawJson = text;
                        if (!string.IsNullOrWhiteSpace(text)) r.Json = MiniJson.Parse(text);
                    }
                    catch (Exception ex)
                    {
                        r.Error = "解析 qmctl 输出失败：" + ex.Message;
                        TrayLog.Write("qmctl json parse failed: " + ex.Message);
                    }
                }
                else if (!r.ElevationCancelled && !r.TimedOut && r.ExitCode != 0)
                {
                    r.Error = r.Error.Length > 0 ? r.Error : "qmctl 未产生输出文件";
                }

                TrayLog.Write("qmctl " + string.Join(" ", args) + " => exit=" + r.ExitCode
                              + (r.TimedOut ? " TIMEOUT" : "")
                              + (r.ElevationCancelled ? " ELEVATION_CANCELLED" : "")
                              + (string.IsNullOrEmpty(r.Error) ? "" : " ERR=" + r.Error));
                return r;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                TrayLog.Error("qmctl call failed [" + r.CommandLine + "]", ex);
                return r;
            }
            finally
            {
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }
        }

        public static string Version()
        {
            QmctlCall r = Run(new string[] { "--help" }, 30000, false);
            return r.Ok ? "qmctl 可执行" : r.FailureText;
        }
    }
}

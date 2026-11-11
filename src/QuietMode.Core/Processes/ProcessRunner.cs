using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuietMode.Core.Processes
{
    /// <summary>
    /// 外部进程调用结果。区分「启动失败」「超时」「非零退出」，禁止把三者混成一个 bool。
    /// </summary>
    public sealed class ProcessResult
    {
        public bool Started;
        public bool TimedOut;
        public int ExitCode = -1;
        public string StdOut = "";
        public string StdErr = "";
        public string StartError = "";
        public long DurationMs;

        public bool Ok { get { return Started && !TimedOut && ExitCode == 0; } }

        /// <summary>给日志用的单行摘要，不含完整输出。</summary>
        public string Summary
        {
            get
            {
                if (!Started) return "启动失败：" + StartError;
                if (TimedOut) return "超时被终止（" + DurationMs + " ms），退出码 " + ExitCode;
                return "退出码 " + ExitCode + "（" + DurationMs + " ms）";
            }
        }
    }

    /// <summary>
    /// 统一的外部进程执行器（规格 §7.2）。要点：
    /// 1) stdout / stderr 用后台线程并行读取，避免管道缓冲区写满导致死锁；
    /// 2) 使用真正的 WaitForExit(timeout)，超时后连子进程一起终止；
    /// 3) 保留完整输出、退出码、耗时，并区分启动失败与超时。
    /// .NET Framework 4.8 没有 Process.Kill(entireProcessTree)，用 taskkill /T 兜底。
    /// </summary>
    public static class ProcessRunner
    {
        public const int DefaultTimeoutMs = 120000;

        public static ProcessResult Run(string fileName, string arguments, int timeoutMs)
        {
            return Run(fileName, arguments, null, timeoutMs, null);
        }

        public static ProcessResult Run(string fileName, string arguments, string workingDirectory, int timeoutMs)
        {
            return Run(fileName, arguments, workingDirectory, timeoutMs, null);
        }

        /// <param name="environment">可选环境变量覆盖，键值均为 null 表示删除。</param>
        public static ProcessResult Run(string fileName, string arguments, string workingDirectory,
                                        int timeoutMs, System.Collections.Generic.IDictionary<string, string> environment)
        {
            ProcessResult result = new ProcessResult();
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments ?? "";
            if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // 子进程统一按 UTF-8 输出（脚本内部也会设置 Console.OutputEncoding）。
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WindowStyle = ProcessWindowStyle.Hidden;

            if (environment != null)
            {
                foreach (System.Collections.Generic.KeyValuePair<string, string> kv in environment)
                {
                    if (kv.Value == null) psi.EnvironmentVariables.Remove(kv.Key);
                    else psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            Stopwatch watch = Stopwatch.StartNew();
            Process process;
            try
            {
                process = Process.Start(psi);
                if (process == null)
                {
                    result.StartError = "Process.Start 返回 null";
                    result.DurationMs = watch.ElapsedMilliseconds;
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.StartError = ex.GetType().Name + ": " + Flatten(ex.Message);
                result.DurationMs = watch.ElapsedMilliseconds;
                return result;
            }

            result.Started = true;
            Task<string> outTask = null;
            Task<string> errTask = null;
            try
            {
                // 必须并行读，否则任一管道写满 4KB 就会互相阻塞。
                outTask = Task.Run(delegate { try { return process.StandardOutput.ReadToEnd(); } catch { return ""; } });
                errTask = Task.Run(delegate { try { return process.StandardError.ReadToEnd(); } catch { return ""; } });

                try { process.StandardInput.Close(); }
                catch { }

                bool exited = process.WaitForExit(timeoutMs);
                if (!exited)
                {
                    result.TimedOut = true;
                    KillTree(process);
                    process.WaitForExit(5000);
                }

                // WaitForExit(timeout) 不等待异步读取排空，这里给读取线程一个明确的收尾窗口。
                try { Task.WaitAll(new Task[] { outTask, errTask }, 10000); }
                catch { }

                result.StdOut = SafeResult(outTask);
                result.StdErr = SafeResult(errTask);

                if (!result.TimedOut)
                {
                    try { result.ExitCode = process.ExitCode; }
                    catch (Exception ex) { result.StartError = "读取退出码失败：" + ex.Message; }
                }
            }
            finally
            {
                try { process.Dispose(); }
                catch { }
                watch.Stop();
                result.DurationMs = watch.ElapsedMilliseconds;
            }
            return result;
        }

        private static string SafeResult(Task<string> t)
        {
            if (t == null) return "";
            try
            {
                if (t.Wait(1000)) return t.Result ?? "";
            }
            catch { }
            return "";
        }

        private static void KillTree(Process process)
        {
            int pid = -1;
            try { pid = process.Id; } catch { }
            try { process.Kill(); }
            catch { }
            if (pid <= 0) return;
            // .NET Framework 没有 Kill(entireProcessTree)，用 taskkill 收掉子进程。
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/F /T /PID " + pid);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process tk = Process.Start(psi))
                {
                    if (tk != null)
                    {
                        tk.StandardOutput.ReadToEnd();
                        tk.StandardError.ReadToEnd();
                        tk.WaitForExit(5000);
                    }
                }
            }
            catch { }
        }

        /// <summary>把多行错误压成一行，避免日志被堆栈撑爆。</summary>
        public static string Flatten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            return s.Length > 600 ? s.Substring(0, 600) + "…" : s;
        }

        /// <summary>定位 Windows PowerShell 5.1 的绝对路径，不依赖 PATH。</summary>
        public static string PowerShellPath()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string full = Path.Combine(sys, @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(full)) return full;
            return "powershell.exe";
        }
    }
}

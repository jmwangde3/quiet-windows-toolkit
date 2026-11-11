using System;
using System.IO;
using System.Text;
using QuietMode.App;
using QuietMode.Core;

namespace QuietMode
{
    /// <summary>
    /// 自检。只调用只读命令（status / config validate / task status），
    /// 输出写到 %APPDATA%\QuietMode\diag.txt，不弹窗、不改任何状态。
    /// </summary>
    internal static class Diagnostics
    {
        public static void Run()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("QuietMode diagnostics " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            b.AppendLine("Exe       : " + System.Windows.Forms.Application.ExecutablePath);
            b.AppendLine("DataDir   : " + AppPaths.DataDir);
            b.AppendLine("TrayIni   : " + AppPaths.TrayIniPath + (File.Exists(AppPaths.TrayIniPath) ? "" : "  (不存在)"));
            b.AppendLine("qmctl     : " + (AppPaths.QmctlPath ?? "<未找到>"));
            b.AppendLine("Root      : " + (string.IsNullOrEmpty(AppPaths.Root) ? "<默认 C:\\ProgramData\\QuietMode>" : AppPaths.Root));
            b.AppendLine("Elevated  : " + IsElevatedText());
            b.AppendLine();

            QmctlCall st = QmctlClient.Status();
            b.AppendLine("=== qmctl status (exit=" + st.ExitCode + ") ===");
            b.AppendLine(string.IsNullOrEmpty(st.RawJson) ? (st.Error.Length > 0 ? st.Error : "(无输出)") : st.RawJson.Trim());
            b.AppendLine();

            QmctlCall cfg = QmctlClient.Run(new string[] { "config", "validate" }, 60000, false);
            b.AppendLine("=== qmctl config validate (exit=" + cfg.ExitCode + ") ===");
            b.AppendLine(string.IsNullOrEmpty(cfg.RawJson) ? cfg.FailureText : cfg.RawJson.Trim());
            b.AppendLine();

            QmctlCall task = QmctlClient.TaskStatus();
            b.AppendLine("=== qmctl task status (exit=" + task.ExitCode + ") ===");
            b.AppendLine(string.IsNullOrEmpty(task.RawJson) ? task.FailureText : task.RawJson.Trim());
            b.AppendLine();

            QmctlCall spin = QmctlClient.SpinShow();
            b.AppendLine("=== qmctl spin show (exit=" + spin.ExitCode + ") ===");
            b.AppendLine(string.IsNullOrEmpty(spin.RawJson) ? spin.FailureText : spin.RawJson.Trim());
            b.AppendLine();

            b.AppendLine("=== 最近 120 行日志 ===");
            b.AppendLine(TrayLog.Tail(120));

            try { File.WriteAllText(AppPaths.DiagPath, b.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { TrayLog.Error("写 diag.txt 失败", ex); }
            TrayLog.Write("diag written to " + AppPaths.DiagPath);
        }

        /// <summary>把 status 的原始 JSON 单独落一份，便于别的工具/脚本直接读。</summary>
        public static void DumpStatus()
        {
            QmctlCall st = QmctlClient.Status();
            string path = Path.Combine(AppPaths.DataDir, "status.json");
            try
            {
                File.WriteAllText(path,
                    string.IsNullOrEmpty(st.RawJson) ? "{\"ok\":false,\"error\":\"" + Escape(st.FailureText) + "\"}" : st.RawJson,
                    new UTF8Encoding(false));
                TrayLog.Write("status dumped to " + path + " (exit=" + st.ExitCode + ")");
            }
            catch (Exception ex) { TrayLog.Error("写 status.json 失败", ex); }
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        private static string IsElevatedText()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "yes" : "no";
                }
            }
            catch { return "?"; }
        }
    }
}

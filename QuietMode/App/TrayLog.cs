using System;
using System.IO;
using System.Text;

namespace QuietMode.App
{
    /// <summary>
    /// 追加式日志。永不抛异常：日志写不出来也不能把托盘带崩。
    /// 超过 1 MB 自动轮转一份 .1（只留一代，够查最近几次）。
    /// </summary>
    internal static class TrayLog
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    AppPaths.EnsureDataDir();
                    RotateIfNeeded();
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine;
                    File.AppendAllText(AppPaths.LogPath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static void Error(string context, Exception ex)
        {
            Write(context + ": " + (ex == null ? "(null)" : ex.ToString()));
        }

        private static void RotateIfNeeded()
        {
            try
            {
                FileInfo f = new FileInfo(AppPaths.LogPath);
                if (!f.Exists || f.Length < 1024 * 1024) return;
                string bak = AppPaths.LogPath + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(AppPaths.LogPath, bak);
            }
            catch { }
        }

        /// <summary>读最后 N 行，用于诊断文件。</summary>
        public static string Tail(int lines)
        {
            try
            {
                if (!File.Exists(AppPaths.LogPath)) return "(无日志)";
                string[] all = File.ReadAllLines(AppPaths.LogPath, Encoding.UTF8);
                if (all.Length <= lines) return string.Join(Environment.NewLine, all);
                string[] part = new string[lines];
                Array.Copy(all, all.Length - lines, part, 0, lines);
                return string.Join(Environment.NewLine, part);
            }
            catch (Exception ex) { return "(读日志失败：" + ex.Message + ")"; }
        }
    }
}

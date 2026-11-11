using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace QuietMode.Core.Configuration
{
    /// <summary>
    /// 原子写：临时文件 → Flush(true) 落盘 → 校验回调 → 原子替换 → 保留上一版。
    /// 任何一步失败都不破坏目标文件（规格 §2.3）。
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteText(string path, string content, string backupPath, Action<string> validateBeforeCommit)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 为空", "path");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            byte[] bytes = new UTF8Encoding(false).GetBytes(content ?? "");

            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                if (validateBeforeCommit != null)
                {
                    string reread = File.ReadAllText(tmp, Encoding.UTF8);
                    if (reread != null) validateBeforeCommit(reread);
                }

                if (File.Exists(path))
                {
                    if (!string.IsNullOrEmpty(backupPath))
                    {
                        string backupDir = Path.GetDirectoryName(backupPath);
                        if (!string.IsNullOrEmpty(backupDir)) Directory.CreateDirectory(backupDir);
                        // File.Replace 同时完成「替换 + 上一版留存」，单次原子操作
                        File.Replace(tmp, path, backupPath, true);
                    }
                    else
                    {
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            finally
            {
                SafeDelete(tmp);
            }
        }

        public static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        public static string Sha256OfFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        public static string Sha256OfText(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(new UTF8Encoding(false).GetBytes(text ?? ""));
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static void WriteIfAbsent(string path, string content)
        {
            if (File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteText(path, content, null, null);
        }

        public static void Retry(Action action, int attempts, int delayMs)
        {
            Exception last = null;
            for (int i = 0; i < Math.Max(1, attempts); i++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (i + 1 < attempts) Thread.Sleep(delayMs);
                }
            }
            if (last != null) throw last;
        }
    }
}

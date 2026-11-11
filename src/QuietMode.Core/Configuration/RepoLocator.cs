using System;
using System.IO;

namespace QuietMode.Core.Configuration
{
    /// <summary>
    /// 定位仓库根目录（用于找到 powershell/ 下的脚本）。
    /// 从可执行文件所在目录逐级上溯，命中含 QuietMode.sln 或 powershell/Modules 的目录即返回；
    /// 找不到时返回 null，调用方必须显式报错而不是猜一个路径去执行脚本。
    /// </summary>
    public static class RepoLocator
    {
        public static string Find(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory)) return null;
            try
            {
                DirectoryInfo dir = new DirectoryInfo(startDirectory);
                for (int i = 0; i < 10 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "QuietMode.sln"))) return dir.FullName;
                    if (Directory.Exists(Path.Combine(dir.FullName, @"powershell\Modules"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        public static string FindForExecutingAssembly()
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(typeof(RepoLocator).Assembly.Location); }
            catch { }
            if (string.IsNullOrEmpty(dir))
                dir = AppDomain.CurrentDomain.BaseDirectory;
            return Find(dir);
        }

        /// <summary>按相对路径取脚本绝对路径；文件不存在返回 null。</summary>
        public static string Script(string repoRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(relativePath)) return null;
            string full = Path.Combine(repoRoot, relativePath.Replace('/', '\\'));
            return File.Exists(full) ? full : null;
        }
    }
}

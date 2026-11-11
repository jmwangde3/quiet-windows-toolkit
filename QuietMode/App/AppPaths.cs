using System;
using System.IO;
using System.Text;

namespace QuietMode.App
{
    /// <summary>
    /// 托盘自己的路径与外部依赖定位。这里只放「程序在哪、日志在哪」，
    /// 不碰任何业务策略 —— 策略全部在 qmctl / config.json 里。
    /// </summary>
    internal static class AppPaths
    {
        /// <summary>托盘自己的运行目录（日志、单实例信号、诊断输出）。v1 沿用 %APPDATA%\QuietMode。</summary>
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuietMode");

        public static readonly string TrayIniPath = Path.Combine(DataDir, "tray.ini");
        public static readonly string LogPath = Path.Combine(DataDir, "QuietMode.log");
        public static readonly string DiagPath = Path.Combine(DataDir, "diag.txt");
        public static readonly string ActivateSignal = Path.Combine(DataDir, "activate.signal");
        public static readonly string TipDonePath = Path.Combine(DataDir, "tip.done");

        private static string _qmctl;
        private static bool _resolved;

        /// <summary>
        /// 找 qmctl.exe。顺序：tray.ini 的 QmctlPath → 环境变量 QMCTL_PATH →
        /// 同目录 → 上一级 → 本仓库的编译输出。找不到返回 null，界面必须显示「不可用」，
        /// 绝不退回 v1 自带的那套 Apply 逻辑。
        /// </summary>
        public static string QmctlPath
        {
            get
            {
                if (_resolved) return _qmctl;
                _resolved = true;

                string configured = TrayPrefs.Get("QmctlPath");
                _qmctl = FirstExisting(configured,
                    Environment.GetEnvironmentVariable("QMCTL_PATH"),
                    Combine(ExeDir, "qmctl.exe"),
                    Combine(Path.GetDirectoryName(ExeDir), "qmctl.exe"),
                    Combine(Path.GetDirectoryName(Path.GetDirectoryName(ExeDir)), "qmctl.exe"),
                    // 源码树里直接跑托盘时的兜底（只对开发者有意义）
                    Combine(Path.GetDirectoryName(Path.GetDirectoryName(ExeDir)),
                            @"src\QuietMode.Cli\bin\Release\net481\qmctl.exe"),
                    Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(ExeDir))),
                            @"src\QuietMode.Cli\bin\Release\net481\qmctl.exe"));
                return _qmctl;
            }
        }

        public static string ExeDir
        {
            get { return Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath); }
        }

        /// <summary>机器级配置根；空表示用 qmctl 的默认值 C:\ProgramData\QuietMode。</summary>
        public static string Root
        {
            get
            {
                string r = TrayPrefs.Get("Root");
                if (!string.IsNullOrEmpty(r)) return r;
                return Environment.GetEnvironmentVariable("QUIETMODE_ROOT") ?? "";
            }
        }

        private static string Combine(string dir, string file)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try { return Path.Combine(dir, file); }
            catch { return null; }
        }

        private static string FirstExisting(params string[] candidates)
        {
            foreach (string c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                try { if (File.Exists(c)) return c; }
                catch { }
            }
            return null;
        }

        public static void EnsureDataDir()
        {
            try { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); }
            catch { }
        }
    }

    /// <summary>
    /// 托盘本地偏好（tray.ini）。只存「程序怎么找依赖、提示弹过没有」这类界面事项。
    /// 风扇/磁盘/调度参数一律在 config.json，由 qmctl 独占写入。
    /// </summary>
    internal static class TrayPrefs
    {
        private static System.Collections.Generic.Dictionary<string, string> _cache;

        private static System.Collections.Generic.Dictionary<string, string> Load()
        {
            if (_cache != null) return _cache;
            System.Collections.Generic.Dictionary<string, string> d =
                new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(AppPaths.TrayIniPath))
                {
                    foreach (string raw in File.ReadAllLines(AppPaths.TrayIniPath, Encoding.UTF8))
                    {
                        string s = raw.Trim();
                        if (s.Length == 0 || s.StartsWith("#") || s.StartsWith(";")) continue;
                        int i = s.IndexOf('=');
                        if (i <= 0) continue;
                        d[s.Substring(0, i).Trim()] = s.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }
            _cache = d;
            return d;
        }

        public static string Get(string key)
        {
            string v;
            return Load().TryGetValue(key, out v) ? v : "";
        }

        public static bool GetBool(string key, bool def)
        {
            string v = Get(key);
            if (v.Length == 0) return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static void Set(string key, string value)
        {
            Load()[key] = value ?? "";
        }

        public static void Save()
        {
            try
            {
                AppPaths.EnsureDataDir();
                StringBuilder b = new StringBuilder();
                foreach (System.Collections.Generic.KeyValuePair<string, string> kv in Load())
                    b.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(AppPaths.TrayIniPath, b.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { TrayLog.Write("TrayPrefs.Save failed: " + ex.Message); }
        }
    }
}

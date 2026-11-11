using System;
using System.IO;

namespace QuietMode.Core.Configuration
{
    /// <summary>
    /// 机器级配置目录布局（规格 §2.1）。所有组件只从这里取路径，禁止各自拼字符串。
    /// </summary>
    public sealed class QuietModePaths
    {
        public const string MachineRootDefault = @"C:\ProgramData\QuietMode";

        public string Root;
        public string ConfigPath;
        public string BackupPath;
        public string StatePath;
        public string BaselinePath;
        public string LogDir;
        public string RecoveryDir;

        /// <summary>v1 遗留目录：%APPDATA%\QuietMode（settings.ini / state.ini / worker.ps1）。</summary>
        public string LegacyAppDataDir;

        public static string DefaultMachineRoot { get { return MachineRootDefault; } }

        public static QuietModePaths ForRoot(string root)
        {
            QuietModePaths p = new QuietModePaths();
            p.Root = string.IsNullOrWhiteSpace(root) ? MachineRootDefault : root;
            p.ConfigPath = Path.Combine(p.Root, "config.json");
            p.BackupPath = Path.Combine(p.Root, "config.backup.json");
            p.StatePath = Path.Combine(p.Root, "state.json");
            p.BaselinePath = Path.Combine(p.Root, "baseline.json");
            p.LogDir = Path.Combine(p.Root, "logs");
            p.RecoveryDir = Path.Combine(p.Root, "recovery");
            p.LegacyAppDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuietMode");
            return p;
        }

        public string TaskBackupDir { get { return Path.Combine(RecoveryDir, "tasks"); } }

        public void EnsureLayout()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(RecoveryDir);
        }

        public override string ToString() { return Root; }
    }
}

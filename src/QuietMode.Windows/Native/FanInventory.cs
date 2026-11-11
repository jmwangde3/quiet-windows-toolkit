using System;
using System.Diagnostics;
using System.IO;
using QuietMode.Core.Configuration;
using QuietMode.Core.Models;

namespace QuietMode.Windows.Native
{
    public static class FanInventory
    {
        public static FanSnapshot Capture(FanSettings fan)
        {
            FanSnapshot f = new FanSnapshot();
            if (fan != null)
            {
                f.ControlExe = fan.ControlExe ?? "";
                f.IpcPath = fan.IpcPath ?? "";
                f.NormalProfile = fan.NormalProfile ?? "";
                f.SilentProfile = fan.SilentProfile ?? "";
                f.FlagPath = fan.FlagPath ?? "";
            }

            try
            {
                f.ProcessRunning = Process.GetProcessesByName("FanControl").Length > 0;
            }
            catch { f.ProcessRunning = false; }

            f.ControlExeSha256 = AtomicFile.Sha256OfFile(f.ControlExe);
            f.NormalProfileSha256 = AtomicFile.Sha256OfFile(f.NormalProfile);
            f.SilentProfileSha256 = AtomicFile.Sha256OfFile(f.SilentProfile);

            if (!string.IsNullOrWhiteSpace(f.FlagPath)) f.FlagExists = File.Exists(f.FlagPath);

            // FanControl 不在文件里记录「当前加载的配置名」，这里不猜。
            f.CurrentProfile = "";
            if (!string.IsNullOrWhiteSpace(f.SilentProfile) && !File.Exists(f.SilentProfile))
            {
                f.Note = "静音配置不存在，夜间风扇切换会失败";
            }
            else if (!string.IsNullOrWhiteSpace(f.NormalProfile) && !File.Exists(f.NormalProfile))
            {
                f.Note = "常态配置不存在，白天恢复会失败";
            }
            else
            {
                f.Note = "FanControl 未暴露当前配置名；以 silent_active.flag 与运行日志为准";
            }
            return f;
        }
    }
}

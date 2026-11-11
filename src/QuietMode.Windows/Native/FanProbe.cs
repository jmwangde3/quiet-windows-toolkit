using System;
using System.IO;
using QuietMode.Core.Models;
using QuietMode.Core.Processes;
using QuietMode.Core.Scheduling;

namespace QuietMode.Windows.Native
{
    /// <summary>
    /// 风扇当前配置的只读探测。
    /// v1 的注释写着「FanControl 未暴露当前配置名」，实测不成立：IpcCtl.exe list 会直接返回
    /// <c>current=&lt;文件名&gt; folder=&lt;目录&gt;</c>，所以 v2 用这个做真实回读，
    /// 而不是靠 silent_active.flag 猜。
    /// </summary>
    public static class FanProbe
    {
        public static FanObservation Read(FanSettings fan, bool wantSilent)
        {
            FanObservation o = new FanObservation();
            if (fan == null) { o.Error = "没有风扇配置"; return o; }

            string expected = wantSilent ? fan.SilentProfile : fan.NormalProfile;
            o.ExpectedProfile = string.IsNullOrEmpty(expected) ? "" : Path.GetFileName(expected);

            if (string.IsNullOrWhiteSpace(fan.IpcPath))
            {
                o.Error = "未配置 ipcPath";
                return o;
            }
            if (!File.Exists(fan.IpcPath))
            {
                o.Error = "IpcCtl.exe 不存在：" + fan.IpcPath;
                return o;
            }

            ProcessResult pr = ProcessRunner.Run(fan.IpcPath, "list", Path.GetDirectoryName(fan.IpcPath), 8000);
            if (!pr.Started) { o.Error = "无法启动 IpcCtl：" + pr.StartError; return o; }
            if (pr.TimedOut) { o.Error = "IpcCtl list 超时（FanControl 可能没运行）"; return o; }

            string current = ParseCurrent(pr.StdOut);
            if (current == null)
            {
                o.Error = "IpcCtl list 没有返回 current=（退出码 " + pr.ExitCode + "）"
                          + (string.IsNullOrEmpty(pr.StdErr) ? "" : "：" + ProcessRunner.Flatten(pr.StdErr));
                return o;
            }
            o.Available = true;
            o.CurrentProfile = current;
            return o;
        }

        /// <summary>解析 "current=FanControl.json folder=..." 里的配置文件名。</summary>
        public static string ParseCurrent(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return null;
            foreach (string raw in stdout.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (!line.StartsWith("current=", StringComparison.OrdinalIgnoreCase)) continue;
                string v = line.Substring(8).Trim();
                int sp = v.IndexOfAny(new char[] { ' ', '\t' });
                if (sp > 0) v = v.Substring(0, sp);
                return v.Length == 0 ? null : v;
            }
            return null;
        }
    }
}

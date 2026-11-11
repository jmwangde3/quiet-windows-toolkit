using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using QuietMode.App;
using QuietMode.Core;
using QuietMode.Ui;

namespace QuietMode
{
    /// <summary>
    /// 入口。托盘只做三件事：起界面、把点击转成 qmctl 调用、把结果如实显示。
    ///
    /// --diag   写一份自检（只读，不改系统）
    /// --status 把 qmctl status 的原始 JSON 写到 diag 目录，便于外部排查
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppPaths.EnsureDataDir();
            TrayLog.Write("Main entered" + (args.Length > 0 ? " arg=" + args[0] : " (no args)"));

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                TrayLog.Error("ThreadException", e.Exception);
                try
                {
                    MessageBox.Show("QuietMode 出错：\n" + e.Exception.Message + "\n\n详情见 " + AppPaths.LogPath,
                        "QuietMode", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                TrayLog.Write("UnhandledException: " + (e.ExceptionObject == null ? "null" : e.ExceptionObject.ToString()));
            };

            string first = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (first == "--diag")
            {
                Diagnostics.Run();
                return;
            }
            if (first == "--status")
            {
                Diagnostics.DumpStatus();
                return;
            }
            if (first == "--selftest")
            {
                // 冒烟测试：真建一次托盘与菜单、跑一轮状态刷新，2.5 秒后自己退出。
                // 用于「不想在桌面上留图标」时验证 UI 不会因为真实 JSON 崩掉。
                TrayApp probe = new TrayApp();
                System.Windows.Forms.Timer stop = new System.Windows.Forms.Timer();
                stop.Interval = 2500;
                stop.Tick += delegate { stop.Stop(); Application.ExitThread(); };
                stop.Start();
                Application.Run();
                GC.KeepAlive(probe);
                return;
            }

            bool created;
            using (Mutex m = new Mutex(true, "Local\\QuietMode_SingleInstance_v2", out created))
            {
                if (!created)
                {
                    try { File.WriteAllText(AppPaths.ActivateSignal, DateTime.Now.ToString("o"), new UTF8Encoding(false)); }
                    catch { }
                    try
                    {
                        MessageBox.Show(
                            "QuietMode 已经在运行了。\n\n"
                            + "托盘图标如果没看到，是被 Windows 折叠进任务栏的 ^ 溢出面板了 —— 点 ^ 展开，"
                            + "或把图标拖到常显区。\n\n"
                            + "双击托盘图标 = 开/关静音；右键 = 菜单 / 设置。",
                            "QuietMode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch { }
                    return;
                }
                Application.Run(new TrayApp());
            }
        }
    }
}

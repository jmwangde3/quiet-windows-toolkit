using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using QuietMode.Core;

namespace QuietMode.App
{
    /// <summary>
    /// 开机自启。这是 shell 层的事（HKCU\Run），所以留在托盘；
    /// 但「想不想自启」这个意图存在 config.json 的 startup.autoStart，由 qmctl 写。
    /// 两边不一致时以配置为准修注册表 —— 且只在配置文件存在时才动手，避免首次运行
    /// 用默认值 false 把用户已经设好的自启删掉（v1 踩过）。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "QuietMode";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        public static void Set(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else k.DeleteValue(ValueName, false);
                }
                TrayLog.Write("AutoStart -> " + enable);
            }
            catch (Exception ex) { TrayLog.Error("SetAutoStart failed", ex); }
        }

        /// <summary>
        /// 启动时对账：配置说要自启就确保注册表有，反之亦然。
        /// 只在 config.json 真实存在时才动 —— 否则会把「默认策略 false」当成用户意图，
        /// 悄悄删掉已经设好的自启项（v1 的首次运行坑）。
        /// </summary>
        public static void ReconcileFromConfigSafe()
        {
            try
            {
                if (!QmctlClient.Available) return;
                QmctlCall c = QmctlClient.Status();
                if (c.Json.IsNull) return;
                if (!c.Json["config"]["exists"].AsBool(false))
                {
                    TrayLog.Write("AutoStart: config.json 不存在，跳过对账");
                    return;
                }
                JNode st = c.Json["startup"];
                if (st.IsNull) return;
                bool want = st["autoStart"].AsBool(false);
                bool actual = IsEnabled();
                if (want != actual)
                {
                    Set(want);
                    TrayLog.Write("AutoStart reconciled to " + want + "（原 " + actual + "）");
                }
            }
            catch (Exception ex) { TrayLog.Error("AutoStart reconcile failed", ex); }
        }
    }

    /// <summary>
    /// Win11 默认把新托盘图标折叠进 ^。explorer 会在 NotifyIconSettings 下建记录，
    /// 把 IsPromoted 置 1 即常显（HKCU，无需管理员）。找不到记录就静默放弃。
    /// </summary>
    internal static class TrayPromote
    {
        public static void TryPromote()
        {
            try
            {
                string self = Application.ExecutablePath;
                using (RegistryKey b = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (b == null) return;
                    foreach (string sub in b.GetSubKeyNames())
                    {
                        using (RegistryKey k = b.OpenSubKey(sub, true))
                        {
                            if (k == null) continue;
                            object p = k.GetValue("ExecutablePath");
                            if (p == null) continue;
                            if (!string.Equals(p.ToString(), self, StringComparison.OrdinalIgnoreCase)) continue;
                            object v = k.GetValue("IsPromoted");
                            bool ok = false;
                            try { ok = v != null && Convert.ToInt32(v) == 1; } catch { }
                            if (!ok)
                            {
                                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                TrayLog.Write("IsPromoted=1 set on " + sub);
                            }
                            return;
                        }
                    }
                    TrayLog.Write("NotifyIconSettings entry for self not found yet");
                }
            }
            catch (Exception ex) { TrayLog.Error("TrayPromote failed", ex); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using QuietMode.App;
using QuietMode.Core;

namespace QuietMode.Ui
{
    /// <summary>
    /// 托盘 shell。职责只有三件：显示 qmctl 报回来的事实、把点击换成一次 qmctl 调用、弹提示。
    /// 它不再计算「现在该不该静音」，也不再有 Apply()/CheckAutoSilent() ——
    /// 那两件事现在只有一个主人：\QuietMode\QuietMode-Reconcile → qmctl reconcile。
    /// </summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private NotifyIcon ni;
        private ContextMenuStrip menu;
        private readonly ToolStripMenuItem[] st = new ToolStripMenuItem[4];
        private ToolStripMenuItem toggle, reapply, bringOnline;
        private ToolStripMenuItem gateToggle, pauseItem, spinToggle, spinMinutesItem;
        private System.Windows.Forms.Timer timer;
        private System.Windows.Forms.Timer signalTimer;
        private System.Windows.Forms.Timer promoteTimer;
        private readonly Control uiHost = new Control();

        private bool busy;
        private bool refreshing;
        private JNode status = JNode.Null;
        private string lastError = "";
        private Icons.Kind? lastIcon;

        public TrayApp()
        {
            IntPtr force = uiHost.Handle;   // 先建句柄，之后才能跨线程 BeginInvoke
            GC.KeepAlive(force);

            BuildMenu();
            ni = new NotifyIcon();
            ni.Text = "QuietMode";
            ni.ContextMenuStrip = menu;
            try { ni.Icon = Icons.Make(Icons.Kind.Normal); }
            catch (Exception ex)
            {
                TrayLog.Error("initial icon failed", ex);
                try { ni.Icon = (Icon)SystemIcons.Application.Clone(); } catch { }
            }
            ni.Visible = true;
            ni.DoubleClick += delegate { Toggle(); };

            App.AutoStart.ReconcileFromConfigSafe();

            RefreshAsync(true);

            Ui(delegate
            {
                ni.ShowBalloonTip(7000, "QuietMode 已启动",
                    "托盘图标若没看到，是被折叠进任务栏的 ^ 溢出面板了 —— 点 ^ 展开，或把图标拖到常显区。\n"
                    + "双击图标 = 开/关静音，右键 = 菜单 / 设置。",
                    ToolTipIcon.Info);
            });

            if (!QmctlClient.Available)
            {
                Ui(delegate
                {
                    ni.ShowBalloonTip(12000, "QuietMode：找不到 qmctl",
                        QmctlClient.UnavailableReason, ToolTipIcon.Error);
                });
            }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 60000;
            timer.Tick += delegate { RefreshAsync(false); };
            timer.Start();

            // 第二个实例双击时写 activate.signal，这里立刻回应
            signalTimer = new System.Windows.Forms.Timer();
            signalTimer.Interval = 1200;
            signalTimer.Tick += delegate
            {
                if (!File.Exists(AppPaths.ActivateSignal)) return;
                try { File.Delete(AppPaths.ActivateSignal); } catch { }
                try { ni.ShowBalloonTip(4000, "QuietMode", "我已经在运行了，托盘图标在这里（可能被折叠进 ^）。", ToolTipIcon.Info); }
                catch { }
            };
            signalTimer.Start();

            promoteTimer = new System.Windows.Forms.Timer();
            promoteTimer.Interval = 3000;
            promoteTimer.Tick += delegate { promoteTimer.Stop(); TrayPromote.TryPromote(); };
            promoteTimer.Start();

            if (!File.Exists(AppPaths.TipDonePath))
            {
                try { File.WriteAllText(AppPaths.TipDonePath, DateTime.Now.ToString("o")); } catch { }
                Ui(delegate
                {
                    try
                    {
                        MessageBox.Show(
                            "QuietMode 已在托盘运行。\n\n"
                            + "· 图标位置：任务栏右下角。没看到就点 ^ 展开溢出面板。\n"
                            + "· 双击图标 = 开 / 关静音\n"
                            + "· 右键图标 = 菜单（实时状态 / 设置 / 开机自启 / 退出）\n\n"
                            + "本提示只出现一次。",
                            "QuietMode 已启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex) { TrayLog.Error("first-run tip failed", ex); }
                });
            }
            TrayLog.Write("TrayApp ready; qmctl=" + (AppPaths.QmctlPath ?? "<未找到>")
                          + "; root=" + (string.IsNullOrEmpty(AppPaths.Root) ? "<默认>" : AppPaths.Root));
        }

        // ---------------------------------------------------------------- UI 线程

        private void Ui(Action a)
        {
            try
            {
                if (uiHost.InvokeRequired)
                    uiHost.BeginInvoke(new MethodInvoker(delegate
                    {
                        try { a(); } catch (Exception ex) { TrayLog.Error("UI action failed", ex); }
                    }));
                else a();
            }
            catch (Exception ex) { TrayLog.Error("Ui dispatch failed", ex); }
        }

        private void Balloon(string title, string body, ToolTipIcon icon)
        {
            try { ni.ShowBalloonTip(6000, title, body, icon); } catch { }
        }

        // ---------------------------------------------------------------- 菜单

        private void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            for (int i = 0; i < st.Length; i++)
            {
                st[i] = new ToolStripMenuItem();
                st[i].Enabled = false;
                st[i].ForeColor = Color.FromArgb(70, 70, 70);
                menu.Items.Add(st[i]);
            }
            menu.Items.Add(new ToolStripSeparator());

            gateToggle = new ToolStripMenuItem("计划闸门");
            gateToggle.Click += delegate { ToggleGate(); };
            menu.Items.Add(gateToggle);

            // 暂停 = 保留任务但不下手（v1 的 Pause/ReArm 语义），与「关闭闸门」区分开
            pauseItem = new ToolStripMenuItem("暂停计划调度");
            pauseItem.Click += delegate { TogglePause(); };
            menu.Items.Add(pauseItem);

            spinToggle = new ToolStripMenuItem("硬盘空闲停转");
            spinToggle.Click += delegate { ToggleSpin(); };
            menu.Items.Add(spinToggle);

            spinMinutesItem = new ToolStripMenuItem("设置停转时长…");
            spinMinutesItem.Click += delegate { SetSpinMinutes(); };
            menu.Items.Add(spinMinutesItem);

            menu.Items.Add(new ToolStripSeparator());

            toggle = new ToolStripMenuItem("开启静音模式");
            toggle.Font = new Font(menu.Font, FontStyle.Bold);
            toggle.Click += delegate { Toggle(); };
            menu.Items.Add(toggle);

            reapply = new ToolStripMenuItem("重新应用当前模式");
            // 显式指定 night/day，而不是 auto：auto 在「状态标记说已生效」时只验证不动手，
            // 用户点「重新应用」就是要强制重放一遍当前目标。
            reapply.Click += delegate { Reconcile(WantQuiet() ? "night" : "day", "重新应用当前模式"); };
            menu.Items.Add(reapply);

            bringOnline = new ToolStripMenuItem("立即联机硬盘（恢复白天状态）");
            bringOnline.Click += delegate { Reconcile("day", "立即恢复白天状态"); };
            menu.Items.Add(bringOnline);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new ToolStripMenuItem("设置…");
            settings.Click += delegate { OpenSettings(); };
            menu.Items.Add(settings);

            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新状态");
            refresh.Click += delegate { RefreshAsync(true); };
            menu.Items.Add(refresh);

            ToolStripMenuItem openLog = new ToolStripMenuItem("查看日志");
            openLog.Click += delegate
            {
                try
                {
                    if (!File.Exists(AppPaths.LogPath)) File.WriteAllText(AppPaths.LogPath, "", new UTF8Encoding(false));
                    Process.Start("notepad.exe", AppPaths.LogPath);
                }
                catch (Exception ex) { TrayLog.Error("open log failed", ex); }
            };
            menu.Items.Add(openLog);

            ToolStripMenuItem trayHelp = new ToolStripMenuItem("托盘图标被隐藏？打开任务栏设置…");
            trayHelp.Click += delegate
            {
                try { Process.Start("ms-settings:taskbar"); }
                catch
                {
                    try { Process.Start("control.exe", "/name Microsoft.NotificationAreaIcons"); }
                    catch (Exception ex) { TrayLog.Error("open tray settings failed", ex); }
                }
            };
            menu.Items.Add(trayHelp);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);

            menu.Opening += delegate { RefreshAsync(false); };
        }

        protected override void ExitThreadCore()
        {
            try { if (timer != null) timer.Stop(); } catch { }
            try { if (signalTimer != null) signalTimer.Stop(); } catch { }
            try { if (promoteTimer != null) promoteTimer.Stop(); } catch { }
            try { if (ni != null) { ni.Visible = false; ni.Dispose(); } } catch { }
            try { if (menu != null) menu.Dispose(); } catch { }
            base.ExitThreadCore();
        }

        // ---------------------------------------------------------------- 刷新

        private void RefreshAsync(bool full)
        {
            if (busy || refreshing) return;
            if (!QmctlClient.Available)
            {
                status = JNode.Null;
                lastError = "找不到 qmctl.exe";
                UpdateUi();
                return;
            }
            refreshing = true;
            Thread t = new Thread(delegate()
            {
                QmctlCall c = null;
                try { c = QmctlClient.Status(); }
                catch (Exception ex) { TrayLog.Error("status failed", ex); }
                Ui(delegate
                {
                    refreshing = false;
                    try
                    {
                        if (c != null && !c.Json.IsNull)
                        {
                            status = c.Json;
                            bool configOk = status["config"]["ok"].AsBool(false);
                            lastError = configOk ? "" : status["config"]["error"].AsString("配置不可用");
                        }
                        else
                        {
                            lastError = c == null ? "状态读取失败" : c.FailureText;
                        }
                        UpdateUi();
                        if (full)
                        {
                            TrayLog.Write("refresh: state=" + ControllerState()
                                          + " modeOn=" + status["state"]["modeOn"].AsBool()
                                          + " fan=" + status["fan"]["match"].AsString("?")
                                          + " spinAC=" + status["spinDown"]["acSeconds"].AsInt(-1)
                                          + (string.IsNullOrEmpty(lastError) ? "" : " err=" + lastError));
                        }
                    }
                    finally { refreshing = false; }
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private string ControllerState()
        {
            return status["controller"]["state"].AsString("");
        }

        private bool WantQuiet()
        {
            string s = ControllerState();
            return s == "NightActive" || s == "NightDegraded" || s == "EnteringNight" || s == "ManualQuiet";
        }

        private void UpdateUi()
        {
            bool available = QmctlClient.Available;
            bool configOk = status["config"]["ok"].AsBool(false);
            bool elevated = status["elevated"].AsBool(false);

            if (!available)
            {
                st[0].Text = "qmctl 未找到：功能不可用";
                st[1].Text = "把 qmctl.exe 放到 QuietMode.exe 同目录";
                st[2].Text = "";
                st[3].Text = "";
                toggle.Text = "开启静音模式（不可用）";
                foreach (ToolStripMenuItem m in new ToolStripMenuItem[] { toggle, reapply, bringOnline, gateToggle, pauseItem, spinToggle, spinMinutesItem })
                    m.Enabled = false;
                SetIcon(Icons.Kind.Error, "QuietMode：找不到 qmctl");
                return;
            }

            // ---- 状态行 ----
            string stateText = status["controller"]["text"].AsString("");
            string reason = status["controller"]["reason"].AsString("");
            st[0].Text = "调度状态：" + (string.IsNullOrEmpty(stateText) ? "未知" : stateText);
            st[0].ToolTipText = reason;

            string fanMatch = status["fan"]["match"].AsString("unknown");
            string fanCur = status["fan"]["currentProfile"].AsString("");
            bool fanEnabled = status["fan"]["enabled"].AsBool(false);
            st[1].Text = "风扇：" + (!fanEnabled ? "未启用"
                : fanMatch == "unknown" ? "读不到（" + status["fan"]["error"].AsString("FanControl 未响应") + "）"
                : fanMatch == "silent" ? "静音档 " + fanCur
                : fanMatch == "normal" ? "常态 " + fanCur
                : "其它配置 " + fanCur);

            JNode disks = status["disks"]["targets"];
            List<string> parts = new List<string>();
            int off = 0, online = 0, unknown = 0;
            for (int i = 0; i < disks.Count; i++)
            {
                JNode d = disks[i];
                string name = "Disk" + d["number"].AsInt(-1);
                if (!d["matched"].AsBool(false)) { parts.Add(name + " 未匹配"); unknown++; continue; }
                bool? of = d["offline"].AsNullableBool();
                string letters = "";
                JNode ls = d["letters"];
                if (ls.Count > 0)
                {
                    List<string> arr = new List<string>();
                    for (int k = 0; k < ls.Count; k++) arr.Add(ls[k].AsString());
                    letters = "(" + string.Join(",", arr.ToArray()) + ")";
                }
                if (!of.HasValue) { parts.Add(name + letters + " 状态未知"); unknown++; }
                else if (of.Value) { parts.Add(name + letters + " 脱机"); off++; }
                else { parts.Add(name + letters + " 在线"); online++; }
            }
            st[2].Text = "硬盘：" + (parts.Count == 0 ? "未配置目标" : string.Join(" / ", parts.ToArray()));

            bool taskExists = status["task"]["exists"].AsBool(false);
            bool conflict = status["task"]["conflict"].AsBool(false);
            string nightStart = status["schedule"]["nightStart"].AsString("23:58");
            string dayStart = status["schedule"]["dayStart"].AsString("10:02");
            st[3].Text = !taskExists
                ? "⚠ 统一调度任务未注册：自动切换不会运行"
                : (conflict ? "⚠ 检测到 v1 闸门仍可能生效" : "自动：夜间 " + nightStart + " → 白天 " + dayStart);
            st[3].ForeColor = (!taskExists || conflict) ? Color.FromArgb(170, 90, 30) : Color.FromArgb(70, 70, 70);
            st[3].ToolTipText = status["task"]["error"].AsString("");

            // ---- 计划闸门 ----
            bool schedEnabled = status["schedule"]["enabled"].AsBool(true);
            gateToggle.Text = "计划闸门：" + (schedEnabled ? "运行中 · " : "已关闭 · ")
                              + nightStart + " 脱机 → " + dayStart + " 联机";
            gateToggle.Checked = schedEnabled;
            gateToggle.Enabled = !busy;

            bool paused = status["state"]["schedulePaused"].AsBool(false);
            pauseItem.Text = paused ? "恢复计划调度（当前已暂停）" : "暂停计划调度（临时，重启保留）";
            pauseItem.Checked = paused;
            pauseItem.Enabled = !busy && schedEnabled;

            // ---- 停转 ----
            int ac = status["spinDown"]["acSeconds"].AsInt(-1);
            int dc = status["spinDown"]["dcSeconds"].AsInt(-1);
            string spinText = ac < 0 ? "读取失败"
                : ac == 0 ? "关闭"
                : "开启 · 空闲 " + FmtDur(ac) + "后停转" + (dc >= 0 && dc != ac ? "（电池 " + (dc == 0 ? "永不" : FmtDur(dc)) + "）" : "");
            spinToggle.Text = "硬盘空闲停转：" + spinText;
            spinToggle.Checked = ac > 0;
            spinToggle.Enabled = !busy;
            int cfgMinutes = status["spinDown"]["configuredMinutes"].AsInt(0);
            spinMinutesItem.Text = "设置停转时长…（配置 " + (cfgMinutes <= 0 ? "关闭" : cfgMinutes + " 分钟") + "）";
            spinMinutesItem.Enabled = !busy;

            // ---- 主开关 ----
            bool wantQuiet = WantQuiet();
            toggle.Text = wantQuiet ? "关闭静音模式" : "开启静音模式";
            bool canAct = !busy && configOk;
            toggle.Enabled = canAct;
            reapply.Enabled = canAct;
            bringOnline.Enabled = canAct && (off > 0 || !wantQuiet);
            if (!configOk)
            {
                toggle.Text = "配置不可用：" + FirstLine(status["config"]["error"].AsString(""));
                toggle.Enabled = false;
            }

            // ---- 图标：只反映观测 ----
            Icons.Kind kind;
            bool fanOk = !fanEnabled || fanMatch == (wantQuiet ? "silent" : "normal");
            bool diskOk = disks.Count == 0
                          || (wantQuiet ? (off == disks.Count && unknown == 0) : (online == disks.Count && unknown == 0));

            string verdict = status["state"]["lastVerdict"].AsString("");
            int fails = status["state"]["consecutiveFailures"].AsInt(0);

            if (!configOk || (!string.IsNullOrEmpty(lastError) && status.IsNull)) kind = Icons.Kind.Error;
            else if (verdict == "Failed" && fails > 0) kind = Icons.Kind.Error;
            else if (!fanOk || !diskOk) kind = Icons.Kind.Partial;
            else kind = wantQuiet ? Icons.Kind.Quiet : Icons.Kind.Normal;

            string tip = "静音:" + (wantQuiet ? "开" : "关")
                         + " 风扇:" + (fanEnabled ? (fanMatch == "silent" ? "静音" : fanMatch == "normal" ? "常态" : "?") : "未启用")
                         + " 硬盘:" + (disks.Count == 0 ? "未配置" : unknown > 0 ? "未知" : off == disks.Count ? "全脱机" : online == disks.Count ? "全在线" : "混合")
                         + " 停转:" + (ac <= 0 ? "关" : FmtDur(ac));
            if (verdict == "Failed") tip += " [!]";
            if (!elevated) tip += "";
            SetIcon(kind, tip);
        }

        private void SetIcon(Icons.Kind kind, string tip)
        {
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            try { ni.Text = tip; } catch { }
            if (lastIcon != null && lastIcon.Value == kind && ni.Icon != null) return;
            Icon old = ni.Icon;
            try { ni.Icon = Icons.Make(kind); }
            catch (Exception ex)
            {
                TrayLog.Error("icon draw failed", ex);
                try { ni.Icon = (Icon)SystemIcons.Application.Clone(); } catch { }
            }
            if (old != null) { try { old.Dispose(); } catch { } }
            lastIcon = kind;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOfAny(new char[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        private static string FmtDur(int sec)
        {
            if (sec <= 0) return "0 秒";
            if (sec % 60 == 0) return (sec / 60) + " 分钟";
            return sec + " 秒";
        }

        // ---------------------------------------------------------------- 动作

        private void Toggle()
        {
            if (busy) return;
            if (!QmctlClient.Available) { Balloon("不可用", QmctlClient.UnavailableReason, ToolTipIcon.Error); return; }
            bool wantQuiet = WantQuiet();
            DoApply(wantQuiet ? "manual-normal" : "manual-quiet",
                    wantQuiet ? "关闭静音模式" : "开启静音模式");
        }

        private void Reconcile(string action, string title)
        {
            if (busy) return;
            if (!QmctlClient.Available) { Balloon("不可用", QmctlClient.UnavailableReason, ToolTipIcon.Error); return; }
            RunAction(title, delegate
            {
                QmctlCall c = QmctlClient.ReconcileAction(action, true, "manual");
                return new ActionResult(c.Ok, DescribeReconcile(c));
            });
        }

        /// <summary>一次用户动作 = 一次提权调用（qmctl apply 内部写意图 + reconcile）。</summary>
        private void DoApply(string intent, string title)
        {
            if (busy) return;
            RunAction(title, delegate
            {
                QmctlCall c = QmctlClient.Run(new string[] { "apply", intent, "--apply" }, 360000, true);
                if (c.ElevationCancelled) return new ActionResult(false, "管理员权限被取消，状态未改变。", ToolTipIcon.Warning);
                return new ActionResult(c.Ok, DescribeReconcile(c), c.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            });
        }

        private void ToggleGate()
        {
            if (busy) return;
            bool enable = !status["schedule"]["enabled"].AsBool(true);
            RunAction("计划闸门：" + (enable ? "启用" : "关闭"), delegate
            {
                QmctlCall c = ConfigSetAuto("schedule.enabled", enable ? "true" : "false");
                if (c.ElevationCancelled) return new ActionResult(false, "管理员权限被取消，闸门未改动。", ToolTipIcon.Warning);
                if (!c.Ok) return new ActionResult(false, "闸门未改动：" + c.FailureText, ToolTipIcon.Error);
                return new ActionResult(true,
                    enable ? "已启用：夜间到点脱机、白天到点联机。"
                           : "已关闭：今晚不会自动脱机。正在脱机的盘需要手动「立即联机硬盘」。",
                    ToolTipIcon.Info);
            });
        }

        private void TogglePause()
        {
            if (busy) return;
            bool paused = status["state"]["schedulePaused"].AsBool(false);
            string intent = paused ? "resume" : "pause";
            RunAction(paused ? "恢复计划调度" : "暂停计划调度", delegate
            {
                QmctlCall c = QmctlClient.ApplyIntentLight(intent);
                if (c.ElevationCancelled) return new ActionResult(false, "管理员权限被取消，调度状态未改变。", ToolTipIcon.Warning);
                if (!c.Ok) return new ActionResult(false, "调度状态未改变：" + c.FailureText, ToolTipIcon.Warning);
                return new ActionResult(true, paused
                    ? "已恢复：下一次 reconcile 会按时间窗口正常进入夜间/白天。"
                    : "已暂停：任务仍在，到点不会动手；恢复后按当前时间重新判定。", ToolTipIcon.Info);
            });
        }

        private void ToggleSpin()
        {
            if (busy) return;
            int ac = status["spinDown"]["acSeconds"].AsInt(-1);
            int want;
            if (ac > 0) want = 0;
            else
            {
                want = status["spinDown"]["configuredMinutes"].AsInt(20);
                if (want <= 0) want = 20;
            }
            ApplySpinAsync(want, want > 0 ? "开启硬盘空闲停转" : "关闭硬盘空闲停转");
        }

        private void SetSpinMinutes()
        {
            if (busy) return;
            int cur = status["spinDown"]["configuredMinutes"].AsInt(20);
            if (cur <= 0) cur = 20;
            int v = Prompt.Number("硬盘空闲停转时长",
                "空闲多少分钟后让机械硬盘停转：\n0 = 永不（等同于关闭）", cur, 0, 180, " 分钟");
            if (v < 0) return;

            if (v == 0)
            {
                if (status["spinDown"]["acSeconds"].AsInt(0) > 0) ApplySpinAsync(0, "关闭硬盘空闲停转");
                else
                {
                    ConfigSetAuto("spinDown.minutes", "0");
                    ConfigSetAuto("spinDown.enabled", "false");
                    Balloon("停转已关闭", "DISKIDLE 本来就是 0，电源方案不用改。", ToolTipIcon.Info);
                    RefreshAsync(true);
                }
                return;
            }

            RunAction("保存停转时长", delegate
            {
                QmctlCall m = ConfigSetAuto("spinDown.minutes", v.ToString());
                if (!m.Ok) return new ActionResult(false, "写入配置失败：" + m.FailureText, ToolTipIcon.Error);
                if (status["spinDown"]["acSeconds"].AsInt(0) <= 0)
                    return new ActionResult(true, "已保存 " + v + " 分钟。当前停转是关闭状态，点「硬盘空闲停转」启用后生效。", ToolTipIcon.Info);
                QmctlCall c = QmctlClient.SpinSet(v, true);
                if (c.ElevationCancelled) return new ActionResult(false, "已保存配置，但管理员权限被取消，DISKIDLE 未改。", ToolTipIcon.Warning);
                return new ActionResult(c.Ok, c.Ok ? "DISKIDLE 已按 " + v + " 分钟生效。" : "DISKIDLE 未生效：" + c.FailureText,
                                        c.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            });
        }

        private void ApplySpinAsync(int minutes, string title)
        {
            RunAction(title, delegate
            {
                QmctlCall c = QmctlClient.SpinSet(minutes, true);
                if (c.ElevationCancelled) return new ActionResult(false, "管理员权限被取消，DISKIDLE 未改。", ToolTipIcon.Warning);
                if (!c.Ok) return new ActionResult(false, "DISKIDLE 未生效：" + c.FailureText, ToolTipIcon.Warning);

                // 写成功才同步配置，避免「配置说开、系统其实关」
                QmctlCall a = ConfigSetAuto("spinDown.enabled", minutes > 0 ? "true" : "false");
                if (minutes > 0) ConfigSetAuto("spinDown.minutes", minutes.ToString());
                string body = minutes > 0
                    ? "空闲 " + minutes + " 分钟后停转（交流/直流都生效，重启后保留）"
                    : "已关闭空闲停转（DISKIDLE = 0，永不停转）";
                if (!a.Ok) body += "\n（配置未同步：" + a.FailureText + "）";
                return new ActionResult(true, body, ToolTipIcon.Info);
            });
        }

        /// <summary>先按普通权限写配置；被拒绝再提权重试一次。用户取消就如实报取消。</summary>
        private QmctlCall ConfigSetAuto(string key, string value)
        {
            return QmctlClient.ConfigSetAuto(key, value);
        }

        private sealed class ActionResult
        {
            public readonly bool Ok;
            public readonly string Body;
            public readonly ToolTipIcon Icon;
            public ActionResult(bool ok, string body)
                : this(ok, body, ok ? ToolTipIcon.Info : ToolTipIcon.Warning) { }

            public ActionResult(bool ok, string body, ToolTipIcon icon)
            {
                Ok = ok;
                Body = body ?? "";
                Icon = icon;
            }
        }

        private void RunAction(string title, Func<ActionResult> action)
        {
            if (busy) return;
            busy = true;
            UpdateUi();
            Thread t = new Thread(delegate()
            {
                ActionResult r;
                try { r = action(); }
                catch (Exception ex)
                {
                    TrayLog.Error("action '" + title + "' failed", ex);
                    r = new ActionResult(false, "操作异常：" + ex.Message, ToolTipIcon.Error);
                }
                finally { busy = false; }
                Ui(delegate
                {
                    Balloon(title + (r.Ok ? "：完成" : "：未完成"), r.Body, r.Icon);
                    RefreshAsync(true);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>从 apply/reconcile 的 JSON 里取出「验证结论」，不自己判断成功。</summary>
        private static string DescribeReconcile(QmctlCall c)
        {
            if (c == null) return "没有返回结果";
            JNode j = c.Json;
            if (j.IsNull) return c.FailureText;

            JNode rec = j.Has("reconcile") ? j["reconcile"] : j;
            bool ok = rec["ok"].AsBool(false);
            string err = rec["error"].AsString("");
            JNode ver = rec["verification"];
            string verdictText = ver["verdictText"].AsString("");

            List<string> lines = new List<string>();
            if (ok)
            {
                string action = rec["action"].AsString("");
                lines.Add(action == "night" ? "已进入夜间：风扇静音档 + 目标盘脱机 + 维护任务暂停。"
                          : action == "day" ? "已恢复白天：目标盘联机 + 维护任务恢复 + 风扇常态。"
                          : action == "restore" ? "已恢复白天状态。"
                          : "已按当前状态应用，无需改动。");
            }
            else
            {
                if (!string.IsNullOrEmpty(err)) lines.Add(err);
                else lines.Add("验证未通过。");
            }
            if (!string.IsNullOrEmpty(verdictText)) lines.Add("验证：" + verdictText.Replace("\r", "").Replace("\n", "；"));
            return string.Join("\n", lines.ToArray());
        }

        private void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm(status))
            {
                if (f.ShowDialog() == DialogResult.OK) RefreshAsync(true);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using QuietMode.App;
using QuietMode.Core;

namespace QuietMode.Ui
{
    /// <summary>
    /// 设置窗口。它是 config.json 的一个「表单视图」：打开时读 qmctl config show，
    /// 保存时逐项 qmctl config set（不直接写文件，写入口只有 ConfigStore 一处）。
    /// 任何一项写入失败都会列出来，不假装整体成功。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private JNode cfg = JNode.Null;
        private readonly string loadError = "";

        private CheckBox cbFan, cbDisks, cbForce, cbMaint, cbStrict, cbSysMain, cbWake;
        private CheckBox cbSched, cbStartupOverride, cbAutoStart, cbRestore, cbSpin;
        private TextBox tbIpc, tbFcExe, tbNormal, tbSilent, tbFlag, tbNight, tbDay, tbStartupDay;
        private NumericUpDown nudMaintHour, nudGrace, nudInterval, nudSpin;
        private ComboBox cbMode;
        private Label lblResult;
        private JNode original = JNode.Null;

        public SettingsForm(JNode status)
        {
            Text = "QuietMode 设置（v2 配置：config.json）";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(640, 566);
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            QmctlCall c = QmctlClient.ConfigShow();
            if (c.Json.IsNull)
            {
                loadError = c.FailureText;
            }
            else
            {
                cfg = c.Json;
                original = c.Json;
            }

            TabControl tabs = new TabControl();
            tabs.Location = new Point(10, 10);
            tabs.Size = new Size(620, 486);
            Controls.Add(tabs);

            tabs.TabPages.Add(BuildFanTab());
            tabs.TabPages.Add(BuildDiskTab());
            tabs.TabPages.Add(BuildMaintTab());
            tabs.TabPages.Add(BuildSpinTab());
            tabs.TabPages.Add(BuildScheduleTab());
            tabs.TabPages.Add(BuildStartupTab());

            lblResult = new Label();
            lblResult.Location = new Point(12, 502);
            lblResult.Size = new Size(400, 20);
            lblResult.ForeColor = Color.Gray;
            lblResult.Text = string.IsNullOrEmpty(loadError) ? "配置已加载（config.json）" : "读取配置失败：" + loadError;
            Controls.Add(lblResult);

            Button save = new Button();
            save.Text = "保存";
            save.Location = new Point(424, 524);
            save.Size = new Size(92, 30);
            save.Click += delegate { Save(); };
            Controls.Add(save);
            AcceptButton = save;

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Location = new Point(526, 524);
            cancel.Size = new Size(92, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            if (!string.IsNullOrEmpty(loadError))
            {
                foreach (Control ctl in Controls) if (ctl is TabControl) ctl.Enabled = false;
                save.Enabled = false;
            }

            LoadFromConfig();
        }

        // ---------------------------------------------------------------- 选项卡

        private TabPage BuildFanTab()
        {
            TabPage p = Page("风扇（FanControl）");
            int y = 14;
            cbFan = Check(p, "启用风扇档位切换（需要 FanControl 正在运行）", 14, y, 560); y += 30;
            tbIpc = Tb(p, "IpcCtl.exe", y); Browse(p, tbIpc, y); y += 30;
            tbFcExe = Tb(p, "FanControl.exe", y); Browse(p, tbFcExe, y); y += 30;
            tbNormal = Tb(p, "常态配置", y); Browse(p, tbNormal, y); y += 30;
            tbSilent = Tb(p, "静音配置", y); Browse(p, tbSilent, y); y += 30;
            tbFlag = Tb(p, "静音标记文件（可选）", y); Browse(p, tbFlag, y); y += 34;
            Note(p, "切换由 qmctl reconcile 执行并回读 IpcCtl list 的 current= 验证，"
                   + "不是靠标记文件猜。", 14, y, 570);
            return p;
        }

        private TabPage BuildDiskTab()
        {
            TabPage p = Page("硬盘（脱机/联机）");
            int y = 14;
            cbDisks = Check(p, "启用目标盘脱机/联机（夜间脱机、白天恢复）", 14, y, 560); y += 30;
            cbForce = Check(p, "允许对「有占用」的盘强制执行（--force 语义，慎用）", 14, y, 560); y += 34;

            string names = "";
            JNode targets = cfg["disks"]["targets"];
            for (int i = 0; i < targets.Count; i++)
            {
                JNode t = targets[i];
                names += "· " + (t["friendlyName"].AsString("(未命名)"))
                         + "  serial=" + t["serialNumber"].AsString("")
                         + (t["enabled"].AsBool(true) ? "" : "  [已停用]") + "\r\n";
            }
            Note(p, "目标盘（身份按 uniqueId / serial 匹配，磁盘编号不参与）：\r\n"
                   + (names.Length == 0 ? "（配置里还没有目标盘，用 qmctl config migrate 或手改 config.json）" : names)
                   + "\r\n增删目标请用 qmctl config migrate / 直接编辑 config.json；这里只做开关。",
                 14, y, 570);
            return p;
        }

        private TabPage BuildMaintTab()
        {
            TabPage p = Page("维护任务");
            int y = 14;
            cbMaint = Check(p, "夜间暂停 Windows 维护任务（碎片整理 / 清理 / 检查等）", 14, y, 560); y += 30;
            cbStrict = Check(p, "严格模式：任何一项没停下来就判定夜间失败", 14, y, 560); y += 30;
            cbSysMain = Check(p, "夜间同时停用 SysMain（Superfetch）", 14, y, 560); y += 30;
            cbWake = Check(p, "夜间禁止维护任务唤醒本机", 14, y, 560); y += 34;

            Label l = new Label();
            l.Text = "维护时段（小时，0-23）：";
            l.Location = new Point(14, y + 3);
            l.Size = new Size(160, 20);
            p.Controls.Add(l);

            nudMaintHour = Num(p, 180, y, 0, 23);
            y += 34;
            Note(p, "白天恢复只按 baseline.json 里的原始值恢复；baseline 缺失时会拒绝执行，不会猜默认值。",
                 14, y, 570);
            return p;
        }

        private TabPage BuildSpinTab()
        {
            TabPage p = Page("硬盘空闲停转（DISKIDLE）");
            int y = 14;
            cbSpin = Check(p, "启用空闲停转（与静音模式解耦，开关静音不会改它）", 14, y, 560); y += 34;

            Label l = new Label();
            l.Text = "空闲多少分钟后停转（0 = 永不）：";
            l.Location = new Point(14, y + 3);
            l.Size = new Size(240, 20);
            p.Controls.Add(l);
            nudSpin = Num(p, 260, y, 0, 180);
            y += 40;

            Note(p, "保存时若当前 DISKIDLE 已开启，会立刻按新时长应用（需要管理员）；\r\n"
                   + "写入后回读 powercfg 验证，读不到就报失败，不当成功。", 14, y, 570);
            return p;
        }

        private TabPage BuildScheduleTab()
        {
            TabPage p = Page("调度");
            int y = 14;
            cbSched = Check(p, "启用自动调度（关闭 = 只手动切换，任务仍在但不会动手）", 14, y, 560); y += 32;

            Label l1 = new Label();
            l1.Text = "模式：";
            l1.Location = new Point(14, y + 3);
            l1.Size = new Size(60, 20);
            p.Controls.Add(l1);
            cbMode = new ComboBox();
            cbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cbMode.Items.Add("override（静音模式期间接管闸门）");
            cbMode.Items.Add("coexist（静音时不接管）");
            cbMode.Location = new Point(80, y);
            cbMode.Size = new Size(300, 23);
            p.Controls.Add(cbMode);
            y += 32;

            tbNight = Tb(p, "夜间开始（HH:mm）", y); y += 30;
            tbDay = Tb(p, "白天开始（HH:mm）", y); y += 32;

            cbStartupOverride = Check(p, "开机补偿：刚开机不久且已过「开机后白天起点」时按白天处理", 14, y, 560); y += 30;
            tbStartupDay = Tb(p, "开机后白天起点（HH:mm）", y); y += 30;

            Label l2 = new Label();
            l2.Text = "开机补偿窗口（分钟）：";
            l2.Location = new Point(14, y + 3);
            l2.Size = new Size(160, 20);
            p.Controls.Add(l2);
            nudGrace = Num(p, 180, y, 0, 120);
            y += 32;

            Label l3 = new Label();
            l3.Text = "巡检间隔（分钟）：";
            l3.Location = new Point(14, y + 3);
            l3.Size = new Size(130, 20);
            p.Controls.Add(l3);
            nudInterval = Num(p, 150, y, 1, 1440);
            y += 34;

            Note(p, "夜间/白天判定用完整 HH:mm 与跨午夜窗口，并带开机补偿；改完由下一次 reconcile 生效。",
                 14, y, 570);
            return p;
        }

        private TabPage BuildStartupTab()
        {
            TabPage p = Page("启动");
            int y = 14;
            cbAutoStart = Check(p, "开机自启托盘（HKCU\\...\\Run）", 14, y, 560); y += 30;
            cbRestore = Check(p, "开机恢复上次模式（关 = 交给统一调度对账）", 14, y, 560); y += 34;
            Note(p, "自启项由托盘维护，意图存在 config.json 的 startup.autoStart；\r\n"
                   + "「开机恢复上次模式」只影响托盘是否主动重放，正常应由 \\QuietMode\\QuietMode-Reconcile 的开机触发决定。",
                 14, y, 570);
            return p;
        }

        // ---------------------------------------------------------------- 读写

        private void LoadFromConfig()
        {
            if (cfg.IsNull) return;
            JNode f = cfg["fan"];
            cbFan.Checked = f["enabled"].AsBool(false);
            tbIpc.Text = f["ipcPath"].AsString("");
            tbFcExe.Text = f["controlExe"].AsString("");
            tbNormal.Text = f["normalProfile"].AsString("");
            tbSilent.Text = f["silentProfile"].AsString("");
            tbFlag.Text = f["flagPath"].AsString("");

            JNode d = cfg["disks"];
            cbDisks.Checked = d["enabled"].AsBool(true);
            cbForce.Checked = d["forceBusy"].AsBool(false);

            JNode m = cfg["maintenance"];
            cbMaint.Checked = m["enabled"].AsBool(true);
            cbStrict.Checked = m["strictNightDisable"].AsBool(true);
            cbSysMain.Checked = m["disableSysMainAtNight"].AsBool(true);
            cbWake.Checked = m["disableWake"].AsBool(true);
            nudMaintHour.Value = Clamp(m["maintenanceHour"].AsInt(10), 0, 23);

            JNode sp = cfg["spinDown"];
            cbSpin.Checked = sp["enabled"].AsBool(false);
            nudSpin.Value = Clamp(sp["minutes"].AsInt(20), 0, 180);

            JNode sc = cfg["schedule"];
            cbSched.Checked = sc["enabled"].AsBool(true);
            cbMode.SelectedIndex = sc["mode"].AsString("override") == "coexist" ? 1 : 0;
            tbNight.Text = sc["nightStart"].AsString("23:58");
            tbDay.Text = sc["dayStart"].AsString("10:02");
            cbStartupOverride.Checked = sc["startupOverrideEnabled"].AsBool(true);
            tbStartupDay.Text = sc["startupDayStart"].AsString("06:00");
            nudGrace.Value = Clamp(sc["startupGraceMinutes"].AsInt(15), 0, 120);
            nudInterval.Value = Clamp(sc["reconcileIntervalMinutes"].AsInt(10), 1, 1440);

            JNode st = cfg["startup"];
            cbAutoStart.Checked = st["autoStart"].AsBool(false);
            cbRestore.Checked = st["restoreOnBoot"].AsBool(false);
        }

        private void Save()
        {
            if (cfg.IsNull) return;

            List<string[]> pending = new List<string[]>();
            AddIfChanged(pending, "fan.enabled", BoolText(cbFan.Checked), cfg["fan"]["enabled"].AsBool(false) ? "true" : "false");
            AddIfChanged(pending, "fan.ipcPath", tbIpc.Text.Trim(), cfg["fan"]["ipcPath"].AsString(""));
            AddIfChanged(pending, "fan.controlExe", tbFcExe.Text.Trim(), cfg["fan"]["controlExe"].AsString(""));
            AddIfChanged(pending, "fan.normalProfile", tbNormal.Text.Trim(), cfg["fan"]["normalProfile"].AsString(""));
            AddIfChanged(pending, "fan.silentProfile", tbSilent.Text.Trim(), cfg["fan"]["silentProfile"].AsString(""));
            AddIfChanged(pending, "fan.flagPath", tbFlag.Text.Trim(), cfg["fan"]["flagPath"].AsString(""));

            AddIfChanged(pending, "disks.enabled", BoolText(cbDisks.Checked), cfg["disks"]["enabled"].AsBool(true) ? "true" : "false");
            AddIfChanged(pending, "disks.forceBusy", BoolText(cbForce.Checked), cfg["disks"]["forceBusy"].AsBool(false) ? "true" : "false");

            AddIfChanged(pending, "maintenance.enabled", BoolText(cbMaint.Checked), cfg["maintenance"]["enabled"].AsBool(true) ? "true" : "false");
            AddIfChanged(pending, "maintenance.strictNightDisable", BoolText(cbStrict.Checked), cfg["maintenance"]["strictNightDisable"].AsBool(true) ? "true" : "false");
            AddIfChanged(pending, "maintenance.disableSysMainAtNight", BoolText(cbSysMain.Checked), cfg["maintenance"]["disableSysMainAtNight"].AsBool(true) ? "true" : "false");
            AddIfChanged(pending, "maintenance.disableWake", BoolText(cbWake.Checked), cfg["maintenance"]["disableWake"].AsBool(true) ? "true" : "false");
            AddIfChanged(pending, "maintenance.maintenanceHour", ((int)nudMaintHour.Value).ToString(), cfg["maintenance"]["maintenanceHour"].AsInt(10).ToString());

            AddIfChanged(pending, "schedule.enabled", BoolText(cbSched.Checked), cfg["schedule"]["enabled"].AsBool(true) ? "true" : "false");
            AddIfChanged(pending, "schedule.mode", cbMode.SelectedIndex == 1 ? "coexist" : "override", cfg["schedule"]["mode"].AsString("override"));
            AddIfChanged(pending, "schedule.nightStart", tbNight.Text.Trim(), cfg["schedule"]["nightStart"].AsString("23:58"));
            AddIfChanged(pending, "schedule.dayStart", tbDay.Text.Trim(), cfg["schedule"]["dayStart"].AsString("10:02"));
            AddIfChanged(pending, "schedule.startupOverrideEnabled", BoolText(cbStartupOverride.Checked), cfg["schedule"]["startupOverrideEnabled"].AsBool(true) ? "true" : "false");
            AddIfChanged(pending, "schedule.startupDayStart", tbStartupDay.Text.Trim(), cfg["schedule"]["startupDayStart"].AsString("06:00"));
            AddIfChanged(pending, "schedule.startupGraceMinutes", ((int)nudGrace.Value).ToString(), cfg["schedule"]["startupGraceMinutes"].AsInt(15).ToString());
            AddIfChanged(pending, "schedule.reconcileIntervalMinutes", ((int)nudInterval.Value).ToString(), cfg["schedule"]["reconcileIntervalMinutes"].AsInt(10).ToString());

            AddIfChanged(pending, "startup.autoStart", BoolText(cbAutoStart.Checked), cfg["startup"]["autoStart"].AsBool(false) ? "true" : "false");
            AddIfChanged(pending, "startup.restoreOnBoot", BoolText(cbRestore.Checked), cfg["startup"]["restoreOnBoot"].AsBool(false) ? "true" : "false");

            int beforeSpin = pending.Count;
            AddIfChanged(pending, "spinDown.enabled", BoolText(cbSpin.Checked), cfg["spinDown"]["enabled"].AsBool(false) ? "true" : "false");
            AddIfChanged(pending, "spinDown.minutes", ((int)nudSpin.Value).ToString(), cfg["spinDown"]["minutes"].AsInt(20).ToString());
            bool spinChanged = pending.Count > beforeSpin;

            if (pending.Count == 0)
            {
                lblResult.Text = "没有改动。";
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            List<string> failed = new List<string>();
            bool elevationCancelled = false;
            bool autoStartWritten = false;
            foreach (string[] kv in pending)
            {
                if (elevationCancelled) { failed.Add(kv[0] + " = " + kv[1] + "：未执行（前一项取消提权）"); continue; }
                QmctlCall c = QmctlClient.ConfigSetAuto(kv[0], kv[1]);
                if (c.ElevationCancelled) { elevationCancelled = true; failed.Add(kv[0] + " = " + kv[1] + "：管理员权限被取消"); continue; }
                if (!c.Ok) { failed.Add(kv[0] + " = " + kv[1] + "：" + c.FailureText); continue; }
                if (kv[0] == "startup.autoStart") autoStartWritten = true;
            }

            // 自启是 shell 层的事：配置写成功后才动注册表，避免「配置没存却改了自启」
            if (autoStartWritten) AutoStart.Set(cbAutoStart.Checked);

            // 停转：配置存好了但 DISKIDLE 还没动，明确提示，不假装已生效
            if (failed.Count == 0 && spinChanged && !cbSpin.Checked)
            {
                QmctlCall off = QmctlClient.SpinSet(0, true);
                if (!off.Ok)
                    failed.Add("停转已关闭，但 DISKIDLE 未归零：" + off.FailureText);
            }
            if (failed.Count == 0 && spinChanged && cbSpin.Checked && nudSpin.Value > 0)
            {
                QmctlCall s = QmctlClient.SpinSet((int)nudSpin.Value, true);
                if (!s.Ok)
                    failed.Add("spinDown 已保存，但 DISKIDLE 未应用：" + s.FailureText
                               + (s.ElevationCancelled ? "（管理员权限被取消）" : ""));
            }

            if (failed.Count == 0)
            {
                lblResult.Text = "已保存 " + pending.Count + " 项。";
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            lblResult.Text = "有 " + failed.Count + " 项未生效：";
            MessageBox.Show("以下改动未生效：\n\n" + string.Join("\n", failed.ToArray())
                            + (elevationCancelled ? "\n\n提示：取消 UAC 后不会写入任何配置。" : ""),
                "QuietMode 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static void AddIfChanged(List<string[]> list, string key, string value, string current)
        {
            if (string.Equals(value ?? "", current ?? "", StringComparison.Ordinal)) return;
            list.Add(new string[] { key, value ?? "" });
        }

        private static string BoolText(bool b) { return b ? "true" : "false"; }

        private static decimal Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // ---------------------------------------------------------------- 控件工厂

        private static TabPage Page(string title)
        {
            TabPage p = new TabPage(title);
            p.Padding = new Padding(3);
            p.BackColor = Color.White;
            return p;
        }

        private static CheckBox Check(Control parent, string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Location = new Point(x, y);
            c.Size = new Size(w, 22);
            parent.Controls.Add(c);
            return c;
        }

        /// <summary>左标签 + 右文本框（y 为行顶）。</summary>
        private static TextBox Tb(Control parent, string label, int y)
        {
            Label l = new Label();
            l.Text = label;
            l.Location = new Point(14, y + 3);
            l.Size = new Size(160, 20);
            parent.Controls.Add(l);

            TextBox t = new TextBox();
            t.Location = new Point(180, y);
            t.Size = new Size(338, 23);
            parent.Controls.Add(t);
            return t;
        }

        private static NumericUpDown Num(Control parent, int x, int y, int min, int max)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Location = new Point(x, y);
            n.Size = new Size(90, 23);
            parent.Controls.Add(n);
            return n;
        }

        private static void Browse(Control parent, TextBox target, int y)
        {
            Button b = new Button();
            b.Text = "浏览…";
            b.Location = new Point(524, y - 1);
            b.Size = new Size(76, 25);
            b.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog())
                {
                    d.Filter = "所有文件|*.*";
                    string cur = target.Text.Trim();
                    if (cur.Length > 0)
                    {
                        try { d.InitialDirectory = Path.GetDirectoryName(cur); } catch { }
                    }
                    if (d.ShowDialog() == DialogResult.OK) target.Text = d.FileName;
                }
            };
            parent.Controls.Add(b);
        }

        private static void Note(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Color.FromArgb(110, 110, 110);
            l.Location = new Point(x, y);
            l.Size = new Size(w, 90);
            parent.Controls.Add(l);
        }
    }
}

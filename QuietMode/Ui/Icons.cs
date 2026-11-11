using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuietMode.Ui
{
    /// <summary>
    /// 托盘图标。四种形态直接对应「观测到的实际状态」，不是「我们以为的状态」：
    ///   Quiet   绿色月亮 = 静音已生效（风扇静音档 + 目标盘脱机）
    ///   Normal  蓝色风扇 = 白天正常
    ///   Partial 琥珀感叹 = 只做到一半 / 状态未知
    ///   Error   红色感叹 = 读不到或上次执行失败
    /// </summary>
    internal static class Icons
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public enum Kind { Quiet, Normal, Partial, Error }

        public static Icon Make(Kind k)
        {
            Bitmap bmp = new Bitmap(32, 32);
            Color bg;
            switch (k)
            {
                case Kind.Quiet: bg = Color.FromArgb(38, 148, 96); break;
                case Kind.Normal: bg = Color.FromArgb(70, 105, 150); break;
                case Kind.Partial: bg = Color.FromArgb(214, 145, 40); break;
                default: bg = Color.FromArgb(190, 60, 55); break;
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush br = new SolidBrush(bg)) g.FillEllipse(br, 1, 1, 29, 29);
                using (SolidBrush wh = new SolidBrush(Color.White))
                {
                    if (k == Kind.Quiet)
                    {
                        // 弯月
                        g.FillEllipse(wh, 8, 6, 19, 19);
                        using (SolidBrush cut = new SolidBrush(bg)) g.FillEllipse(cut, 14, 3, 19, 19);
                    }
                    else if (k == Kind.Normal)
                    {
                        // 三叶风扇
                        GraphicsState st = g.Save();
                        g.TranslateTransform(16, 16);
                        for (int i = 0; i < 3; i++)
                        {
                            g.RotateTransform(120);
                            g.FillEllipse(wh, 1, -3.2f, 12, 6.4f);
                        }
                        g.Restore(st);
                        g.FillEllipse(wh, 13.5f, 13.5f, 5, 5);
                    }
                    else
                    {
                        // 感叹号
                        g.FillRectangle(wh, 14, 8, 4, 12);
                        g.FillEllipse(wh, 14, 22, 4, 4);
                    }
                }
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            bmp.Dispose();
            return ic;
        }
    }

    /// <summary>极简数字输入框（WinForms 没有内置 InputBox）。取消返回 -1。</summary>
    internal static class Prompt
    {
        public static int Number(string title, string text, int value, int min, int max, string suffix)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = true;
                f.TopMost = true;
                f.ClientSize = new Size(396, 156);
                f.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

                Label l = new Label();
                l.Text = text;
                l.Location = new Point(14, 14);
                l.Size = new Size(366, 40);
                f.Controls.Add(l);

                NumericUpDown n = new NumericUpDown();
                n.Minimum = min;
                n.Maximum = max;
                n.Value = Math.Max(min, Math.Min(max, value));
                n.Location = new Point(14, 62);
                n.Size = new Size(92, 23);
                f.Controls.Add(n);

                Label s = new Label();
                s.Text = suffix;
                s.Location = new Point(112, 64);
                s.Size = new Size(70, 20);
                f.Controls.Add(s);

                Label hint = new Label();
                hint.Text = "范围 " + min + " - " + max;
                hint.ForeColor = Color.Gray;
                hint.Location = new Point(190, 64);
                hint.Size = new Size(120, 20);
                f.Controls.Add(hint);

                Button ok = new Button();
                ok.Text = "确定";
                ok.Location = new Point(202, 108);
                ok.Size = new Size(84, 28);
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.Location = new Point(294, 108);
                cancel.Size = new Size(84, 28);
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(cancel);
                f.CancelButton = cancel;

                if (f.ShowDialog() != DialogResult.OK) return -1;
                return (int)n.Value;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MonitorCtl
{
    /// <summary>
    /// 设置窗口：一页调画面（DDC 能控的那几项），一页编快捷键。
    /// 画面项按"显示器到底开放了什么"生成——低蓝光/锐利度/动态对比度 显示器没开放给 PC，
    /// 这里如实写清楚，不放假滑块。
    /// </summary>
    public sealed class SettingsForm : Form
    {
        readonly Config cfg;
        readonly MonitorEntry mon;

        TrackBar barBright, barContrast, barBlue;
        Label lbBright, lbContrast, lbBlue;
        ComboBox cbGamma, cbScene;
        CheckBox chkHdr;
        Label lbHdrInfo, lbNotice, lbStatus;
        readonly Timer debounce = new Timer();
        bool loading = true;

        // 快捷键页
        readonly Dictionary<string, TextBox> boxes = new Dictionary<string, TextBox>();

        public SettingsForm(Config c, MonitorEntry m)
        {
            cfg = c;
            mon = m;
            Text = "monitorctl 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 600);
            Font = new Font("Microsoft YaHei UI", 9f);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildScreenPage());
            tabs.TabPages.Add(BuildHotkeyPage());

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 46;
            Button ok = new Button();
            ok.Text = "保存并关闭";
            ok.Width = 110;
            ok.Height = 30;
            ok.Left = 460 - 240;
            ok.Top = 8;
            ok.Click += delegate { SaveAll(); Close(); };
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Width = 80;
            cancel.Height = 30;
            cancel.Left = 460 - 118;
            cancel.Top = 8;
            cancel.Click += delegate { Close(); };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            Controls.Add(tabs);
            Controls.Add(bottom);

            debounce.Interval = 260;
            debounce.Tick += delegate
            {
                debounce.Stop();
                ApplyLight();
            };
        }

        TabPage BuildScreenPage()
        {
            TabPage page = new TabPage("画面 / 显示");
            page.AutoScroll = true;
            page.Padding = new Padding(12);
            int y = 10;

            // 亮度
            Label l1 = new Label(); l1.Text = "亮度"; l1.Left = 6; l1.Top = y; l1.Width = 60;
            lbBright = new Label(); lbBright.Left = 70; lbBright.Top = y; lbBright.Width = 60;
            barBright = new TrackBar(); barBright.Left = 6; barBright.Top = y + 20; barBright.Width = 380; barBright.Height = 40;
            barBright.TickStyle = TickStyle.None;
            barBright.Scroll += delegate { lbBright.Text = barBright.Value.ToString(); Arm(); };
            page.Controls.Add(l1); page.Controls.Add(lbBright); page.Controls.Add(barBright);
            y += 66;

            // 对比度
            Label l2 = new Label(); l2.Text = "对比度"; l2.Left = 6; l2.Top = y; l2.Width = 60;
            lbContrast = new Label(); lbContrast.Left = 70; lbContrast.Top = y; lbContrast.Width = 60;
            barContrast = new TrackBar(); barContrast.Left = 6; barContrast.Top = y + 20; barContrast.Width = 380; barContrast.Height = 40;
            barContrast.TickStyle = TickStyle.None;
            barContrast.Scroll += delegate { lbContrast.Text = barContrast.Value.ToString(); Arm(); };
            page.Controls.Add(l2); page.Controls.Add(lbContrast); page.Controls.Add(barContrast);
            y += 66;

            // 低蓝光（软件滤镜，走显卡 gamma ramp，不是显示器 DDC）
            Label lb = new Label(); lb.Text = "低蓝光（软件）"; lb.Left = 6; lb.Top = y; lb.Width = 100;
            lbBlue = new Label(); lbBlue.Left = 110; lbBlue.Top = y; lbBlue.Width = 60;
            barBlue = new TrackBar(); barBlue.Left = 6; barBlue.Top = y + 20; barBlue.Width = 380; barBlue.Height = 40;
            barBlue.TickStyle = TickStyle.None;
            barBlue.Minimum = 0; barBlue.Maximum = 100; barBlue.SmallChange = 5; barBlue.LargeChange = 10;
            barBlue.Scroll += delegate { lbBlue.Text = barBlue.Value.ToString(); Arm(); };
            page.Controls.Add(lb); page.Controls.Add(lbBlue); page.Controls.Add(barBlue);
            y += 66;

            // 伽马
            Label l3 = new Label(); l3.Text = "伽马"; l3.Left = 6; l3.Top = y + 4; l3.Width = 60;
            cbGamma = new ComboBox(); cbGamma.Left = 70; cbGamma.Top = y; cbGamma.Width = 100;
            cbGamma.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < Cli.GammaNames.Length; i++) cbGamma.Items.Add(Cli.GammaNames[i]);
            cbGamma.SelectedIndexChanged += delegate { if (!loading) ApplyGamma(); };
            page.Controls.Add(l3); page.Controls.Add(cbGamma);

            // 情景模式
            Label l4 = new Label(); l4.Text = "情景模式"; l4.Left = 200; l4.Top = y + 4; l4.Width = 70;
            cbScene = new ComboBox(); cbScene.Left = 280; cbScene.Top = y; cbScene.Width = 100;
            cbScene.DropDownStyle = ComboBoxStyle.DropDownList;
            cbScene.SelectedIndexChanged += delegate { if (!loading) ApplyScene(); };
            page.Controls.Add(l4); page.Controls.Add(cbScene);
            y += 40;

            // HDR
            chkHdr = new CheckBox(); chkHdr.Text = "HDR"; chkHdr.Left = 6; chkHdr.Top = y; chkHdr.Width = 80;
            chkHdr.CheckedChanged += delegate { if (!loading) ApplyHdr(); };
            lbHdrInfo = new Label(); lbHdrInfo.Left = 90; lbHdrInfo.Top = y + 3; lbHdrInfo.Width = 340;
            lbHdrInfo.ForeColor = Color.DimGray;
            page.Controls.Add(chkHdr); page.Controls.Add(lbHdrInfo);
            y += 34;

            // 输入源
            Label l5 = new Label(); l5.Text = "切换输入源（切走后本机就控制不了了）"; l5.Left = 6; l5.Top = y + 4; l5.Width = 400;
            page.Controls.Add(l5);
            y += 26;
            for (int i = 0; i < Vcp.InputNames.Length; i++)
            {
                string nm = Vcp.InputNames[i];
                Button b = new Button();
                b.Text = nm.ToUpperInvariant();
                b.Left = 6 + i * 96;
                b.Top = y;
                b.Width = 88;
                b.Height = 28;
                b.Click += delegate { Threaded(delegate { Cli.DoInput(cfg, new string[] { "input", nm }); }); };
                page.Controls.Add(b);
            }
            y += 42;

            // 说明
            lbNotice = new Label();
            lbNotice.Left = 6;
            lbNotice.Top = y;
            lbNotice.Width = 420;
            lbNotice.Height = 80;
            lbNotice.ForeColor = Color.DimGray;
            lbNotice.Text = "低蓝光：显示器没把这档开放给 PC（DDC 能力串里没有对应码），上面那根滑块是显卡 gamma 软件滤镜，\r\n"
                + "HDR 开着时不生效。锐利度 / 动态对比度 同样只能按显示器按键调，这里不做假滑块。\r\n"
                + "HDR 走 Windows 高级颜色接口，切换时会黑屏 1～2 秒。";
            page.Controls.Add(lbNotice);

            lbStatus = new Label();
            lbStatus.Left = 6;
            lbStatus.Top = y + 78;
            lbStatus.Width = 420;
            lbStatus.Height = 44;
            lbStatus.ForeColor = Color.DarkGreen;
            page.Controls.Add(lbStatus);

            LoadCurrent();
            return page;
        }

        TabPage BuildHotkeyPage()
        {
            TabPage page = new TabPage("快捷键");
            page.Padding = new Padding(10);
            page.AutoScroll = true;

            Label tip = new Label();
            tip.Text = "点进输入框，直接按组合键即可（必须带 Alt/Ctrl/Shift/Win 至少一个）。留空＝不绑定。";
            tip.Left = 6; tip.Top = 6; tip.Width = 430; tip.Height = 34;
            tip.ForeColor = Color.DimGray;
            page.Controls.Add(tip);

            int y = 46;
            for (int i = 0; i < Config.Actions.Length; i++)
            {
                string key = Config.Actions[i][0];
                string label = Config.Actions[i][1];
                bool peer = Config.Actions[i][2] == "peer";

                Label lb = new Label();
                lb.Text = label + (peer ? "  [对端]" : "");
                lb.Left = 6; lb.Top = y + 3; lb.Width = 150;
                if (peer) lb.ForeColor = Color.DarkOrange;

                TextBox tb = new TextBox();
                tb.Left = 160; tb.Top = y; tb.Width = 190;
                tb.ReadOnly = true;
                tb.Text = cfg.GetHk(key);
                tb.Tag = key;
                tb.KeyDown += HotkeyKeyDown;
                tb.KeyUp += delegate(object s, KeyEventArgs e) { e.SuppressKeyPress = true; };
                tb.Enter += delegate(object s, EventArgs e) { ((TextBox)s).BackColor = Color.FromArgb(255, 255, 225); };
                tb.Leave += delegate(object s, EventArgs e) { ((TextBox)s).BackColor = SystemColors.Window; };
                boxes[key] = tb;

                Button clr = new Button();
                clr.Text = "清除";
                clr.Left = 356; clr.Top = y - 1; clr.Width = 52; clr.Height = 24;
                clr.Tag = key;
                clr.Click += delegate(object s, EventArgs e)
                {
                    string k = (string)((Button)s).Tag;
                    boxes[k].Text = "";
                };

                page.Controls.Add(lb);
                page.Controls.Add(tb);
                page.Controls.Add(clr);
                y += 30;
            }

            Button def = new Button();
            def.Text = "恢复默认热键";
            def.Left = 6; def.Top = y + 10; def.Width = 120; def.Height = 28;
            def.Click += delegate
            {
                for (int i = 0; i < Config.Actions.Length; i++)
                {
                    string k = Config.Actions[i][0];
                    boxes[k].Text = Config.DefaultHk(k);
                }
            };
            page.Controls.Add(def);

            Label peerTip = new Label();
            peerTip.Text = "［对端］＝ 委托另一台主机执行的动作（后端尚未实现，只保留动作名与按键）：\r\n没填 [peer] ssh 或后端未实现时，按下去会明确报错，不会假装成功。";
            peerTip.Left = 6; peerTip.Top = y + 46; peerTip.Width = 430; peerTip.Height = 40;
            peerTip.ForeColor = Color.DimGray;
            page.Controls.Add(peerTip);

            return page;
        }

        void HotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            TextBox tb = (TextBox)sender;
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey
                || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin || e.KeyCode == Keys.None)
                return;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                tb.Text = "";
                return;
            }

            List<string> parts = new List<string>();
            if ((e.Modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((e.Modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((e.Modifiers & Keys.Shift) != 0) parts.Add("Shift");
            if (parts.Count == 0) return; // 必须有修饰键，否则会吃掉全局输入
            parts.Add(e.KeyCode.ToString());
            tb.Text = string.Join("+", parts.ToArray());
        }

        void LoadCurrent()
        {
            loading = true;
            try
            {
                uint cur, max;
                int err;
                if (Ddc.ReadVcp(mon, Vcp.Brightness, out cur, out max, out err))
                {
                    barBright.Maximum = (int)(max > 0 ? max : 100);
                    barBright.Value = (int)Math.Min(cur, (uint)barBright.Maximum);
                    lbBright.Text = cur.ToString();
                }
                else lbBright.Text = "读取失败";

                if (Ddc.ReadVcp(mon, Vcp.Contrast, out cur, out max, out err))
                {
                    barContrast.Maximum = (int)(max > 0 ? max : 100);
                    barContrast.Value = (int)Math.Min(cur, (uint)barContrast.Maximum);
                    lbContrast.Text = cur.ToString();
                }
                else lbContrast.Text = "读取失败";

                barBlue.Value = Math.Max(0, Math.Min(100, cfg.Blue));
                lbBlue.Text = barBlue.Value.ToString();
                lastBlue = barBlue.Value;

                if (Ddc.ReadVcp(mon, Vcp.Gamma, out cur, out max, out err) && cur < Cli.GammaNames.Length)
                    cbGamma.SelectedIndex = (int)cur;
                else
                {
                    cbGamma.Enabled = false;
                    cbGamma.Items.Add("不可读");
                    cbGamma.SelectedIndex = 0;
                }

                if (Ddc.ReadVcp(mon, Vcp.SceneMode, out cur, out max, out err))
                {
                    for (uint i = 0; i <= max && i <= 11; i++) cbScene.Items.Add(Cli.SceneName(i));
                    if (cur < (uint)cbScene.Items.Count) cbScene.SelectedIndex = (int)cur;
                }
                else cbScene.Enabled = false;

                Hdr.State hs = Hdr.Get(mon.Device);
                if (!hs.Found) { chkHdr.Enabled = false; lbHdrInfo.Text = hs.ErrorText; }
                else if (!hs.Supported) { chkHdr.Enabled = false; lbHdrInfo.Text = "显示器/接口不支持 HDR"; }
                else
                {
                    chkHdr.Checked = hs.Enabled;
                    lbHdrInfo.Text = hs.TargetName + "  当前 " + (hs.Enabled ? "开" : "关");
                }
            }
            catch (Exception ex) { Log.Write("设置窗口读取失败 " + ex.Message); }
            loading = false;
        }

        void Arm()
        {
            if (loading) return;
            debounce.Stop();
            debounce.Start();
        }

        void ApplyLight()
        {
            ApplyChecked(Vcp.Brightness, (uint)barBright.Value, "亮度");
            ApplyChecked(Vcp.Contrast, (uint)barContrast.Value, "对比度");
            ApplyBlue();
        }

        int lastBlue = -1;

        /// <summary>低蓝光是软件滤镜：写显卡 gamma ramp，只在这个值真的变了时才写。</summary>
        void ApplyBlue()
        {
            int v = barBlue.Value;
            if (v == lastBlue) return;
            string detail;
            if (!BlueFilter.Apply(mon.Device, v, out detail))
            {
                Note(detail, true);
                return;
            }
            lastBlue = v;
            cfg.Blue = v;
            cfg.Save();
            Note(detail, false);
        }

        /// <summary>写一个连续量并回读校验。写不进时如实提示，不假装成功。</summary>
        void ApplyChecked(byte code, uint want, string label)
        {
            int err;
            if (!Ddc.WriteVcp(mon, code, want, out err))
            {
                Note(label + " 写入失败：" + Ddc.ErrText(err), true);
                return;
            }
            uint cur = 0, max = 0;
            int rerr = 0;
            bool okRead = false;
            for (int i = 0; i < 3; i++)
            {
                if (Ddc.ReadVcp(mon, code, out cur, out max, out rerr)) { okRead = true; if (cur == want) break; }
                System.Threading.Thread.Sleep(120);
            }
            if (okRead && cur != want)
            {
                Note(label + " 写不进：写入被接受，但回读仍是 " + cur + "。\r\n"
                    + "OSD 里开着「动态对比度」时，亮度会被锁住（OSD 里亮度那一项会变灰），关掉它再试。", true);
                Log.Write("设置窗口 " + label + " 未生效 want=" + want + " after=" + cur);
                return;
            }
            Note(label + " 已写入 " + want + (okRead ? "" : "（回读失败，但写入被接受）"), false);
        }

        void Note(string text, bool warn)
        {
            if (lbStatus == null) return;
            lbStatus.ForeColor = warn ? Color.FromArgb(200, 90, 0) : Color.DarkGreen;
            lbStatus.Text = text;
        }

        void ApplyGamma()
        {
            int idx = cbGamma.SelectedIndex;
            if (idx < 0) return;
            int err;
            if (!Ddc.WriteVcp(mon, Vcp.Gamma, (uint)idx, out err))
                Log.Write("设置窗口写伽马失败 " + Ddc.ErrText(err));
        }

        void ApplyScene()
        {
            int idx = cbScene.SelectedIndex;
            if (idx < 0) return;
            int err;
            if (!Ddc.WriteVcp(mon, Vcp.SceneMode, (uint)idx, out err))
                Log.Write("设置窗口写情景模式失败 " + Ddc.ErrText(err));
        }

        void ApplyHdr()
        {
            bool want = chkHdr.Checked;
            string detail;
            int rc = Hdr.Set(mon.Device, want, out detail);
            if (rc != 0)
            {
                MessageBox.Show(detail, "HDR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                loading = true;
                chkHdr.Checked = !want;
                loading = false;
                return;
            }
            lbHdrInfo.Text = detail;
        }

        void SaveAll()
        {
            // 先落盘，失败的热键在重新注册时会被日志与气泡暴露
            for (int i = 0; i < Config.Actions.Length; i++)
            {
                string k = Config.Actions[i][0];
                cfg.SetHk(k, boxes[k].Text.Trim());
            }
            cfg.Save();
            TrayHost.ReloadHotkeys();

            // 亮度/对比度以滑块为准，避免"拖动后立刻关窗口"丢掉最后一次
            ApplyLight();
        }

        void Threaded(Action a)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { a(); } catch (Exception ex) { Log.Write("设置窗口动作异常 " + ex.Message); }
            });
        }
    }
}

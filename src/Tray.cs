using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MonitorCtl
{
    /// <summary>托盘常驻 + 全局热键。所有动作最终都走 Cli，保证与命令行行为一致。</summary>
    public static class TrayHost
    {
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x1;
        const uint MOD_CTRL = 0x2;
        const uint MOD_SHIFT = 0x4;
        const uint MOD_WIN = 0x8;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        static Config cfg;
        static NotifyIcon ni;
        static MessageForm form;
        static Icon trayIcon;
        static readonly List<int> ids = new List<int>();
        static ToolStripMenuItem statusItem;

        sealed class MessageForm : Form
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    ThreadPool.QueueUserWorkItem(delegate { TrayHost.OnHotkey(id); });
                    return;
                }
                base.WndProc(ref m);
            }
        }

        public static int Run(Config c)
        {
            cfg = c;
            Log.Verbose = c.DebugLog;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            form = new MessageForm();
            form.ShowInTaskbar = false;
            form.FormBorderStyle = FormBorderStyle.None;
            form.WindowState = FormWindowState.Minimized;
            IntPtr unused = form.Handle; // 强制创建句柄，热键需要它

            trayIcon = MakeIcon();
            ContextMenuStrip menu = BuildMenu();

            ni = new NotifyIcon();
            ni.Icon = trayIcon;
            ni.Text = "monitorctl";
            ni.Visible = true;
            ni.ContextMenuStrip = menu;
            ni.DoubleClick += delegate { ThreadPool.QueueUserWorkItem(delegate { ShowStatusBalloon(); }); };

            RegisterAll();
            Log.Info("托盘启动，热键已注册 " + ids.Count + " 个");

            Application.Run(new ApplicationContext());
            return 0;
        }

        static ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("（读取中…）");
            statusItem.Enabled = false;
            m.Items.Add(statusItem);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new ToolStripMenuItem("设置…");
            settings.Click += delegate { ShowSettings(); };
            m.Items.Add(settings);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem up = new ToolStripMenuItem("亮度 +" + cfg.Step);
            up.Click += delegate { RunAsync("up", delegate { return Cli.DoBrightness(cfg, new string[] { "brightness", "+" + cfg.Step }); }); };
            m.Items.Add(up);

            ToolStripMenuItem down = new ToolStripMenuItem("亮度 -" + cfg.Step);
            down.Click += delegate { RunAsync("down", delegate { return Cli.DoBrightness(cfg, new string[] { "brightness", "-" + cfg.Step }); }); };
            m.Items.Add(down);

            ToolStripMenuItem cup = new ToolStripMenuItem("对比度 +" + cfg.Step);
            cup.Click += delegate { RunAsync("contrast_up", delegate { return Cli.DoContrast(cfg, new string[] { "contrast", "+" + cfg.Step }); }); };
            m.Items.Add(cup);

            ToolStripMenuItem cdown = new ToolStripMenuItem("对比度 -" + cfg.Step);
            cdown.Click += delegate { RunAsync("contrast_down", delegate { return Cli.DoContrast(cfg, new string[] { "contrast", "-" + cfg.Step }); }); };
            m.Items.Add(cdown);

            ToolStripMenuItem gamma = new ToolStripMenuItem("伽马 下一档");
            gamma.Click += delegate { RunAsync("gamma_next", delegate { return Cli.DoGamma(cfg, new string[] { "gamma", "next" }); }); };
            m.Items.Add(gamma);

            ToolStripMenuItem scene = new ToolStripMenuItem("情景模式 下一档");
            scene.Click += delegate { RunAsync("scene_next", delegate { return Cli.DoScene(cfg, new string[] { "scene", "next" }); }); };
            m.Items.Add(scene);

            ToolStripMenuItem hdr = new ToolStripMenuItem("HDR 开关");
            hdr.Click += delegate { RunAsync("hdr", delegate { return Cli.DoHdr(cfg, new string[] { "hdr", "toggle" }); }); };
            m.Items.Add(hdr);

            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem inputs = new ToolStripMenuItem("切换输入源");
            for (int i = 0; i < Vcp.InputNames.Length; i++)
            {
                string name = Vcp.InputNames[i];
                ToolStripMenuItem it = new ToolStripMenuItem(name.ToUpperInvariant());
                it.Click += delegate { SwitchAsync(name); };
                inputs.DropDownItems.Add(it);
            }
            m.Items.Add(inputs);
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem rescan = new ToolStripMenuItem("重新识别显示器");
            rescan.Click += delegate { RunAsync("rescan", delegate { return Rescan(); }); };
            m.Items.Add(rescan);

            ToolStripMenuItem logItem = new ToolStripMenuItem("打开日志");
            logItem.Click += delegate
            {
                try
                {
                    Log.Info("用户打开日志");
                    System.Diagnostics.Process.Start("notepad.exe", Log.FilePath);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            };
            m.Items.Add(logItem);

            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            quit.Click += delegate { Shutdown(); };
            m.Items.Add(quit);

            m.Opening += delegate { ThreadPool.QueueUserWorkItem(delegate { UpdateStatus(); }); };
            return m;
        }

        /// <summary>打开设置窗口（可从热键线程或托盘菜单调用）。</summary>
        public static void ShowSettings()
        {
            try
            {
                if (form == null || form.IsDisposed) return;
                form.BeginInvoke((Action)delegate
                {
                    try
                    {
                        MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
                        if (m == null) { Balloon("没有找到可控制的显示器"); return; }
                        using (SettingsForm f = new SettingsForm(cfg, m))
                        {
                            f.ShowDialog(form);
                        }
                        UpdateStatus();
                        Balloon("设置已保存");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("设置窗口异常 " + ex);
                        MessageBox.Show("设置窗口打不开：" + ex.Message, "monitorctl");
                    }
                });
            }
            catch (Exception ex) { Log.Write("ShowSettings 异常 " + ex); }
        }

        /// <summary>设置窗口保存后重新注册全部热键。</summary>
        public static void ReloadHotkeys()
        {
            try
            {
                if (form == null || form.IsDisposed) return;
                UnregisterAll();
                RegisterAll();
                Log.Info("热键重新注册，成功 " + ids.Count + " 个");
            }
            catch (Exception ex) { Log.Write("ReloadHotkeys 异常 " + ex); }
        }

        static void RunAsync(string tag, Func<int> action)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                int rc = 1;
                try { rc = action(); }
                catch (Exception ex) { Log.Write(tag + " 异常 " + ex); }
                if (rc != 0) Balloon("执行失败（" + tag + "），详见日志");
                UpdateStatus();
            });
        }

        static void SwitchAsync(string name)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (cfg.Confirm == 1)
                    {
                        DialogResult r = MessageBox.Show("确定切到 " + name.ToUpperInvariant() + "？切走后本机将无法切回。",
                            "monitorctl", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                        if (r != DialogResult.OK) return;
                    }
                    int rc = Cli.DoInput(cfg, new string[] { "input", name });
                    if (rc != 0) Balloon("切换失败，详见日志");
                }
                catch (Exception ex) { Log.Write("switch 异常 " + ex); }
                UpdateStatus();
            });
        }

        static int Rescan()
        {
            MonitorEntry[] all = Ddc.Enumerate();
            Log.Write("rescan 找到 " + all.Length + " 个物理显示器");
            Balloon("找到 " + all.Length + " 个可控制显示器");
            return all.Length > 0 ? 0 : 1;
        }

        static void UpdateStatus()
        {
            if (form == null || form.IsDisposed) return;
            string text = "（不可控）";
            try
            {
                MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
                if (m != null)
                {
                    uint cur, max;
                    int err;
                    if (Ddc.ReadVcp(m, Vcp.Brightness, out cur, out max, out err))
                    {
                        uint ic, im, gc, gm;
                        int ierr, gerr;
                        string input = Ddc.ReadVcp(m, Vcp.InputSource, out ic, out im, out ierr) ? Vcp.InputName(ic) : "?";
                        string gamma = Ddc.ReadVcp(m, Vcp.Gamma, out gc, out gm, out gerr) && gc < Cli.GammaNames.Length
                            ? Cli.GammaNames[gc] : "?";
                        text = "亮度 " + cur + " · 伽马 " + gamma + " · 输入 " + input;
                        ni.Text = "monitorctl · 亮度 " + cur + " · " + input;
                    }
                    else
                    {
                        text = "屏幕不在这路输入上";
                        ni.Text = "monitorctl · 屏幕不在本机";
                    }
                }
            }
            catch { }
            try
            {
                form.BeginInvoke((Action)delegate
                {
                    if (statusItem != null) statusItem.Text = text;
                });
            }
            catch { }
        }

        static void ShowStatusBalloon()
        {
            try
            {
                MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
                if (m == null) { Balloon("没有找到可控制的显示器"); return; }
                uint c, mx, ic, im, gc, gm;
                int e1, e2, e3;
                string b = Ddc.ReadVcp(m, Vcp.Brightness, out c, out mx, out e1) ? c + "/" + mx : "读取失败";
                string i = Ddc.ReadVcp(m, Vcp.InputSource, out ic, out im, out e2) ? Vcp.InputName(ic) : "读取失败";
                string g = Ddc.ReadVcp(m, Vcp.Gamma, out gc, out gm, out e3) && gc < Cli.GammaNames.Length
                    ? Cli.GammaNames[gc] : "?";
                Balloon(m.Device + "  " + m.Desc + "\r\n亮度 " + b + "  伽马 " + g + "  输入源 " + i);
            }
            catch (Exception ex) { Log.Write("status 异常 " + ex); }
        }

        public static void Balloon(string text)
        {
            try
            {
                if (ni == null) return;
                if (form != null && !form.IsDisposed)
                {
                    form.BeginInvoke((Action)delegate
                    {
                        try
                        {
                            ni.BalloonTipTitle = "monitorctl";
                            ni.BalloonTipText = text;
                            ni.ShowBalloonTip(2500);
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        /// <summary>按 Config.Actions 顺序注册：热键 id = 动作下标，避免再维护一份映射表。</summary>
        static void RegisterAll()
        {
            IntPtr h = form.Handle;
            for (int i = 0; i < Config.Actions.Length; i++)
            {
                string key = Config.Actions[i][0];
                string spec = cfg.GetHk(key);
                if (string.IsNullOrEmpty(spec)) continue;
                if (Register(h, i, spec)) ids.Add(i);
            }
            Log.Info("热键注册结果：" + ids.Count + "/" + Config.Actions.Length);
        }

        static bool Register(IntPtr h, int id, string spec)
        {
            uint mods, vk;
            if (!ParseHotkey(spec, out mods, out vk))
            {
                Log.Write("热键解析失败：" + spec);
                return false;
            }
            if (!RegisterHotKey(h, id, mods, vk))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Write("热键注册失败 " + spec + " err=" + err);
                Balloon("热键 " + spec + " 注册失败（已被其他程序占用？）");
                return false;
            }
            return true;
        }

        static bool ParseHotkey(string spec, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(spec)) return false;
            string[] parts = spec.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                string low = p.ToLowerInvariant();
                if (low == "alt") { mods |= MOD_ALT; continue; }
                if (low == "ctrl" || low == "control") { mods |= MOD_CTRL; continue; }
                if (low == "shift") { mods |= MOD_SHIFT; continue; }
                if (low == "win") { mods |= MOD_WIN; continue; }

                if (p.Length == 1 && char.IsDigit(p[0]))
                {
                    vk = (uint)(Keys.D0 + (p[0] - '0'));
                    continue;
                }
                Keys k;
                if (Enum.TryParse<Keys>(p, true, out k)) { vk = (uint)k; continue; }
                return false;
            }
            return mods != 0 && vk != 0;
        }

        static void UnregisterAll()
        {
            try
            {
                for (int i = 0; i < ids.Count; i++) UnregisterHotKey(form.Handle, ids[i]);
                ids.Clear();
            }
            catch { }
        }

        static void Shutdown()
        {
            Log.Info("托盘退出");
            UnregisterAll();
            if (ni != null) { ni.Visible = false; ni.Dispose(); }
            Application.ExitThread();
        }

        static void OnHotkey(int id)
        {
            if (id < 0 || id >= Config.Actions.Length) return;
            string key = Config.Actions[id][0];
            try
            {
                switch (key)
                {
                    case "up": Cli.DoBrightness(cfg, new string[] { "brightness", "+" + cfg.Step }); break;
                    case "down": Cli.DoBrightness(cfg, new string[] { "brightness", "-" + cfg.Step }); break;
                    case "contrast_up": Cli.DoContrast(cfg, new string[] { "contrast", "+" + cfg.Step }); break;
                    case "contrast_down": Cli.DoContrast(cfg, new string[] { "contrast", "-" + cfg.Step }); break;
                    case "gamma_next": Cli.DoGamma(cfg, new string[] { "gamma", "next" }); break;
                    case "gamma_prev": Cli.DoGamma(cfg, new string[] { "gamma", "prev" }); break;
                    case "hdr": Cli.DoHdr(cfg, new string[] { "hdr", "toggle" }); break;
                    case "scene_next": Cli.DoScene(cfg, new string[] { "scene", "next" }); break;
                    case "input1": Cli.DoInput(cfg, new string[] { "input", "dp1" }); break;
                    case "input2": Cli.DoInput(cfg, new string[] { "input", "dp2" }); break;
                    case "input3": Cli.DoInput(cfg, new string[] { "input", "hdmi1" }); break;
                    case "input4": Cli.DoInput(cfg, new string[] { "input", "hdmi2" }); break;
                    case "rescan": Rescan(); break;
                    case "settings": ShowSettings(); break;
                    case "peer_pull": Cli.DoPull(cfg); break;
                }
            }
            catch (Exception ex) { Log.Write("hotkey " + key + " 异常 " + ex); }
            UpdateStatus();
        }

        /// <summary>画一个 16x16 的显示器图标（深底 + 亮绿屏），避免外挂 ico 文件。</summary>
        static Icon MakeIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 18, 18, 18)))
                using (SolidBrush scr = new SolidBrush(Color.FromArgb(255, 45, 214, 122)))
                using (SolidBrush wht = new SolidBrush(Color.FromArgb(255, 230, 230, 230)))
                using (Pen edge = new Pen(Color.FromArgb(255, 230, 230, 230), 1))
                {
                    g.FillRectangle(bg, 1, 2, 13, 9);
                    g.DrawRectangle(edge, 1, 2, 12, 8);
                    g.FillRectangle(scr, 3, 4, 9, 5);
                    g.FillRectangle(wht, 6, 11, 4, 2);
                    g.FillRectangle(wht, 4, 13, 8, 1);
                }
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = Icon.FromHandle(h);
            // 复制一份再释放原句柄，避免句柄泄漏导致图标丢失
            Icon copy = (Icon)ic.Clone();
            DestroyIcon(h);
            bmp.Dispose();
            return copy;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);
    }
}

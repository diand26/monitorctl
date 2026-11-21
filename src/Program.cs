using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MonitorCtl
{
    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [STAThread]
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { }

            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Config cfg = Config.Load(Path.Combine(dir, "monitorctl.ini"));
            Log.Verbose = cfg.DebugLog;

            // 低蓝光是显卡 gamma ramp：重启/重新登录后 Windows 会把它清掉，所以每次起来都按
            // 配置重新写一遍。写在托盘和 serve 两条路径都会经过的位置。
            if (cfg.Blue > 0)
            {
                try
                {
                    string detail;
                    MonitorEntry m0 = Ddc.Pick(cfg.MonitorIndex);
                    BlueFilter.Apply(m0 == null ? @"\\.\DISPLAY1" : m0.Device, cfg.Blue, out detail);
                }
                catch { }
            }

            bool trayMode = args.Length == 0 || string.Equals(args[0], "tray", StringComparison.OrdinalIgnoreCase);
            if (!trayMode)
            {
                // serve --hide：开机自启时把控制台窗口藏起来，只留后台服务
                for (int i = 1; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--hide", StringComparison.OrdinalIgnoreCase))
                    {
                        IntPtr c0 = GetConsoleWindow();
                        if (c0 != IntPtr.Zero) ShowWindow(c0, 0);
                        break;
                    }
                }
                return Cli.Run(args, cfg);
            }

            // 托盘模式：隐藏控制台窗口（exe 是 console 子系统，CLI 需要 stdout）
            IntPtr con = GetConsoleWindow();
            if (con != IntPtr.Zero) ShowWindow(con, 0);

            bool created;
            using (Mutex mu = new Mutex(true, "Local\\monitorctl_tray_singleton", out created))
            {
                if (!created)
                {
                    MessageBox.Show("monitorctl 已经在运行了，看系统托盘。", "monitorctl",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                try
                {
                    return TrayHost.Run(cfg);
                }
                catch (Exception ex)
                {
                    Log.Write("托盘异常 " + ex);
                    MessageBox.Show("monitorctl 启动失败：" + ex.Message, "monitorctl",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
    }
}

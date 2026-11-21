using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MonitorCtl
{
    /// <summary>极简 ini：section / key=value，; 与 # 开头为注释。</summary>
    public sealed class Config
    {
        public string Path = "";

        public int MonitorIndex = 0;
        public int Step = 5;
        public int Confirm = 0;
        public bool Notify = true;
        public bool DebugLog = false;
        /// <summary>软件低蓝光强度 0-100（走显卡 gamma ramp，不是显示器 DDC）。</summary>
        public int Blue = 0;

        public string PeerSsh = "";
        public int HttpPort = 8788;
        public string HttpKey = "";

        /// <summary>热键表：动作键 -> "Alt+H" 这种写法。空字符串＝不绑定。</summary>
        public readonly Dictionary<string, string> Hk =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>动作清单：key、中文名、是否对端动作。顺序即设置窗口里的显示顺序。</summary>
        public static readonly string[][] Actions =
        {
            new[] { "up",          "亮度 +",        "" },
            new[] { "down",        "亮度 -",        "" },
            new[] { "contrast_up", "对比度 +",      "" },
            new[] { "contrast_down", "对比度 -",    "" },
            new[] { "gamma_next",  "伽马 下一档",   "" },
            new[] { "gamma_prev",  "伽马 上一档",   "" },
            new[] { "hdr",         "HDR 开关",      "" },
            new[] { "scene_next",  "情景模式 下一档", "" },
            new[] { "input1",      "切到 DP1",      "" },
            new[] { "input2",      "切到 DP2",      "" },
            new[] { "input3",      "切到 HDMI1",    "" },
            new[] { "input4",      "切到 HDMI2",    "" },
            new[] { "rescan",      "重新识别显示器", "" },
            new[] { "settings",    "打开设置窗口",  "" },
            new[] { "peer_pull",   "对端切回",       "peer" }
        };

        public static string DefaultHk(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "up": return "Alt+H";
                case "down": return "Alt+L";
                case "contrast_up": return "";
                case "contrast_down": return "";
                case "gamma_next": return "";
                case "gamma_prev": return "";
                case "hdr": return "Alt+Shift+H";
                case "scene_next": return "";
                case "input1": return "Alt+1";
                case "input2": return "Alt+2";
                case "input3": return "Alt+3";
                case "input4": return "Alt+4";
                case "rescan": return "Ctrl+Alt+R";
                case "settings": return "Ctrl+Alt+S";
                case "peer_pull": return "";
                default: return "";
            }
        }

        public string GetHk(string key)
        {
            string v;
            if (Hk.TryGetValue(key, out v)) return v;
            return DefaultHk(key);
        }

        public void SetHk(string key, string value)
        {
            Hk[key] = value == null ? "" : value.Trim();
        }

        /// <summary>把当前值写回 ini（保留注释模板，值用当前值）。</summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(Path)) return;
            try { File.WriteAllText(Path, ToText(), new UTF8Encoding(false)); }
            catch (Exception ex) { Log.Write("保存配置失败 " + ex.Message); }
        }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; monitorctl 配置（改完重启托盘生效；CLI 每次读取）");
            sb.AppendLine("; 输入源映射是显示器协议决定的，不要改：dp1=0x0F dp2=0x10 hdmi1=0x11 hdmi2=0x12");
            sb.AppendLine("; 画面项里 DDC 能控的只有 亮度/对比度/伽马；低蓝光/锐利度/动态对比度 显示器没开放给 PC，只能按 OSD 键");
            sb.AppendLine();
            sb.AppendLine("[general]");
            sb.AppendLine("; 第几个物理显示器（0 = 第一个/主显示器）");
            sb.AppendLine("monitor=" + MonitorIndex.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("; 亮度步长");
            sb.AppendLine("step=" + Step.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("; 切走前是否要确认（0=直接切；1=弹窗确认）");
            sb.AppendLine("confirm=" + Confirm.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("; 托盘气泡提示");
            sb.AppendLine("notify=" + (Notify ? "1" : "0"));
            sb.AppendLine("; 调试日志");
            sb.AppendLine("debuglog=" + (DebugLog ? "1" : "0"));
            sb.AppendLine("; 软件低蓝光强度 0-100（gamma ramp，不是 DDC；HDR 开启时不生效）");
            sb.AppendLine("blue=" + Blue.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("[hotkey]");
            sb.AppendLine("; 修饰键 Alt/Ctrl/Shift/Win 可组合，主键用 .NET Keys 名称，如 F8 / MediaPlayPause / ]");
            sb.AppendLine("; 留空＝不绑定。设置窗口里直接按键改，不用手写。");
            for (int i = 0; i < Actions.Length; i++)
                sb.AppendLine(Actions[i][0] + "=" + GetHk(Actions[i][0]));
            sb.AppendLine();
            sb.AppendLine("[peer]");
            sb.AppendLine("; 另一台主机（Linux）的 SSH 目标，例如 user@other-host（该功能尚未实现）");
            sb.AppendLine("ssh=" + PeerSsh);
            sb.AppendLine();
            sb.AppendLine("[http]");
            sb.AppendLine("; 手机网页入口端口与访问 key（key 为空时 serve 拒绝启动）");
            sb.AppendLine("port=" + HttpPort.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("key=" + HttpKey);
            return sb.ToString();
        }

        public static string DefaultText()
        {
            Config c = new Config();
            for (int i = 0; i < Actions.Length; i++) c.SetHk(Actions[i][0], DefaultHk(Actions[i][0]));
            return c.ToText();
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            c.Path = path;
            for (int i = 0; i < Actions.Length; i++) c.Hk[Actions[i][0]] = DefaultHk(Actions[i][0]);
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, c.ToText(), new UTF8Encoding(false)); }
                catch { }
                return c;
            }

            string section = "";
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();

                if (section == "general")
                {
                    if (k == "monitor") c.MonitorIndex = ToInt(v, c.MonitorIndex);
                    else if (k == "step") c.Step = Math.Max(1, ToInt(v, c.Step));
                    else if (k == "confirm") c.Confirm = ToInt(v, c.Confirm);
                    else if (k == "notify") c.Notify = ToBool(v, c.Notify);
                    else if (k == "debuglog") c.DebugLog = ToBool(v, c.DebugLog);
                    else if (k == "blue") c.Blue = Math.Max(0, Math.Min(100, ToInt(v, c.Blue)));
                }
                else if (section == "hotkey")
                {
                    c.Hk[k] = v;
                }
                else if (section == "peer")
                {
                    if (k == "ssh") c.PeerSsh = v;
                }
                else if (section == "http")
                {
                    if (k == "port") c.HttpPort = ToInt(v, c.HttpPort);
                    else if (k == "key") c.HttpKey = v;
                }
            }
            return c;
        }

        static int ToInt(string s, int def)
        {
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        static bool ToBool(string s, bool def)
        {
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }
    }

    public static class Log
    {
        public static bool Verbose = false;
        static readonly object Sync = new object();

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "monitorctl");
                return Path.Combine(dir, "log.txt");
            }
        }

        public static void Write(string msg)
        {
            Write(msg, true);
        }

        public static void Info(string msg)
        {
            Write(msg, false);
        }

        static void Write(string msg, bool always)
        {
            if (!always && !Verbose) return;
            try
            {
                string p = FilePath;
                string dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                lock (Sync)
                {
                    // 超过 1 MB 就轮转一份 log.1.txt，只留一代——这个日志是排查用的，
                    // 不需要历史归档；不轮转的话长期跑会把 LOCALAPPDATA 撑大。
                    try
                    {
                        FileInfo fi = new FileInfo(p);
                        if (fi.Exists && fi.Length > 1024 * 1024)
                        {
                            string old = Path.Combine(dir, "log.1.txt");
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(p, old);
                        }
                    }
                    catch { }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
            if (Verbose) Console.WriteLine("[log] " + msg);
        }
    }
}

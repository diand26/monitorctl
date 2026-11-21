using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MonitorCtl
{
    public static class Cli
    {
        public const int Ok = 0;
        public const int Fail = 1;
        public const int Unknown = 2;

        public static int Run(string[] args, Config cfg)
        {
            string cmd = args[0].Trim().ToLowerInvariant();
            try
            {
                switch (cmd)
                {
                    case "help":
                    case "-h":
                    case "/?":
                    case "--help":
                        PrintHelp();
                        return Ok;
                    case "get":
                        return DoGet(cfg);
                    case "list":
                        return DoList(cfg);
                    case "init":
                        return DoInit();
                    case "brightness":
                    case "bright":
                        return DoBrightness(cfg, args);
                    case "input":
                        return DoInput(cfg, args);
                    case "pull":
                        return DoPull(cfg);
                    case "caps":
                        return DoCaps(cfg);
                    case "probe":
                        return DoProbe(cfg);
                    case "set":
                        return DoSet(cfg, args);
                    case "snap":
                        return DoSnap(cfg);
                    case "contrast":
                        return DoContrast(cfg, args);
                    case "gamma":
                        return DoGamma(cfg, args);
                    case "scene":
                        return DoScene(cfg, args);
                    case "hdr":
                        return DoHdr(cfg, args);
                    case "blue":
                        return DoBlue(cfg, args);
                    case "settings":
                        return DoSettings(cfg);
                    case "serve":
                        return HttpServer.Run(cfg);
                    default:
                        Console.WriteLine("未知命令：" + args[0]);
                        PrintHelp();
                        return Unknown;
                }
            }
            catch (Exception ex)
            {
                Log.Write("CLI 异常 " + cmd + " -> " + ex);
                Console.WriteLine("出错：" + ex.Message);
                return Fail;
            }
        }

        public static void PrintHelp()
        {
            Console.WriteLine("monitorctl - 显示器控制（DDC/CI）");
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  monitorctl                          启动托盘（无参数 = 托盘）");
            Console.WriteLine("  monitorctl tray                     启动托盘");
            Console.WriteLine("  monitorctl get                      读 亮度/对比度/色温/RGB/音量/输入源/电源");
            Console.WriteLine("  monitorctl list                     列出物理显示器与当前输入源");
            Console.WriteLine("  monitorctl brightness +5            相对调整（也接受 -5 或绝对 45）");
            Console.WriteLine("  monitorctl contrast +5              对比度（同样支持 -5 / 绝对 70）");
            Console.WriteLine("  monitorctl gamma next               伽马档位 next/prev，或直接写 2.4");
            Console.WriteLine("  monitorctl scene next               情景模式 next/prev，或直接写序号");
            Console.WriteLine("  monitorctl hdr on|off|toggle        切换 Windows HDR（不走 DDC）");
            Console.WriteLine("  monitorctl blue 40                  软件低蓝光 0-100（显卡 gamma ramp，不是 DDC）");
            Console.WriteLine("  monitorctl blue 40                  软件低蓝光 0-100（显卡 gamma ramp，不是 DDC）");
            Console.WriteLine("  monitorctl input dp2                切走：dp1 / dp2 / hdmi1 / hdmi2");
            Console.WriteLine("  monitorctl input hdmi1 --dry-run    只打印将要做什么，不写显示器");
            Console.WriteLine("  monitorctl pull                     让另一台主机把屏幕抢回来（尚未实现）");
            Console.WriteLine("  monitorctl caps                     打印显示器能力串（含全部 VCP 码）");
            Console.WriteLine("  monitorctl probe                    把 caps 里每个 VCP 码都读一遍（找 OSD 未对上号的项）");
            Console.WriteLine("  monitorctl set 0x86 3               通用写入任意 VCP 码（写后回读验证）");
            Console.WriteLine("  monitorctl serve                    手机入口：局域网里用浏览器切输入源/调亮度");
            Console.WriteLine("  monitorctl settings                 直接打开设置窗口（画面 + 快捷键）");
            Console.WriteLine("  monitorctl init                     在 exe 旁边生成默认 monitorctl.ini");
            Console.WriteLine();
            Console.WriteLine("注意：DDC 只在“当前正在显示的那路输入”上可达。切走之后本机控制不了显示器，");
            Console.WriteLine("      只能按显示器按键，或由另一台正在显示的主机反向切换。");
        }

        static int DoInit()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string p = Path.Combine(dir, "monitorctl.ini");
            if (File.Exists(p))
            {
                Console.WriteLine("配置已存在，未覆盖：" + p);
                return Ok;
            }
            File.WriteAllText(p, Config.DefaultText(), new UTF8Encoding(false));
            Console.WriteLine("已生成配置：" + p);
            return Ok;
        }

        static MonitorEntry Pick(Config cfg)
        {
            MonitorEntry[] all = Ddc.Enumerate();
            if (all.Length == 0)
            {
                Console.WriteLine("没有找到可控制的物理显示器（虚拟显示适配器已忽略）。");
                return null;
            }
            int idx = cfg.MonitorIndex;
            if (idx < 0 || idx >= all.Length) idx = 0;
            return all[idx];
        }

        public static int DoGet(Config cfg)
        {
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            Console.WriteLine("显示器  " + m.Device + "  " + m.Desc + "  " + m.Size);
            PrintVcp(m, "亮度", Vcp.Brightness);
            PrintVcp(m, "对比度", Vcp.Contrast);
            PrintVcp(m, "色温预设", Vcp.ColorPreset);
            PrintVcp(m, "红色增益", Vcp.RedGain);
            PrintVcp(m, "绿色增益", Vcp.GreenGain);
            PrintVcp(m, "蓝色增益", Vcp.BlueGain);
            PrintVcp(m, "音量", Vcp.Volume);
            PrintVcp(m, "电源", Vcp.PowerMode);
            PrintGamma(m);
            PrintScene(m);

            Hdr.State hs = Hdr.Get(m.Device);
            if (hs.Found)
                Console.WriteLine("  " + Pad("HDR") + (hs.Supported ? (hs.Enabled ? "开" : "关") : "不支持") + "  (" + hs.TargetName + ")");
            else
                Console.WriteLine("  " + Pad("HDR") + "查不到");

            uint cur, max;
            int err;
            if (Ddc.ReadVcp(m, Vcp.InputSource, out cur, out max, out err))
                Console.WriteLine("输入源  " + Vcp.InputName(cur) + " (0x" + cur.ToString("X2") + ")  max=" + max);
            else
                Console.WriteLine("输入源  读取失败：" + Ddc.ErrText(err));
            return Ok;
        }

        static void PrintVcp(MonitorEntry m, string label, byte code)
        {
            uint cur, max;
            int err;
            if (Ddc.ReadVcp(m, code, out cur, out max, out err))
                Console.WriteLine("  " + Pad(label) + cur + " / " + max);
            else
                Console.WriteLine("  " + Pad(label) + "读取失败：" + Ddc.ErrText(err));
        }

        static string Pad(string s)
        {
            // 中文按两个宽度算，凑够 10 列
            int w = 0;
            for (int i = 0; i < s.Length; i++) w += s[i] > 0x2E80 ? 2 : 1;
            StringBuilder sb = new StringBuilder(s);
            while (w < 10) { sb.Append(' '); w++; }
            return sb.ToString();
        }

        public static int DoList(Config cfg)
        {
            MonitorEntry[] all = Ddc.Enumerate();
            if (all.Length == 0)
            {
                Console.WriteLine("没有找到可控制的物理显示器。");
                return Fail;
            }
            for (int i = 0; i < all.Length; i++)
            {
                MonitorEntry m = all[i];
                string input = "?";
                uint cur, max;
                int err;
                if (Ddc.ReadVcp(m, Vcp.InputSource, out cur, out max, out err))
                    input = Vcp.InputName(cur) + " (0x" + cur.ToString("X2") + ")";
                else
                    input = "读取失败";
                Console.WriteLine("[" + i + "] " + m.Device + (m.Primary ? " (主)" : "") + "  " + m.Desc + "  " + m.Size + "  " + (i == cfg.MonitorIndex ? "<- 当前使用" : ""));
                Console.WriteLine("     输入源 " + input);
            }
            return Ok;
        }

        public static int DoBrightness(Config cfg, string[] args)
        {
            return Adjust(cfg, args, Vcp.Brightness, "亮度");
        }

        public static int DoContrast(Config cfg, string[] args)
        {
            return Adjust(cfg, args, Vcp.Contrast, "对比度");
        }

        /// <summary>相对/绝对调整任意 0..max 的连续量（亮度、对比度）。写后必回读。</summary>
        static int Adjust(Config cfg, string[] args, byte code, string label)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：monitorctl <brightness|contrast> +5 | -5 | 45");
                return Unknown;
            }
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            int delta;
            bool relative = args[1].Trim().StartsWith("+") || args[1].Trim().StartsWith("-");
            if (!int.TryParse(args[1].Trim().TrimStart('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out delta))
            {
                Console.WriteLine("无法解析数值：" + args[1]);
                return Unknown;
            }

            uint cur, max;
            int err;
            if (!Ddc.ReadVcp(m, code, out cur, out max, out err))
            {
                Console.WriteLine("读取当前" + label + "失败：" + Ddc.ErrText(err) + "（可能屏幕不在本路输入上）");
                Log.Write(label + " 读取失败 " + Ddc.ErrText(err));
                return Fail;
            }

            long target = relative ? (long)cur + delta : delta;
            if (target < 0) target = 0;
            if (max > 0 && target > max) target = max;
            if (target == cur)
            {
                Console.WriteLine(label + "已是 " + cur + "，无需调整。");
                return Ok;
            }

            if (!Ddc.WriteVcp(m, code, (uint)target, out err))
            {
                Console.WriteLine("写入" + label + "失败：" + Ddc.ErrText(err));
                Log.Write(label + " 写入失败 " + Ddc.ErrText(err));
                return Fail;
            }

            uint after = 0, amax = 0;
            int aerr = 0;
            bool okRead = false;
            // 显示器处理 SetVCP 是异步的：立刻回读常常还是旧值，重试几次再判
            for (int tries = 0; tries < 4; tries++)
            {
                if (Ddc.ReadVcp(m, code, out after, out amax, out aerr)) okRead = true;
                if (okRead && after == target) break;
                Thread.Sleep(120);
            }
            if (okRead)
            {
                if (after == target)
                {
                    Console.WriteLine(label + " " + cur + " -> " + after);
                    return Ok;
                }
                // 有些显示器会把值量化到步进（如 5 的倍数），差 1 不算失败
                long diff = (long)after - target;
                if (diff < 0) diff = -diff;
                if (diff <= 1)
                {
                    Console.WriteLine(label + " 目标 " + target + "，显示器实际落在 " + after + "（量化步进，算成功）");
                    return Ok;
                }
                Console.WriteLine(label + "写入后被接受但回读为 " + after + "（期望 " + target + "）");
                if (code == Vcp.Brightness)
                    Console.WriteLine("提示：OSD 里开了「动态对比度」时显示器会锁住亮度（OSD 里亮度/对比度变灰），PC 写入被接受但不生效。关掉动态对比度再试。");
                Log.Write(label + " 回读不一致 cur=" + cur + " target=" + target + " after=" + after);
                return Fail;
            }
            Console.WriteLine(label + "已写入 " + target + "，但回读失败：" + Ddc.ErrText(aerr));
            return Ok;
        }

        /// <summary>
        /// 切走。成功判定不能只看写返回值：写完之后必须回读，
        /// 读不到（显示器不再应答）= 切走真的生效；仍读得到且值没变 = 假成功。
        /// </summary>
        public static int DoInput(Config cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：monitorctl input dp1|dp2|hdmi1|hdmi2 [--now] [--dry-run]");
                return Unknown;
            }

            byte target;
            if (!Vcp.TryParseInput(args[1], out target))
            {
                Console.WriteLine("未知输入源：" + args[1] + "（可用 dp1 / dp2 / hdmi1 / hdmi2）");
                return Unknown;
            }

            bool dryRun = HasFlag(args, "--dry-run");
            bool now = HasFlag(args, "--now") || cfg.Confirm == 0;

            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            uint cur, max;
            int err;
            bool canRead = Ddc.ReadVcp(m, Vcp.InputSource, out cur, out max, out err);
            string curName = canRead ? (Vcp.InputName(cur) + " (0x" + cur.ToString("X2") + ")") : "读取失败(" + Ddc.ErrText(err) + ")";
            Console.WriteLine("当前输入源 " + curName + " -> 目标 " + Vcp.InputName(target) + " (0x" + target.ToString("X2") + ")");

            if (!canRead)
            {
                Console.WriteLine("现在读不到显示器：屏幕很可能不在本路输入上。本机此刻控制不了它。");
                Log.Write("input 前置读取失败 " + Ddc.ErrText(err) + " target=0x" + target.ToString("X2"));
                return Fail;
            }
            if (cur == target)
            {
                Console.WriteLine("已经是 " + Vcp.InputName(target) + "，无需切换。");
                return Ok;
            }

            if (dryRun)
            {
                Console.WriteLine("[dry-run] 将写入 SetVCPFeature(0x60, 0x" + target.ToString("X2") + ")，不会真的切。");
                return Ok;
            }

            if (!now)
            {
                Console.Write("3 秒后切走（Ctrl+C 取消）... ");
                for (int i = 3; i > 0; i--)
                {
                    Thread.Sleep(1000);
                    Console.Write(i + " ");
                }
                Console.WriteLine();
            }

            if (!Ddc.WriteVcp(m, Vcp.InputSource, target, out err))
            {
                Console.WriteLine("写入输入源失败：" + Ddc.ErrText(err));
                Log.Write("input 写入失败 " + Ddc.ErrText(err) + " target=0x" + target.ToString("X2"));
                return Fail;
            }

            Console.WriteLine("已写入 0x60=0x" + target.ToString("X2") + "，等待 1.2 秒后回读验证...");
            Thread.Sleep(1200);

            MonitorEntry[] after = Ddc.Enumerate();
            MonitorEntry again = null;
            for (int i = 0; i < after.Length; i++)
            {
                if (after[i].Device == m.Device) { again = after[i]; break; }
            }
            if (again == null && after.Length > 0) again = after[0];

            if (again == null)
            {
                Console.WriteLine("结果：显示器已从系统枚举中消失 —— 切走生效。");
                Balloon(cfg, "已切到 " + Vcp.InputName(target) + "（按显示器按键可切回）");
                return Ok;
            }

            uint v2, m2;
            int e2;
            if (!Ddc.ReadVcp(again, Vcp.InputSource, out v2, out m2, out e2))
            {
                Console.WriteLine("结果：回读不到显示器（" + Ddc.ErrText(e2) + "）—— 切走生效。");
                Log.Write("input 切走生效 device=" + m.Device + " target=0x" + target.ToString("X2"));
                Balloon(cfg, "已切到 " + Vcp.InputName(target) + "（按显示器按键可切回）");
                return Ok;
            }
            if (v2 == target)
            {
                Console.WriteLine("结果：回读输入源 = 0x" + v2.ToString("X2") + "，切换成功。");
                return Ok;
            }

            Console.WriteLine("结果：写入返回成功，但回读仍是 0x" + v2.ToString("X2") + " —— 假成功，显示器没有真的切换。");
            Log.Write("input 假成功 target=0x" + target.ToString("X2") + " readback=0x" + v2.ToString("X2"));
            return Fail;
        }

        static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static void Balloon(Config cfg, string text)
        {
            if (!cfg.Notify) return;
            try { TrayHost.Balloon(text); }
            catch { }
        }

        /// <summary>打印显示器能力串原文。私有码、子值范围都在这串里。</summary>
        public static int DoCaps(Config cfg)
        {
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;
            string caps;
            int err;
            if (!Ddc.GetCapabilities(m, out caps, out err))
            {
                Console.WriteLine("读取能力串失败：" + Ddc.ErrText(err));
                return Fail;
            }
            Console.WriteLine(caps);
            return Ok;
        }

        /// <summary>把 caps 里列出的每个 VCP 码都读一遍，找 OSD 里那些还没对上号的设置。</summary>
        public static int DoProbe(Config cfg)
        {
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            string caps;
            int err;
            if (!Ddc.GetCapabilities(m, out caps, out err))
            {
                Console.WriteLine("读取能力串失败：" + Ddc.ErrText(err));
                return Fail;
            }
            List<byte> codes = Ddc.ParseVcpList(caps);
            Console.WriteLine("caps 里共 " + codes.Count + " 个 VCP 码，逐个读取：");
            Console.WriteLine("  码    当前 / 最大   备注");
            int okCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                byte code = codes[i];
                uint cur, max;
                int e;
                if (Ddc.ReadVcp(m, code, out cur, out max, out e))
                {
                    okCount++;
                    Console.WriteLine("  0x" + code.ToString("X2") + "  " + PadNum(cur) + " / " + PadNum(max) + "  " + KnownName(code));
                }
                else
                {
                    Console.WriteLine("  0x" + code.ToString("X2") + "  无响应(" + e + ")  " + KnownName(code));
                }
            }
            Console.WriteLine("可读 " + okCount + " / " + codes.Count);
            return Ok;
        }

        static string PadNum(uint v)
        {
            string s = v.ToString(CultureInfo.InvariantCulture);
            while (s.Length < 5) s = " " + s;
            return s;
        }

        static string KnownName(byte code)
        {
            switch (code)
            {
                case Vcp.Brightness: return "亮度 MCCS";
                case Vcp.Contrast: return "对比度 MCCS";
                case Vcp.ColorPreset: return "色温预设 MCCS";
                case Vcp.RedGain: return "红增益 MCCS";
                case Vcp.GreenGain: return "绿增益 MCCS";
                case Vcp.BlueGain: return "蓝增益 MCCS";
                case Vcp.InputSource: return "输入源 MCCS";
                case Vcp.Volume: return "音量 MCCS";
                case Vcp.PowerMode: return "电源模式 MCCS";
                case Vcp.SceneMode: return "情景模式（私有）";
                case 0x0F: return "输入源子值 DP1";
                case 0x11: return "输入源子值 HDMI1";
                case 0x87: return "锐利度 MCCS";
                default: return "";
            }
        }

        /// <summary>通用写入：monitorctl set 0x86 3 / set 86 3。写后回读，杜绝"假成功"。</summary>
        public static int DoSet(Config cfg, string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("用法：monitorctl set <码> <值>［--dry-run］  例如 set 0x86 3");
                return Unknown;
            }
            byte code;
            if (!TryParseByte(args[1], out code))
            {
                Console.WriteLine("无法解析 VCP 码：" + args[1]);
                return Unknown;
            }
            int value;
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0 || value > 65535)
            {
                Console.WriteLine("无法解析值：" + args[2]);
                return Unknown;
            }

            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            uint cur, max;
            int err;
            bool canRead = Ddc.ReadVcp(m, code, out cur, out max, out err);
            if (canRead)
                Console.WriteLine("0x" + code.ToString("X2") + " 当前 " + cur + " / " + max + (KnownName(code).Length > 0 ? "  " + KnownName(code) : ""));
            else
                Console.WriteLine("0x" + code.ToString("X2") + " 当前读取失败：" + Ddc.ErrText(err) + "（写仍会尝试，但大概率无效）");

            if (HasFlag(args, "--dry-run"))
            {
                Console.WriteLine("[dry-run] 将写入 0x" + code.ToString("X2") + "=" + value);
                return Ok;
            }

            if (!Ddc.WriteVcp(m, code, (uint)value, out err))
            {
                Console.WriteLine("写入失败：" + Ddc.ErrText(err));
                return Fail;
            }
            Thread.Sleep(150);

            uint after, amax;
            int aerr;
            if (Ddc.ReadVcp(m, code, out after, out amax, out aerr))
            {
                if (after == value) { Console.WriteLine("已写入，回读 " + after + " —— 生效。"); return Ok; }
                Console.WriteLine("已写入 " + value + "，但回读 " + after + " —— 显示器没接受这个值。");
                return Fail;
            }
            Console.WriteLine("已写入 " + value + "，但回读失败(" + Ddc.ErrText(aerr) + ")，无法确认。");
            return Ok;
        }

        static bool TryParseByte(string s, out byte b)
        {
            b = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X"))
                return byte.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
            if (byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) return true;
            return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        /// <summary>一行输出所有 VCP 码的当前值，供外部脚本轮询比对（找 OSD 项对的是哪个码）。</summary>
        public static int DoSnap(Config cfg)
        {
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;
            string caps;
            int err;
            if (!Ddc.GetCapabilities(m, out caps, out err)) return Fail;
            List<byte> codes = Ddc.ParseVcpList(caps);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < codes.Count; i++)
            {
                uint cur, max;
                int e;
                if (Ddc.ReadVcp(m, codes[i], out cur, out max, out e))
                    sb.Append("0x").Append(codes[i].ToString("X2")).Append("=").Append(cur).Append(" ");
                else
                    sb.Append("0x").Append(codes[i].ToString("X2")).Append("=- ");
            }
            Console.WriteLine(sb.ToString().Trim());
            return Ok;
        }

        public static readonly string[] GammaNames = { "2.0", "2.2", "2.4", "2.6", "2.8" };

        static void PrintGamma(MonitorEntry m)
        {
            uint cur, max;
            int err;
            if (Ddc.ReadVcp(m, Vcp.Gamma, out cur, out max, out err))
            {
                string name = cur < GammaNames.Length ? GammaNames[cur] : "?";
                Console.WriteLine("  " + Pad("伽马") + name + "  (档位 " + cur + "/" + max + ")");
            }
            else Console.WriteLine("  " + Pad("伽马") + "读取失败：" + Ddc.ErrText(err));
        }

        static void PrintScene(MonitorEntry m)
        {
            uint cur, max;
            int err;
            if (Ddc.ReadVcp(m, Vcp.SceneMode, out cur, out max, out err))
                Console.WriteLine("  " + Pad("情景模式") + SceneName(cur) + "  (码 " + cur + "/" + max + ")");
            else Console.WriteLine("  " + Pad("情景模式") + "读取失败：" + Ddc.ErrText(err));
        }

        public static string SceneName(uint v)
        {
            // 0xE0 的档位顺序与 OSD 列表一致；max 通常为 11（共 12 档），未识别的按序号显示
            switch (v)
            {
                case 0: return "标准";
                case 1: return "RTS/RPG";
                case 2: return "FPS";
                case 3: return "MOBA";
                case 4: return "电影";
                case 5: return "阅读";
                case 6: return "夜晚";
                default: return "模式 " + v;
            }
        }

        /// <summary>伽马：0xE1，档位 0..4 对应 2.0/2.2/2.4/2.6/2.8（已用 OSD 对照确认）。</summary>
        public static int DoGamma(Config cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：monitorctl gamma next | prev | 2.4 | 2");
                return Unknown;
            }
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            uint cur, max;
            int err;
            if (!Ddc.ReadVcp(m, Vcp.Gamma, out cur, out max, out err))
            {
                Console.WriteLine("读取伽马失败：" + Ddc.ErrText(err));
                return Fail;
            }

            string a = args[1].Trim().ToLowerInvariant();
            long target = cur;
            if (a == "next" || a == "+1") target = cur + 1;
            else if (a == "prev" || a == "-1") target = cur - 1;
            else if (a.IndexOf('.') >= 0)
            {
                for (int i = 0; i < GammaNames.Length; i++)
                {
                    if (GammaNames[i] == a) { target = i; break; }
                }
            }
            else
            {
                int n;
                if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) target = n;
            }
            if (target < 0) target = 0;
            if (max > 0 && target > max) target = max;

            string labels = "(" + (cur < GammaNames.Length ? GammaNames[cur] : "?") + " -> " + (target < GammaNames.Length ? GammaNames[target] : "?") + ")";
            if (target == cur) { Console.WriteLine("伽马已是 " + labels + "，无需调整。"); return Ok; }

            if (!Ddc.WriteVcp(m, Vcp.Gamma, (uint)target, out err))
            {
                Console.WriteLine("写入伽马失败：" + Ddc.ErrText(err));
                return Fail;
            }
            uint after, amax;
            int aerr;
            if (Ddc.ReadVcp(m, Vcp.Gamma, out after, out amax, out aerr))
            {
                if (after == target) { Console.WriteLine("伽马 " + labels + " 生效。"); return Ok; }
                Console.WriteLine("伽马写入被接受但回读 " + after + "（期望 " + target + "）");
                return Fail;
            }
            Console.WriteLine("伽马已写入 " + target + "，但回读失败。");
            return Ok;
        }

        /// <summary>情景模式：0xE0，档位数由 max 决定。</summary>
        public static int DoScene(Config cfg, string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：monitorctl scene next | prev | 2");
                return Unknown;
            }
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            uint cur, max;
            int err;
            if (!Ddc.ReadVcp(m, Vcp.SceneMode, out cur, out max, out err))
            {
                Console.WriteLine("读取情景模式失败：" + Ddc.ErrText(err));
                return Fail;
            }

            string a = args[1].Trim().ToLowerInvariant();
            long target = cur;
            if (a == "next" || a == "+1") target = (long)cur + 1;
            else if (a == "prev" || a == "-1") target = (long)cur - 1;
            else
            {
                int n;
                if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) target = n;
            }
            if (target < 0) target = 0;
            if (max > 0 && target > max) target = max;

            if (target == cur) { Console.WriteLine("情景模式已是 " + SceneName(cur) + "，无需调整。"); return Ok; }
            if (!Ddc.WriteVcp(m, Vcp.SceneMode, (uint)target, out err))
            {
                Console.WriteLine("写入情景模式失败：" + Ddc.ErrText(err));
                return Fail;
            }
            uint after, amax;
            int aerr;
            if (Ddc.ReadVcp(m, Vcp.SceneMode, out after, out amax, out aerr))
            {
                if (after == target) { Console.WriteLine("情景模式 " + SceneName(cur) + " -> " + SceneName(after)); return Ok; }
                Console.WriteLine("情景模式写入被接受但回读 " + after + "（期望 " + target + "）");
                return Fail;
            }
            Console.WriteLine("情景模式已写入 " + target + "，但回读失败。");
            return Ok;
        }

        /// <summary>HDR：走 Windows 高级颜色接口，不走 DDC。</summary>
        public static int DoHdr(Config cfg, string[] args)
        {
            MonitorEntry m = Pick(cfg);
            if (m == null) return Fail;

            Hdr.State st = Hdr.Get(m.Device);
            if (!st.Found)
            {
                Console.WriteLine("HDR：" + st.ErrorText);
                return Fail;
            }
            if (!st.Supported)
            {
                Console.WriteLine("HDR：显示器/接口不支持（Windows 没报告 advanced color 支持）。");
                Console.WriteLine(st.ErrorText);
                return Fail;
            }

            string a = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "status";
            if (a == "status") 
            {
                Console.WriteLine(m.Device + "  " + st.TargetName);
                Console.WriteLine("HDR 支持：是   当前：" + (st.Enabled ? "开" : "关"));
                return Ok;
            }

            bool want;
            if (a == "on") want = true;
            else if (a == "off") want = false;
            else if (a == "toggle") want = !st.Enabled;
            else { Console.WriteLine("用法：monitorctl hdr on|off|toggle|status"); return Unknown; }

            if (want == st.Enabled)
            {
                Console.WriteLine("HDR 已是" + (want ? "开" : "关") + "，无需切换。");
                return Ok;
            }

            string detail;
            int rc = Hdr.Set(m.Device, want, out detail);
            Console.WriteLine(detail);
            if (rc != 0) return Fail;

            Thread.Sleep(1200);
            Hdr.State after = Hdr.Get(m.Device);
            Console.WriteLine("回读：HDR " + (after.Enabled ? "开" : "关"));
            return after.Enabled == want ? Ok : Fail;
        }

        /// <summary>直接打开设置窗口（命令行用；托盘菜单与热键是另一条入口）。</summary>
        public static int DoSettings(Config cfg)
        {
            MonitorEntry m = Pick(cfg);
            if (m == null)
            {
                Console.WriteLine("没有找到可控制的显示器（可能屏幕不在本路输入上）");
                return Fail;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (SettingsForm f = new SettingsForm(cfg, m))
            {
                f.ShowDialog();
            }
            return Ok;
        }

        /// <summary>软件低蓝光。显示器 OSD 的低蓝光没有 VCP 码（OSD 差分实测过），只能走显卡 gamma ramp。</summary>
        public static int DoBlue(Config cfg, string[] args)
        {
            int cur = cfg.Blue;
            int target;
            if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
            {
                string det;
                int real = BlueFilter.Current(PrimaryDevice(cfg), out det);
                Console.WriteLine("低蓝光（软件滤镜）：配置 " + cur + "%" + (real >= 0 ? ("，实测 " + real + "%") : ("，读不到（" + det + "）")));
                Console.WriteLine("用法：monitorctl blue 40 | blue +10 | blue -10 | blue off");
                return Ok;
            }
            string a = args[1].Trim().ToLowerInvariant();
            if (a == "off") target = 0;
            else if (a.StartsWith("+") || a.StartsWith("-"))
            {
                int d;
                if (!int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out d))
                {
                    Console.WriteLine("参数看不懂：" + args[1]);
                    return Fail;
                }
                target = cur + d;
            }
            else
            {
                if (!int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out target))
                {
                    Console.WriteLine("参数看不懂：" + args[1]);
                    return Fail;
                }
            }
            if (target < 0) target = 0;
            if (target > 100) target = 100;

            string detail;
            bool ok = BlueFilter.Apply(PrimaryDevice(cfg), target, out detail);
            Console.WriteLine(detail);
            if (!ok) return Fail;
            cfg.Blue = target;
            cfg.Save();
            Log.Info("blue -> " + target);
            return Ok;
        }

        static string PrimaryDevice(Config cfg)
        {
            MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
            return m == null ? @"\\.\DISPLAY1" : m.Device;
        }

        public static int DoPull(Config cfg)
        {
            if (string.IsNullOrEmpty(cfg.PeerSsh))
            {
                Console.WriteLine("pull：尚未实现。");
                Console.WriteLine("设计意图：在 monitorctl.ini 的 [peer] ssh= 填另一台主机的 SSH 目标，");
                Console.WriteLine("由对端执行 ddcutil setvcp 60 0x10 把显示器的输入源切换回来。");
                return Unknown;
            }
            Console.WriteLine("pull：对端已配置为 " + cfg.PeerSsh + "，但 SSH 后端尚未实现。");
            return Unknown;
        }
    }
}

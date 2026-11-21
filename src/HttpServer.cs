using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MonitorCtl
{
    /// <summary>
    /// 手机入口：局域网里用浏览器切输入源 / 调亮度。
    ///
    /// 不用 HttpListener —— 绑定非 localhost 需要管理员或 netsh urlacl，会破坏"复制即用"。
    /// 手写最小 HTTP：只支持 GET，只回 Connection: close，够手机用。
    /// 鉴权靠 ?k=&lt;key&gt;，key 必须在 ini 里显式设置，否则拒绝启动（默认不暴露是更安全的默认值）。
    /// </summary>
    public static class HttpServer
    {
        static Config cfg;

        public static int Run(Config c)
        {
            cfg = c;
            if (string.IsNullOrEmpty(cfg.HttpKey))
            {
                Console.WriteLine("手机入口未启动：为了不把显示器控制暴露到局域网，必须先设 key。");
                Console.WriteLine("在 " + cfg.Path + " 里加一行（[http] 段下）：");
                Console.WriteLine("  key=" + MakeKey());
                return Cli.Fail;
            }

            TcpListener srv = new TcpListener(IPAddress.Any, cfg.HttpPort);
            try
            {
                srv.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("监听 0.0.0.0:" + cfg.HttpPort + " 失败：" + ex.Message);
                Console.WriteLine("端口被占用时改 ini 的 [http] port。");
                return Cli.Fail;
            }

            Console.WriteLine("手机入口已启动：");
            foreach (string ip in LocalIPs())
                Console.WriteLine("  http://" + ip + ":" + cfg.HttpPort + "/?k=" + cfg.HttpKey);
            Console.WriteLine("Ctrl+C 退出。日志：" + Log.FilePath);
            Log.Info("serve 启动 port=" + cfg.HttpPort);

            while (true)
            {
                TcpClient cl;
                try { cl = srv.AcceptTcpClient(); }
                catch (Exception ex) { Log.Write("accept 失败 " + ex.Message); continue; }
                ThreadPool.QueueUserWorkItem(delegate(object o) { Handle((TcpClient)o); }, cl);
            }
        }

        static string MakeKey()
        {
            Random r = new Random();
            const string abc = "abcdefghijkmnpqrstuvwxyz23456789";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append(abc[r.Next(abc.Length)]);
            return sb.ToString();
        }

        static List<string> LocalIPs()
        {
            List<string> list = new List<string>();
            try
            {
                IPHostEntry he = Dns.GetHostEntry(Dns.GetHostName());
                for (int i = 0; i < he.AddressList.Length; i++)
                {
                    IPAddress a = he.AddressList[i];
                    if (a.AddressFamily == AddressFamily.InterNetwork) list.Add(a.ToString());
                }
            }
            catch { }
            if (list.Count == 0) list.Add("127.0.0.1");
            return list;
        }

        static void Handle(TcpClient cl)
        {
            try
            {
                using (cl)
                using (NetworkStream ns = cl.GetStream())
                {
                    cl.ReceiveTimeout = 5000;
                    StreamReader reader = new StreamReader(ns, Encoding.ASCII);
                    string line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) return;
                    // 吞掉剩余请求头（最多 40 行，防呆）
                    for (int i = 0; i < 40; i++)
                    {
                        string h = reader.ReadLine();
                        if (string.IsNullOrEmpty(h)) break;
                    }

                    string[] parts = line.Split(' ');
                    if (parts.Length < 2) { Respond(ns, 400, "text/plain; charset=utf-8", "bad request"); return; }
                    string method = parts[0].ToUpperInvariant();
                    if (method != "GET" && method != "POST") { Respond(ns, 405, "text/plain; charset=utf-8", "only GET/POST"); return; }

                    string raw = parts[1];
                    string path = raw;
                    string query = "";
                    int q = raw.IndexOf('?');
                    if (q >= 0) { path = raw.Substring(0, q); query = raw.Substring(q + 1); }
                    Dictionary<string, string> qs = ParseQuery(query);
                    Log.Info("http " + method + " " + path + (qs.ContainsKey("k") ? "" : " (no key)"));

                    string k;
                    qs.TryGetValue("k", out k);
                    if (!string.Equals(k, cfg.HttpKey, StringComparison.Ordinal))
                    {
                        Respond(ns, 401, "text/html; charset=utf-8",
                            "<!doctype html><meta charset=utf-8><body style='font:16px sans-serif;padding:24px'>"
                            + "<h3>需要 key</h3><p>地址里要带 <code>?k=&lt;ini 里的 [http] key&gt;</code></p></body>");
                        return;
                    }

                    if (path == "/" || path == "/index.html") { Respond(ns, 200, "text/html; charset=utf-8", Page()); return; }
                    if (path == "/api/state") { Respond(ns, 200, "application/json; charset=utf-8", StateJson()); return; }
                    if (path == "/api/switch") { RespondApi(ns, DoSwitch(qs)); return; }
                    if (path == "/api/brightness") { RespondApi(ns, DoBrightness(qs)); return; }
                    if (path == "/api/set") { RespondApi(ns, DoSet(qs)); return; }
                    if (path == "/api/hdr") { RespondApi(ns, DoHdr(qs)); return; }
                    if (path == "/api/blue") { RespondApi(ns, DoBlueApi(qs)); return; }
                    Respond(ns, 404, "text/plain; charset=utf-8", "not found");
                }
            }
            catch (Exception ex)
            {
                // 手机浏览器的"预连接"（TCP 建好但不发请求、或提前关闭）会固定抛这两类
                // IOException，属于正常现象。以前按异常记，日志被刷成几百行噪声，
                // 真正的故障反而看不见——所以这类只走 Info（debuglog=1 才写）。
                string m = ex.Message ?? "";
                bool preconnect = m.IndexOf("传输连接", StringComparison.Ordinal) >= 0
                    || m.IndexOf("远程主机", StringComparison.Ordinal) >= 0
                    || m.IndexOf("连接方", StringComparison.Ordinal) >= 0;
                if (preconnect) Log.Info("http 连接被对端提前关闭（预连接，正常）");
                else Log.Write("http 处理异常 " + m);
            }
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Length == 0) continue;
                int eq = pairs[i].IndexOf('=');
                if (eq < 0) d[Uri.UnescapeDataString(pairs[i])] = "";
                else d[Uri.UnescapeDataString(pairs[i].Substring(0, eq))] = Uri.UnescapeDataString(pairs[i].Substring(eq + 1));
            }
            return d;
        }

        // ---------- 动作 ----------

        static string DoSwitch(Dictionary<string, string> qs)
        {
            string to;
            if (!qs.TryGetValue("to", out to) || string.IsNullOrEmpty(to))
                return "{\"ok\":false,\"error\":\"缺少 to 参数（dp1/dp2/hdmi1/hdmi2）\"}";

            to = to.Trim().ToLowerInvariant();
            byte val;
            if (!Vcp.TryParseInput(to, out val))
                return "{\"ok\":false,\"error\":\"未知输入源：" + to + "\"}";

            MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
            if (m == null)
                return "{\"ok\":false,\"error\":\"" + Escape(PeerHint("现在这台机器控制不了显示器")) + "\"}";

            uint cur, max;
            int err;
            if (!Ddc.ReadVcp(m, Vcp.InputSource, out cur, out max, out err))
            {
                // 读不到输入源 = 屏幕已经不在本机这路上
                return "{\"ok\":false,\"error\":\"" + Escape(PeerHint("现在这台机器读不到显示器")) + "\"}";
            }
            if (cur == val)
                return "{\"ok\":true,\"message\":\"已经在 " + to + " 了\"}";

            if (!Ddc.WriteVcp(m, Vcp.InputSource, val, out err))
            {
                Log.Write("serve switch 写入失败 " + to + " " + Ddc.ErrText(err));
                return "{\"ok\":false,\"error\":\"写入失败：" + Escape(Ddc.ErrText(err)) + "\"}";
            }
            Thread.Sleep(1200);
            uint after, amax;
            int aerr;
            if (!Ddc.ReadVcp(m, Vcp.InputSource, out after, out amax, out aerr))
            {
                Log.Info("serve 切走生效 to=" + to);
                return "{\"ok\":true,\"message\":\"已切到 " + to + "（屏幕现在归另一台机器；要切回来按显示器按键）\"}";
            }
            Log.Write("serve 切走疑似假成功 to=" + to + " 回读=" + after);
            return "{\"ok\":false,\"error\":\"写入被接受但显示器没切过去（假成功），详见日志\"}";
        }

        static string DoBrightness(Dictionary<string, string> qs)
        {
            string d;
            if (!qs.TryGetValue("d", out d) || string.IsNullOrEmpty(d))
                return "{\"ok\":false,\"error\":\"缺少 d 参数（如 +5 / -5 / 45）\"}";

            string arg = d.Trim();
            if (arg.Length > 0 && arg[0] != '+' && arg[0] != '-') arg = "+" + arg;

            MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
            if (m == null)
                return "{\"ok\":false,\"error\":\"" + Escape(PeerHint("现在这台机器控制不了显示器")) + "\"}";

            uint cur, max;
            int err;
            if (!Ddc.ReadVcp(m, Vcp.Brightness, out cur, out max, out err))
                return "{\"ok\":false,\"error\":\"" + Escape(PeerHint("读不到亮度")) + "\"}";

            int delta;
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out delta))
                return "{\"ok\":false,\"error\":\"数值解析失败：" + Escape(d) + "\"}";

            long target = (long)cur + delta;
            if (target < 0) target = 0;
            if (max > 0 && target > max) target = max;

            if (!Ddc.WriteVcp(m, Vcp.Brightness, (uint)target, out err))
                return "{\"ok\":false,\"error\":\"写入失败：" + Escape(Ddc.ErrText(err)) + "\"}";

            uint after = cur, amax = max;
            int aerr = 0;
            bool okRead = false;
            for (int i = 0; i < 3; i++)
            {
                if (Ddc.ReadVcp(m, Vcp.Brightness, out after, out amax, out aerr)) { okRead = true; if (after == target) break; }
                Thread.Sleep(120);
            }
            if (okRead && after != target)
                return "{\"ok\":false,\"error\":\"亮度写不进：显示器可能在 OSD 里开着「动态对比度」，会锁住亮度\"}";
            return "{\"ok\":true,\"message\":\"亮度 " + cur + " -> " + target + "\"}";
        }

        static string PeerHint(string baseMsg)
        {
            if (string.IsNullOrEmpty(cfg.PeerSsh))
                return baseMsg + "，且没有配置对端。请按显示器按键切回。";
            return baseMsg + "，对端后端尚未实现。";
        }

        // ---------- 输出 ----------

        static string StateJson()
        {
            MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
            bool controllable = false;
            string input = "?", gamma = "?", desc = "", sceneName = "?";
            uint bright = 0, brightMax = 0, contrast = 0, contrastMax = 0, gammaNum = 0, gammaMax = 0, sceneMax = 0;
            uint volume = 0, volumeMax = 0;
            int scene = -1;

            if (m != null)
            {
                desc = m.Device + " " + m.Desc;
                uint cur, max;
                int err;
                if (Ddc.ReadVcp(m, Vcp.Brightness, out cur, out max, out err))
                {
                    controllable = true;
                    bright = cur;
                    brightMax = max;
                }
                if (Ddc.ReadVcp(m, Vcp.Contrast, out cur, out max, out err)) { contrast = cur; contrastMax = max; }
                if (Ddc.ReadVcp(m, Vcp.Volume, out cur, out max, out err)) { volume = cur; volumeMax = max; }
                if (Ddc.ReadVcp(m, Vcp.InputSource, out cur, out max, out err)) input = Vcp.InputName(cur);
                if (Ddc.ReadVcp(m, Vcp.Gamma, out cur, out max, out err))
                {
                    gammaNum = cur;
                    gammaMax = max;
                    if (cur < Cli.GammaNames.Length) gamma = Cli.GammaNames[cur];
                }
                if (Ddc.ReadVcp(m, Vcp.SceneMode, out cur, out max, out err))
                {
                    scene = (int)cur;
                    sceneMax = max;
                    sceneName = Cli.SceneName(cur);
                }
            }

            string hdr = "n/a";
            if (m != null)
            {
                try
                {
                    Hdr.State hs = Hdr.Get(m.Device);
                    if (hs.Found && hs.Supported) hdr = hs.Enabled ? "on" : "off";
                    else if (hs.Found) hdr = "unsupported";
                }
                catch { }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"ok\":true");
            sb.Append(",\"controllable\":").Append(controllable ? "true" : "false");
            sb.Append(",\"display\":\"").Append(Escape(desc)).Append("\"");
            sb.Append(",\"input\":\"").Append(input).Append("\"");
            sb.Append(",\"brightness\":").Append(bright);
            sb.Append(",\"brightnessMax\":").Append(brightMax);
            sb.Append(",\"contrast\":").Append(contrast);
            sb.Append(",\"contrastMax\":").Append(contrastMax);
            sb.Append(",\"volume\":").Append(volume);
            sb.Append(",\"volumeMax\":").Append(volumeMax);
            sb.Append(",\"gamma\":\"").Append(gamma).Append("\"");
            sb.Append(",\"gammaNum\":").Append(gammaNum);
            sb.Append(",\"gammaMax\":").Append(gammaMax);
            sb.Append(",\"scene\":").Append(scene);
            sb.Append(",\"sceneMax\":").Append(sceneMax);
            sb.Append(",\"sceneName\":\"").Append(Escape(sceneName)).Append("\"");
            sb.Append(",\"blue\":").Append(cfg.Blue);
            sb.Append(",\"hdr\":\"").Append(hdr).Append("\"");
            sb.Append(",\"hint\":\"").Append(Escape(controllable ? "" : PeerHint("屏幕不在本机这路输入上"))).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        // ---------- 通用写入（给前端滑块用） ----------

        /// <summary>允许通过 HTTP 写哪些 VCP 码。白名单，防止有人拿 URL 乱写显示器。</summary>
        static readonly byte[] Writable =
        {
            Vcp.Brightness, Vcp.Contrast, Vcp.ColorPreset, Vcp.Volume, Vcp.Gamma, Vcp.SceneMode
        };

        static string CodeName(byte code)
        {
            if (code == Vcp.Brightness) return "亮度";
            if (code == Vcp.Contrast) return "对比度";
            if (code == Vcp.ColorPreset) return "色温";
            if (code == Vcp.Volume) return "音量";
            if (code == Vcp.Gamma) return "伽马";
            if (code == Vcp.SceneMode) return "情景模式";
            return "0x" + code.ToString("X2");
        }

        static string DoSet(Dictionary<string, string> qs)
        {
            string sc, sv;
            if (!qs.TryGetValue("code", out sc) || string.IsNullOrEmpty(sc))
                return "{\"ok\":false,\"error\":\"缺少 code 参数（如 0x12）\"}";
            if (!qs.TryGetValue("value", out sv) || string.IsNullOrEmpty(sv))
                return "{\"ok\":false,\"error\":\"缺少 value 参数\"}";

            byte code;
            string s = sc.Trim();
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) code = Convert.ToByte(s.Substring(2), 16);
                else code = Convert.ToByte(s, 16);   // 不写 0x 也按十六进制，符合 VCP 惯例
            }
            catch { return "{\"ok\":false,\"error\":\"code 解析失败：" + Escape(sc) + "\"}"; }

            bool allowed = false;
            for (int i = 0; i < Writable.Length; i++) if (Writable[i] == code) allowed = true;
            if (!allowed)
                return "{\"ok\":false,\"error\":\"0x" + code.ToString("X2") + " 不在可写白名单里（只有亮度/对比度/色温/音量/伽马/情景模式）\"}";

            int want;
            if (!int.TryParse(sv.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out want))
                return "{\"ok\":false,\"error\":\"value 解析失败：" + Escape(sv) + "\"}";

            MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
            if (m == null)
                return "{\"ok\":false,\"error\":\"" + Escape(PeerHint("现在这台机器控制不了显示器")) + "\"}";

            uint cur, max;
            int err;
            bool canRead = Ddc.ReadVcp(m, code, out cur, out max, out err);
            uint target = (uint)(want < 0 ? 0 : want);
            if (canRead && max > 0 && target > max) target = max;

            if (!Ddc.WriteVcp(m, code, target, out err))
            {
                Log.Write("http set 写入失败 0x" + code.ToString("X2") + " -> " + target + " " + Ddc.ErrText(err));
                return "{\"ok\":false,\"error\":\"写入失败：" + Escape(Ddc.ErrText(err)) + "\"}";
            }

            uint after = target;
            int aerr = 0;
            bool okRead = false;
            for (int i = 0; i < 4; i++)
            {
                if (Ddc.ReadVcp(m, code, out after, out max, out aerr)) { okRead = true; if (after == target) break; }
                Thread.Sleep(120);
            }

            if (okRead && after != target)
            {
                string why = (code == Vcp.Brightness)
                    ? "写入被接受但值没变：显示器 OSD 里可能开着「动态对比度」，它锁亮度"
                    : "写入被接受但值没变：显示器没接受这个值";
                return "{\"ok\":false,\"error\":\"" + Escape(why) + "\"}";
            }
            if (!okRead)
                return "{\"ok\":false,\"error\":\"写入后读不回来：" + Escape(Ddc.ErrText(aerr)) + "\"}";

            Log.Info("http set 0x" + code.ToString("X2") + " " + (canRead ? cur.ToString() : "?") + " -> " + target + " ok");
            return "{\"ok\":true,\"message\":\"" + CodeName(code) + " -> " + target + "\",\"value\":" + target + "}";
        }

        static string DoHdr(Dictionary<string, string> qs)
        {
            MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
            string dev = (m == null) ? @"\\.\DISPLAY1" : m.Device;

            Hdr.State st = Hdr.Get(dev);
            if (!st.Found) return "{\"ok\":false,\"error\":\"找不到显示输出：" + Escape(st.ErrorText) + "\"}";
            if (!st.Supported) return "{\"ok\":false,\"error\":\"显示器不支持 HDR（或当前线材/接口不支持）\"}";

            string on;
            bool enable = !st.Enabled;
            if (qs.TryGetValue("on", out on) && !string.IsNullOrEmpty(on))
            {
                on = on.Trim().ToLowerInvariant();
                if (on == "1" || on == "true" || on == "on") enable = true;
                else if (on == "0" || on == "false" || on == "off") enable = false;
                else if (on == "toggle") enable = !st.Enabled;
                else if (on == "status") return "{\"ok\":true,\"message\":\"HDR " + (st.Enabled ? "已开" : "已关") + "\",\"value\":\"" + (st.Enabled ? "on" : "off") + "\"}";
                else return "{\"ok\":false,\"error\":\"on 参数只认 1/0/toggle/status\"}";
            }

            if (enable == st.Enabled)
                return "{\"ok\":true,\"message\":\"HDR " + (enable ? "已经是开" : "已经是关") + "\",\"value\":\"" + (enable ? "on" : "off") + "\"}";

            string detail;
            int rc = Hdr.Set(dev, enable, out detail);
            if (rc != 0)
            {
                Log.Write("http hdr 失败 " + detail);
                return "{\"ok\":false,\"error\":\"" + Escape(detail) + "\"}";
            }
            Thread.Sleep(1200);
            Hdr.State after = Hdr.Get(dev);
            Log.Info("http hdr -> " + (enable ? "on" : "off") + " 回读=" + after.Enabled);
            return "{\"ok\":true,\"message\":\"" + Escape(detail) + "\",\"value\":\"" + (after.Enabled ? "on" : "off") + "\"}";
        }

        /// <summary>软件低蓝光（显卡 gamma ramp）。支持绝对 0-100 与 +/- 相对。</summary>
        static string DoBlueApi(Dictionary<string, string> qs)
        {
            string sv;
            int target = cfg.Blue;
            if (qs.TryGetValue("value", out sv) && !string.IsNullOrEmpty(sv))
            {
                string s2 = sv.Trim();
                int v;
                if (s2.StartsWith("+") || s2.StartsWith("-"))
                {
                    if (!int.TryParse(s2, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                        return "{\"ok\":false,\"error\":\"value 解析失败：" + Escape(sv) + "\"}";
                    target = cfg.Blue + v;
                }
                else
                {
                    if (!int.TryParse(s2, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                        return "{\"ok\":false,\"error\":\"value 解析失败：" + Escape(sv) + "\"}";
                    target = v;
                }
            }
            if (target < 0) target = 0;
            if (target > 100) target = 100;

            MonitorEntry m = Ddc.Pick(cfg.MonitorIndex);
            string dev = (m == null) ? @"\\.\DISPLAY1" : m.Device;
            string detail;
            if (!BlueFilter.Apply(dev, target, out detail))
            {
                Log.Write("http blue 失败 " + detail);
                return "{\"ok\":false,\"error\":\"" + Escape(detail) + "\"}";
            }
            cfg.Blue = target;
            cfg.Save();
            Log.Info("http blue -> " + target);
            return "{\"ok\":true,\"message\":\"" + Escape(detail) + "\",\"value\":" + target + "}";
        }

        static void RespondApi(NetworkStream ns, string json)
        {
            Respond(ns, 200, "application/json; charset=utf-8", json);
        }

        static void Respond(NetworkStream ns, int code, string ctype, string body)
        {
            byte[] data = Encoding.UTF8.GetBytes(body);
            StringBuilder head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : (code == 401 ? " Unauthorized" : " Error")).Append("\r\n");
            head.Append("Content-Type: ").Append(ctype).Append("\r\n");
            head.Append("Content-Length: ").Append(data.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n\r\n");
            byte[] hb = Encoding.ASCII.GetBytes(head.ToString());
            ns.Write(hb, 0, hb.Length);
            ns.Write(data, 0, data.Length);
            ns.Flush();
        }

        static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        }

        static string Page()
        {
            string k = Uri.EscapeDataString(cfg.HttpKey);
            StringBuilder sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=zh-CN><head><meta charset=utf-8>");
            sb.Append("<meta name=viewport content='width=device-width,initial-scale=1,viewport-fit=cover'>");
            sb.Append("<title>monitorctl</title><style>");
            sb.Append(":root{--bg:#0b0c0e;--card:#15171b;--line:#24272d;--mut:#8b929c;--fg:#e9ecef;--acc:#2dd67a}");
            sb.Append("*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}");
            sb.Append("html,body{margin:0;padding:0}");
            // overscroll-behavior: 到顶/到底时不要触发浏览器的下拉刷新/回弹，滑条附近拖不动页面时也不会连锁滚动。
            sb.Append("html,body{overscroll-behavior:none}");
            sb.Append("body{font:15px/1.45 -apple-system,'SF Pro Text','PingFang SC','Microsoft YaHei',system-ui,sans-serif;background:var(--bg);color:var(--fg);padding-bottom:48px}");
            sb.Append("header{position:sticky;top:0;z-index:5;display:flex;align-items:center;justify-content:space-between;gap:10px;padding:16px 16px 10px;background:rgba(11,12,14,.92)}");
            sb.Append("h1{font-size:18px;font-weight:650;margin:0}");
            sb.Append("#pill{font-size:12px;padding:5px 11px;border-radius:999px;background:#1b1e23;color:var(--mut);border:1px solid var(--line);white-space:nowrap}");
            sb.Append("#pill.ok{color:var(--acc);border-color:#1f4d33;background:#102119}");
            sb.Append("#pill.bad{color:#f0a45b;border-color:#4d3a1f;background:#221a10}");
            sb.Append("main{padding:2px 12px 0;max-width:560px;margin:0 auto}");
            sb.Append("section{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:14px 16px;margin-bottom:10px}");
            sb.Append(".row{display:flex;align-items:center;justify-content:space-between;gap:10px;color:var(--mut);font-size:13px}");
            sb.Append(".row b{color:var(--fg);font-size:17px;font-weight:600;font-variant-numeric:tabular-nums}");
            sb.Append("input[type=range]{-webkit-appearance:none;appearance:none;width:100%;height:34px;margin:6px 0 -4px;background:transparent}");
            sb.Append("input[type=range]::-webkit-slider-runnable-track{height:8px;border-radius:99px;background:linear-gradient(90deg,var(--acc) 0,var(--acc) var(--p,0%),#2a2e35 var(--p,0%),#2a2e35 100%)}");
            sb.Append("input[type=range]::-webkit-slider-thumb{-webkit-appearance:none;width:26px;height:26px;margin-top:-9px;border-radius:50%;background:#fff;box-shadow:0 2px 6px rgba(0,0,0,.55);border:0}");
            // 滑块自己画（不用 input[type=range]）：原生 range 旋转 -90 度后，浏览器仍按横向控件
            // 处理手势，竖着拖会被判成"滚动页面"，滑条纹丝不动。自绘条里用 pointer 事件 +
            // touch-action:none，才真正做到了"在滑条区域内拖动绝不带动页面"。
            sb.Append(".vgrid{display:flex;justify-content:center;gap:18px;margin:0 -4px 10px}");
            sb.Append(".vcell{width:92px;display:flex;flex-direction:column;align-items:center;background:var(--card);border:1px solid var(--line);border-radius:18px;padding:12px 0 14px}");
            sb.Append(".vcell .val{font-size:17px;font-weight:650;font-variant-numeric:tabular-nums;line-height:1.1}");
            sb.Append(".nudge{width:54px;height:40px;min-height:0;padding:0;margin:8px 0;border-radius:12px;font-size:22px;line-height:1;touch-action:manipulation}");
            sb.Append(".vtrack{position:relative;width:46px;height:min(52vh,440px);margin:2px 0 10px;touch-action:none}");
            sb.Append(".vbar{position:absolute;left:50%;top:0;bottom:0;width:12px;margin-left:-6px;border-radius:99px;background:#2a2e35}");
            sb.Append(".vfill{position:absolute;left:50%;bottom:0;width:12px;margin-left:-6px;border-radius:99px;background:var(--acc);height:0}");
            sb.Append(".vthumb{position:absolute;left:50%;width:30px;height:30px;margin-left:-15px;bottom:0;border-radius:50%;background:#fff;box-shadow:0 2px 6px rgba(0,0,0,.6)}");
            sb.Append(".lbl{font-size:12px;color:var(--mut);margin-top:10px;white-space:nowrap}");
            sb.Append(".chips{display:flex;gap:8px;flex-wrap:wrap;margin-top:12px}");
            sb.Append(".grid2{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:12px}");
            sb.Append("button{font:inherit;color:var(--fg);background:#1b1e23;border:1px solid var(--line);border-radius:12px;min-height:46px;padding:10px 8px}");
            sb.Append("button:active{transform:scale(.98);background:#22262c}");
            sb.Append("button.on{border-color:var(--acc);color:var(--acc);background:#102119}");
            sb.Append(".chips button{flex:1 1 auto;min-width:64px;font-size:14px;padding:9px 6px}");
            sb.Append("#sce{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-top:12px}");
            sb.Append("#sce button{min-width:0;font-size:13px;padding:9px 2px}");
            sb.Append(".wide{width:100%;margin-top:12px}");
            sb.Append(".tip{color:#5b626c;font-size:12px;line-height:1.65;text-align:center;margin:16px 8px 0}");
            sb.Append(".note{color:#5b626c;font-size:12px;margin-top:10px}");
            sb.Append("body.busy main{opacity:.6;pointer-events:none}");
            sb.Append("#toast{position:fixed;left:50%;bottom:24px;transform:translate(-50%,16px);opacity:0;transition:.18s ease;background:#242830;color:#fff;font-size:13px;line-height:1.4;padding:11px 16px;border-radius:12px;max-width:86vw;text-align:center;border:1px solid #333941;pointer-events:none}");
            sb.Append("#toast.show{opacity:1;transform:translate(-50%,0)}");
            sb.Append("</style></head><body>");
            sb.Append("<header><h1>显示器控制</h1><div id=pill>连接中</div></header><main>");

            sb.Append("<div class=vgrid id=vgrid></div>");
            sb.Append("<div class=note>滑条区域内拖动只调数值、不会带动页面；要滚动页面就用滑条<b>左右两侧的空白</b>。低蓝光是软件滤镜（显卡 gamma），HDR 开着时不生效。</div>");

            sb.Append("<section><div class=row><span>伽马</span></div><div class=chips id=gam></div></section>");
            sb.Append("<section><div class=row><span>情景模式</span></div><div class=chips id=sce></div>");
            sb.Append("<div class=note>切换会把亮度/对比度/伽马重置成该模式预设</div></section>");
            sb.Append("<section><div class=row><span>锐度</span><b style='color:#f0a45b;font-size:13px'>只能 OSD 调</b></div>");
            sb.Append("<div class=note>显示器没把锐度开放给 PC：caps 里没有对应码，0x86/0x87 无响应、0xB2 写入被拒（已用 OSD 差分验证），所以这里不放滑块。</div></section>");
            sb.Append("<section><div class=row><span>HDR</span><b id=hdrv>–</b></div><button id=hdr class=wide>切换</button></section>");
            sb.Append("<section><div class=row><span>输入源</span></div><div class=grid2 id=inp></div></section>");

            sb.Append("<p class=tip>切走后本机就控制不了显示器；切回来按显示器按键，或由另一台正在显示的主机反向切换。</p>");
            sb.Append("</main><div id=toast></div>");

            sb.Append("<script>var K='").Append(k).Append("';");
            sb.Append(Script);
            sb.Append("</script></body></html>");
            return sb.ToString();
        }

        // 刻意用 ES5（XMLHttpRequest + 普通函数）：手机上的浏览器/内置 WebView 不一定支持
        // 箭头函数，2026-11-21 第一版用 fetch + 箭头函数就整页 JS 语法错误，页面卡在"读取中…"。
        const string Script = @"var S=null, busy=false;
function q(i){return document.getElementById(i);}
function toast(t,bad){
  var e=q('toast');
  e.textContent=t;
  e.style.borderColor=bad?'#5a3320':'#333941';
  e.className='show';
  clearTimeout(e._t);
  e._t=setTimeout(function(){ e.className=''; },2600);
}
function setBusy(b){ busy=b; document.body.className=b?'busy':''; }
function fill(el,max){
  var v=parseInt(el.value,10)||0;
  var p=(max>0)?(v*100/max):0;
  el.style.setProperty('--p',p+'%');
}
function api(p,cb){
  var x=new XMLHttpRequest();
  x.open('GET',p+(p.indexOf('?')<0?'?':'&')+'k='+K,true);
  x.timeout=9000;
  x.onreadystatechange=function(){
    if(x.readyState!==4) return;
    var j=null; try{ j=JSON.parse(x.responseText||'{}'); }catch(e){}
    if(x.status!==200||!j){ toast('请求失败（HTTP '+x.status+'）',true); setBusy(false); return; }
    cb(j);
  };
  x.ontimeout=function(){ toast('超时：显示器没响应',true); setBusy(false); };
  x.send();
}
var SCENE=['标准','RTS/RPG','FPS','MOBA','电影','阅读','夜晚'];
var GAM=['2.0','2.2','2.4','2.6','2.8'];
function addChip(box,code,val,text,label,warn){
  var b=document.createElement('button');
  b.setAttribute('data-i',val);
  b.textContent=text;
  b.onclick=function(){
    if(warn && !confirm(warn)) return;
    setVcp(code,val,label);
  };
  box.appendChild(b);
}
function buildGamma(max){
  var box=q('gam'); if(box.childNodes.length) return;
  for(var i=0;i<=max;i++) addChip(box,'0xE1',i,GAM[i]||('档'+i),'伽马');
}
function buildScene(max){
  var box=q('sce'); if(box.childNodes.length) return;
  // 显示器实际只有 7 档（标准/RTS-RPG/FPS/MOBA/电影/阅读/夜晚），0xE0 报的 max 会给到 11，
  // 多出来的都按「模式 N」显示等于把没验证过的档位当功能卖，所以只画到第 7 档。
  if(max>6) max=6;
  for(var i=0;i<=max;i++){
    var t=(SCENE[i]||('模式'+i));
    addChip(box,'0xE0',i,t,'情景模式',
      '切到「'+t+'」会把亮度/对比度/伽马重置成这个模式的预设值，继续？');
  }
}
function buildInputs(){
  var box=q('inp'); if(box.childNodes.length) return;
  var names=['dp1','dp2','hdmi1','hdmi2'], labs=['DP1','DP2','HDMI1','HDMI2'];
  for(var i=0;i<4;i++){
    var b=document.createElement('button');
    b.setAttribute('data-v',names[i]);
    b.textContent=labs[i];
    b.onclick=function(){ switchTo(this.getAttribute('data-v')); };
    box.appendChild(b);
  }
}
function markAll(sel,attr,val){
  var bs=q(sel).querySelectorAll('button');
  for(var i=0;i<bs.length;i++){
    bs[i].className=(bs[i].getAttribute(attr)===String(val))?'on':'';
  }
}
function setSlider(id,valId,v,max){
  var el=q(id);
  if(document.activeElement!==el) el.value=v;
  q(valId).textContent=v;
  fill(el,max);
}
// ---- 自绘竖排滑块 ----
// 为什么不用 input[type=range]：原生 range 旋转 -90 度后，浏览器仍按「横向控件」判定手势，
// 竖着拖会被当成滚动页面（滑条不动、页面跟着跑，用户实测过）。自绘 + pointer 事件 +
// touch-action:none 才能做到「在滑条上拖绝不带动页面」。
var SLIDERS=[
  {id:'br',code:'0x10',name:'亮度',blue:false,max:100,val:0,dragging:false},
  {id:'ct',code:'0x12',name:'对比度',blue:false,max:100,val:0,dragging:false},
  {id:'bl',code:'',name:'低蓝光',blue:true,max:100,val:0,dragging:false}
];
function slPct(s){ return s.max>0 ? (s.val*100/s.max) : 0; }
function slPaint(s){
  var p=slPct(s), tr=q('t_'+s.id);
  q('v_'+s.id).textContent=s.val;
  var h=tr.clientHeight||360, inset=15;              // inset = 球半径，保证 0%/100% 时球不越出滑道
  var y=Math.round(inset+p*(h-2*inset)/100);
  q('f_'+s.id).style.height=y+'px';                  // 填充到球心
  q('h_'+s.id).style.bottom=(y-inset)+'px';
}
function slMoveTo(s,clientY){
  var r=q('t_'+s.id).getBoundingClientRect(), inset=15;
  var t=(r.bottom-inset-clientY)/(r.height-2*inset);
  if(t<0) t=0; if(t>1) t=1;
  var v=Math.round(t*s.max);
  if(v!==s.val){ s.val=v; slPaint(s); }
}
function slCommit(s){
  if(s.blue) setBlue(s.val); else setVcp(s.code,s.val,s.name);
}
function slBump(s,dir){
  var step=5;
  var v=s.val+dir*step;
  if(v<0) v=0; if(v>s.max) v=s.max;
  if(v===s.val) return;
  s.val=v; slPaint(s); slCommit(s);
}
function buildSliders(){
  var grid=q('vgrid');
  for(var i=0;i<SLIDERS.length;i++){ (function(s){
    var cell=document.createElement('div');
    cell.className='vcell';
    cell.innerHTML='<div class=val id=v_'+s.id+'>–</div>'
      +'<button class=nudge id=p_'+s.id+'>+</button>'
      +'<div class=vtrack id=t_'+s.id+'><div class=vbar></div><div class=vfill id=f_'+s.id+'></div><div class=vthumb id=h_'+s.id+'></div></div>'
      +'<button class=nudge id=m_'+s.id+'>−</button>'
      +'<div class=lbl>'+s.name+'</div>';
    grid.appendChild(cell);

    var track=q('t_'+s.id);
    track.addEventListener('pointerdown',function(e){
      e.preventDefault();                     // 关键：不让浏览器把这次触摸升级成页面滚动
      s.dragging=true;
      try{ track.setPointerCapture(e.pointerId); }catch(err){}
      slMoveTo(s,e.clientY);
    });
    track.addEventListener('pointermove',function(e){
      if(!s.dragging) return;
      e.preventDefault();
      slMoveTo(s,e.clientY);
    });
    function up(e){
      if(!s.dragging) return;
      s.dragging=false;
      slCommit(s);                            // 松手才写 DDC：拖动中每格都发会把显示器刷死
    }
    track.addEventListener('pointerup',up);
    track.addEventListener('pointercancel',up);
    q('p_'+s.id).onclick=function(){ slBump(s,1); };
    q('m_'+s.id).onclick=function(){ slBump(s,-1); };
  })(SLIDERS[i]); }
}
function slSync(key,val,max){
  for(var i=0;i<SLIDERS.length;i++){
    var s=SLIDERS[i];
    if(s.id!==key) continue;
    if(max>0) s.max=max;
    if(s.dragging) return;                    // 正在拖就不让轮询把数值抢回去
    s.val=val; slPaint(s);
  }
}
function render(j){
  S=j;
  var pill=q('pill');
  if(j.controllable){ pill.textContent=String(j.input).toUpperCase()+' · 可控制'; pill.className='ok'; }
  else { pill.textContent='不可控'; pill.className='bad'; }
  slSync('br',j.brightness,j.brightnessMax||100);
  slSync('ct',j.contrast,j.contrastMax||100);
  slSync('bl',j.blue||0,100);
  buildGamma(j.gammaMax||4);
  buildScene(j.sceneMax||11);
  buildInputs();
  markAll('gam','data-i',j.gammaNum);
  markAll('sce','data-i',j.scene);
  markAll('inp','data-v',j.input);
  q('hdrv').textContent=(j.hdr==='on'?'已开':(j.hdr==='off'?'已关':'不支持/未知'));
  q('hdr').className=(j.hdr==='on')?'on wide':'wide';
}
function refresh(){
  api('/api/state',function(j){ render(j); if(location.search.indexOf('rect')>=0) measure(); });
}
function measure(){
  var old=document.getElementById('rectout'); if(old) old.parentNode.removeChild(old);
  var o=[];
  for(var i=0;i<SLIDERS.length;i++){
    var s=SLIDERS[i], tr=q('t_'+s.id).getBoundingClientRect();
    var mb=q('m_'+s.id).getBoundingClientRect();
    var pb=q('p_'+s.id).getBoundingClientRect();
    var th=q('h_'+s.id).getBoundingClientRect();
    o.push(s.id+':track '+Math.round(tr.top)+'..'+Math.round(tr.bottom)+' h'+Math.round(tr.height)
      +' plus '+Math.round(pb.top)+'..'+Math.round(pb.bottom)
      +' minus '+Math.round(mb.top)+'..'+Math.round(mb.bottom)
      +' thumb '+Math.round(th.top)+'..'+Math.round(th.bottom));
  }
  var t=document.createElement('div');
  t.id='rectout'; t.style.cssText='position:fixed;left:0;bottom:0;font-size:9px;color:#0f0;background:#000;z-index:9999';
  t.textContent=o.join(' / ');
  document.body.appendChild(t);
}
function setVcp(code,val,label){
  if(busy) return;
  setBusy(true);
  toast(label+' → '+val);
  api('/api/set?code='+code+'&value='+encodeURIComponent(val),function(j){
    toast(j.ok?(j.message||'完成'):('失败：'+j.error),!j.ok);
    setBusy(false);
    refresh();
  });
}
function switchTo(v){
  if(busy) return;
  setBusy(true);
  toast('切换中…');
  api('/api/switch?to='+v,function(j){
    toast(j.ok?(j.message||'完成'):('失败：'+j.error),!j.ok);
    setBusy(false);
    refresh();
  });
}
function setBlue(v){
  if(busy) return;
  setBusy(true);
  toast('低蓝光 → '+v);
  api('/api/blue?value='+encodeURIComponent(v),function(j){
    toast(j.ok?(j.message||'完成'):('失败：'+j.error),!j.ok);
    setBusy(false);
    refresh();
  });
}
buildSliders();
// 兜底：老 iOS 上 pointer 事件的 touch-action 有时不生效，拖动滑条期间在 document 上吃掉 touchmove，
// 保证页面绝不跟着滚（只在这段时间内拦，平时上下滑动滚动页面不受影响）。
document.addEventListener('touchmove',function(e){
  for(var i=0;i<SLIDERS.length;i++){ if(SLIDERS[i].dragging){ e.preventDefault(); return; } }
},{passive:false});
q('hdr').onclick=function(){
  if(busy) return;
  setBusy(true);
  toast('切换 HDR …');
  api('/api/hdr?on=toggle',function(j){
    toast(j.ok?(j.message||'完成'):('失败：'+j.error),!j.ok);
    setBusy(false);
    refresh();
  });
};
refresh(); setInterval(refresh,8000);
if(location.search.indexOf('diag')>=0){
  toast('iw='+window.innerWidth+' cw='+document.documentElement.clientWidth
    +' sw='+document.documentElement.scrollWidth+' bw='+document.body.clientWidth
    +' bsw='+document.body.scrollWidth+' dpr='+window.devicePixelRatio);
}";
    }
}

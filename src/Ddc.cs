using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorCtl
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public sealed class MonitorEntry
    {
        public IntPtr HMonitor;
        public string Device = "";
        public string Desc = "";
        public RECT Rect;
        public bool Primary;

        public string Size
        {
            get { return (Rect.Right - Rect.Left) + "x" + (Rect.Bottom - Rect.Top); }
        }
    }

    /// <summary>VCP 代码与输入源映射。数值来自本机 P2711R2+ 的 caps 串与 OSD 对照。</summary>
    public static class Vcp
    {
        public const byte Brightness = 0x10;
        public const byte Contrast = 0x12;
        public const byte ColorPreset = 0x14;
        public const byte RedGain = 0x16;
        public const byte GreenGain = 0x18;
        public const byte BlueGain = 0x1A;
        public const byte InputSource = 0x60;
        public const byte Volume = 0x62;
        public const byte PowerMode = 0xD6;
        public const byte SceneMode = 0xE0;
        /// <summary>伽马档位（私有码，已与 OSD 对照确认：0..4 对应 2.0/2.2/2.4/2.6/2.8）。</summary>
        public const byte Gamma = 0xE1;

        public static readonly string[] InputNames = { "dp1", "dp2", "hdmi1", "hdmi2" };
        public static readonly byte[] InputValues = { 0x0F, 0x10, 0x11, 0x12 };

        public static bool TryParseInput(string name, out byte value)
        {
            value = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().ToLowerInvariant();
            for (int i = 0; i < InputNames.Length; i++)
            {
                if (InputNames[i] == n) { value = InputValues[i]; return true; }
            }
            // 也接受数字：0x11 / 17 / 11(十六进制写法)
            try
            {
                if (n.StartsWith("0x")) { value = Convert.ToByte(n.Substring(2), 16); return true; }
                int v;
                if (int.TryParse(n, out v) && v >= 0 && v <= 255) { value = (byte)v; return true; }
            }
            catch { }
            return false;
        }

        public static string InputName(uint value)
        {
            for (int i = 0; i < InputValues.Length; i++)
            {
                if (InputValues[i] == value) return InputNames[i];
            }
            return "0x" + value.ToString("X2");
        }
    }

    /// <summary>
    /// DDC/CI 访问层。三条纪律（来自 2026-11-21 的实测）：
    /// 1) 每次操作都重新枚举显示器，句柄绝不缓存（切走之后旧句柄必失效）；
    /// 2) 所有访问串行化并带退避重试，不并发（官方软件每 10s 也在轮询 DDC）；
    /// 3) SetVCPFeature 返回 true 不代表显示器真的动了，调用方必须回读验证。
    /// </summary>
    public static class Ddc
    {
        const string Dxva2 = "dxva2.dll";
        static readonly object Gate = new object();

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport(Dxva2, SetLastError = true)]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);

        [DllImport(Dxva2, SetLastError = true)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] arr);

        [DllImport(Dxva2, SetLastError = true)]
        static extern bool DestroyPhysicalMonitors(uint n, [In] PHYSICAL_MONITOR[] arr);

        [DllImport(Dxva2, SetLastError = true)]
        static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);

        [DllImport(Dxva2, SetLastError = true)]
        static extern bool SetVCPFeature(IntPtr h, byte code, uint val);

        [DllImport(Dxva2, SetLastError = true)]
        static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);

        [DllImport(Dxva2, SetLastError = true, CharSet = CharSet.Ansi)]
        static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, [Out] System.Text.StringBuilder buf, uint len);

        /// <summary>枚举"有物理显示器"的显示输出（过滤掉 GameViewer/MuMu 这类虚拟适配器）。</summary>
        public static MonitorEntry[] Enumerate()
        {
            List<MonitorEntry> list = new List<MonitorEntry>();
            MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, IntPtr lprc, IntPtr data)
            {
                try
                {
                    MONITORINFOEX info = new MONITORINFOEX();
                    info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    if (!GetMonitorInfo(hMon, ref info)) return true;
                    if (info.szDevice == null || !info.szDevice.StartsWith("\\\\.\\DISPLAY")) return true;

                    uint n;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) || n == 0) return true;

                    MonitorEntry e = new MonitorEntry();
                    e.HMonitor = hMon;
                    e.Device = info.szDevice;
                    e.Rect = info.rcMonitor;
                    e.Primary = (info.dwFlags & 1) != 0;

                    PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
                    if (GetPhysicalMonitorsFromHMONITOR(hMon, n, arr))
                    {
                        e.Desc = arr[0].szPhysicalMonitorDescription;
                        DestroyPhysicalMonitors(n, arr);
                    }
                    list.Add(e);
                }
                catch { }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);

            list.Sort(delegate(MonitorEntry a, MonitorEntry b)
            {
                if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
                return string.CompareOrdinal(a.Device, b.Device);
            });
            return list.ToArray();
        }

        static bool OpenFirst(MonitorEntry m, out PHYSICAL_MONITOR[] arr, out uint n, out int err)
        {
            arr = null;
            err = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m.HMonitor, out n) || n == 0)
            {
                err = Marshal.GetLastWin32Error();
                if (err == 0) err = -1;
                return false;
            }
            arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(m.HMonitor, n, arr))
            {
                err = Marshal.GetLastWin32Error();
                if (err == 0) err = -1;
                arr = null;
                return false;
            }
            return true;
        }

        public static bool ReadVcp(MonitorEntry m, byte code, out uint cur, out uint max, out int err)
        {
            cur = 0;
            max = 0;
            err = 0;
            if (m == null) { err = -2; return false; }

            lock (Gate)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    uint n = 0;
                    PHYSICAL_MONITOR[] arr = null;
                    if (OpenFirst(m, out arr, out n, out err))
                    {
                        try
                        {
                            uint type = 0;
                            if (GetVCPFeatureAndVCPFeatureReply(arr[0].hPhysicalMonitor, code, out type, out cur, out max))
                                return true;
                            err = Marshal.GetLastWin32Error();
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(n, arr);
                        }
                    }
                    Thread.Sleep(attempt == 0 ? 200 : 300);
                }
            }
            return false;
        }

        public static bool WriteVcp(MonitorEntry m, byte code, uint value, out int err)
        {
            err = 0;
            if (m == null) { err = -2; return false; }

            lock (Gate)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    uint n = 0;
                    PHYSICAL_MONITOR[] arr = null;
                    if (OpenFirst(m, out arr, out n, out err))
                    {
                        try
                        {
                            if (SetVCPFeature(arr[0].hPhysicalMonitor, code, value)) return true;
                            err = Marshal.GetLastWin32Error();
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(n, arr);
                        }
                    }
                    Thread.Sleep(attempt == 0 ? 200 : 300);
                }
            }
            return false;
        }

        public static string ErrText(int err)
        {
            if (err == -2 || err == -1) return "句柄/枚举失败(" + err + ")";
            return "Win32 " + err;
        }

        /// <summary>读显示器能力串（caps）。里面有完整的 vcp(...) 清单，是找私有码的唯一可靠入口。</summary>
        public static bool GetCapabilities(MonitorEntry m, out string caps, out int err)
        {
            caps = "";
            err = 0;
            if (m == null) { err = -2; return false; }

            lock (Gate)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    uint n = 0;
                    PHYSICAL_MONITOR[] arr = null;
                    if (OpenFirst(m, out arr, out n, out err))
                    {
                        try
                        {
                            uint len = 0;
                            if (GetCapabilitiesStringLength(arr[0].hPhysicalMonitor, out len) && len > 0)
                            {
                                System.Text.StringBuilder sb = new System.Text.StringBuilder((int)len + 2);
                                if (CapabilitiesRequestAndCapabilitiesReply(arr[0].hPhysicalMonitor, sb, len + 1))
                                {
                                    caps = sb.ToString();
                                    return true;
                                }
                            }
                            err = Marshal.GetLastWin32Error();
                            if (err == 0) err = -1;
                        }
                        finally
                        {
                            DestroyPhysicalMonitors(n, arr);
                        }
                    }
                    Thread.Sleep(attempt == 0 ? 200 : 300);
                }
            }
            return false;
        }

        /// <summary>从 caps 串里解析 vcp(...) 的顶层十六进制码列表（跳过 60( 11 12 ) 这种子值括号）。</summary>
        public static List<byte> ParseVcpList(string caps)
        {
            List<byte> list = new List<byte>();
            if (string.IsNullOrEmpty(caps)) return list;
            int i = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return list;
            i += 4;
            int depth = 0;
            System.Text.StringBuilder tok = new System.Text.StringBuilder();
            for (; i < caps.Length; i++)
            {
                char c = caps[i];
                if (c == '(') { PushToken(tok, list); depth++; continue; }
                if (c == ')')
                {
                    if (depth == 0) break;
                    depth--; tok.Length = 0; continue;
                }
                if (depth > 0) continue;
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (hex) tok.Append(c);
                else PushToken(tok, list);
            }
            PushToken(tok, list);
            return list;
        }

        static void PushToken(System.Text.StringBuilder tok, List<byte> list)
        {
            if (tok.Length == 0) return;
            string s = tok.ToString();
            tok.Length = 0;
            if (s.Length > 2) s = s.Substring(s.Length - 2);
            byte b;
            if (byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out b))
            {
                if (!list.Contains(b)) list.Add(b);
            }
        }

        /// <summary>取第 index 个显示器；越界则取第一个。</summary>
        public static MonitorEntry Pick(int index)
        {
            MonitorEntry[] all = Enumerate();
            if (all.Length == 0) return null;
            if (index < 0 || index >= all.Length) index = 0;
            return all[index];
        }
    }
}

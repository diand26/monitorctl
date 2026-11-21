using System;
using System.Runtime.InteropServices;

namespace MonitorCtl
{
    /// <summary>
    /// HDR（Windows 高级颜色）开关。HDR 不走 DDC/CI——由操作系统通过 DP/HDMI 链路协商，
    /// 所以不需要管理员权限；但显示器必须自称支持 HDR（EDID/CTA 带 HDR 元数据）。
    /// 用 DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE 切换，等价于
    /// 设置 → 系统 → 显示 → HDR 那个开关。
    ///
    /// 关键坑：DISPLAYCONFIG_DEVICE_INFO_HEADER 的字段顺序是
    ///   type(4) / size(4) / adapterId(8) / id(4)，共 20 字节。
    /// 把 size 当成最后一个字段写 → DisplayConfigGetDeviceInfo 一律返回 87。
    /// </summary>
    public static class Hdr
    {
        // QDC_ALL_PATHS = 1，QDC_ONLY_ACTIVE_PATHS = 2（写 0 会得到 ERROR_INVALID_PARAMETER 87）
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const int GET_SOURCE_NAME = 1;
        const int GET_TARGET_NAME = 2;
        const int GET_ADVANCED_COLOR_INFO = 9;
        const int SET_ADVANCED_COLOR_STATE = 10;

        const int HeaderSize = 20;   // type(4) + size(4) + adapterId(8) + id(4)
        const int AdapterOffset = 8;
        const int IdOffset = 16;

        [StructLayout(LayoutKind.Sequential)]
        struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_SOURCE
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_TARGET
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public RATIONAL refreshRate;
            public uint scanLineOrdering;
            public uint targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_INFO
        {
            public PATH_SOURCE sourceInfo;
            public PATH_TARGET targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public ulong pad0;
            public ulong pad1;
            public ulong pad2;
            public ulong pad3;
            public ulong pad4;
            public ulong pad5;
        }

        [DllImport("user32.dll")]
        static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths,
            ref uint numModes, [Out] MODE_INFO[] modes, IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        static extern int GetDeviceInfo(IntPtr pkt);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
        static extern int SetDeviceInfo(IntPtr pkt);

        public sealed class State
        {
            public bool Found;
            public bool Supported;
            public bool Enabled;
            public string TargetName = "";
            public int Error;
            public string ErrorText = "";
        }

        /// <summary>找 deviceName（如 "\\.\DISPLAY1"）对应的 target，并读出 HDR 支持/开启状态。</summary>
        public static State Get(string deviceName)
        {
            State st = new State();
            uint numPaths = 0, numModes = 0;
            int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes);
            if (rc != 0) { st.Error = rc; st.ErrorText = "GetDisplayConfigBufferSizes rc=" + rc; return st; }

            PATH_INFO[] paths = new PATH_INFO[numPaths];
            MODE_INFO[] modes = new MODE_INFO[numModes];
            rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc != 0) { st.Error = rc; st.ErrorText = "QueryDisplayConfig rc=" + rc; return st; }

            LUID ad = new LUID();
            uint tid = 0;
            for (uint i = 0; i < numPaths; i++)
            {
                string gdi = SourceName(paths[i].sourceInfo);
                if (gdi == null) continue;
                if (!string.Equals(gdi, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                ad = paths[i].targetInfo.adapterId;
                tid = paths[i].targetInfo.id;
                st.Found = true;
                st.TargetName = TargetName(paths[i].targetInfo);
                break;
            }
            if (!st.Found) { st.ErrorText = "没有找到 " + deviceName + " 对应的显示输出（可用 list 看设备名）"; return st; }

            IntPtr buf = IntPtr.Zero;
            try
            {
                // DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO：header + value(4) + colorEncoding(4) + bitsPerChannel(4)
                buf = AllocPacket(GET_ADVANCED_COLOR_INFO, ad, tid, 12);
                rc = GetDeviceInfo(buf);
                if (rc != 0)
                {
                    st.Error = rc;
                    st.ErrorText = "该显示输出不支持高级颜色查询（rc=" + rc + "），通常是不支持 HDR";
                    return st;
                }
                uint flags = (uint)Marshal.ReadInt32(buf, HeaderSize);
                st.Supported = (flags & 1) != 0;
                st.Enabled = (flags & 2) != 0;
                if (!st.Supported) st.ErrorText = "Windows 未报告 HDR 支持";
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
            return st;
        }

        /// <summary>打开/关闭 HDR。返回 0 表示成功。</summary>
        public static int Set(string deviceName, bool enable, out string detail)
        {
            detail = "";
            State st = Get(deviceName);
            if (!st.Found) { detail = st.ErrorText; return st.Error != 0 ? st.Error : -1; }
            if (!st.Supported) { detail = "显示器不支持 HDR（或当前接口/线材不支持）"; return -2; }

            LUID ad;
            uint tid;
            if (!FindTarget(deviceName, out ad, out tid)) { detail = "找不到 " + deviceName; return -1; }

            IntPtr buf = IntPtr.Zero;
            try
            {
                // DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE：header + value(4)
                buf = AllocPacket(SET_ADVANCED_COLOR_STATE, ad, tid, 4);
                Marshal.WriteInt32(buf, HeaderSize, enable ? 1 : 0);
                int rc = SetDeviceInfo(buf);
                if (rc != 0)
                {
                    detail = "写入 HDR 状态失败 rc=" + rc + "（0x" + rc.ToString("X") + "）";
                    return rc;
                }
                detail = enable ? "HDR 已打开（屏幕会黑一下属正常）" : "HDR 已关闭";
                return 0;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        static bool FindTarget(string deviceName, out LUID ad, out uint tid)
        {
            ad = new LUID();
            tid = 0;
            uint numPaths = 0, numModes = 0;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0) return false;
            PATH_INFO[] paths = new PATH_INFO[numPaths];
            MODE_INFO[] modes = new MODE_INFO[numModes];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return false;
            for (uint i = 0; i < numPaths; i++)
            {
                string gdi = SourceName(paths[i].sourceInfo);
                if (gdi == null) continue;
                if (!string.Equals(gdi, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                ad = paths[i].targetInfo.adapterId;
                tid = paths[i].targetInfo.id;
                return true;
            }
            return false;
        }

        /// <summary>按正确字段顺序排一个 DISPLAYCONFIG_DEVICE_INFO 包，返回首地址，调用方 FreeHGlobal。</summary>
        static IntPtr AllocPacket(int type, LUID adapter, uint id, int payloadSize)
        {
            int total = HeaderSize + payloadSize;
            IntPtr buf = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, type);
            Marshal.WriteInt32(buf, 4, total);          // size 必须在偏移 4
            Marshal.StructureToPtr(adapter, (IntPtr)(buf.ToInt64() + AdapterOffset), false);
            Marshal.WriteInt32(buf, IdOffset, (int)id);
            return buf;
        }

        static string SourceName(PATH_SOURCE src)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = AllocPacket(GET_SOURCE_NAME, src.adapterId, src.id, 64); // CCHDEVICENAME=32 wchar
                if (GetDeviceInfo(buf) != 0) return null;
                return Marshal.PtrToStringUni((IntPtr)(buf.ToInt64() + HeaderSize));
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        static string TargetName(PATH_TARGET tgt)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                // header + flags(4) + outputTechnology(4) + edidManufactureId(2) + edidProductCodeId(2)
                // + connectorInstance(4) + monitorFriendlyDeviceName[64 wchar] + monitorDevicePath[128 wchar]
                buf = AllocPacket(GET_TARGET_NAME, tgt.adapterId, tgt.id, 16 + 128 + 256);
                if (GetDeviceInfo(buf) != 0) return "";
                return Marshal.PtrToStringUni((IntPtr)(buf.ToInt64() + HeaderSize + 16));
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }
    }
}

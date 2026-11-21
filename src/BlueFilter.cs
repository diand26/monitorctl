using System;
using System.Runtime.InteropServices;

namespace MonitorCtl
{
    /// <summary>
    /// 软件低蓝光：走显卡的 gamma ramp（gdi32 SetDeviceGammaRamp），不是显示器 DDC。
    ///
    /// 为什么要绕这一圈：P2711R2+ 的 OSD「低蓝光」没有对应的 VCP 码——2026-11-21 用 OSD
    /// 差分验证过，把低蓝光从 25 调到 50，31 个可读码里只有 0xE1（伽马）变了。所以 DDC
    /// 拿不到它，只能用显卡侧滤光模拟一个近似效果，并且必须在 UI 里说清楚这是"软件滤镜"。
    ///
    /// 已知边界：
    /// - HDR 打开时 Windows 会忽略 gamma ramp，滤镜不生效（GetDeviceGammaRamp 仍返回旧值）；
    /// - 独占全屏游戏可能绕过；部分驱动会拒绝写 ramp（返回 false）。
    /// </summary>
    public static class BlueFilter
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        // 满强度时蓝通道降到 45%，绿通道降到 82%，红不动——这是常见护眼滤镜的量级，
        // 再狠就明显发黄了。
        const double BlueDrop = 0.55;
        const double GreenDrop = 0.18;

        /// <summary>强度 0-100 写进 gamma ramp。0 = 还原成线性。</summary>
        public static bool Apply(string device, int strength, out string detail)
        {
            detail = "";
            if (strength < 0) strength = 0;
            if (strength > 100) strength = 100;

            ushort[] ramp = BuildRamp(strength);
            IntPtr hdc = OpenDc(device, out detail);
            if (hdc == IntPtr.Zero) return false;
            try
            {
                if (!SetDeviceGammaRamp(hdc, ramp))
                {
                    int err = Marshal.GetLastWin32Error();
                    detail = "写 gamma ramp 失败（错误 " + err + "）：HDR 开着或显卡驱动拒绝了软件滤镜";
                    return false;
                }
                detail = strength == 0 ? "低蓝光已关闭" : ("低蓝光 " + strength + "%（软件滤镜）");
                return true;
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        /// <summary>从当前 ramp 反推强度，用来在状态栏显示"实际是多少"，而不是只信配置值。</summary>
        public static int Current(string device, out string detail)
        {
            detail = "";
            ushort[] ramp = new ushort[3 * 256];
            IntPtr hdc = OpenDc(device, out detail);
            if (hdc == IntPtr.Zero) return -1;
            try
            {
                if (!GetDeviceGammaRamp(hdc, ramp)) { detail = "读 gamma ramp 失败"; return -1; }
                // 取中灰点，蓝通道比值反推（ramp 布局是 R 0..255 / G 256..511 / B 512..767）
                // 注意分母是 128*257：ramp 的线性基准值随 i 变化，拿 65535 当分母会把强度算飞
                // （2026-11-21 首版就错在这里，30% 会读成 100%）。
                int b = ramp[512 + 128];
                double ratio = b / (128.0 * 257.0);
                double s = (1.0 - ratio) / BlueDrop;
                if (s < 0) s = 0;
                if (s > 1) s = 1;
                return (int)Math.Round(s * 100);
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        static ushort[] BuildRamp(int strength)
        {
            double s = strength / 100.0;
            double kg = 1.0 - GreenDrop * s;
            double kb = 1.0 - BlueDrop * s;
            ushort[] ramp = new ushort[3 * 256];
            for (int i = 0; i < 256; i++)
            {
                ushort v = (ushort)(i * 257);
                ramp[i] = v;                                  // R
                ramp[256 + i] = (ushort)(v * kg);             // G
                ramp[512 + i] = (ushort)(v * kb);             // B
            }
            return ramp;
        }

        static IntPtr OpenDc(string device, out string detail)
        {
            detail = "";
            // device 形如 \\.\DISPLAY1；空则用主显示器
            string dev = string.IsNullOrEmpty(device) ? @"\\.\DISPLAY1" : device;
            IntPtr hdc = CreateDC("DISPLAY", dev, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                detail = "打不开显示设备 " + dev + "（错误 " + err + "）";
            }
            return hdc;
        }
    }
}

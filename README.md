# monitorctl

[![build](https://github.com/diand26/monitorctl/actions/workflows/build.yml/badge.svg)](https://github.com/diand26/monitorctl/actions/workflows/build.yml)

Windows 上的 DDC/CI 显示器控制小工具：**托盘 + 全局热键 + 命令行 + 桌面设置窗口 + 局域网手机网页**。

直接读写显示器暴露的 VCP 码（亮度、对比度、输入源、伽马、情景模式等），不装驱动、不注入进程、不模拟按键、不读游戏内存。HDR 开关走 Windows 显示接口，软件低蓝光走显卡 gamma ramp——两者都不碰固件。

> **仅支持 Windows。** DDC/CI 只在「当前正在显示的那路输入」上可达：切到别的输入源之后，本机就控制不了显示器了。这是协议/硬件限制，不是 bug，详见[已知限制](#已知限制)。
>
> 手机网页功能请**只在可信内网使用**，不要暴露到公网，详见[手机网页](#手机网页)。

[English summary ↓](#english)

## 目录

- [功能](#功能)
- [安装](#安装)
- [快速上手](#快速上手)
- [命令行](#命令行)
- [全局热键](#全局热键)
- [手机网页](#手机网页)
- [配置文件](#配置文件)
- [构建](#构建)
- [已知限制](#已知限制)
- [排错](#排错)
- [VCP 对码表（实测）](#vcp-对码表实测)
- [许可证](#许可证)

## 功能

| 功能 | 说明 |
| --- | --- |
| 亮度 / 对比度 | 读取、写入、相对加减（VCP `0x10` / `0x12`），写完回读校验 |
| 输入源切换 | DP1 / DP2 / HDMI1 / HDMI2（VCP `0x60`），切后 1.2 秒回读，区分「真切走」与「假成功」 |
| 伽马档位 | 0..4 五档（VCP `0xE1`，厂商私有扩展，非标准 MCCS 语义） |
| 情景模式 | 0..N 档（VCP `0xE0`；切换会把亮度/对比度/伽马重置成该模式预设） |
| HDR 开关 | 调 Windows 显示接口（DisplayConfig），不走 DDC；不是所有显示器/驱动组合都支持 |
| 软件低蓝光 | 显卡 gamma ramp 压蓝通道，软件模拟；HDR 开启时不生效 |
| 托盘 + 热键 | 托盘菜单、气泡提示、15 个可自定义全局热键 |
| 设置窗口 | 画面参数 + 热键录制，改完写回 `monitorctl.ini`，无需重启 |
| 手机网页 | 局域网浏览器里拖滑块、切伽马/情景/HDR/输入源，8 秒轮询 |
| 诊断命令 | 列出显示器、读全部 VCP、打印 caps、逐码探测、通用写入、快照 |

## 安装

### 方式一：下载 Release（推荐）

1. 打开 [Releases](https://github.com/diand26/monitorctl/releases)，下载对应版本的 `monitorctl.exe`。
2. 放到任意目录（不要放系统目录，避免权限问题）。双击 = 托盘模式；命令行用法见下。
3. 首次运行会在 exe 同目录生成 `monitorctl.ini`；在设置窗口或命令行里改完会自动保存。
4. 校验下载完整性（和 Release 里的 `.sha256` 资产或发布说明对比）：

```powershell
Get-FileHash .\monitorctl.exe -Algorithm SHA256
# 或者
certutil -hashfile .\monitorctl.exe SHA256
```

> exe 没有代码签名，Windows SmartScreen 会提示「未知发布者」。核对 SHA-256 后可选「更多信息 → 仍要运行」；介意的话请从源码自己构建。

### 方式二：从源码构建

见[构建](#构建)，只需要 Windows 自带的 .NET Framework 4.8，不用装 SDK。

## 快速上手

```powershell
.\monitorctl.exe                       # 托盘模式（无参数 = 托盘）
.\monitorctl.exe list                  # 列出物理显示器与当前输入源
.\monitorctl.exe get                   # 读亮度/对比度/色温/RGB/音量/输入源/电源
.\monitorctl.exe brightness +5         # 亮度 +5（也接受 -5 或绝对值 45）
.\monitorctl.exe contrast 70           # 对比度设为 70
.\monitorctl.exe gamma next            # 伽马下一档（next / prev / 2.4 / 档位号）
.\monitorctl.exe scene next            # 情景模式下一档
.\monitorctl.exe hdr toggle            # 切换 Windows HDR
.\monitorctl.exe blue 40               # 软件低蓝光 40/100（不是 DDC）
.\monitorctl.exe input dp2             # 切到 DP2
.\monitorctl.exe input hdmi1 --dry-run # 只打印将要做什么，不写显示器
.\monitorctl.exe settings              # 打开设置窗口
.\monitorctl.exe serve                 # 手机网页（先配 key，见下）
```

CLI 每次执行都重新枚举显示器、不缓存句柄；所有 DDC 访问串行并带退避与重试，写完回读。所以单条命令可能要几百毫秒，属正常。

## 命令行

| 命令 | 作用 |
| --- | --- |
| `monitorctl` / `monitorctl tray` | 启动托盘（单实例；已在运行时会提示看托盘） |
| `monitorctl help` | 打印用法 |
| `monitorctl list` | 列出物理显示器与当前输入源 |
| `monitorctl get` | 读 亮度/对比度/色温/RGB/音量/输入源/电源 |
| `monitorctl brightness +5` | 亮度相对调整（`-5` 或绝对值 `45`） |
| `monitorctl contrast +5` | 对比度（同样支持相对/绝对） |
| `monitorctl gamma next` | 伽马 `next` / `prev` / `2.4` / 档位号 |
| `monitorctl scene next` | 情景模式 `next` / `prev` / 序号 |
| `monitorctl hdr on\|off\|toggle\|status` | 切换/查询 Windows HDR（不走 DDC） |
| `monitorctl blue 40` | 软件低蓝光 0-100；`blue 0` 还原 |
| `monitorctl input dp2` | 切输入源：`dp1` / `dp2` / `hdmi1` / `hdmi2`；加 `--dry-run` 只打印 |
| `monitorctl caps` | 打印显示器能力串（含全部 VCP 码） |
| `monitorctl probe` | 把 caps 里每个 VCP 码都读一遍（找 OSD 未对上号的项） |
| `monitorctl set 0x86 3` | 通用写入任意 VCP 码（写后回读验证） |
| `monitorctl snap` | 一行输出全部 VCP 当前值（排查用） |
| `monitorctl serve [--hide]` | 手机网页入口；`--hide` 隐藏控制台窗口 |
| `monitorctl settings` | 直接打开设置窗口 |
| `monitorctl init` | 在 exe 旁边生成默认 `monitorctl.ini`（已存在则不覆盖） |
| `monitorctl pull` | 设计上用于让另一台主机把屏幕切回来；**尚未实现**，只打印说明 |

## 全局热键

默认绑定如下，可在设置窗口里录制修改，或直接改 `monitorctl.ini` 的 `[hotkey]` 段（空值 = 不绑定）。

| 热键 | 作用 |
| --- | --- |
| `Alt+H` / `Alt+L` | 亮度 +5 / −5 |
| `Alt+Shift+H` | HDR 开关 |
| `Alt+1` .. `Alt+4` | 切 DP1 / DP2 / HDMI1 / HDMI2 |
| `Ctrl+Alt+R` | 重新识别显示器 |
| `Ctrl+Alt+S` | 打开设置窗口 |
| （默认空） | 对比度 ±、伽马 ±、情景模式下一档、对端切回 |

## 手机网页

```powershell
.\monitorctl.exe serve          # 前台运行，启动时打印所有可用地址
.\monitorctl.exe serve --hide   # 后台运行（开机自启用这个）
```

1. 在 `monitorctl.ini` 的 `[http]` 段设置 `key=`（首次运行 `serve` 时程序会提示一个随机 key，粘进去即可）。
2. 手机和电脑在同一局域网，浏览器打开 `http://<电脑IP>:8788/?k=<key>`。
3. 页面提供：亮度 / 对比度 / 低蓝光三根竖排自绘滑块（步长跟 `[general] step` 一致）、伽马 5 档、情景模式档位、HDR 开关、四个输入源按钮、状态胶囊与 toast 反馈。

> **安全提醒（重要）**
>
> - 访问 key 只有 6 位字符，放在 URL query 里，**没有 TLS、没有速率限制**；服务监听所有网卡（`0.0.0.0`，默认端口 8788）。
> - 只适合**可信内网**：不要做端口映射/公网反代，不要在公共 Wi-Fi 下开，不要在截图或录屏里露出带 `?k=` 的完整 URL。
> - 不用时直接 Ctrl+C 或关掉 `serve`。`key` 为空时 `serve` 会拒绝启动，这是刻意的默认值。
> - 走 Tailscale 之类 VPN 时，用 VPN 分配的地址访问。

## 配置文件

`monitorctl.ini` 在 exe 同目录，首次运行自动生成；改完重启托盘生效（CLI 每次读取）。

```ini
[general]
monitor=0        ; 第几个物理显示器（0 = 第一个/主显示器）
step=5           ; 亮度/对比度步长
confirm=0        ; 1 = 切走输入源前弹窗确认
notify=1         ; 托盘气泡提示
debuglog=0       ; 1 = 详细日志
blue=0           ; 软件低蓝光强度 0-100；启动时按此值重放 gamma ramp

[hotkey]         ; 动作=组合键；空值 = 不绑定
up=Alt+H
down=Alt+L
contrast_up=
contrast_down=
gamma_next=
gamma_prev=
hdr=Alt+Shift+H
scene_next=
input1=Alt+1
input2=Alt+2
input3=Alt+3
input4=Alt+4
rescan=Ctrl+Alt+R
settings=Ctrl+Alt+S
peer_pull=

[peer]
; 另一台主机的 SSH 目标（该功能尚未实现）
ssh=

[http]
port=8788
; 访问 key：必须自己设；留空时 serve 拒绝启动
key=
```

日志写在 `%LOCALAPPDATA%\monitorctl\log.txt`，超过 1 MB 轮转为 `log.1.txt`（只留一代）。`debuglog=1` 会记录每次 DDC 读写，排查「写完没生效」时有用。

## 构建

### 要求

- Windows 10 1809+ 或 Windows 11。
- .NET Framework 4.8（上述系统自带）。
- PowerShell 5.1（系统自带）或 PowerShell 7。
- **不需要** dotnet SDK、NuGet、Visual Studio。

### 步骤

```powershell
git clone https://github.com/diand26/monitorctl.git
cd monitorctl
pwsh -NoProfile -File .\build.ps1
# Windows 自带 5.1 也可以：
# powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

产物是 `dist\monitorctl.exe`，脚本会同时打印大小与 SHA-256。

`build.ps1` 做的事：

- 先结束「正在占用 `dist\monitorctl.exe` 这个文件」的实例（只按完整路径匹配，不误杀其它目录的拷贝），避免 Windows 上覆盖运行中的 exe 报 CS0016。
- 按顺序找 C# 编译器：`Framework64` 的 csc → 32 位 `Framework` 的 csc → 用 `vswhere` 找到的 Visual Studio / Build Tools 里的 Roslyn csc。都找不到时打印可操作的安装指引。
- 用 `/langversion:5`（代码只用了 C# 5 语法）和 `/codepage:65001`（源码是无 BOM 的 UTF-8，防止换系统代码页后中文乱码）编译 `src\*.cs`。
- 可选 `-Test`：编译后跑 `init` / `list` / `get` 冒烟，需要接一台支持 DDC/CI 的显示器。

CI：GitHub Actions 在 `windows-latest` 上每次 push 都会构建，并把 `dist\monitorctl.exe` 与 `.sha256` 作为 artifact 上传（见 [Actions](https://github.com/diand26/monitorctl/actions)）。

### 下载 Release 后校验

```powershell
Get-FileHash .\monitorctl.exe -Algorithm SHA256
```

哈希应与 Release 的 `.sha256` 资产一致。

## 已知限制

1. **低蓝光是软件滤镜，不是硬件低蓝光。** 它通过 `gdi32!SetDeviceGammaRamp` 压蓝/绿通道（满强度：蓝 45%、绿 82%、红不变），作用于整个桌面；HDR 开启时 Windows 会忽略 gamma ramp；重启/重新登录后 ramp 会被清掉，程序启动时按 ini 的 `blue=` 重放；部分驱动/独占全屏可能绕过。它不是显示器 OSD 里那个「低蓝光」档——那档没有开放给 PC。
2. **锐利度 / 动态对比度在本型号上不可控。** 作者实测的显示器没有把这两项开放给 PC：DDC caps 里没有对应码，`0x86`/`0x87` 无响应，`0xB2` 写入被拒，OSD 改这两项时也没有任何 VCP 值跟着变。所以程序不做假滑块，只能用显示器按键调。别的型号可能支持，可自行用 `monitorctl set` 试。
3. **切走输入源后，本机就控制不了显示器。** DDC/CI 只对「当前正在显示的那路输入」可达。切回只有两条路：按显示器上的按键；或由另一台正在显示的主机反向切回（`pull` 的设计目标，**尚未实现**）。程序对每次切换都做 1.2 秒后回读：读不到 = 真的切走了；读得到但值没变 = 假成功，会明确报警。

其他限制：

- 仅 Windows。DDC/CI 兼容性因显示器、线材、扩展坞而异；虚拟显示适配器和多数笔记本内屏不可控。
- HDR 开关依赖 Windows 显示接口与驱动支持，不是所有配置都能切。
- 情景模式 / 伽马的 VCP 码是厂商私有语义，别的显示器上可能不存在或含义不同；`caps` / `probe` 可先探测。
- exe 未签名，SmartScreen 会告警；Release 附 SHA-256。
- 界面目前是中文。
- 多显示器时用 `[general] monitor=` 选第几个物理显示器。

## 排错

| 现象 | 处理 |
| --- | --- |
| `list` 说找不到可控制的物理显示器 | 显示器 OSD 里打开 DDC/CI；用 DP/HDMI 直连（部分扩展坞/转接器不透传 DDC）；笔记本内屏通常不支持 |
| 亮度/对比度写完没变化 | 先看 OSD 里是否开着「动态对比度」，它会锁住亮度/对比度；关掉再试 |
| 切走后画面回不来 | 按显示器按键，或由另一台正在显示的主机切回（物理限制） |
| 手机页打不开 | 确认同一网段；Windows 防火墙允许专用网络；URL 带 `?k=<key>`；端口被占用时改 `[http] port` |
| 页面一直「读取中」 | 看日志；浏览器控制台里的 JS 报错能定位问题 |
| 双击没反应 / 提示已在运行 | 单实例：看系统托盘图标 |
| SmartScreen 拦截 | 「更多信息 → 仍要运行」，并核对 SHA-256 |

排查日志：`%LOCALAPPDATA%\monitorctl\log.txt`（`debuglog=1` 更详细）。

## VCP 对码表（实测）

以下数值来自作者自用的泰坦军团 P2711R2+ 实测，**不同型号会不同**，仅作参考；用 `monitorctl caps` / `probe` 探测自己的显示器：

| 码 | 含义 | 实测值 | 说明 |
| --- | --- | --- | --- |
| `0x10` | 亮度 | 0–100 | 连续量；OSD 开「动态对比度」时被锁 |
| `0x12` | 对比度 | 0–100 | 连续量 |
| `0x14` | 色温预设 | 5/12 | 暖色/冷色/自然/用户等 |
| `0x16/18/1A` | RGB 增益 | 47/47/50 | 0–100 |
| `0x60` | 输入源 | `0x0F` DP1、`0x10` DP2、`0x11` HDMI1、`0x12` HDMI2 | 与 OSD 对照确认 |
| `0x62` | 音量 | 57/100 | |
| `0xD6` | 电源模式 | 1 | 1=开 |
| `0xE0` | 情景模式 | 0/11 | 写入会重置 `0x10` / `0x12` / `0xE1` |
| `0xE1` | 伽马 | 1/4 | 0–4 ↔ 2.0 / 2.2 / 2.4 / 2.6 / 2.8 |
| `0xFE` | 固件版本 | 20737 | = `0x5101` |
| `0xF3` | 变更计数器 | 递增 | 每次设置变更 +1，不是控件 |

## 设计说明

- 每次操作重新枚举显示器，句柄从不缓存。
- 所有 DDC 访问串行 + 200 ms 退避 + 1 次重试。
- 写完必须回读，不信返回值：显示器异步处理，回读带 4 次重试、容忍 ±1 量化误差。
- 亮度写不进时先怀疑 OSD 的「动态对比度」锁。
- 手机页 JS 刻意用 ES5（XMLHttpRequest + 普通函数），兼容老手机浏览器。

## 许可证

[GPL-3.0-only](LICENSE)。简单说：可以自由使用、修改、分发；分发修改版时必须按 GPL-3.0 提供对应源码。本程序不提供任何担保。

## English

**monitorctl** is a small Windows tool for controlling monitors over DDC/CI: tray icon, global hotkeys, CLI, a settings window, and a LAN web page for your phone. It reads/writes monitor VCP codes (brightness, contrast, input source, gamma, picture mode) and does not install drivers, inject processes, or emulate input.

- **Requirements:** Windows 10 1809+ / Windows 11. No SDK required — it is built with the C# compiler that ships with .NET Framework 4.8. PowerShell 5.1 (built in) or PowerShell 7.
- **Install:** download `monitorctl.exe` from [Releases](https://github.com/diand26/monitorctl/releases), verify the SHA-256, run it. Double-click starts the tray; run it from a terminal for CLI use.
- **Build from source:** `pwsh -NoProfile -File .\build.ps1` → `dist\monitorctl.exe` (PowerShell 5.1 works too).
- **Known limitations:** DDC/CI is reachable only on the currently displayed input — switching to another input makes this host lose control (physical limitation; switch back with the monitor buttons or from the other host). The blue-light filter is a software gamma ramp, not a hardware feature (ignored while HDR is on). Sharpness and dynamic contrast are not exposed to the PC on the tested monitor, so there are no fake controls for them. The `pull` peer feature is not implemented. The binary is unsigned, so SmartScreen warns.
- **Security:** the phone page listens on all interfaces with a 6-character key in the URL query, without TLS or rate limiting. Use it only on a trusted LAN; never expose it to the internet. Leave `[http] key` empty and the server refuses to start.
- **License:** GPL-3.0-only.

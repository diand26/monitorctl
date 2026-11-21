# Changelog

本文件记录公开发布的变化。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.1.0] - 2026-11-22

首个公开发布版本。

### Added

- 亮度 / 对比度：读取、写入、相对加减（VCP `0x10` / `0x12`），写后回读校验。
- 输入源切换 DP1 / DP2 / HDMI1 / HDMI2（VCP `0x60`），切换后 1.2 秒回读，区分「真的切走了」和「假成功」。
- 伽马档位 0..4（VCP `0xE1`，非标准私有用法）与情景模式（VCP `0xE0`）。
- HDR 开关（走 Windows 显示接口，不是 DDC）。
- 软件低蓝光 0-100（显卡 gamma ramp，软件模拟）。
- 托盘图标与菜单、15 个可自定义全局热键、托盘气泡提示。
- 桌面设置窗口：画面参数 + 热键录制，改完写回 `monitorctl.ini`。
- 局域网手机网页：竖排自绘滑块调亮度/对比度/低蓝光，切换伽马/情景模式/HDR/输入源，8 秒轮询；`[http] key` 为空时拒绝启动。
- CLI 诊断命令：`list` / `get` / `caps` / `probe` / `snap` / `set`。
- `build.ps1`：零 SDK 依赖编译（自动探测 csc：Framework64 → Framework → Visual Studio），输出 exe 与 SHA-256。
- GitHub Actions：`windows-latest` 上每次 push 构建并上传 exe + `.sha256` artifact。

### Known limitations

- 仅支持 Windows，且 DDC/CI 只在「当前正在显示的那路输入」上可达：切走输入源后本机失联，只能按显示器按键或由另一台主机切回。
- 低蓝光是软件 gamma 滤镜，不是显示器原生硬件低蓝光；HDR 开启时不生效，重启后需要程序重新应用。
- 锐利度 / 动态对比度在作者实测的显示器上没有开放给 PC（无对应 VCP 码），程序不提供控件。
- `pull`（让另一台主机把屏幕切回来）只有占位命令，SSH 后端尚未实现。
- exe 没有代码签名，Windows SmartScreen 会提示未知发布者。

### Security

- 手机网页只适合可信内网：访问 key 是 6 位字符、走 URL query、无 TLS、无速率限制。不要把端口映射到公网。

[0.1.0]: https://github.com/diand26/monitorctl/releases/tag/v0.1.0

# Switch2 Pro Wireless VIIPER

[English](README.md)

Switch2 Pro Wireless VIIPER 是一个非官方 WinUI 3 桌面程序，用于让真实的
Switch 2 Pro Controller 通过 Bluetooth LE 直接连接 Windows，并通过 VIIPER 的
`ns2pro` 虚拟 USB 设备呈现给 Steam、SDL 和游戏。

本项目的目标是通过功能整合和现代化 UI，实现一个低门槛且美观实用的手柄适配方案。

## 快速开始

1. 安装 [usbip-win2](https://github.com/vadimgrn/usbip-win2)。Windows 上的
   VIIPER 需要这个驱动来挂载虚拟 USB 手柄。
2. 从本仓库的 [GitHub Releases](../../releases) 页面下载最新 ZIP。
3. 解压整个 ZIP 到普通文件夹。请保持 `Switch2ProWirelessViiper.exe` 和 `app`
   文件夹放在一起。
4. 打开 `Switch2ProWirelessViiper.exe`。
5. 按照初次启动向导选择语言、检查环境、扫描手柄，并确认启动和托盘设置。
6. 按住手柄配对键直到指示灯闪烁，然后在程序里扫描并连接。

正式发布包应在 `app\` 中包含 `viiper.exe`。如果你从源码本地发布，请先把
`viiper.exe` 和 `VIIPER_LICENSES.txt` 放入 `tools\viiper\`。

## 运行链路

```text
Switch 2 Pro Controller (BLE)
  -> Windows BLE GATT 客户端
  -> FD2 输入和体感解析
  -> VIIPER ns2pro TCP 数据流
  -> VIIPER 虚拟 USB 设备
  -> Steam / SDL / 游戏
```

## 特性

- 直接 Bluetooth LE 扫描和连接，不再需要外部设备桥接。
- 一键连接/断开，并支持启动时预加载 VIIPER，减少点击连接后的等待时间。
- 初次启动向导覆盖语言选择、环境检查、手柄扫描和启动行为设置。
- Windows 11 风格 WinUI 3 界面。
- 支持系统托盘、托盘菜单连接/断开、开机自启动和启动后隐藏到托盘。
- 通过 VIIPER `ns2pro` 输出为虚拟有线 Switch 2 Pro Controller。
- 转发按键、摇杆、扳机和体感/陀螺仪数据。
- 支持摇杆端点校准，配置会持久保存。
- 支持把 VIIPER 的震动反馈转发回真实手柄。
- 性能页显示 BLE 回报率、VIIPER 提交率、桥接延迟、报告间隔、积压和错误数。
- 支持闲置自动断开和单实例检测。
- 运行时较低的系统占用。
- 界面语言：简体中文、English、日本語。

## 鸣谢和灵感来源

这个程序建立在多个社区项目的研究和实现基础上：

- [VIIPER](https://github.com/Alia5/VIIPER) 提供了虚拟 USB/USBIP 架构和
  `ns2pro` 设备路径，使 Steam 和 SDL 能识别类似 Switch 2 Pro 有线手柄的设备。
- `y700-switch2-pro-bridge` 验证了 Switch 2 Pro 从 BLE 到 USB 桥接的整体思路，
  对 FD2 输入报告布局、震动路径、Steam 映射行为和回报率预期提供了重要参考。
- [joycon2-connector](https://github.com/Misaka10571/joycon2-connector) 展示了
  直接连接 Windows 蓝牙的可行工作流，并启发了本项目的 BLE 扫描和连接方向。

## 已知缺陷

- 当前 Windows BLE 链路在已测试系统上实际受 15 ms 连接间隔限制，因此通常只能达到
  约 67 Hz，而不是 125 Hz 或 133 Hz。公开 WinRT BLE API 不能稳定地把该手柄链路
  强制到 7.5 ms。
- 未实现语音相关功能。
- 未实现耳机音频、扬声器音频和麦克风音频。
- 未实现完整 HD Rumble 2 的音频级复刻；目前只转发已经理解的震动反馈路径。
- 实际表现会受到 Windows 版本、蓝牙适配器、手柄固件、Steam/SDL 版本和无线环境
  影响。
- 最终想达到完整官方级适配，仍然需要任天堂和平台厂商提供正式支持。

## 运行要求

普通用户需要：

- Windows 11，或 Windows 10 22H2 及更新版本。
- Windows 支持的 Bluetooth LE 适配器。
- 已安装 [usbip-win2](https://github.com/vadimgrn/usbip-win2)。
- 包含本程序和 `viiper.exe` 的发布包。

本地构建需要：

- .NET SDK 10。
- 来自 [VIIPER](https://github.com/Alia5/VIIPER/releases) 的 `viiper.exe`。

## 构建

```powershell
dotnet build .\Switch2ProWirelessViiper.csproj -c Release
```

## 发布

```powershell
.\scripts\publish.ps1
```

发布结果会写入：

```text
release\Switch2ProWirelessViiper.exe
release\app\
```

直接打开 `release\Switch2ProWirelessViiper.exe`。`app` 文件夹包含
WinUI/.NET 运行时文件、语言资源和 `viiper.exe`；请保持它和启动器位于同一目录下。

`release`、`bin` 和 `obj` 文件夹都是生成输出，已经被 Git 忽略。

## 仓库结构

```text
Core\                         BLE、解析器、震动和 VIIPER 桥接代码
launcher\Launcher.cs           指向 app 子目录的小型发布启动器
scripts\publish.ps1           干净的本地发布脚本
tools\viiper\                 可选的本地 VIIPER 二进制来源
App.xaml / App.xaml.cs        WinUI 应用启动代码
MainWindow*.cs                主界面和初次启动向导
Switch2ProWirelessViiper.csproj
```

## 免责声明

这是一个非官方社区项目，与 Nintendo、Valve、Microsoft、Alia5 以及被参考项目的作者
均无从属、赞助或背书关系。Nintendo Switch、Switch 2 Pro Controller、Steam、
Windows、VIIPER 等名称归各自权利人所有。

本软件仍属实验性质，不提供任何担保。它会与蓝牙设备、虚拟 USB 设备和第三方驱动
交互，请自行承担使用风险。如果某些游戏、比赛、平台条款或反作弊环境不允许非官方
输入转换，请不要在这些场景使用。

## 许可证

本仓库原创代码采用 Apache License 2.0 开源协议，详见 [LICENSE](LICENSE)。

第三方组件保留各自许可证：

- VIIPER 是独立项目，由其上游作者授权。若发布包内包含 `viiper.exe`，请同时包含
  上游许可证文本，例如 `VIIPER_LICENSES.txt`。
- `y700-switch2-pro-bridge` 采用 Apache License 2.0。
- `joycon2-connector` 采用 MIT License。
- Microsoft .NET、Windows App SDK、WinUI 和 usbip-win2 遵循各自上游许可证。

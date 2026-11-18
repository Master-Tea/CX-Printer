# CX Printer

把 Windows 打印机共享给同一局域网中的 iPhone、iPad 和 Mac。

CX Printer 使用 Windows 已安装的打印驱动处理作业，通过 IPP 和 DNS-SD 提供打印与设备发现。Windows 端用图形界面管理共享，Apple 设备无需另装本项目的客户端。

**当前版本：1.0.0.8 · Windows 10 1809 及以上 · x64**

[Windows7/8.1 兼容版请看这里](https://github.com/Master-Tea/CX-Printer-Legacy)

[下载安装包](https://github.com/Master-Tea/CX-Printer/releases/latest) · [安装说明](docs/msix-installation.md) · [本版更新](docs/release-1.0.0.8.md)

## 能做什么

| 功能 | 说明 |
| --- | --- |
| 局域网打印 | iPhone、iPad 的系统打印入口，以及 Mac 的打印机添加界面可发现已共享的打印机 |
| 自定义显示名称 | 为每台打印机设置 Apple 设备所见名称，支持中文、emoji 和点号 |
| 驱动纸张 | 读取 Windows 驱动提供的纸张尺寸，支持设置每台打印机的默认纸张 |
| 常用打印选项 | 份数、彩色/黑白，以及驱动支持时的双面打印 |
| 耗材信息 | 读取驱动提供的墨水、碳粉等余量，在管理界面显示并通过 IPP 上报 |
| 打印认证 | 可设置独立的打印账户和密码 |
| 后台运行 | 独立 Windows 服务、开机启动、系统托盘、作业记录和连接诊断 |

## 安装与首次使用

准备一台能正常打印的 Windows 电脑，并让电脑与 Apple 设备接入可互通的局域网。当前发布包为 x64，包含所需的 .NET 和 Windows App SDK 运行时。

1. 从 [发布](https://github.com/Master-Tea/CX-Printer/releases/latest) 下载 `CX-Printer_1.0.0.8_x64.msix` 和 `MasterTea.cer`。
2. 首次安装时，双击证书，选择“本地计算机 → 将所有的证书都放入下列存储 → 受信任人”。导入证书需要管理员权限。
3. 双击 MSIX 安装，从开始菜单打开 **CX Printer**。
4. 在“打印机共享”页点击“安装服务”，按 Windows 提示完成管理员授权。
5. 勾选要共享的打印机，按需设置显示名称和默认纸张，点击“保存共享设置”。
6. 在 iPhone/iPad 中打开支持打印的应用，选择“分享 → 打印”；在 Mac 中打开“系统设置 → 打印机与扫描仪 → 添加打印机”。

默认共享名称为 `Windows 打印机名称- CX Printer`。选中打印机可修改名称；留空或“恢复默认”可恢复默认值。同一电脑的共享名称不能重复，名称最长为 63 个 UTF-8 字节，约为 63 个英文字符或 21 个汉字。

电脑休眠、关机或打印服务停止期间无法提供共享打印。关闭管理窗口后，已安装的后台服务仍会继续运行。

## 更新到 1.0.0.8

直接安装新版 MSIX，无需先卸载旧版。更新后打开 **“启动与连接 → 修复服务与防火墙”**，将新版后台更新到系统服务目录，并确认页面上的“后台版本”为 `1.0.0.8`。

已有共享选择、名称、默认纸张和认证设置会保留。本版新增耗材读取与上报；更新后台后，选中实体打印机，等待约一分钟即可查看驱动返回的结果。

## 耗材信息的支持条件

耗材数据来自 Windows 驱动的标准双向通信接口。USB 和网络打印机都取决于驱动是否提供这类数据；厂商自己的状态工具能显示余量，并不代表标准接口一定可用。

- 管理界面显示耗材名称、余量及读取状态，约每分钟更新。
- Mac 可查询耗材属性，具体显示入口取决于 macOS 版本，通常位于打印机的“选项与耗材”。
- iPhone/iPad 可通过 IPP 查询这些属性，系统打印界面不一定提供耗材百分比页面。
- 未知、未安装或读取失败会明确显示；超过两分钟未更新的读数停止上报。

本机自动测试已覆盖读取、缓存、警告和协议返回，实体打印机与 Apple 设备上的余量展示仍需实机验证。支持字段、刷新方式和排查步骤见 [耗材支持说明](docs/supplies.md)。

## 连接与常见问题

**Apple 设备找不到打印机**

确认打印服务正在运行、打印机已勾选并保存；电脑网络应设为专用或域网络，路由器不能启用客户端隔离。跨 VLAN 自动发现需要网络侧支持 mDNS 转发。“启动与连接”页可以检查本机连接和复制诊断信息。

Mac 也可使用诊断页中的 IPP 地址手动添加：

```text
ipp://电脑局域网IP:8631/printers/打印机ID
```

**名称或纸张仍然显示旧值**

Mac 可能缓存已添加队列的名称和能力。先确认后台已更新，再删除并重新添加该打印机。

**Windows 能打印，软件里却看不到队列**

后台服务以 SYSTEM 运行，只能使用该账户可访问的 Windows 队列。仅为当前用户安装的网络打印机可能需要改为全机安装。

**如何设置密码**

在“账户与密码”页开启认证并设置凭据，然后在 Apple 设备的打印认证窗口输入。此账户仅用于 CX Printer 打印；更新时密码框留空可保留原密码，更改用户名需要重新设置密码。

**如何卸载**

先在软件的“启动与连接”页点击“卸载打印服务”，再通过 Windows 设置卸载 CX Printer。设置和日志会保留。仅卸载 MSIX 不会移除独立安装的服务和防火墙规则。

## 兼容性

接收格式包括 PDF、Apple URF、PWG Raster、JPEG 和 PNG。具体纸张、彩色和双面能力以 Windows 驱动报告为准；作业记录中的“已提交”表示 Windows 队列已接收，实际出纸仍由驱动和设备完成。

Microsoft Print to PDF 可作为测试队列，后台自动保存到 `%ProgramData%\CX Printer\TestOutput`。Fax、XPS 等需要交互操作的虚拟队列无法在后台服务中共享。

打印通信使用局域网 HTTP，认证采用 Digest，凭据摘要通过 Windows DPAPI 保存。请在可信局域网中使用；内置防火墙规则限定专用/域网络和本地子网。

## 从源码构建

需要 Visual Studio 2022 17.14 或更新版本、.NET 桌面开发与 Windows 应用开发工具、.NET 8 目标支持，以及 Windows SDK 19041 和 26100。项目使用 .NET 8；本机通过 .NET SDK 9.0.304 和 VS 17.14 构建。

在已提交源码的工作区运行：

```powershell
# 构建、运行测试并生成 x64 自包含发布目录
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1

# 生成并签名 MSIX 安装包
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 -Version 1.0.0.8
```

发布目录为 `artifacts\CX-Printer-win-x64`，安装包位于 `artifacts\MSIX`。构建脚本会校验后台版本包含当前 Git 提交号。签名私钥及密码保存在已忽略的 `.tools` 目录，分发安装包时仅附带公开的 `.cer` 证书。

F5 调试、运行时选项、测试命令和后台路径见 [开发说明](docs/development.md)。其他架构尚未完成运行验证。

## 文档

- [MSIX 安装、更新与卸载](docs/msix-installation.md)
- [1.0.0.8 更新说明](docs/release-1.0.0.8.md)
- [耗材接口与验证范围](docs/supplies.md)
- [开发、调试与测试](docs/development.md)
- [历史功能验证](docs/verification.md) · [Mac 添加问题修复](docs/verification-1.0.0.6.md) · [显示名称与界面验证](docs/verification-1.0.0.7.md)
- [第三方组件与许可](THIRD_PARTY_NOTICES.md)

# CX Printer

用 WinUI 3 管理 Windows 打印机共享，供同一局域网中的 iPhone、iPad 和 Mac 使用。后台为独立实现的 IPP / DNS-SD Windows 服务，软件图标使用本项目用户提供的透明图片，安装包包含浅色和深色主题的无底板图标，避免 Windows 添加主题色背景。

## 安装 MSIX

发布者为 **MasterTea**，最低系统版本为 **Windows 10 1809 / build 17763**，当前安装包版本为 **1.0.0.7（x64）**。从 GitHub Releases 下载 MSIX 和 `MasterTea.cer`，首次安装先将公开证书导入“本地计算机 → 受信任人”。具体步骤见 [MSIX 安装说明](docs/msix-installation.md)。

编译目标使用 Windows SDK 19041 的 API 定义，应用与后台声明的最低运行版本为 17763；开发所需 SDK 版本与最低运行系统版本不同。

## 在 Visual Studio 中 F5 调试

1. 打开根目录的 **`CX Printer.sln`**。
2. 使用 Visual Studio 2022 17.14 或更新版本，安装“.NET 桌面开发”、Windows 应用开发/WinUI 工具，以及 Windows SDK 10.0.19041 或更新版本。本项目目标为 .NET 8，本机已用 .NET SDK 9.0.304 与 VS 17.14 构建。
3. 选择 **Debug / x64**，将 **CX Printer** 设为启动项目。不要启动目录中保留的旧 Package 工程。
4. 在启动配置下拉框中选择 **`CX Printer · UI + 本机调试服务`**，按 **F5**。VS 首次会还原 NuGet 包，随后同时构建后台并复制到界面的 `Bridge` 子目录。
5. 可在 `MainWindow.xaml.cs` 中下断点。UI 会启动本机调试服务；关闭窗口会结束由它启动的调试服务。

默认调试模式使用 `127.0.0.1:18631` 和独立命名管道 `CxPrintBridge.Control.v1.Debug`，不安装系统服务，不创建防火墙规则，不向局域网广播。调试数据放在当前后台输出目录的 `DebugData`。真实队列能力会显示在界面中；Microsoft Print to PDF 可测试自动文件输出，Fax、XPS 等交互队列标为不可共享。

若需要给后台下断点，可选择“调试 → 附加到进程 → CxPrint.Service.exe”，使用托管 .NET 调试器。也可以先把 **CxPrint.Service** 设为启动项目，选择 **`CX Printer Bridge · 本机调试`** 并 F5；然后在 VS 另一个实例中启动 UI。UI 检测到同名调试管道后会连接既有后台，不重复创建。

要调试真实局域网共享，先运行发布版本安装系统服务，再把 UI 启动配置切换成 **`CX Printer · 已安装的服务`**。后台以 SYSTEM 运行，附加调试它需要以管理员身份运行 VS。服务管理动作会明确触发 UAC。

## 使用

运行 `artifacts/CX-Printer-win-x64/CX Printer.exe`。在“打印机共享”页安装服务，勾选物理打印机，选中后可设置“Apple 设备显示名称”和默认纸张，再保存。默认名称为 `Windows 打印机名称- CX Printer`；留空或点击“恢复默认”可恢复。自定义名称支持中文、emoji 和点号，最多 63 个 UTF-8 字节（全英文 63 个字符、纯汉字 21 个字），同一台电脑的共享名称不能重复。过长的默认名称自动缩短并保留区分标识及 `- CX Printer` 后缀。改名只改变 Apple 设备所见名称，Windows 队列、打印机 ID 和 IPP 地址保持稳定。

打印机先要能在 Windows 中正常打印。搜索会先列出队列，再在后台逐台补充驱动与纸张，显示当前读取对象和耗时；短时间刷新复用能力缓存。服务安装在界面中显示阶段、文件数量和耗时。

从旧版更新后，进入“启动与连接”点击一次“修复服务与防火墙”，将新版后台复制到系统服务目录；已有共享选择和认证设置会保留。确认该页“后台版本”为 `1.0.0.7`。本版新增每台打印机的 Apple 显示名称，并统一列表、表单、标题及按钮的间距与对齐；侧栏折叠时隐藏底部服务状态，展开后恢复。DNS-SD 广播和 IPP 的 `printer-name` / `printer-info` 使用同一名称，改名保存后自动更新广播。

1.0.0.6 的 Mac 添加修复继续保留：使用稳定的 `cx-<主机标识>.local` 主机名，同步 SRV、A、AAAA 记录，避免显示名称中的空格使 IPP URI 无效。“复制诊断信息”包含实际后台版本、DLL 哈希、最近 IPP 请求及发现异常堆栈。

“账户与密码”页的账户只用于打印。开启认证后设置用户名、新密码和确认密码，在 Apple 设备打印认证窗口输入这套凭据。密码框可按住显示；留空保留已设置的密码，更改用户名必须输入新密码。服务不会创建 Windows 用户，也不会修改 Guest 或空密码策略。

“启动与连接”页可以启用延迟自动启动、修复服务恢复策略与防火墙、停止或卸载服务。最小化窗口或点击“最小化到系统托盘”会收起主窗口；双击托盘图标恢复，右键可显示窗口或退出管理界面。关闭窗口和托盘退出均会移除托盘图标；已安装的打印服务独立运行。电脑休眠期间不可打印，唤醒后会重新处理网络发现。

Mac 一般可从“系统设置 → 打印机与扫描仪 → 添加打印机”发现配置的 Apple 显示名称，默认带 `- CX Printer` 后缀。若自动发现受路由器配置影响，可以使用界面诊断页显示的地址手动添加：

```text
ipp://电脑局域网IP:8631/printers/打印机ID
```

纸张列表来自 Windows 驱动实际尺寸，不把所有项目写为 Letter。若 Mac 已缓存旧版队列，请删除并重新添加，再检查 A4 / Letter / A3 的实际宽高和默认值。

## 构建发布版本

在 PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

脚本使用 Visual Studio 的完整 MSBuild 来生成 WinUI 资源，运行两组自动测试，然后发布 x64 自包含目录。默认完整发布包含 .NET、Windows Desktop、ASP.NET Core 和 Windows App SDK 运行时，不需要另装旧 Bonjour。

```powershell
# 只使用本机已有 .NET 8 运行时的较小版本
.\scripts\build.ps1 -FrameworkDependent

# 仅构建，不执行测试；用于交叉编译时
.\scripts\build.ps1 -Platform ARM64 -SkipTests
```

当前实际完成的构建和验证平台是 x64；其他架构尚未运行验证。命令行编译 WinUI 应使用脚本或 VS MSBuild，单独 `dotnet build` 可能找不到 VS 的 PRI 资源任务。

## 后台和数据

| 项目 | 位置/行为 |
| --- | --- |
| Windows 服务 | `CxPrintBridge` / CX Printer Bridge |
| 后台程序 | `%ProgramFiles%\CX Printer\Bridge` |
| 生产设置、缓存、日志 | `%ProgramData%\CX Printer`，限管理员和 SYSTEM |
| UI 异常日志 | `%LocalAppData%\CX Printer\Logs\ui-error.log` |
| 调试数据 | 后台输出目录 `DebugData` |
| 生产端口 | TCP 8631；mDNS UDP 5353 |
| 管理通信 | 本机命名管道，限安装账户、管理员、SYSTEM |

管理员命令行也可使用后台的 `--install`、`--repair`、`--start`、`--stop`、`--enable-autostart`、`--disable-autostart` 和 `--remove`。推荐通过界面调用，安装时会记录正确的管理账户 SID。`--console` 专供本机 F5 调试；`--list-printers` 只输出能力，不打印。

卸载会移除本软件的服务和防火墙规则，保留设置和日志。旧版 AirPrint 服务由其原安装器管理；若同时运行，会看到旧、新两套广播。

## 兼容性和验证边界

已实现 PDF、URF W8/sRGB24、PWG sgray_8/srgb_8、JPEG/PNG；支持默认纸张、自定义驱动纸张、份数、彩色/黑白、驱动支持时的双面，以及单文档 IPP 作业。PDF 以 300 dpi 渲染。任务“已提交”表示 Windows 队列已接收，不代表已验证物理出纸。

服务只看得到其账户可访问的 Windows 队列。仅当前用户安装的网络打印机可能需要改为全机安装。Microsoft Print to PDF 保留为测试队列，可勾选共享；后台自动生成 `%ProgramData%\CX Printer\TestOutput\CX-Test-日期-唯一编号.pdf`，不会弹出“另存为”，打印记录会提示输出目录。测试文件保留供检查，最多 100 个或 1 GB 后需手动清理，目录需管理员权限查看。Fax、XPS 等其他需要交互的虚拟打印机不支持服务共享。

防火墙规则限定专用/域网络和本地子网。电脑和 Apple 设备需要网络互通，路由器不能启用客户端隔离；跨 VLAN 自动发现需要网络侧的 mDNS 转发。认证使用 Digest，凭据摘要通过 DPAPI 保存；打印文档流使用局域网 HTTP，应在可信网络中部署。

1.0.0.6 的 Mac 添加修复已由用户在实际环境确认成功。1.0.0.7 的名称设置和界面通过自动测试与本机检查；新增名称功能尚待用户在 Apple 设备端确认。详细结果见 [验证记录](docs/verification.md)、[1.0.0.6 根因修复验证](docs/verification-1.0.0.6.md) 和 [1.0.0.7 名称与界面验证](docs/verification-1.0.0.7.md)。

旧程序的原理、证据和纸张问题分析见 [静态分析报告](docs/installer-analysis.md)。第三方许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

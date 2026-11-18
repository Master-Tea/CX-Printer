# CX Printer 开发说明

## 开发环境

- Visual Studio 2022 17.14 或更新版本，包含 .NET 桌面开发、Windows 应用开发/WinUI 工具。
- .NET 8 目标支持；本机使用 .NET SDK 9.0.304。
- Windows SDK 10.0.19041 和 10.0.26100。前者用于目标 API，后者供现有脚本调用 MakePRI、MakeAppx 和 SignTool。
- Python 与 Pillow，用于安装包结构、资源和哈希验证。

应用和后台最低运行系统为 Windows 10 1809 / build 17763。开发工具使用的 SDK 版本与最低运行系统版本不同。当前完成构建和运行验证的架构为 x64。

## Visual Studio F5

1. 打开根目录 `CX Printer.sln`，选择 **Debug / x64**。
2. 将 **CX Printer** 设为启动项目，选择 **CX Printer · UI + 本机调试服务** 启动配置。
3. 按 F5。构建时会生成后台，并复制到界面输出目录的 `Bridge` 子目录。
4. UI 启动本机调试服务，关闭窗口时结束由它启动的调试服务。

默认调试使用 `127.0.0.1:18631` 和命名管道 `CxPrintBridge.Control.v1.Debug`，数据放在后台输出目录的 `DebugData`。此模式不安装系统服务、不创建防火墙规则，也不向局域网广播。

后台可通过“附加到进程 → CxPrint.Service.exe”调试，或单独将 CxPrint.Service 设为启动项目并使用 **CX Printer Bridge · 本机调试** 配置。UI 检测到已有的调试管道后会复用连接。

测试真实局域网共享时，先安装发布版本的系统服务，再把 UI 启动配置切到 **CX Printer · 已安装的服务**。后台以 SYSTEM 运行，附加调试需要管理员权限。

## 构建与打包

先提交待发布源码。构建脚本把 Git 提交号写入后台版本，并在发布后检查实际 DLL 的产品版本是否一致。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 -Version 1.0.0.8
```

完整构建使用 VS 的 MSBuild 生成 WinUI 资源，执行核心与后台两组测试，发布 x64 自包含目录。默认 .NET 运行时为 8.0.34。直接使用 `dotnet build` 构建 WinUI 可能缺少 VS 的 PRI 资源任务。

其他构建选项：

```powershell
# 使用目标电脑已有的 .NET 8 运行时
.\scripts\build.ps1 -FrameworkDependent

# 指定包源；可用于本地已有的 NuGet 缓存
.\scripts\build.ps1 -PackageSources C:\path\to\feed

# 交叉编译；ARM64 尚未完成运行验证
.\scripts\build.ps1 -Platform ARM64 -SkipTests
```

版本号同时维护在 `Directory.Build.props`、`packaging/msix/AppxManifest.xml`、`scripts/package-msix.ps1` 和 `scripts/build.ps1` 的发布校验中。升级安装需要提升四段版本号，并沿用原包身份与签名证书。

输出目录：

| 内容 | 路径 |
| --- | --- |
| 自包含发布程序 | `artifacts\CX-Printer-win-x64\CX Printer.exe` |
| 发布目录中的后台 | `artifacts\CX-Printer-win-x64\Bridge` |
| MSIX 与公开证书 | `artifacts\MSIX` |
| 私有签名材料 | `.tools\MasterTea-MSIX.pfx`、`.tools\MasterTea-MSIX.password` |

## 验证

构建脚本自动运行两组测试，涵盖 IPP 编解码、纸张、栅格处理、Digest、作业生命周期、DNS-SD、显示名称、耗材解析与缓存，以及真实本机 HTTP 端点。

```powershell
# 检查签名包内容、块哈希、实际 DLL 与透明图标
python .\scripts\verify-msix.py .\artifacts\MSIX\CX-Printer_1.0.0.8_x64.msix --version 1.0.0.8

# 在信任公开证书的测试机上安装并从开始菜单入口激活
powershell -ExecutionPolicy Bypass -File .\scripts\test-msix-launch.ps1 -PackagePath .\artifacts\MSIX\CX-Printer_1.0.0.8_x64.msix -CloseAfterTest
```

安装入口测试会更新本机 MSIX 安装。它验证应用激活、可见窗口和正常退出；实体打印机、Apple 设备端显示及物理出纸另需实机检查。

## 后台与诊断

| 项目 | 位置或行为 |
| --- | --- |
| 服务名 | `CxPrintBridge` / CX Printer Bridge |
| 后台安装目录 | `%ProgramFiles%\CX Printer\Bridge` |
| 设置、缓存与日志 | `%ProgramData%\CX Printer`，限管理员和 SYSTEM |
| UI 异常日志 | `%LocalAppData%\CX Printer\Logs\ui-error.log` |
| 生产端口 | TCP 8631；mDNS UDP 5353 |
| 管理通信 | 本机命名管道，限安装账户、管理员和 SYSTEM |

“复制诊断信息”包含后台产品版本、后台 DLL SHA-256、最近 IPP 请求结果、发现异常和耗材读取状态。

后台命令包括 `--install`、`--repair`、`--start`、`--stop`、`--enable-autostart`、`--disable-autostart` 和 `--remove`。推荐从界面管理服务，以记录正确的管理账户 SID。`--console` 用于本机调试；`--list-printers` 读取队列能力，不提交打印作业。

Microsoft Print to PDF 测试文件保存在 `%ProgramData%\CX Printer\TestOutput`，最多保留到 100 个文件或 1 GB，达到上限后需手动清理。其他需要交互的虚拟打印队列不能作为后台共享设备。

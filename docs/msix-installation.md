# CX Printer MSIX 安装

发布者：MasterTea（清单和签名证书主题均为 `CN=MasterTea`）

版本：1.0.0.4；架构：x64；系统最低要求：Windows 10 1809 / build 17763。

## 首次安装

该包使用为本项目生成的自签名代码签名证书。首次安装到一台电脑前，需要由管理员信任随包交付的公开证书。

1. 双击 `MasterTea.cer`，核对颁发给/颁发者均为 MasterTea。
2. 点击“安装证书”，选择“本地计算机”。
3. 选择“将所有的证书都放入下列存储”，选择“受信任人（Trusted People）”，完成导入。
4. 双击 `CX-Printer_1.0.0.4_x64.msix`，点击安装。
5. 从开始菜单打开 CX Printer。首次共享时，在软件中安装打印后台服务并接受 UAC 提示，然后选择物理打印机共享。

公开 `.cer` 文件不包含私钥。这个自签名证书不会自动获得其他电脑的信任，也不代表 Microsoft Store 或商业 CA 认证。更新旧版时直接安装新版本即可，无需先卸载；沿用同一张 MasterTea 证书。

也可在“管理员 PowerShell”中，在本目录运行：

```powershell
Import-Certificate -FilePath .\MasterTea.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\CX-Printer_1.0.0.4_x64.msix
```

## 更新与卸载

更新时须使用相同的包名称、发布者与签名私钥，且提升四段版本号。签名私钥与密码只保留在源码工作区 `.tools\MasterTea-MSIX.pfx` 和 `.tools\MasterTea-MSIX.password`，请安全备份；不要与安装包一起分发。

1.0.0.4 更新后请打开“启动与连接”并点击一次“修复服务与防火墙”，更新独立安装的后台组件。此操作会显示真实安装阶段和耗时，保留原有打印机选择及认证设置。更新完成后，设备发现状态应显示“已广播 … 台打印机”，再在 Mac 中重新打开“添加打印机”。

打印服务由软件内的服务管理功能独立安装到 Program Files。卸载 MSIX 前，请先在“启动与连接”中点击“卸载打印服务”，再通过 Windows 设置卸载 CX Printer。仅卸载 MSIX 不会移除独立安装的服务、防火墙规则或 ProgramData 中的设置与日志。

## 构建

在源码工作区执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 -Version 1.0.0.4
```

脚本复用 `.tools` 下的签名证书。默认使用 Windows SDK 26100 的 MakeAppx 和 SignTool。完整运行时随包附带，无需单独安装 .NET 或 Windows App SDK 运行时。

1.0.0.2 修复了安装后启动崩溃：打包时从随发布目录导出的 Windows App SDK 清单合并运行时类注册，并为包身份生成 `resources.pri`，同时保留独立发布目录的启动方式。

可在已信任证书的测试机运行以下安装入口回归检查。脚本实际安装指定 MSIX，再通过与开始菜单相同的应用激活 API 打开窗口，检查存活与可见窗口，然后正常关闭：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test-msix-launch.ps1 -PackagePath .\artifacts\MSIX\CX-Printer_1.0.0.4_x64.msix -CloseAfterTest
```

UAC 服务安装、真实 Apple 设备连接及物理打印仍需在实际环境联测。

# CX Printer MSIX 安装

版本：**1.0.0.8**；架构：**x64**；最低系统：**Windows 10 1809 / build 17763**。发布者为 MasterTea，包清单和签名证书主题均为 `CN=MasterTea`。

从 [GitHub Releases](https://github.com/Master-Tea/CX-Printer/releases/latest) 下载安装文件。包内包含 .NET 和 Windows App SDK 运行时，无需单独安装这些运行时或旧 Bonjour。

## 首次安装

1. 下载 `CX-Printer_1.0.0.8_x64.msix`、`MasterTea.cer` 和可选的 `.msix.sha256` 校验文件。
2. 双击 `MasterTea.cer`，核对颁发给/颁发者均为 MasterTea。
3. 点击“安装证书”，选择“本地计算机 → 将所有的证书都放入下列存储 → 受信任人（Trusted People）”，完成管理员授权。
4. 双击 MSIX 安装，从开始菜单打开 CX Printer。
5. 在“打印机共享”页安装打印服务，按 Windows 提示完成管理员授权，勾选打印机并保存。

安装包使用项目自己的代码签名证书，首次安装需要手动信任。公开的 `.cer` 不含私钥。

也可以在下载目录打开管理员 PowerShell：

```powershell
Import-Certificate -FilePath .\MasterTea.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\CX-Printer_1.0.0.8_x64.msix
```

如需核对下载完整性，运行以下命令，并将结果与 `.sha256` 文件中的值比较：

```powershell
Get-FileHash .\CX-Printer_1.0.0.8_x64.msix -Algorithm SHA256
```

## 从旧版更新

直接安装新版 MSIX，无需先卸载；同一台电脑已信任原 MasterTea 证书后，无需重复导入。

更新后进入 **“启动与连接 → 修复服务与防火墙”**，把新版后台复制到系统服务目录，并确认后台版本为 `1.0.0.8`。MSIX 更新与独立服务更新是两个步骤；仅安装新界面不会替换已经运行的后台。

共享选择、Apple 显示名称、默认纸张和认证设置会保留。本版新增耗材信息，选中实体打印机后等待约一分钟查看结果。数据取决于 Windows 驱动支持，参见 [耗材说明](supplies.md)。

Mac 可能缓存已添加打印机的名称和纸张能力；需要时删除后重新添加。当前广播继续使用稳定的 `cx-<主机标识>.local` 主机名，修改显示名称不会改变打印机 ID 或 IPP 路径。

## 卸载

先在 CX Printer 的“启动与连接”页点击“卸载打印服务”，再通过 Windows 设置卸载应用。服务管理功能会移除独立安装的后台及防火墙规则，保留设置和日志。

仅卸载 MSIX 不会移除独立安装的服务、防火墙规则或 `%ProgramData%\CX Printer` 中的数据。

## 构建与安装入口测试

在已提交源码的工作区执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 -Version 1.0.0.8
```

脚本沿用 `.tools` 下的签名证书与密码，调用 Windows SDK 26100 的打包工具。升级包需要沿用原包身份、发布者与签名私钥，并提升四段版本号。私钥和密码不随安装包分发。

在已信任证书的测试机上，可以实际安装 MSIX，并通过与开始菜单相同的激活 API 检查可见窗口与正常退出：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test-msix-launch.ps1 -PackagePath .\artifacts\MSIX\CX-Printer_1.0.0.8_x64.msix -CloseAfterTest
```

该测试会更新测试机上的应用安装。实体打印机、Apple 设备连接及实际出纸需要在对应环境中检查。更多构建与调试信息见 [开发说明](development.md)。

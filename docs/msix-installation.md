# CX Printer MSIX 安装

发布者：MasterTea（清单和签名证书主题均为 `CN=MasterTea`）

版本：1.0.0.1；架构：x64；系统最低要求：Windows 10 1809 / build 17763。

## 首次安装

该包使用为本项目生成的自签名代码签名证书。首次安装到一台电脑前，需要由管理员信任随包交付的公开证书。

1. 双击 `MasterTea.cer`，核对颁发给/颁发者均为 MasterTea。
2. 点击“安装证书”，选择“本地计算机”。
3. 选择“将所有的证书都放入下列存储”，选择“受信任人（Trusted People）”，完成导入。
4. 双击 `CX-Printer_1.0.0.1_x64.msix`，点击安装。
5. 从开始菜单打开 CX Printer。首次共享时，在软件中安装打印后台服务并接受 UAC 提示，然后选择物理打印机共享。

公开 `.cer` 文件不包含私钥。这个自签名证书不会自动获得其他电脑的信任，也不代表 Microsoft Store 或商业 CA 认证。这里只生成包和证书，没有自动修改当前电脑的证书信任或执行安装。

也可在“管理员 PowerShell”中，在本目录运行：

```powershell
Import-Certificate -FilePath .\MasterTea.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\CX-Printer_1.0.0.1_x64.msix
```

## 更新与卸载

更新时须使用相同的包名称、发布者与签名私钥，且提升四段版本号。签名私钥与密码只保留在源码工作区 `.tools\MasterTea-MSIX.pfx` 和 `.tools\MasterTea-MSIX.password`，请安全备份；不要与安装包一起分发。

打印服务由软件内的服务管理功能独立安装到 Program Files。卸载 MSIX 前，请先在“启动与连接”中点击“卸载打印服务”，再通过 Windows 设置卸载 CX Printer。仅卸载 MSIX 不会移除独立安装的服务、防火墙规则或 ProgramData 中的设置与日志。

## 构建

在源码工作区执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 -Version 1.0.0.2
```

脚本复用 `.tools` 下的签名证书。默认使用 Windows SDK 26100 的 MakeAppx 和 SignTool。完整运行时随包附带，无需单独安装 .NET 或 Windows App SDK 运行时。

本次已执行清单/包结构校验与签名检查。MSIX 在目标系统中的真实安装、开始菜单激活、UAC 服务安装及物理打印仍需联测；此前通过的是解压发布目录的窗口与后台连接检查。

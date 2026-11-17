# 验证记录与 Apple 设备联测

记录日期：2026-11-17。工作区：`C:\Users\chaxiang\source\repos\CX Printer`。

## 已执行

| 范围 | 结果 | 检查内容 |
| --- | --- | --- |
| 核心协议回归 | 17/17 通过 | 不同纸张尺寸、嵌套 media-col、未知尺寸拒绝、默认纸张、单双面/彩色能力、文档边界、重复组、截断/畸形消息、500 个有界随机输入 |
| 摘要认证 | 通过，包含在上行 | SHA-256 / MD5 正确及错误密码、请求目标绑定、nonce 过期、重放拒绝、用户名密码校验 |
| URF/PWG 解码 | 通过，包含在上行 | RGB 字面值、灰阶、行重复、白色填充、像素/页数上限、越界和截断 |
| 后台集成测试 | 18/18 通过 | 启动真实本机 Kestrel HTTP 端点，使用替代打印后端，验证作业生命周期和 IPP 操作 |
| 防重复出纸 | 通过 | 服务重启中止未完成作业；重复上传拒绝；并发重复提交与取消；编号跨历史清理保持递增 |
| 纸张全链路 | 通过 | HTTP 接收 A3，传入队列与替代后端的选择仍为 A3；Get-Printer-Attributes 各尺寸保持不同 |
| 实际文档渲染 | 通过 | Windows PDF 渲染产生 600×300 的预期页面；PNG/JPEG 尺寸正确；URF/PWG 的红蓝通道正确 |
| 凭据存储 | 通过 | 保存的文本不含测试密码；DPAPI 解密后的摘要与预期一致；有密码时更改用户名必须输入新密码 |
| 本机驱动读取 | 已执行，只读 | 读取驱动提供的纸张 ID、尺寸、默认值；虚拟队列的 A4 与 Letter 尺寸不同 |
| WinUI 实际启动 | 已执行 | 窗口、用户提供图标、本机调试后台与命名管道连接、关闭退出 |
| Release 构建/发布 | 已执行 | 使用 VS MSBuild，补充复制应用 PRI / XBF 资源，生成 x64 自包含目录 |
| 依赖审计 | 未发现匹配项 | 直接获取 NuGet 官方漏洞数据库，将已解析包版本与公告版本范围对比；结果保存于 `analysis/dependency-audit.json` |

测试入口：

```powershell
# 完整构建、测试、发布
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

核心和后台测试均为无外部测试框架依赖的可执行测试程序，失败返回非零退出码。后台测试只在本机监听临时端口，打印后端是替代实现；渲染测试直接检查生成图片。它们不会安装系统服务、修改防火墙、创建 Windows 账户或执行真实打印。

测试曾发现 `StorageFile.GetFileFromPathAsync` 在该后台上下文中拒绝打开普通文件，现已改为把 .NET 文件流传给 WinRT PDF / 图片解码 API。修复后所有文档渲染测试通过。

构建环境包含 VS 2022 Community 17.14.18、.NET SDK 9.0.304、Windows SDK 19041/26100。自包含发布内置 .NET / Windows Desktop / ASP.NET Core 8.0.34；开发机 F5 使用本机已安装的 .NET 8 运行时。项目仍以 .NET 8 为目标，后续维护应升级到处于支持期的 .NET 版本。

当前沙箱限制命名管道和部分桌面操作。UI 与后台完整连接检查已在获准的正常桌面环境运行。VS 完整 MSBuild 在本机加载 Windows SDK 的 `WinRT.SourceGenerator` 时会产生 CS8034 警告；没有关闭警告或从项目删除分析器。实际 UI 启动、x:Bind 以及文件流渲染已通过运行检查，`dotnet` 编译后台测试时不出现此警告。这是尚需在目标开发机工具链中复核的环境差异。

## 1.0.0.2 / MSIX 安装后启动修复

2026-11-17：复现 1.0.0.1 包身份下启动崩溃，事件日志报 `Microsoft.UI.Xaml.dll` / `0xc000027b`，WER 的实际 HRESULT 为 `0x80040154`（类未注册）。旧打包流程仅复制了独立发布目录，缺少自包含 Windows App SDK 的包级运行时注册。合并 SDK 对应清单中的 24 个运行时扩展、928 个可激活类后，又出现缺少 `ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml` 的 XAML 异常；补充以 `MasterTea.CXPrinter` 为索引名的 `resources.pri` 后启动成功。

修复已加入 `scripts/package-msix.ps1`。运行时注册信息由 UI 发布任务从实际构建使用的 SDK 导出，打包时校验所有注册 DLL 均存在；资源索引由应用自身已合并 WinUI 资源的 PRI 生成，保留独立发布目录原有 PRI。

实际安装 `CX-Printer_1.0.0.2_x64.msix` 到 `C:\Program Files\WindowsApps` 后，包状态 `Ok`、`IsDevelopmentMode=False`。当前系统报告 `10.0.17763.0`；通过 `IApplicationActivationManager.ActivateApplication` 启动得到可见 `CX Printer` 窗口，正常关闭退出码 `0`。回归脚本为 `scripts/test-msix-launch.ps1`，结果文件随 Release 发布。

为本机安装测试将项目公开证书导入了当前用户的 Trusted People；未修改系统级证书信任或安装打印服务。

## 1.0.0.1 / 17763 更新

2026-11-17：MSIX 最低版本改为 `10.0.17763.0`，UI 与后台最小平台版本一并调整；仍使用 19041 SDK API 定义编译。当前主机的 `Environment.OSVersion.Version` 报告 `10.0.17763.0`。重新运行核心 17 项与后台 18 项测试，全部通过；发布目录中的 WinUI 窗口成功启动并连接本机调试后台，退出码为 0。

本次 NuGet 自带的 x86 MakePri 返回访问拒绝。使用 `diagnose-windows-sandbox-acl` 检查 obj 目录及祖先后未发现可修复问题，未修改权限；相同输入交给已安装的 SDK 26100 x64 MakePri 成功。构建目标已在 x64 主机有该工具时优先选用它。此结果只说明当前工具差异，不推断全部访问拒绝的根因。

## 尚未执行

- 以管理员权限实际安装、修复、卸载生产服务，确认 SCM 启动/恢复和防火墙规则结果。
- Windows 真正重启、用户注销、休眠/唤醒、网线/Wi-Fi 切换和长期持续运行。
- 真实 iPhone / iPad / Mac 的自动发现、认证弹窗、系统队列缓存与出纸。
- 物理打印机的 A4 / A3 / Letter、双面、彩色、页边距和驱动差异。
- x86 / ARM64 运行。

因此当前交付是已构建并通过本机验证的实现，不能将自动测试结果解释为新版 iOS / macOS 的实机认证。

## 推荐的生产联测顺序

1. 在 Windows 中安装物理打印机，先确认 Windows 测试页正常。启动发布版 UI，安装服务，只共享该物理队列，选择默认 A4。
2. 检查服务属性：`CxPrintBridge` 应为“自动（延迟启动）”，依赖 Print Spooler；恢复动作应为 5 / 15 / 60 秒后重新启动。确认只有本软件的两条入站规则生效，网络为专用或域。
3. 在同网段 Mac 执行 `dns-sd -B _ipp._tcp local.`，观察带 CX 后缀的实例；用 `dns-sd -L "实例名" _ipp._tcp local.` 检查 `rp`、`pdl`、`URF`、`UUID` 等 TXT 值与主机地址。
4. 从 Mac 添加队列。用 `lpstat -p` 找出队列名，再运行 `lpoptions -p "队列名" -l` 检查纸张；在打印对话框分别选择 A4、Letter、A3（仅当驱动支持）。实际尺寸应分别为 210×297、215.9×279.4、297×420 mm；打印带刻度边框的测试页并核对出纸。
5. 在 iPhone / iPad 的“共享 → 打印”中检查发现和 PDF 打印。分别测试单页、多页、横向、黑白/彩色、双面、两份，以及取消作业。
6. 开启专用打印账户，确认正确密码可用、错误密码被拒绝；更改密码后重新提交，清除 Apple 设备缓存凭据再测一次。不要使用 Windows 登录密码作为默认打印凭据。
7. 关闭 UI 后打印，再注销 Windows、重启 Windows，确认后台与发现恢复。测试断网后恢复、睡眠唤醒和 DHCP 地址变更；保留发现恢复所需时间和服务日志。
8. 在允许中断测试的窗口结束后台进程，确认 SCM 恢复；在作业中途重启，确认未完成作业标为中止，并由用户检查实际出纸后决定是否重打。
9. 通过 UI 卸载服务，确认服务项和本软件防火墙规则移除；设置/日志按设计保留，系统 Guest、账户与空密码策略没有变化。

Mac 先前连接过旧服务时，需要移除旧队列再添加新实例，避免把旧纸张缓存误当作新能力报告。记录联测时使用的 iOS / iPadOS / macOS 版本、Windows 版本、打印机型号和驱动版本，才能定位版本差异。

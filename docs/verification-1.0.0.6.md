# 1.0.0.6 验证记录

- 日期：2026-11-17（Asia/Shanghai）
- 根因证据：Mac `dns-sd -Z _ipp._tcp local.` 显示 SRV target `HP\032Laser\032NS\032MFP\0321005\032CX-6691e5-ca516c.ipp.local.`；Mac 到 `192.168.0.31:8631` TCP 连接成功。
- 根因复现：新增的真实 HTTP Get-Printer-Attributes 回归，以 SRV target 构造请求 `printer-uri`，修复前返回 IPP `0x0400 Printer URI does not match target`；修复后返回成功。
- DNS-SD：验证 DNS 主机名语法、实际 PTR/SRV/TXT 查询、目标名下 A/AAAA 与 IPv4、全局 IPv6、带 scope 链路本地 IPv6 地址字节一致。DNS-SD 与 IPP URI 集成回归通过。
- 修复前完整后台套件：27/28；唯一失败是上述根因回归，日志 `artifacts/srv-uri-before-fix.log`。
- 修复后完整后台套件：29/29，退出码 0；日志 `artifacts/srv-uri-after-fix.log`。
- 完整发布构建成功；核心协议套件 17/17、后台套件 29/29 通过。
- 签名 MSIX `CX-Printer_1.0.0.6_x64.msix` 已在 Windows 10 build 17763 全新版本身份安装、激活、显示主窗口并正常退出；Authenticode `Valid`，发布者 `CN=MasterTea`。MSIX 1,668 个载荷文件的 8,642 个 block 哈希全通过。
- 尚未在用户实际 Mac 和物理打印机进行修复后联测。包内没有签名私钥。

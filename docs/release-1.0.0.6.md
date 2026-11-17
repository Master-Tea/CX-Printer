# CX Printer 1.0.0.6

修复 Mac 能发现打印机、但在添加队列时报告“目前无法与打印机通信”的问题。

DNS-SD 服务实例名保留可读的打印机名称；SRV 主机目标改用稳定、符合 DNS 主机名规则的 `cx-<主机标识>.local`，并将 A/AAAA 地址记录关联到同一主机目标。之前 SRV 目标包含打印机名称中的空格（Mac `dns-sd -Z _ipp._tcp local.` 会显示为 `HP\\032Laser\\032...ipp.local.`），客户端据此构造的 IPP URI 会被端点校验以 `0x0400 Printer URI does not match target` 拒绝。

生产 IPP 回归现在使用实际 DNS-SD SRV 查询所得的目标构造 IPP URI，并经真实 HTTP 请求验证 Get-Printer-Attributes 成功。地址回归涵盖多字节及带空格打印机名称、IPv4、全局 IPv6 和带 scope 的链路本地 IPv6。完整后台套件 29/29 通过。

管理端状态和“复制诊断信息”增加后台产品版本、`CxPrint.Service.dll` SHA-256、最近 32 条 IPP 请求及状态、最近 DNS-SD 异常完整堆栈。后台启动及每台打印机实际 SRV 目标写入服务日志。

已知验证边界：未安装新版到用户 Windows 主机，未使用用户 Mac 或物理打印机验证 1.0.0.6。发布构建、协议和本机 HTTP 测试通过不等于跨设备或物理出纸验证。

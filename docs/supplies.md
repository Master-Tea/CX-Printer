# Apple 设备耗材信息

1.0.0.8 接入 Windows 双向通信（bidi）耗材接口。安装本版后，在管理界面执行“修复服务与防火墙”更新后台，再选中打印机查看读取结果。

## 数据来源与设备支持

后台使用 Windows `IBidiSpl2`，只读查询 `\Printer.Consumables`。解析驱动返回的 `Type`、`Color`、`Level`、`Installed` 和 `Model`；百分比仅采用 0–100 的有效整数。驱动的未知值、无效值或错误响应不会被转换成 0% 或 100%。未安装的耗材不会显示驱动残留的百分比。

USB 和网络打印机都可使用该接口，前提是当前驱动和端口监视器提供标准 bidi 耗材数据。部分厂商仅在自己的状态工具中提供余量，这类私有接口暂未接入；本实现也未加入直接 SNMP 或直接连接打印机 IPP 的备用查询。没有物理耗材的 Microsoft Print to PDF 自动跳过读取。

后台通过单独的工作线程查询，最多两个读取同时进行，同一队列不会重复启动尚未结束的查询。成功、无数据和失败响应均缓存一分钟；两分钟未更新的数据从 IPP 中撤下。驱动卡住时继续提供打印与能力信息。耗材读取不复用五分钟的纸张能力缓存。

管理界面的“耗材信息”和复制的诊断信息可查看读取结果。余量未知、未安装、驱动未提供、读取失败和过期有不同提示。

## Apple 可查询的属性

| 属性 | 类型与含义 |
| --- | --- |
| `marker-names`、`marker-colors` | `name`；按相同顺序提供耗材名称和 sRGB 颜色，无颜色时为 `none` |
| `marker-types` | `keyword`；墨水、碳粉、显影剂、定影油、蜡及对应废料 |
| `marker-levels` | `integer`；百分比 0–100、未知 -2、未安装/不可用 -1 |
| `marker-low-levels`、`marker-high-levels` | 普通耗材为 10/100，废料容器为 0/90 |
| `marker-change-time` | 数据内容最后发生变化时的服务运行秒数 |
| `printer-supply` | `octetString`；索引、耗材类别、类型、百分比单位、容量和当前读数 |
| `printer-supply-description` | `text`；与标准耗材记录一一对应的名称 |
| `printer-state-reasons` | 低余量、耗尽、废料接近装满或已满的 warning |

普通耗材的百分比表示剩余量；废墨、废粉等容器的百分比表示已装满比例。10% 与 90% 是本服务的提示阈值，未声称来自厂商传感器。未知或缺失的数据不会生成低余量警告。警告保留 Windows 队列接收作业的行为。

属性支持通过名称单独请求，也支持 `requested-attributes=printer-status`。没有真实记录时省略耗材数组；不构造示例墨盒或默认满量读数。

macOS 的打印系统使用 CUPS `marker-*` 耗材属性，可从打印机的“选项与耗材”查看。实际界面与缓存刷新取决于 macOS 版本。iPhone/iPad 客户端同样可以查询 IPP 属性，但系统打印对话框未必提供耗材百分比页面；协议支持不能保证每个 Apple 界面都展示余量。

## 自动验证与实机边界

2026-11-18（Asia/Shanghai）本机检查：核心协议测试 **21/21 通过**，后台集成测试 **37/37 通过**，完整 x64 Release 解决方案（含 WinUI）构建通过。

自动测试覆盖真实 Windows XML 路径及类型、多种颜色与耗材、未知及无效百分比、未安装、错误响应、XML 外部实体禁用、并发上限、慢查询、缓存与过期、删除队列后的迟到响应，以及经本机真实 HTTP 返回的耗材属性和请求过滤。原生 Windows COM 接口使用不存在的队列与 Microsoft Print to PDF 验证不支持数据时的退化行为，不创建打印作业。

本机只有 Fax、XPS、Microsoft Print to PDF 虚拟队列，无法验证实体打印机余量。实体驱动与 Mac/iPhone/iPad 的端到端展示需要在对应设备上检查：

1. 更新后台，选中实体打印机，等待约一分钟，确认管理界面显示真实耗材信息。
2. 若显示驱动未提供数据，先检查 Windows 队列启用双向支持，并安装厂商完整驱动；Windows 厂商工具有读数不代表标准 bidi 接口一定可用。
3. 在 Mac 上查询这台共享打印机的耗材，核对名称、颜色和百分比；有必要时删除后重新添加队列以刷新缓存。
4. 设备离线或驱动读取失败后，检查旧读数被撤下或显示未知，打印队列仍可按原有逻辑提交。

接口依据：

- [Microsoft：构造双向通信查询](https://learn.microsoft.com/en-us/windows-hardware/drivers/print/constructing-a-bidi-communication-schema-query)
- [Microsoft：耗材字段定义](https://learn.microsoft.com/en-us/windows-hardware/drivers/print/-name-)
- [Microsoft：IBidiSpl2](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/bidispl/nn-bidispl-ibidispl2)
- [CUPS：IPP 耗材属性](https://openprinting.github.io/cups/doc/spec-ipp.html)

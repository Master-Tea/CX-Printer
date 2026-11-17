# AirPrint Installer 静态分析与替代实现

分析对象：用户提供的 `AirPrint_Installer.exe`，文件版本 1.3.0.0，大小 2,189,312 字节。SHA-256：`52F33A324822EBB429C360157CA29EE413D4D213215BA15044D5207BCC61B407`。分析只读取与反编译文件，没有执行旧安装器。

## 原软件的工作原理

安装器是托管 Windows Forms 程序。主要逻辑位于反编译生成的 `analysis/installer/AirPrint_Installer/frmMain.cs`。它把资源中的 `airprint.exe`、`libairprint.dll` 和 `XpdfPrint.dll` 解出到安装目录，借助 Windows 服务和 Bonjour 将 Windows 打印队列发布给 Apple 设备。

```mermaid
flowchart LR
    A[iPhone / iPad / Mac] -->|Bonjour / mDNS 发现| B[airprint.exe Windows 服务]
    A -->|HTTP / IPP 打印请求| B
    B --> C[libairprint.dll 协议与打印桥]
    C --> D[XpdfPrint.dll 文档渲染]
    C --> E[Windows 驱动与打印队列]
    D --> E
    E --> F[打印机]
```

安装器明确执行 `sc.exe create AirPrint ... depend= "Bonjour Service" start= auto`，并使用带 `-s` 参数的 `airprint.exe` 作为服务程序。安装器本身不是打印协议实现；它主要负责解压、创建服务、配置账户和启停。

原生文件是 32 位 PE。`airprint.exe` 的 PE 时间戳为 2010-10-07 20:20:17 UTC，`libairprint.dll` 为 20:19:53，`XpdfPrint.dll` 为 19:30:50。同样的日期表明安装器包装的是很早的打印引擎，但 PE 时间戳本身不等同于可靠的产品发布日期。

## 可以从代码确认的问题

- `frmMain.cs:1028` 附近创建旧服务，随后启动；进程辅助函数没有形成可靠的等待、退出码校验和回滚流程。安装、服务注册和启动之间存在竞争与错误被吞掉的情况。
- `frmMain.cs:1128` 附近通过 `Interaction.GetSetting` 恢复密码；保存逻辑通过 VB `SaveSetting` 写入用户注册表设置。这是可恢复的明文设置，不是 Windows 凭据保护机制。
- 账户配置通过 `net.exe user ...` 创建或更新 Windows 用户，密码出现在子进程参数中。Guest 访问、Windows 用户账户和打印身份耦合，使用户难以理解应该输入哪套凭据。
- 代码包含 Guest 账户启停以及 `LimitBlankPasswordUse` 相关操作。为打印功能修改系统账户和空密码限制，会扩大配置影响范围。
- 服务只设置了普通自动启动，安装器没有完整设置服务失败恢复、延迟启动、网络变化后的重新发布和明确的诊断结果。

本地的反编译文件用于追溯上述结论。它们和原始原生 DLL 没有作为新软件的运行时依赖或分发内容。

## 关于 Mac 上所有尺寸都显示 Letter

原生协议与驱动代码未获得可重建源码。通过导入表和字符串可以识别 IPP、DNS-SD、纸张与驱动相关路径，但不能仅凭字符串证明某一个 native 函数就是问题根因。

合理的故障路径包括：驱动以英寸为单位的纸张尺寸转换错误；`media-col` 中重复使用默认 Letter 宽高；能力列表和 `DEVMODE.dmPaperSize` 不对应；Mac 缓存了旧能力。这里把这些视为待验证的旧实现缺陷，不声称已定位其原生源码中的具体错误行。

新实现从两端消除这些问题：

1. 通过 Windows `DeviceCapabilities(DC_PAPERS / DC_PAPERSIZE)` 获取实际纸张 ID 和 0.1 mm 尺寸，转换成 IPP 的 0.01 mm。
2. 为每一种纸张分别生成 `media-supported`、`media-size-supported`、`media-col-database` 与对应默认值，不用同一组 Letter 数值填充所有项目。
3. 客户端提交 `media` 或 `media-col` 后，校验尺寸确实存在；未知尺寸返回 IPP 错误，避免静默改成 Letter。
4. 出队打印前重新读取能力，把选择映射到驱动的实际 `PaperSize.RawKind`，在默认页设置和每页设置中应用它。
5. 用户可以为每台共享打印机指定默认纸张。旧 Mac 队列可能需要删除并重新添加，才能清除系统缓存。

## 新实现的结构与取舍

`CxPrint.Core` 实现有界 IPP 编解码、纸张能力、打印选项、摘要认证和 URF/PWG 解码；`CxPrint.Windows` 负责驱动查询、DPAPI 和本地控制客户端；`CxPrint.Service` 负责 HTTP、发现、作业、渲染、服务与防火墙配置；WinUI 3 项目提供管理界面。

生产服务监听 TCP 8631，通过 `_ipp._tcp` 与 `_universal` 子类型发布；不依赖旧 Bonjour 安装器。接口或网络变化后重建发布状态，定期重发广播。TTL 为 120 秒，广播间隔约 60 秒，发现循环约 5 秒；不宣称具有 Apple 或 Mopria 认证。

支持 PDF、URF（W8 / sRGB24）、PWG raster（sgray_8 / srgb_8）、JPEG 和 PNG。PDF 使用 Windows PDF 渲染器，栅格与图片转换后经 Windows GDI 打印。文档以 300 dpi 渲染，保持宽高比适配驱动的硬件可打印区域。没有实现 PostScript、PCL 直通、多文档合并或任意完成装订功能；能力中只发布实际实现的功能。

打印账户独立于 Windows 账户。支持 Digest SHA-256，并为兼容客户端保留 MD5 Digest。服务保存由机器 DPAPI 保护的 HA1 摘要，通过只允许安装账户、管理员与 SYSTEM 访问的本地命名管道管理。密码不写进命令行、诊断信息或普通设置文本。HTTP 文档流没有 TLS 加密，部署范围是可信的局域网。

服务按延迟自动启动安装，依赖 Spooler，并配置异常后的 5/15/60 秒恢复。后台任务失败会让进程返回非零退出码，使恢复策略实际触发。防火墙规则限定本程序、专用/域网络和本地子网。

作业持久化后才入队；单文档上限 256 MB，未完成作业上限 64 个，文档缓存总额 2 GB，渲染缓存每作业最多 1 GB。重启后未完成作业标记中止，不自动重印。完成状态表示已交给 Windows 队列，不等于已确认物理出纸。

## 证据边界

已完成协议、认证、纸张与栅格自动测试，以及真实本机 HTTP 端点、作业生命周期、DPAPI 和渲染测试。实际验证结果见 `docs/verification.md`。

当前机器检测到的队列是需要交互的虚拟/传真队列；新界面会将这类队列标为不可后台共享。没有真实 iPhone、iPad、Mac 和物理打印机的联测结果，因此跨设备发现、长期唤醒恢复以及实际纸张出纸仍需按联测清单执行。

## 参考资料

- RFC 8010：IPP 编码和传输。
- RFC 8011：IPP 对象、操作、属性和状态。
- RFC 7616：HTTP Digest 认证。
- OpenPrinting CUPS `cups/raster-stream.c`：URF/PWG 的数据格式参考。
- Makaretu net-mdns：服务发布 API 与 DNS-SD 实现。

栅格解码依据公开格式独立实现，没有复制原安装器中的二进制引擎。

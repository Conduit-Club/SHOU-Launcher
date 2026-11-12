# R45：截图、导出目录与 WPF 页面衔接

2026-11-12，续作 `features/windows-parity`，起点 `333f56db`，包含已拉取的 `origin/dev`。本轮按 R44 的只读审计，先修截图格式/60 张限制，再接 CF/MRpack 目录选择，随后补服务器状态、日志打开、收藏图标/搜索与 Windows 全局 Java 控制台设置。旧 WPF 源码仅作行为参考，未改旧端；用户本轮明确不启动 Minecraft。

## 旧新行为与本轮处理

| 对照项 | 旧 WPF 入口与行为 | R45 结果 |
| --- | --- | --- |
| 截图格式 | `PageInstanceScreenshot.xaml.cs` 枚举 PNG/JPG/JPEG/BMP/WebP/TIFF | 统一枚举、缩略图和复制解码，另兼容 TIF；不再出现 JPG 可列出却无法解码的问题 |
| 截图续载与打开 | `RequireAppend`、`ListAppendAsync(20, ...)`；点击缩略图由默认程序打开 | 每批 20 张，滚到底追加，另有手动续载；移除 60 张总上限并显示已加载/总数；补原图打开 |
| 客户端导出 | `PageInstanceExport.xaml.cs` 可选其它目录、存档及资源分项 | 新目录浏览与单文件/整目录选择接既有 `OverridePaths`；显示选择清单，可移除/清空，父目录覆盖子项时去重 |
| 实例服务器 | `ServerCard.xaml/.cs` 图标、MOTD、人数、状态、刷新/复制 | 复用状态查询，补图标、纯文本 MOTD、人数上限、版本、查询耗时和单项/全部刷新、复制地址；不是重新读取 `servers.dat` 冒充网络刷新 |
| 历史日志 | `PageSetupLog.xaml.cs` 条目打开原日志 | 补独立打开按钮，通过系统默认程序打开精确文件；脱敏导出路径不变 |
| 收藏 | `PageDownloadCompFavorites.xaml.cs` 图标与本地搜索 | 收藏恢复线上图标，新增名称/作者/描述/来源/类型的本地包含匹配；分组、批量与旧分享码尚未接 |
| Windows Java 控制台 | 旧启动设置及现有 Core 的 `UseConsoleJava` | 补全局设置绑定、加载/自动保存；仅 Windows 显示，Mac 不接 Windows 可执行文件选择 |

R44 的原始审计与实窗证据保存在 `.shou-dev/reports/macos-r44-audit-20261112/AUDIT.md`，不将那轮旧候选测试计作本轮新测试。

## 实现边界

- 图片文件继续限制 32 MiB，尺寸轴不超过 16,384、总像素不超过 64 Mi；这是解码预算，不是截图张数上限。按批异步解码，离页/换实例/销毁取消并释放图片，旧回调不污染新页。复制沿用现有剪贴板所有权及竞争保护。
- TIFF 使用纯托管 `BitMiracle.LibTiff.NET 2.4.660`，避免依赖 Mac 外部命令或给 Windows 添系统工具要求；先检查尺寸再分配有界栅格，缩略图最大 480×270。压缩变体、EXIF 方向/多页等更大矩阵尚未穷尽，解码不能保证任意损坏文件可读。
- 包中随附新依赖的官方许可正文、来源和 SHA-256；构建采集本地固定文件，不在打包时临时联网。参考 [官方 API](https://bitmiracle.github.io/libtiff.net/help/api/BitMiracle.LibTiff.Classic.Tiff.html) 与 [官方许可源文件](https://github.com/BitMiracle/libtiff.net/blob/master/license.txt)。
- 目录枚举只读并检查链接、相对路径和当前实例；根凭据文件显示不可选。可以明确选择未知模组目录、脚本、存档或普通文件，没有添加导入/导出类型白名单。原归档预算、精确凭据排除、事务和运行实例写保护仍保留。
- CF/MRpack 选择共用现有本地配置/资源复选框；同实例切页保留额外选择，换实例清空。服务/UI 可取消，旧目录行、迟到结果和旧实例回调不生效。
- 服务器最多四项并发查询，失败清理旧状态，取消/切页/换实例防迟到回填；只读取状态协议，不启动游戏，不写服务器 NBT。显示的耗时含连接与查询，**不称为 Minecraft 协议 ping RTT**；彩色 MOTD 和完整信号/菜单尚未还原。

详细接口见 [CONTRACTS](../../CONTRACTS.md)。

## 自动验证

SDK `.NET 10.0.403`，Release，macOS arm64。重任务串行并使用既有空间门禁；35 GiB 任务预算与 20 GiB 系统余量未放宽。

| 检查 | 结果 |
| --- | --- |
| Core 全量 | 872 通过、5 条件跳过、0 失败；较 R43 新增 12 项 |
| Desktop/Avalonia 全量 | 347 通过、0 跳过/失败；较 R43 新增 15 项 |
| Windows x64 交叉构建 | 通过，0 警告/错误；不代替 Windows 真机 |
| Mac 发布构建 | 通过，0 警告/错误；显式使用本机 `.env`，微软/CF 配置检查为真，未输出值 |

新增覆盖七种图片扩展名、83 张续载、非 PNG 回收恢复、目录/文件导出及导入往返、越界/链接/凭据/取消、状态协议元数据/坏图标/取消、旧页面回调、日志文件路径、收藏图标/搜索，以及 Windows 设置持久化和 Mac 能力区分。5 条跳过沿用两项 Windows 目录锁、真实 B5 证书运行时、PKCS12/keytool 与官方 Delta21 下载条件。

初轮 SKCodec 调用使用错误重载导致编译失败，修正为托管流；针对测试还先后发现图片夹具父目录未创建、MRpack 测试误用 `overrides/` 而不是 `client-overrides/`，均已修正后通过针对及全量。Windows 首次门禁统计与夹具回收并发导致 `du` 路径消失，未开始编译；改为串行后交叉构建通过。补许可后的打包曾被 35 GiB 预算拦住，核实未运行、ZIP CRC 与程序集一致后回收旧展开候选，保留 ZIP/日志/证据再构建。未关闭门禁或将这些失败记成通过。

## Mac 实窗与文件核验

完整功能实窗使用 RC1，产品源码提交 `4ded8c1530f55273c743b3fdda99ea82de31a36d`。隔离目录 `.shou-dev/test-data/r45-native`，账户 0，无游戏本体；只有受控资源、截图与脚本样本。服务器在线样本是 Python 在 localhost 返回的一份协议响应，不是 Minecraft 或实际游戏服务器进服。

1. 20→40→60→80→83 张截图随滚动追加，末尾续载按钮消失；PNG、JPG、JPEG、BMP、WebP、TIFF、TIF 缩略图显示。TIFF 删除后总数 82，恢复后 83 且原字节/扩展名一致；JPEG 点击在 Preview 打开精确测试路径，随后只关闭该测试文档。
2. 导出页勾选整个 `kubejs`，进入 `scripts` 只选 `one.zs`，同时包含 config 和现有本地资源。真实生成 CF ZIP 与 MRpack；分别检查 `overrides/`、`client-overrides/` 恰好 12 份文件、全部 SHA-256 与输入一致。未选的脚本/存档/根 `.env`、截图与服务器文件未混入。Core 另验证导入往返，不冒称做了外部启动器实窗互认。
3. 实例服务器自动显示图标、两行 MOTD、3/12、1.21.1 和响应毫秒；离线样本显示不可用。行内/全部刷新完成，原始 `servers.dat` 哈希不变。复制地址由 Avalonia 测试覆盖，本轮未读取或覆盖用户剪贴板做额外原生验证。
4. 收藏官方 Sodium 翻译资源显示真实图标，搜索 Sodium 命中，不匹配词过滤为空，清空后恢复；没有修改收藏集合。日志按钮在 Console 打开精确隔离 `launcher.log`，只关闭该测试文档。
5. 正常 Cmd-Q 退出，CUA 库存中独立 QA bundle 消失，native result 为 `normal_close`，测试进程已退出。101 份实例原文件 SHA-256 均与测试前相同，截图仍 83 张；本机 `.env` 元数据与未跟踪计划文件 SHA-256 保持不变。

补许可后的 RC2 不改产品行为，最终补验与候选校验值见下方。证据根 `.shou-dev/reports/macos-r45-20261112/`，包括完整 AX、JPEG、TRX、原生结果和 `native-integrity.json`；旧失败记录保留。未操作个人应用、实例或账户，未启动 Minecraft。

## 最终候选

- 源码：`d4504777224c23dc7c720f81bec41d1ed410819d`；相较完整功能验收的 `4ded8c15` 只补打包许可，`src`、`tests`、`CONTRACTS.md` 无差异。
- 文件：`.shou-dev/dist/MACOS-R45-20261112/rc2/SHOU-Launcher-osx-arm64-v1.5.0-dev.gd4504777.zip`。
- 大小：53,166,667 字节。
- SHA-256：`135b5835a320931e0a9603980c8fe45cecfdbf5968c971120c403d1cbb5f883c`。
- ZIP CRC、标准应用/QA 的代码签名验证、Core/Desktop 程序集逐字节一致性与新许可正文包含检查均通过；显式 `.env` 构建，微软/CF 配置验证为真。QA 仅改 bundle 身份和根签名。
- RC2 首轮 CUA 遇锁屏，180 秒后按 `exit_after` 退出，要求 `normal_close` 的烟测脚本因此失败，原记录保留。用户解锁后使用同一 ZIP/QA 产物补验通过，详见下节。本地 ad-hoc 签名候选，未正式发布。

## 解锁后 RC2 补验

2026-11-12 21:45–21:48（UTC+8），复用同一隔离目录，未重新编译或更改候选。启动身份为 `d4504777`，原生窗口、微软/CF 配置检查和精确测试目录正确。

- CUA 核对首页与账户 0；截图页再次完成 20→40→60→80→83 张滚动续载，末尾无继续加载按钮。七类扩展名在列表中，首尾缩略图实际渲染正常；已知辅助名称问题仍单列待办。
- 导出页确认根 `.env` 不可选；选择整个 `kubejs`，进入 `scripts` 只选 `one.zs`，加现有 config 和本地资源，重新导出 CF ZIP 与 MRpack。两包分别恰好 12 份所选内容，逐文件 SHA-256 与原始输入一致，未选内容没有混入。
- Cmd-Q 正常退出，CUA 库存无该 QA 应用，受监视进程结束，结果为 `normal_close`；补验脚本 exit 0、未超时。101 份实例原文件哈希、账户 0、候选 ZIP SHA-256、本机 `.env` 元数据和未跟踪计划哈希均保持不变。未启动 Minecraft，未操作个人应用或实例。

证据：`native-result-rc2-unlocked.json`、`native-rc2-unlocked-run.json`、`rc2-unlocked-integrity.json` 与 `rc2-unlocked-*.ax.txt/.jpg`。此为最终 RC2 的截图/导出/退出补验；前文服务器、收藏、日志、图片回收和外部看图等完整功能验收仍记作 RC1，未冒称全套重新测试。

## 下一优先级

1. P1：R37 归档/清单固定预算与其它旧格式，CF 无第三方 URL 的手动补档；客户端导出逐资源来源策略、外部启动器互认、资源子目录与更新备份恢复入口。完整依赖版本求解与冲突处理仍单列，不能把读取声明当作兼容通过。
2. P2：收藏分类分组/批量/旧分享码及模糊搜索；简介翻译与有依据时的 MC 百科链接；跨平台可交互自定义主页。其它渲染器/兼容设置须按平台逐项实现，不能照搬 Windows Mesa/Java 方案到 Mac。
3. UI/验收：截图按钮辅助名称仍读到隐藏的“无法预览”文字，视觉显示正常；收藏无匹配提示和完整三语长文本待改。彩色 MOTD、协议 ping/信号样式、完整菜单、Windows 真机/Intel Mac/DPI/键盘/VoiceOver 与跨应用图片剪贴板矩阵未齐。本轮不验证游戏行为。

本轮完成项及未完成项同步记录在 [todo](../../todo.md)，未将整套 WPF 复刻勾为完成。

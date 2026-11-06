# R12：下载目录自动加载与页面内状态

2026-11-06 至 11-07。继续 `features/windows-parity` 上的 Avalonia 跨平台实现；本轮没有启动 Minecraft、运行安装器、读取真实账户缓存、推送、合并或切换默认入口。

## 源码与参考

- 工作树 `D:\Code\C#\SHOU-Launcher-Windows`；origin 为 `https://github.com/Conduit-Club/SHOU-Launcher.git`。本轮 fetch 后迁移分支远端仍为 `fb1b483a3373359fe9a257bcc86d498d068864b6`，没有合并 dev/main，原目录修改保留。
- 产品提交：`1b74f35294c24ee4116f086f5cdecda2b6b2ba73` 实现目录与页面内状态；`3cd5f4bcc837246713e20f7518c0305921d66c8d` 修正官方仓库地址、NeoForge 热修复分组和预发行隔离；`656f4ebef9331cd3e43fdfb34d1ed134e41c97d9` 支持并转义合法 Maven build 坐标。**最终产品源码为 656f4ebe。**
- 参考用户提供的六张截图：加载器横条、目录加载卡、热门模组列表和 Forge 独立安装器目录。仅复用 v1.5.0 的独立 Anvil/Fabric/Quilt PNG，来源与哈希见 [素材记录](../../src/SHOU.Launcher.Desktop/Assets/Minecraft/README.md)；未读取旧 WPF 视图、样式模板、控件结构 JSON 或采集脚本。
- 新增的 pickaxe 来自固定版本的 [Lucide 原始 SVG](https://raw.githubusercontent.com/lucide-icons/lucide/0.468.0/icons/pickaxe.svg)，沿用已有许可记录。共享图标共 88 个；未打包新字体。

## 差异与本轮修改

| 范围 | 此前差异 | 本轮实现 |
|---|---|---|
| 首次浏览 | 目录/社区资源需要手动读取或搜索；加载时出现底部进度条 | 首次进入自动查询；空列表在右侧内容区显示镐子加载卡，刷新已有列表时显示局部图标与取消入口，不挤压页面 |
| 加载器配置 | 默认大段展开、无版本图标、需逐个读取；无目录与不支持安装容易混淆 | Minecraft 版本选定后独立读取 Fabric/Forge/NeoForge/Quilt；四项默认收成整行，显示图标、可添加/已选版本/仅浏览/无可用版本，展开后显示真实版本与图标 |
| 热门资源 | 首次空白、默认相关性排序、只用首字替代图标，元信息少 | 模组、整合包、数据包、资源包、光影包首次空查询并按下载量排序；接入真实缩略图、标签、版本/加载器、下载量、更新时间与来源 |
| 筛选 | 修改条件后仍需再次搜索；离页丢列表与条件 | 查询及筛选变化经 350ms 合并后自动查询；保留 Enter/搜索按钮；分类型缓存条件和结果 5 分钟，离页取消，回来自动补读缺失/过期结果 |
| 数据包 | 旧 mod + datapack 标签组合遗漏热门数据包 | 使用实际 `project_type:datapack` 目录；项目链接按返回的实际类型构造。通用实例装入仍不代替存档目标选择 |
| 独立安装器 | Forge 等只有官网/返回按钮和空版本框 | Forge、NeoForge、Fabric、Quilt、Legacy Fabric 自动读取官方目录；按 Minecraft 版本或通用安装器分组，首组展开，Forge 推荐版有标记；点具体版本进入系统另存 |
| 跨页操作 | 所有长操作都在底部增加状态和进度条 | 全局长操作改为相应内容区的加载卡与取消；浏览目录使用独立状态，不锁住导航、搜索条件或已有结果。下载任务页自身的真实传输进度保留 |

本轮没有为了显示“可以添加”而放宽安装范围。Forge/NeoForge/Quilt 的游戏安装仍限定已验证的 Minecraft 1.21.1；其它版本可读目录，明确显示仅浏览。Fabric 安装范围沿用现有后端。未自动选择加载器、创建实例或执行下载。

官方目录与筛选依据：[Modrinth 搜索接口](https://docs.modrinth.com/api/operations/searchprojects/)、[Fabric Meta](https://meta.fabricmc.net/) 与各项目官方 Maven 元数据。标签来源为官方 Modrinth tag/category；数据包当前沿用其 mod 分类标签。现有 Modrinth 镜像/HTTP 代理规则继续生效，独立安装器只使用官方来源并保留 HTTP 代理。

联网复核后补修：Legacy Fabric 原 Maven 域名会跳转，改用其 [官方现址](https://repo.legacyfabric.net/legacyfabric/net/legacyfabric/fabric-installer/1.1.1/)，继续禁止自动跳转；其 `1.1.0+1` 合法坐标按单个 URL 段转义，仍拒绝路径、查询串和任意 URL。NeoForge 按 [官方版本规则](https://docs.neoforged.net/docs/gettingstarted/versioning/) 区分旧三段号与新四段号，例如 `26.1.2.126` 属于 Minecraft 26.1.2，`26.1.0.19-beta` 属于 26.1；暂不支持的 alpha/snapshot 坐标单独跳过，不拖垮正式目录，不冒认兼容性。

## 取消、校验与保留范围

- 每类浏览请求有取消令牌和 45 秒页面超时；网络读取另有现有的有界超时。新请求替换旧请求，迟到结果不能覆盖新条件；离页取消不会向新页面显示旧错误。
- 社区缩略图最多四路加载；离页释放图片，缓存返回时重新读取缺失图标。搜索结果仍可操作，读取不使用全局 busy 锁。
- 独立安装器 URL 从经过标识校验的版本坐标构造，不接受元数据里的任意下载 URL。使用不附带 API 凭据、禁止自动重定向的专用传输；取得官方 SHA-1 和 HEAD 大小后才下载，单文件上限 64 MiB。
- 安装器先进入应用临时目录，经大小/哈希验证后复制到另存位置旁的唯一临时文件，再发布完整目标。失败或取消保留原目标，清理本次暂存；不运行 JAR，不创建实例。
- 共享业务位于 Core，页面和加载图案由 Desktop 共用；没有增加 Windows 专属业务路径或 WPF fallback。既有凭据、清单、归档导入和实例安装边界未放宽。

## 自动验证

使用项目锁定的 .NET SDK **10.0.403**、Avalonia **12.1.5**，经既有 Invoke-Dev / Validate-Desktop / Publish-Desktop 入口串行执行。没有构建整个旧解决方案加 RID。

| 检查 | 最终结果 | 记录 |
|---|---|---|
| Desktop Release / win-x64 | **145 通过，0 失败，0 跳过** | [TRX](../../artifacts/windows-main-20261105/build/main/r12-desktop-final3/Desktop/Release/win-x64/default/reports/tests.trx) |
| Core Release / win-x64 | **373 通过，0 失败，16 显式跳过** | [TRX](../../artifacts/windows-main-20261105/build/main/r12-core-final3/Desktop/Release/win-x64/default/reports/tests.trx) |
| Windows RC2 自包含发布 | 成功；干净源码 `cb31fe63` | [控制记录](../../artifacts/windows-main-20261105/reports/r12-download-rc2-Publish-win-x64-default-controller.json) |
| macOS ARM64 RC2 交叉构建 | 成功，0 warning / 0 error；原生待验 | [控制记录](../../artifacts/windows-main-20261105/reports/r12-macos-rc2-Build-osx-arm64-default-controller.json) |

新增有行为意义的回归：五类资源自动加载/排序与导航可用、迟到响应隔离、右侧加载范围、四类加载器自动读取/横条宽度/图标/无隐式选择、全局加载不增加底行；官方安装器身份/推荐版/类别字段、拒绝 DTD、忽略任意元数据 URL、真实文件保存/重复执行、坏哈希与取消均保留原文件并清理暂存。

直接调用最终 **Core DLL** 的真实联网验证也已完成，没有启动 UI 或 Java：

| 范围 | 实际结果 |
|---|---|
| 五类热门资源 | 每类 30 项，空查询 + downloads 排序，首项真实 CDN 图标读取成功；模组首三项为 Fabric API / Sodium / Iris Shaders，数据包首项 VeinMiner |
| 独立安装器目录 | Forge 5104、NeoForge 1838、Fabric 67、Quilt 34、Legacy Fabric 6 个版本；NeoForge 最新首组为 26.3，热修复单独分组 |
| Fabric 1.1.2 真实另存 | 212123 字节，SHA-256 `61e035bf7bf70153e127440ce34de47c9036f0a2d0c65d1529454bd35ceefe4f`；两次保存一致，暂存文件为 0 |
| Legacy Fabric 1.1.1 真实另存 | 193716 字节，SHA-256 `5923d75fd549943deedd79ec209e98fbfc100b03194e529661e15193b3dcfebd`；两次保存一致，暂存文件为 0 |

[Core 社区目录记录](../../artifacts/windows-main-20261105/reports/r12-real-core-community-final.json)、[Core 安装器记录](../../artifacts/windows-main-20261105/reports/r12-real-core-installers-final.json)。保存仅在任务的受控目录进行，使用禁止自动重定向/不携带凭据的 HTTP 客户端；文件没有执行、安装或进入候选包。它们不代替系统另存窗口或鼠标验收。

16 项跳过原因：8 项 POSIX 链接场景需另行配置 Windows reparse/junction 原生夹具；1 项 POSIX 失败 keytool 子进程场景；4 项 macOS 原生 argv/Keychain 场景；2 项显式受控 Java 副本门禁未启用；1 项在线 Java 门禁未启用。没有将这些跳过计为通过，也没有为外壳修改自动扫描或执行用户 Java。

首次失败与修正均保留：

1. `r12-download-first`：编译 CS0236，命令初始化器错误引用实例字段；改为执行条件和方法守卫。
2. `r12-download-second`：138 通过、3 失败。发现离页取消后 loader loading 标志未复位并修正；另两项旧测试仍假定全局 busy/手动首次搜索，更新为实际的页面局部读取语义。
3. `r12-download-third`：144 通过、1 失败。旧夹具在选版本触发自动读取后才替换响应，调整夹具设置顺序；`r12-download-fourth` 随后 145 全过。最终版本另跑上述完整 Desktop/Core 回归。
4. [首轮公开端点探测](../../artifacts/windows-main-20261105/reports/r12-official-installer-probe.json)发现 Legacy Fabric 跳转；[首轮真实 Core 目录探测](../../artifacts/windows-main-20261105/reports/r12-real-core-installers.json)发现合法 `+1` 坐标被拒绝。均已补修，最终五类目录和两类真实另存全部通过。社区探测助手首轮有 PowerShell 单元素数组解包错误，未发送请求；修正助手返回方式后五类真实 Core 请求通过，原记录保留。此助手错误不计为产品缺陷。

首轮 Mac 构建被 **35 GiB 任务预算**在执行前阻止，[原记录](../../artifacts/windows-main-20261105/reports/r12-macos-rc1-admission-first.json)保留。仅对已确认 exit0 的历史任务在核对绝对 buildRoot 后执行编译器 `dotnet clean`，保留失败输出、报告、候选和数据；重试成功。磁盘保留 20 GiB 下限，没有提高预算或递归删除工作目录。

## Windows 原生验收

RC1 已以全新空账户/空实例数据根打开，窗口为 976×636、200% DPI；首页显示实际只读社团在线数。Computer Use 点击仍报 `coordinate input geometry is unavailable`，继续使用本会话明确授权的 Win32/UIA 备用路径。

23:58 再次确认实际前台进程为 LockApp，已停止全部界面输入并请用户解锁。最初命名为 catalog 的 001–003 截图实际仍为首页，**不作为下载页验收证据，不打包冒充目录截图**。下载页真实鼠标、首次加载图案、热门列表、安装器分组、最小窗口及深色状态待恢复会话后完成。[原生阻挡记录](../../artifacts/windows-main-20261105/reports/r12-native-blocker.json)。

为避免留着本轮空数据测试进程，核对 RC1 的 PID、完整 EXE 路径和启动时间后仅清理其进程，没有终止进程树或触碰用户原有 WPF/R11 窗口。[清理记录](../../artifacts/windows-main-20261105/reports/r12-native-rc1-owned-cleanup.json)；这次清理不计为正常点击关闭通过。RC2 另以全新数据根执行 30 秒原生启动/自动退出检查：内部状态 Ready、已观察到原生窗口句柄，**exit0、无外层超时清理、stderr 为空**。[启动观察](../../artifacts/windows-main-20261105/reports/r12-native-rc2-startup-native.json)、[退出记录](../../artifacts/windows-main-20261105/reports/r12-native-rc2-startup-exit.json)。仅记录应用内部窗口观察，不在锁屏下发送 UI 输入，也不计为下载页实际截图或鼠标验收。

## Windows 候选

[Windows x64 自包含 RC2](../../artifacts/windows-main-20261105/dist/r12-download-rc2/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.gcb31fe63.zip)，**80,886,165 字节**。

SHA-256：`8ae2f9f6c904b31a1705f7c16004d488d187965ab8ba165b7ac3b3036865f8e5`

Release / win-x64 / self-contained，构建来源 `cb31fe63ff21b084cffd1e81c11a7215ef2a8e5a`，工作区干净；它仅比最终产品源码 `656f4ebe` 多本轮说明和 todo 更新，两者产品代码相同。主程序、CoreCLR、Skia 本机库均为 AMD64；266 个包内条目未发现 .env、账户缓存、实例、日志、密钥或测试数据。[RC2 候选审计](../../artifacts/windows-main-20261105/reports/r12-rc2-candidate-audit.json)。未签名，只有本机候选；旧 WPF 工程和默认入口保留。RC1 及其审计保留为早期结果，不作为最终交付包。未向 SMB/Mac 外传包或截图。

收尾核对：任务占用 **34.883 / 35 GiB**，D 盘可用 **277.439 GiB**，保留 20 GiB 下限；无 heavy owner，本轮 RC1/RC2 进程均已结束。原工作区 AGENTS.md 修改与参考 ZIP/目录、todo 均保留。[收尾记录](../../artifacts/windows-main-20261105/reports/r12-delivery-final.json)。

## 未完成项

- 世界目录仍缺可靠可移植数据源；CF 包解析不等于 CF 全站搜索。当前社区来源为 Modrinth，不宣称其它来源与全部标签均已迁移。
- OptiFine、Cleanroom、LabyMod、LiteLoader 的可校验独立安装器目录仍未接入；入口显示实际限制和官网，不以隐藏/禁用算完成。历史 Forge 工件并非都提供 installer.jar，更多历史版本的另存与失败恢复需原生补验。
- 安装器本轮已做受控文件系统成功/坏哈希/取消/重复执行回归，以及两类真实官方 JAR 另存；真实系统选择器的完整提供方、取消/断网/覆盖矩阵待验。独立安装器暂未接镜像优先级。
- 资源详情长介绍、完整兼容性、其它来源、依赖与存档目标安装链仍按 todo.md 追踪。数据包可浏览不等于已支持通用实例直接装入。
- macOS 原生、125%/150%、跨显示器、全键盘/焦点、屏幕阅读器和高对比度完整矩阵待验。交叉构建不代替原生运行。
- 没有新增游戏、正版登录或游戏内效果证据；不执行游戏验证，除非用户在后续当前任务中明确要求。

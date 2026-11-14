# R12：下载目录自动加载与页面内状态

2026-11-06 至 11-07。继续 `features/windows-parity` 上的 Avalonia 跨平台实现；本轮没有启动 Minecraft、运行安装器、读取真实账户缓存、推送、合并或切换默认入口。

## 源码与参考

- 工作树 `D:\Code\C#\SHOU-Launcher-Windows`；origin 为 `https://github.com/Conduit-Club/SHOU-Launcher.git`。本轮 fetch 后迁移分支远端仍为 `fb1b483a3373359fe9a257bcc86d498d068864b6`，没有合并 dev/main，原目录修改保留。
- 产品提交：`1b74f35294c24ee4116f086f5cdecda2b6b2ba73` 实现目录与页面内状态；`3cd5f4bcc837246713e20f7518c0305921d66c8d` 修正官方仓库地址、NeoForge 热修复分组和预发行隔离；`656f4ebef9331cd3e43fdfb34d1ed134e41c97d9` 支持并转义合法 Maven build 坐标；`16689afc2d7bb0373354e8f8e9fcaea3414ba90e` 修复原生发现的 Fabric 目录失败、资源行换行和加载图案颜色；`572c8b101728e018f13f5c54b0f4b38aaf4adc36` 修正新增回归的来源夹具。**最终 RC3 源码为 572c8b10。**
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

解锁后的原生走查还发现并修正三项：Fabric 官方响应同时包含历史 `0.10.6+build.214` 等坐标，原标识校验使整份列表失败；现在只隔开安装身份策略尚不支持的历史 build 坐标，保留现代版本，非法路径仍拒绝。资源简介与标签受通用 muted 换行样式影响，行高和标签背景被拉长；改为单行截断及标签垂直居中。加载镐子原来被图标默认色覆盖，现显式跟随调用处的强调色；加载器横条状态字重也恢复正常。

最终 Core 对 **26.3 和 1.21.1** 的真实 Fabric 请求均返回 **157** 项，首项 0.20.1；[联网修复验证](../../artifacts/windows-main-20261105/reports/r12-live-fabric-fixed.json)。这只证明目录可读，不宣称所有历史加载器都已安装或运行验证。

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
| Desktop Release / win-x64 | **145 通过，0 失败，0 跳过** | [TRX](../../artifacts/windows-main-20261105/build/main/r12-desktop-rc3/Desktop/Release/win-x64/default/reports/tests.trx) |
| Core Release / win-x64 | **374 通过，0 失败，16 显式跳过** | [TRX](../../artifacts/windows-main-20261105/build/main/r12-core-rc3-fixed/Desktop/Release/win-x64/default/reports/tests.trx) |
| Windows RC3 自包含发布 | 成功；干净源码 `572c8b10` | [控制记录](../../artifacts/windows-main-20261105/reports/r12-download-rc3-Publish-win-x64-default-controller.json) |
| macOS ARM64 RC3 交叉构建 | 成功，0 warning / 0 error；原生待验 | [控制记录](../../artifacts/windows-main-20261105/reports/r12-macos-rc3-Build-osx-arm64-default-controller.json) |

新增有行为意义的回归：五类资源自动加载/排序与导航可用、迟到响应隔离、右侧加载范围、四类加载器自动读取/横条宽度/图标/无隐式选择、全局加载不增加底行；官方安装器身份/推荐版/类别字段、拒绝 DTD、忽略任意元数据 URL、真实文件保存/重复执行、坏哈希与取消均保留原文件并清理暂存。

早期候选直接调用 **Core DLL（656f4ebe）** 完成如下真实联网验证，没有启动 UI 或 Java；相关社区目录和独立安装器代码在 RC3 保持相同，最终 RC3 另有上文 Fabric 探测及下文 UI 复验：

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
5. RC2 原生发现 Fabric 整份目录失败，原因与修复见上。`r12-core-rc3` 新增回归首轮 373 通过、1 失败、16 跳过：测试只配置了 Mod 来源，加载器请求没有候选地址；修正为 Loader 官方来源后 `r12-core-rc3-fixed` 374 通过。首轮 TRX 和日志保留，未清理失败输出；Desktop 随后 145 全过。

首轮 Mac 构建被 **35 GiB 任务预算**在执行前阻止，[原记录](../../artifacts/windows-main-20261105/reports/r12-macos-rc1-admission-first.json)保留。仅对已确认 exit0 的历史任务在核对绝对 buildRoot 后执行编译器 `dotnet clean`，保留失败输出、报告、候选和数据；重试成功。磁盘保留 20 GiB 下限，没有提高预算或递归删除工作目录。

## Windows 原生验收

**2026-11-07 用户解锁后，先前下载页原生验收阻挡已解除。** Computer Use 仍报 `coordinate input geometry is unavailable`，按本会话既有授权使用 Win32/UIA 备用接口；每次核对候选 EXE、PID、窗口归属与前台。200% DPI，全新空账户/空实例隔离数据；没有向其它应用或锁屏发送输入。

| 实际范围 | 结果与边界 |
|---|---|
| Minecraft 目录 | 首次进入右侧加载卡，随后自动显示最新版本和分组；没有点击读取或安装 |
| 加载器配置 | 26.3 的四类横条独立自动读取；Fabric 修复后展开可见 0.20.1 等版本和图标，状态字重正常；其它三类在此版本明确仅浏览 |
| 五类社区资源 | 空查询自动按下载量排序；真实图标、单行简介、紧凑标签及元信息；未安装任何资源 |
| 输入与缓存 | UIA 输入 sodium 后自动更新，无需点击搜索；离页返回保留同会话查询与结果，图标缺失时局部补读 |
| 刷新取消与恢复 | 深色最小窗口中真实 Esc 取消正在读取的查询/图标并保留当前列表；随后 Enter 重试，取消状态消失，结果恢复 |
| 五类独立安装器 | RC2 实际自动打开 Forge/NeoForge/Fabric/Quilt/Legacy Fabric 目录和图标；RC3 又复验深色最小窗口的 Quilt 加载及结果；未点击 JAR 另存或执行安装器 |
| 主题与最小窗口 | 浅色 976×636、深色 880×560；资源控件与元信息可读，加载卡和横条无底部进度栏；最小化后恢复正常 |
| 保存与重启 | 仅在隔离数据中选择深色并保存；正常关闭再启动后仍为深色，设置页实际显示“深色” |
| 正常退出 | RC2 续验、RC3、RC3 重启三次均点击关闭按钮后 **exit0**，stderr 为空，无外层超时清理 |

[RC2 续验退出](../../artifacts/windows-main-20261105/reports/r12-native-rc2-resumed-exit.json)、[RC3 退出](../../artifacts/windows-main-20261105/reports/r12-native-rc3-exit.json)、[RC3 重启退出](../../artifacts/windows-main-20261105/reports/r12-native-rc3-restart-exit.json)。控件树切换期间两次只读 UIA 快照出现瞬时异常；重新观察后继续，并为采集增加最多三次的只读重取，没有重复导航或写入。

此前 RC1 锁屏时误命名为 catalog 的 001–003 实际为首页，仍不作为下载页证据；[原阻挡记录](../../artifacts/windows-main-20261105/reports/r12-native-blocker.json)和 [RC1 清理记录](../../artifacts/windows-main-20261105/reports/r12-native-rc1-owned-cleanup.json)保留为历史，不再作为当前阻挡。未触碰用户原有 WPF 窗口或默认入口。

## Windows 候选

[Windows x64 自包含 RC3](../../artifacts/windows-main-20261105/dist/r12-download-rc3/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.g572c8b10.zip)，**80,886,341 字节**。

SHA-256：`fa094223eabddbfdf77050c3dd8b5a45c6b00b798dd51441df60d74bec4b2313`

Release / win-x64 / self-contained，构建来源 `572c8b101728e018f13f5c54b0f4b38aaf4adc36`，工作区干净。主程序、CoreCLR、Skia 本机库均为 AMD64；266 个包内条目未发现 .env、账户缓存、实例、日志、密钥或测试数据。[RC3 候选审计](../../artifacts/windows-main-20261105/reports/r12-rc3-candidate-audit.json)。未签名，只有本机候选；旧 WPF 工程和默认入口保留。RC1/RC2 及其审计保留为早期结果，不作为最终交付包。未向 SMB/Mac 外传包或截图。

[按页截图与说明](../../artifacts/windows-main-20261105/public-evidence-r12-final/README.md)为 **13 个页面文件夹、23 张实际截图**；每张记录真实源码、DPI、尺寸与 SHA-256。四张未改布局的独立安装器目录来自 RC2，其余为最终 RC3；已区分来源，不将 RC2 的 Fabric 配置失败图放入最终参考。[截图 ZIP](../../artifacts/windows-main-20261105/SHOU-UI-Evidence-R12-RC3-20261107.zip)为 **4,569,550 字节**，SHA-256 `665e4899fa1f808078794e643ab4967749de32601c04885902469462ad9429e4`；复制哈希一致、ZIP 完整性通过，白名单仅 PNG/说明/清单，不含 UIA、账户、日志、程序或 JAR。[归档核对](../../artifacts/windows-main-20261105/reports/r12-final-public-evidence.json)。

续验收尾：任务占用 **34.769 / 35 GiB**，D 盘可用 **277.026 GiB**，保留 20 GiB 下限；无 heavy owner，续验的三个候选进程均已正常结束。只清理已核对成功输出的编译器产物，失败目录、报告、候选、原工作区 AGENTS.md 修改、参考归档和 todo 均保留。[最终收尾记录](../../artifacts/windows-main-20261105/reports/r12-resumed-delivery-final.json)。

## 未完成项

- 世界目录仍缺可靠可移植数据源；CF 包解析不等于 CF 全站搜索。当前社区来源为 Modrinth，不宣称其它来源与全部标签均已迁移。
- OptiFine、Cleanroom、LabyMod、LiteLoader 的可校验独立安装器目录仍未接入；入口显示实际限制和官网，不以隐藏/禁用算完成。历史 Forge 工件并非都提供 installer.jar，更多历史版本的另存与失败恢复需原生补验。
- 安装器本轮已做受控文件系统成功/坏哈希/取消/重复执行回归，以及两类真实官方 JAR 另存；真实系统选择器的完整提供方、取消/断网/覆盖矩阵待验。独立安装器暂未接镜像优先级。
- 资源详情长介绍、完整兼容性、其它来源、依赖与存档目标安装链仍按 todo.md 追踪。数据包可浏览不等于已支持通用实例直接装入。
- 加载器组与 Fabric 之间的间距、本机整合包导入卡占高，以及更多标签中文名仍需下一轮继续收紧；不将代表状态走查写成逐像素或所有业务已齐。
- macOS 原生、125%/150%、跨显示器、全键盘/焦点、屏幕阅读器和高对比度完整矩阵待验。交叉构建不代替原生运行。
- 没有新增游戏、正版登录或游戏内效果证据；不执行游戏验证，除非用户在后续当前任务中明确要求。

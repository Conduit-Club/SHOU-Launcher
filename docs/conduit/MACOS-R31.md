# Mac R31：同一整合包多窗口与下载链

日期：2026-11-11。开发分支：`features/windows-parity`。用户本轮授权两个隔离 Minecraft，仅主菜单/本地单机；不连接服务器、不操作个人实例。不合并主线或发布正式版本。

## 行为变化

- 已运行的已安装整合包可再次启动，首页显示“再开一个游戏窗口”。两次启动共用实例配置与存档，Minecraft 自身的世界锁仍适用；不可同时打开同一世界。
- 每次启动单独分配临时目录和原生库目录；重复启动只校验既有客户端、库和资源，不修复正在使用的基础文件、不重复执行加载器处理器、不重写安装完成标记。缺失/坏文件明确要求退出全部窗口后补全。
- 完成私密启动参数交接后即释放全局操作；窗口探测在独立观察任务中执行。游戏按 PID 登记与观察，一个退出不清空其余会话。会话文件兼容旧单条格式，并登记全部存活 PID 与操作系统创建时间。
- 关闭启动器时交接游戏进程和独立日志，重开通过会话记录识别仍运行的窗口。模组/资源/实例写入保护仍按物理目录内任一存活游戏判断。停止命令只针对本启动器拥有的所选实例最新进程，磁盘记录不授予杀进程权限。
- CF/FTB 的官方 CDN 专用传输接入共享并发/限速配置，继续独立验证重定向与认证头，不交由外部 RPC 绕过来源校验。整合包、库和资源生产循环使用配置中的并发数。
- Aria2 非默认并发可实际使用；≥16 MiB 的已校验文件最多四段，每一段占用共享连接预算。小文件单连接。取消暂停本任务 GID，重试重连后续传；断线/失败后尝试暂停，再回退内置传输；发布前本机复验大小和哈希。
- 下载条目显示实际后端、连接/速度、重试或切源、哈希校验。严格 HTTPS 与全局限速仍选择内置后端并说明原因，未暗中修改用户已有 daemon 的全局设置。
- 安装任务区分包下载、清单解析、包文件安装、游戏本体/加载器准备。最后阶段给出库/资源真实检查计数、原生库解包及状态保存；剩余阶段未知时用不定进度，全部返回成功后才完成总任务。

- 本轮实窗发现的运行中“补全/校验”、启动计划和脚本入口已同步禁用；实例概览的启动按钮也显示再次开窗文案。模组更新查询增加已检查/总数/当前文件，取消后的迟到进度不能覆盖结果。

## 正式 WPF 下载链对照

基线：正式标签 `v1.5.0`，提交 `8746ad26f9df1eff9f5a3ea57311f82a14ec181b`。通过 `git show v1.5.0:<路径>` 阅读实际旧实现，并结合 `git diff v1.5.0 -- <相关路径>` 追踪新调用链。旧新目录不同，不能仅靠文件名判断有无移植。

| 能力/旧实现入口 | 新实现与状态 | 验证/剩余范围 |
| --- | --- | --- |
| `FileDownloader.DownloadCoreAsync` 来源顺序与 `ExpandVersionMetadata` | `DownloadSources` / `ModDownloadSources`、版本解析器及 `VerifiedDownloader` 已有逐源重试；本轮显示重试/来源和后端 | 坏源、哈希、Range/ETag 的现有回归；全提供方实际联网矩阵仍待补 |
| `ClubAria2Download.DownloadAsync` / `Aria2RpcClient` | `DownloadCoordinator` 本机 RPC、最终哈希校验、故障回退；本轮非默认并发与加权分段预算 | 增加真实 daemon 镜像回退/取消测试；服务自动发现/安装/启停尚未实现 |
| `FileDownloader` 的 `AdaptiveRangeDownloader` | 内置后端仍为单文件逐源传输；Aria2 分段可多源并行 | 尚未移植内置自适应分块算法；不可将“有多个来源”称为全部后端都支持多源分片 |
| WPF CF API key 分流 | CF 元数据只在固定 API 携带 key；文件下载保留 CDN 允许列表 | 本轮仅接齐共享预算，不把认证头交给 aria2 或任意镜像 |
| 旧 DownloadFile 字节、速度、活动线程与任务阶段 | 新端四阶段任务 + 文件行 + 全局速率；补后端、重试/校验、游戏资源真实计数 | 历史文件快照最多 250 行，不用它估算完整大包总文件数；任意单文件暂停/恢复尚未形成完整 UI |
| 失败/取消后的恢复 | 内部同源续传收据、aria2 owned GID；整包失败可重试，已导入包不重复导入 | 网络切换/SMB 中断与 Windows 原生下载窗口尚待验 |

这张表仅完成下载部分的本轮逐项核对。账户、实例、设置、工具和其它页面仍须按正式 WPF 实际行为扩展清单；已明确移除的联机/视频背景不恢复。

## RLCraft 调查记录

已读用户提供的 GPT Pro 分享：https://chatgpt.com/share/6af44d44-d1a4-83ee-9270-65c1938f48e5 。纠正“LWJGL 2 必须 x64 Java/Rosetta”的绝对判断：存在 ARM64 Java 8 与匹配 LWJGL 2/JInput/OpenAL 的原生路径。当前仅纳入调查与实现清单，不安装 Rosetta、不启动 RLCraft。

原始资料：

- Prism 官方 Java 表：https://prismlauncher.org/wiki/getting-started/installing-java/
- MinecraftMachina ARM64 补丁元数据：https://minecraftmachina.github.io/meta-multimc-arm64/
- LWJGL 2 补丁发布：https://github.com/MinecraftMachina/lwjgl/releases/tag/2.9.4-20150209-mmachina.2
- Prism 规则：https://raw.githubusercontent.com/PrismLauncher/meta/main/meta/common/mojang-library-patches.json
- aria2 参数/RPC 语义：https://aria2.github.io/manual/en/html/aria2c

后续以实际 Java 架构、固定补丁版本/哈希为规则，在 loader 合并后处理；不把 LWJGL 2 升为 3，不泛改旧模组或混合 ARM/x64 动态库。先原版→Forge→原包，核对输入、声音、全屏/DPI和 Windows 回归；具体数字版本尚须固定元数据 revision 后核验。

## 验证与交付

本轮证据目录：`.shou-dev/reports/macos-r31-20261111/`。首次失败与修复后的记录保留。

- Core 全量 732 通过、5 个沿用平台跳过；包括真实 aria2 daemon 的镜像回退、本机校验、取消暂停和重连续传。最后记录 `core-final.json/.log`。
- Desktop 全量 243 通过、0 失败/跳过；含同包再次启动、运行中准备入口保护、整包等待最终准备/发布、模组更新计数及取消后迟到回调。最后记录 `ui-final.json/.log`。
- RC1 已在真实同一个隔离 XPlus 1.21.1 副本启动两个游戏，PID/创建时间分别保留，临时/原生目录各两份；退出启动器后两者 PPID 均变为 1，重开识别运行状态，模组写入操作受保护。
- RC1 真实 ATM10 更新列表：检查 494 项，37 项更新、6 项未核实；版本选择与确认启用/撤回通过，未应用更新。RC2 已实窗观察 67/494→105/494 的计数与当前文件，取消后立即恢复操作，未应用模组更新。
- 第一对进程在等待原生 UI 许可期间已按准确 PID/创建身份用 TERM 收起，未冒称主菜单或正常菜单退出。用户随后明确批准本轮 QA 启动器和两个隔离游戏的原生接口；RC2 已使用同一副本完成后续验收。

剩余跟踪见 `todo.md` 的 R31/R30。用户个人 `/Applications` 安装与 `~/Games` 实例不作为测试目标。


## RC2 最终原生验收

- 同一隔离 XPlus 6.1.3 / Minecraft 1.21.1 / Fabric 0.18.4，85 个顶层模组 JAR（游戏主菜单报告含嵌入项共 98 Mods），官方安装的 ARM64 Java 21，单窗口内存 3072 MiB。
- 两个窗口分别为 PID 92737、92822，独立主菜单均实见。退出父启动器后，两进程同 PID/创建时间继续运行，PPID 为 1；重开启动器重新识别运行状态，准备/修复/启动计划与脚本入口保持保护。
- 在第一窗口创建并进入隔离世界 `R3 Isolated`；第二窗口显示红色“Locked by another running instance of Minecraft”，禁止同时打开同一世界。第一窗口“Save and Quit to Title”后显示保存完成，第二窗口刷新后解除锁定并成功读回该世界，位置与场景保留。
- 第一窗口从 Quit Game 退出时第二窗口独立存活并进入存档；第二窗口随后保存、回到标题并 Quit Game。两进程均消失，独立日志均包含 Stopping。因父启动器故意脱离，未另行采集这两个游戏的退出码，不将菜单退出观察伪写成 code=0。测试启动器两次关闭均由受监视 open 进程记录 exit0。
- 用户个人应用、个人实例与存档未操作。全部测试游戏和 QA 窗口已关闭，原生工具本轮目标授权表已设 inactive；QA 启动器配置恢复为测试前逐字节副本。隔离测试世界与证据保留。
- 关键证据：`rc2-game-results.json`、`rc2-first-normal-quit.json`、`rc2-two-games-{before-close,after-launcher-close,after-launcher-reopen}.json`、`rc2-native.json`、`rc2-reopen.json`、`cleanup-and-preservation.json`，以及 `native-evidence/04` 至 `19` 图像。

### 候选包

- 源码：`f5aa2dccba667ac5380a5cd801393bc1c414c43b`。后续文档提交不改变此包的应用源码。
- ZIP：`/Users/moear/Code/SHOU-Launcher/.shou-dev/dist/MACOS-R31-20261111/rc2/SHOU-Launcher-osx-arm64-v1.5.0-dev.gf5aa2dcc.zip`
- SHA-256：`1f8610ecd1d389dd45675270af4e908416980cfaa2c2bfa894dbcb9b113027bc`；大小 51,906,331 字节。
- .NET SDK 10.0.403、Avalonia 12.1.5、Release / osx-arm64 自包含；显式传入原 `.env`，构建核实 Microsoft/CurseForge 配置存在，未输出其值。
- 本机 ad-hoc 签名候选，未公证、未正式发布。为避免 CUA 绑定个人应用，QA 副本只换 bundle 标识/显示名并重签 apphost；Core/Desktop 程序集逐字节一致，正式 ZIP 保持原样。

### 仍待完成

Aria2 服务发现/安装/启停，CF/FTB 在保持来源允许列表下的外部 RPC 支持，内置自适应分片与完整单文件暂停/恢复；下载管理完整历史与大包指标仍需扩展。实例目录重命名/迁移/恢复尚未做成产品能力。全 WPF 页面逐项差表、Windows 本轮原生回归、长时游玩与更多包/加载器矩阵继续跟踪。

RLCraft 的 ARM64 Java 8/LWJGL 2 补丁方案只完成资料调查与 TODO，未安装或启动。原生自动化在 Minecraft 文本框出现输入丢字，本轮使用实际可见的 `R3 Isolated` 名称完成测试，不能据此判断用户键盘输入或启动器存在确定缺陷。Mac 长列表滚动后的 CUA 点击几何不可靠，官方接口选中后 Enter 可进入；真实鼠标双击与搜索交互仍待独立复核。

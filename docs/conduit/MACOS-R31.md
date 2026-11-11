# Mac R31：同一整合包多窗口与下载链

日期：2026-11-11。开发分支：`features/windows-parity`。用户本轮授权两个隔离 Minecraft，仅主菜单/本地单机；不连接服务器、不操作个人实例。不合并主线或发布正式版本。

## 行为变化

- 已运行的已安装整合包可再次启动，首页显示“再开一个游戏窗口”。两次启动共用实例配置与存档，Minecraft 自身的世界锁仍适用；不可同时打开同一世界。
- 每次启动单独分配临时目录和原生库目录；重复启动只校验既有客户端、库和资源，不修复正在使用的基础文件、不重复执行加载器处理器、不重写安装完成标记。缺失/坏文件明确要求退出全部窗口后补全。
- 完成私密启动参数交接后即释放全局操作；窗口探测在独立观察任务中执行。游戏按 PID 登记与观察，一个退出不清空其余会话。会话文件兼容旧单条格式，并登记全部存活 PID 与操作系统创建时间。
- 关闭启动器时交接游戏进程和独立日志，重开通过会话记录识别仍运行的窗口。模组/资源/实例写入保护仍按物理目录内任一存活游戏判断。停止命令只针对本启动器拥有的所选实例最新进程，磁盘记录不授予杀进程权限。
- CF/FTB 的官方 CDN 专用传输接入共享并发/限速配置，继续独立验证重定向与认证头，不交由外部 RPC 绕过来源校验。整合包、库和资源生产循环使用配置中的并发数。
- Aria2 非默认并发可实际使用；≥16 MiB 的已校验文件最多四段，每一段占用共享连接预算。小文件单连接。取消暂停本任务 GID，断线/失败后尝试暂停，再回退内置传输；发布前本机复验大小和哈希。
- 下载条目显示实际后端、连接/速度、重试或切源、哈希校验。严格 HTTPS 与全局限速仍选择内置后端并说明原因，未暗中修改用户已有 daemon 的全局设置。
- 安装任务区分包下载、清单解析、包文件安装、游戏本体/加载器准备。最后阶段给出库/资源真实检查计数、原生库解包及状态保存；剩余阶段未知时用不定进度，全部返回成功后才完成总任务。

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

本轮证据目录：`.shou-dev/reports/macos-r31-20261111/`。首次失败与修复后的记录保留；最终结果在候选实窗验收后补入。

剩余跟踪见 `todo.md` 的 R31/R30。用户个人 `/Applications` 安装与 `~/Games` 实例不作为测试目标。

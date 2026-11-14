# Mac R28：整合包直装与原生验收

本轮延续 `features/windows-parity`。用户授权修改、提交和推送当前功能分支，未授权合并 main/dev、合并 PR 或正式发布。本轮隔离游戏授权覆盖主菜单、单机和正常退出；未读取真实账户凭据、未连接服务器、未上传游戏日志。

## 候选与环境

- 最终候选：`.shou-dev/dist/MACOS-R28-20261111/rc7/osx-arm64/SHOU-Launcher-osx-arm64-v1.5.0-dev.g2591d500.zip`（本地 ad-hoc 签名，非正式发行）。
- 源码：`2591d50091129f2f61f7e39e02daf4de0a58275b`；.NET SDK `10.0.403`，Release / osx-arm64。报告提交可晚于构建源码提交。
- SHA-256：`7b3aa22a8b3b7ad48316d1e3ab89bdeee8dc230bd1688d32fae35c55a9b92802`。
- macOS 26.6 / Apple Silicon；游戏 Java 21.0.13+9。候选通过既有 EnvFile 流程载入本机 `.env`；微软/CurseForge 配置均验证已嵌入，只记录布尔结果，不输出值。无需 SMB 复制。

## 已实现

- 整合包版本行直接启动后台下载、解析、独立实例导入及本体/加载器准备。右下角通知进入任务管理；过程中可导航和浏览社区资源。整合包任务与文件传输分别计数，明细可展开。
- 展示阶段、文件进度、剩余文件及最近一秒网络速度；未知总数明确显示“统计中”。短文件、并发和尾块计入，缓存命中与断点偏移不当成新流量。
- 最多四个文件并发。取消等待所有写入者停止后回滚暂存；基础游戏准备失败保留已导入实例，重试不会重复导入。重复点击和迟到进度有保护。
- CF 未提供地址时，仅接受 FTB 官方公开同版本清单中匹配 MC/加载器、CF 项目/文件 ID、目录、大小和 SHA-1 的地址，再校验 SHA-512。保留不可用、恶意、已删除及归档限制，不重建 CDN URL，不向 FTB/Modrinth/CDN 发送 CF key。读取包建议内存并应用于新实例。
- Mac 品牌顺序为 **SHOU → 潮涌核心社启动器 → 图标**。使用 AppKit 标准交通灯，以 NSView 坐标转换维持 48pt 标题栏的中心；最小化/全屏跳过维护，关闭停止。修复大号数值裁切及进度条越界。
- 修复 BootstrapLauncher 原版 JAR 重复模块：加载器 profile 身份与实际继承客户端 JAR 名不同，导致官方 `${version_name}.jar` 忽略项失配。仅为声明该模板的 BootstrapLauncher 补入实际文件名，保留 profile、其它过滤项和 classpath，不改包内模组。[官方语义](https://github.com/McModLauncher/bootstraplauncher/blob/main/README.md)。
- Fabric 1.21.1 世界的 `fabric`、`fabric-convention-tags-v2` 按目标实例中未改动的安装清单双哈希、模块元数据及资源内容核验。预检与发布前重新核对，不删除/改写 NBT 包身份；未知 ID/其它版本继续阻塞。[官方资源包提供实现](https://github.com/FabricMC/fabric/blob/1.21.1/fabric-resource-loader-v0/src/main/java/net/fabricmc/fabric/impl/resource/loader/ModResourcePackCreator.java)。

## 测试与原生结果

Core **707 通过 / 5 显式跳过 / 0 失败**；Desktop **233 通过 / 0 失败**，构建零警告/错误。Core 新增 28 项覆盖 FTB 身份/来源/哈希、并发取消、速度统计、Fabric 世界身份和 Bootstrap 参数；Desktop 新增 3 项覆盖后台导航/通知、最小窗口、取消/重试、保留实例及迟到进度。5 项跳过为 Windows 文件语义两项，以及需要特定真实运行时环境的三项，未伪称通过。

| 实测项 | 结果与证据 |
| --- | --- |
| CF StoneBlock 4 1.24.1 | 软件内一键完整安装；418 项 required，安装清单 6,894 项，建议内存 8,192 MiB。三个旧阻塞文件由同版本 FTB 清单解决，大小/SHA-1/SHA-512 均匹配。 |
| StoneBlock 游戏 | 修复前模块冲突失败日志已留存。RC5 修复后观察 437 Mods / MC 1.21.1 / NeoForge 21.1.262 主菜单，创建并进入单机 `New World`，保存回标题后点击退出；启动器记录 `code=0, cancelled=False, timeout=False`。默认关闭光影。 |
| Modrinth XPlus Fabric 6.1.3 | RC6 软件内一键下载、解析、安装 332 项文件并准备 Fabric 0.18.4；85 个顶层模组 JAR，实际 98 Mods 主菜单，点击游戏退出并记录 code=0。实窗速度曾为 5.06 MiB/s。 |
| 真实 Fabric 世界 ZIP | 原 R27 样本成功预检/导入为 `Mac28-ImportedFabric`；22 项文件全部与 ZIP 字节一致。`level.dat` SHA-256 保持 `c0f47e5643b21200e693ba8363d5dd519b5440683ad387a6e79112a9bedf92ee`。 |
| Mac 窗口 | 实际拖到 880×560 后任务指标/卡片可读；原生按钮中心距顶 24pt。全屏来回、原生最小化/恢复/关闭已验。系统共享浮层覆盖交通灯的鼠标区域时使用已授权 AX 动作，不把覆盖层当作产品按钮。 |
| 取消与最终收尾 | RC1 下载阶段正常关闭后无残留暂存、无残缺 StoneBlock 实例。四并发取消另有确定性回归。最终 RC7 开窗收据校验源码/隔离目录/原生句柄，原生关闭 `normal_close`，测试启动器及 Java 进程均已结束。 |

游戏验收分别在 RC5、RC6；RC7 仅改变任务摘要计数，Core 与 RC6 相同，并另作真实开窗/关闭检查。CUA 无法绑定 Java 窗口时沿用用户明确授权的 macOS 官方接口，校验 PID、可执行文件、窗口和前台状态；未操控其它应用。

完整收据/截图位于 `.shou-dev/reports/macos-r28-20261111/`：`stoneblock-installed.json`、`stoneblock-native-result.json`、`xplus-direct-native.json`、`fabric-world-import.json`、`native-final-result.json`、`native-evidence/`；TRX 留存于其中 `validation/`。

## 未完成与边界

详见根目录 `todo.md` 的 R28 条目：其它无公开同哈希来源包的合法手动补文件流程、未知世界内建包/版本、世界快捷游玩、更多 UI 徽标/二级文案/默认实例名，以及 Windows 真机、真实登录/皮肤、DPI/跨屏/VoiceOver、断网/限速和更多加载器矩阵。未据本轮结果宣称已经完全取代 WPF。

StoneBlock 基础游玩通过，日志仍出现 Malum 配置反复纠正、Apple OpenGL copy-buffer 与 Vector API 优化告警。未改写原包掩盖问题；全部机器/配方、长期游玩与光影仍需下一轮兼容验证。

## 旧实现参考

用户本轮明确允许参考旧实现，因此本轮不再声称源代码隔离的 cleanroom。实际阅读 `Plain Craft Launcher 2/Pages/PageDownload/Comp/PageDownloadCompDetail.xaml.cs` 的 `Install_Click` 以核对后台组合任务/通知行为；仅检索 `Plain Craft Launcher 2/Modules/Minecraft/ModModpack.cs` 的入口/阶段。新 Core/Desktop 不引用旧 WPF 项目。原有未跟踪计划文件保留，未纳入提交。

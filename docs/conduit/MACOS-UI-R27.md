# Mac R27：失败项修复与整合包实测

本轮结论：上轮 7 项失败已修复，XPlus 的导入、实际游玩、导出再导入和最终候选主菜单 / 退出通过。StoneBlock 4 已由软件内下载，修复三处启动器边界后仍被三个必需文件的下载来源阻塞，完整安装未通过；真实 Fabric 世界 ZIP 的身份核验缺口保留待办。

日期：2026-11-11。工作分支 `features/windows-parity`；只做本分支普通提交与推送，不合并 `dev` / `main`、不创建或合并 PR、不正式发布。沿用 cleanroom 边界，仅修改新 Core / Avalonia 实现及其测试，不读取旧 WPF 视图或模板。

## 已完成的修复

- 修正 Unix 打开文件句柄与原子替换的测试契约，保留 Windows 锁失败验证；另以备份路径被目录占用验证真正的发布失败回滚。
- 用移动完成的内部测试检查点确定性验证数据包回收 / 更新取消回滚，不依赖 macOS FileSystemWatcher 投递时序。
- 分离 detached Java 进程的完成证据与父进程的 ExitCode 读取，保留日志脱敏和启动器退出后游戏存活验证。
- 修复跨平台绝对路径测试；数据包启停写盘成功时替换过期反馈并通知控件。
- MRpack 导出对明确选入的本地覆盖文件使用当前字节、大小和哈希，规划及 ZIP 写入再次核对；未选入远端资源仍按安装清单严格校验。
- 缓存内容下载恢复其 2 GiB 边界，避免把 215.63 MiB 的外层整合包当成 128 MiB 的包内单文件；重定向、来源、哈希与凭据隔离验证保留。
- 操作覆盖层显示当前文件和实际传输 MiB，结束后清空；Mac 增加 Control–Command–F 全屏往返。
- ZIP 条目上限统一为 10,000；客户端文件上限 8,192；实例安装清单上限 10,000。外层 256 MiB、展开 512 MiB、包内单文件 128 MiB、清单 4 MiB、CF 引用 512 项及下载总量 4 GiB 保留。
- CurseForge 包覆盖文件支持 KubeJS / scripts 游戏脚本、datapacks、configureddefaults、ftbteambases、schematics 和 default-server.properties。系统可执行脚本仍拒绝，`.js` 仅允许在 kubejs / scripts 中；玩家 saves 与启动器账户路径仍不导入。
- CF 模组、资源包、光影、数据包按明确 classId 和扩展名分配目录；数据包仅放实例全局 datapacks，不自动改写任何已有世界启停状态。所有安装清单发布前统一校验 4 MiB 字节预算，避免写出随后无法读回的文件。

## 提交与构建

| 源码提交 | 内容 |
|---|---|
| `107bfeb59634e4d3f194c953854a5a5647b2a587` | 原生回归契约、MRpack 本地配置导出和反馈 |
| `d07b7daf191d5516032001362afcc883ce04b7a0` | 大缓存文件下载、传输提示及 Mac 全屏快捷键 |
| `761819d87fbd30de3cdb61abde4d97ac8ad784e2` | 大型 CF 包数量预算与必需游戏数据覆盖目录 |
| `2ad242d424ed87cf4b32d443fc76db1afce5c73d` | CF 光影 / 数据包类型及安装清单发布前字节预算 |
| `cf90727f35ba93e8593fa826e9ee189edd8b016c` | Mac Command–M 最小化及全屏保护 |

上述提交已普通推送到社团 `origin/features/windows-parity`。原有未跟踪的 `SHOU_Launcher_macOS_Codex_Plan.md` 未改动、未提交。

最新 RC5 为干净 detached 源码构建，.NET SDK 10.0.403、Release、自包含、osx-arm64，版本 `1.5.0-dev.gcf90727f`。ZIP 解包及 `codesign --verify --deep --strict` 通过；签名为本地 ad-hoc，未公证。

- 候选：`.shou-dev/dist/MACOS-R27-20261111/rc5/osx-arm64/SHOU-Launcher-osx-arm64-v1.5.0-dev.gcf90727f.zip`
- SHA-256：`3d811b7f9e70e06d228dade8bcdab30d2f6c6dd52f09b98496c143e45b459413`
- 元数据：`.shou-dev/reports/macos-r27-20261111/candidate-rc5.json`

## 自动化验证

Core **679 通过 / 5 显式跳过 / 0 失败**（2ad242d；RC5 的 Core 与 Core.Tests 无差异，见 core-source-unchanged-rc5.json）；Desktop 最新 **230 通过 / 0 失败**（cf90727f）。C# 构建为 0 警告 / 0 错误。跳过项是条件限定的 Windows 目录句柄，以及需要单独显式启用的真实 Java 修复 / 下载测试，未算作通过。

大包回归包含 6,500 多个文件的真实暂存与清单读回、必需游戏脚本及预设数据保留、超额客户端 / ZIP 条目 / 安装清单拒绝。其余新增回归覆盖当前本地内容导出再导入、移动后取消回滚、真实反馈绑定、当前文件传输显示和缓存 / 包内文件大小边界。

结果与 TRX：`.shou-dev/reports/macos-r27-20261111/validation/rc4-core/`、`validation/rc5-desktop/`；有界运行记录为 `core-resource-final.json`、`desktop-shortcut.json` 和 `publish-rc5.json`。

## 原生验证记录

所有操作使用任务隔离数据 `.shou-dev/test-data/day-native-20261105` 和合成离线角色；未读取或修改真实登录凭据，未加入社团服务器。游戏窗口在 CUA 无法绑定时沿用本聊天已获准的 macOS 官方接口范围，目标进程 / 可执行文件 / 窗口先登记再操作。

### XPlus Fabric

作者 Wudji 的官方 Modrinth 项目 `UCpApD3P`，选用 Minecraft 1.21.1 的 `v6.1.3-fabric-mc1.21.1`，版本 ID `1jWN3guT`。下载 MRpack 2,046,006 字节，按官方元数据 SHA-512 核验。

RC1 原生文件选择器读取、预检、显式新实例导入完成；清单 332 文件，85 个顶层模组 JAR。首页首次单击自动准备 Minecraft / Fabric 0.18.4，实际主菜单显示 98 Mods，生成并渲染本地世界，保存后回主菜单并 Quit Game。游戏日志记录 `code=0, cancelled=False, timeout=False`。

独立清单采样发生在首次启动已经点击之后：5 个配置文件由模组改写，其他清单项和模组 JAR 相符，不能称作启动前所有 332 项独立匹配。Mac OpenGL 有驱动诊断输出，但本次未崩溃且世界已渲染；长时间运行 / 更多光影组合仍待扩展。

RC4 对导出后重新导入的新实例执行首次单击启动，自动补全游戏 / Fabric，再次到达 98 Mods 的真实主菜单并 Quit Game，exit0。RC5 也实际重开同实例、观察主菜单及正常 Quit Game，exit0。真实世界渲染证据来自本轮 RC1，不冒充 RC5 又生成了世界。

证据：`native-evidence/01-xplus-first-window.png`、`03-xplus-world.png`、`05-xplus-saved.png`、`12-rc4-xplus-menu.png`、`16-rc5-xplus-menu.png` 与 `native-observations.json`。

### FTB StoneBlock 4

软件内搜索官方 FTB / FTBTeam 项目，选择 `1.24.1`（Minecraft 1.21.1 / NeoForge 21.1.262）。外层 ZIP 226,103,028 字节，独立 SHA-1 `17b277a094b575210a48e3e2ae3708bd745b73c3` 与下载记录相符；含 418 项 required 引用、6,477 个文件条目，展开约 322.76 MiB，推荐内存 8,192 MiB。

首轮错误使用 128 MiB 单文件限额；RC2 修复后软件内下载和校验完成，随后因旧 5,000 条目上限预检失败。RC3 修复数量边界与覆盖目录后，418 项引用中四项阻塞：三个文件无第三方 URL 且 Modrinth 同 SHA-1 查询 404，另一项 Extra Data Models 数据包属于尚未接入的包级资源类别。其[官方文件页](https://www.curseforge.com/minecraft/data-packs/extra-data-models/files/8644133)确认项目 1010203 / 文件 8644133；早期曾误称着色器，已更正。RC4 补齐类型后原生重新核验，数据包已解析，剩三个 required 阻塞：1289504/8640618、1138140/7964810、1189887/6139357，均为 Modrinth 精确同哈希查询 HTTP404。未创建不完整实例，完整安装、主菜单与世界未通过；RC5 仅改 Mac 快捷键，Core 与 RC4 相同。证据见 native-evidence/11-stoneblock-three-blockers.png。未用外部解包或手工填充实例冒充软件内安装成功。

### 窗口及其他 UI

RC2 / RC4 / RC5 原生 Control–Command–F 全屏往返成功。RC4 经标准 AXMinimizeButton 的 AXPress 实际最小化，CG 窗口不可见；Raise 恢复后位置 / 尺寸保持 X247 Y131 / 976×636。RC2 曾出现恢复到 X=-77，历史现象保留，未以单次正常恢复替代完整位置矩阵。RC4 真实拖动窗口角至 **880×560**，主要导航、账户、实例选择及启动入口可见，证据 15-rc4-minimum-boundary.png。

RC4 的 Command–M 未响应；RC5 新增 Mac 快捷键并实窗验证：正常状态下最小化（CG onscreen=false），Raise 恢复，全屏状态下不会错误最小化，Control–Command–F 仍能返回。

RC3 本轮新增代表用例：

| 流程 | 结果与证据 |
|---|---|
| 游玩后的 MRpack 导出 → 原生再导入 | 通过；124,955,038 字节，952 覆盖文件全部与当前实例 SHA-512 相符，5 个运行时配置保留；新实例 952 文件。见 xplus-native-export.json |
| 世界 ZIP 原生选择器 / Finder 文件剪贴板 | 两种入口均预检后显式确认创建新目录；源夹 SHA-256 不变。采用隔离 R5 编辑对照世界，不称作真实游戏世界成功迁移 |
| 真实 Fabric 世界 ZIP → 同为 Fabric 的新实例 | 失败：内建包 fabric 未核实，拒绝发布；见 R27-W01。对照成功不抵消该失败 |
| 数据包加载顺序 | 界面下移并显式保存，独立解码 NBT 验证 qa-disabled → qa-enabled 顺序；vanilla 保留 |
| 本地 ZIP 替换、更新备份、预检恢复、明确确认恢复 | 通过；新文件与样本字节相同，原包备份精确；恢复后 level.dat 与两原包逐字节一致，新版本另存保留。见 datapack-native-update / restore / order.json |
| Finder 数据包文件剪贴板 → 启用 | 通过；3 个包可见，启用成功后旧的“尚未修改世界启停状态”消失；截图 10-datapack-enabled-feedback.png |
| 独立内存 / JVM 参数 → 重命名 → 保存 | 通过；独立 3072 MiB、-Dfile.encoding=UTF-8，实例 ID / 路径不变，全局 2048 MiB 不变 |
| Mac 脚本原生另存 | 通过；脚本路由到明确实例及隔离目录，sh -n 通过，未含凭据字段；仅导出检查，未执行 |

RC4 重启已在实窗读回改名后的实例、独立内存 / JVM 参数、两个世界、三个数据包及保存的加载顺序。

## 测试工具问题与边界

首次测试参数误设 `--exit-after 6500`，超过工具 3,600 秒上限，启动失败记录保留。改为 3,300 秒后，直接执行文件启动与 CUA 按 Bundle ID 激活混用时，CUA 曾打开已登记的旧 RC1 空白默认窗口；未在这些窗口执行登录、导入或保存，随后正常关闭。默认目录可能更新启动日志 / 锁 / bootstrap 缓存，不声称零写入。

缓解：仅注销旧任务候选的 LaunchServices 登记，登记当前候选，用 `open -n -W --args` 传隔离参数，核对 Ready 收据 / PID / 数据目录后再绑定 CUA。旧候选文件未改动，登记可恢复。关闭后避免 `getAXState()` 自动重开。

RC4 的 CUA 最小化坐标点击未隐藏窗口，左上可见另一个蓝色系统控件，定位影响尚未确认；改用已授权的标准 AXMinimizeButton 动作完成验证。AX 设置尺寸也未改变实际 CG 尺寸，故最小窗口以真实角拖动和截图几何为准，不把调用成功当作调整成功。

任务保持 `.shou-dev` 35 GiB 总预算和系统 20 GiB 余量检查，重活互斥；仅回收可重建的闲置旧 build 目录，保留源码、候选、报告和测试数据。回收收据见 `reclaimed-builds.json`。

## 未完成

本轮测试已结束，候选与两个最终游戏进程均正常退出，无测试任务留在后台。最终交付前的完整性检查见 final-audit.json。以下事项保留在 todo.md，未算作通过：

- **R27-P01 / P1**：StoneBlock 三个 required 文件没有合法自动来源；需设计合法手动补文件、精确哈希校验和重试流程。完整安装 / 主菜单 / 世界仍阻塞。
- **R27-W01 / P2**：真实 Fabric 世界 ZIP 的内建 / 模组包身份未与目标实例核验，预检拒绝 fabric；保留原 NBT，不手改样本去掉身份。
- 世界列表快捷游玩尚未接入；Mac 自定义游戏标题适配仍缺。
- 导入已显示当前文件 / 实传 MiB，总文件数、解析阶段和失败重试诊断尚不完整。
- 数据包真实在线更新、CF 数据包外网更新、更多加载器 / Java / 光影组合、已有目录切换、classpath / 启动前程序和导出脚本实际执行等原生矩阵仍待验。
- 完整 DPI / 跨屏 / VoiceOver / 键盘 / SMB 中断矩阵，以及没有系统覆盖控件时的交通灯鼠标操作未全量验收。
- 真实在线账户、在线皮肤披风、认证入服未测；本轮只用合成离线角色。大型 CF 包包含新增覆盖目录后的各导出格式往返也未全量覆盖。

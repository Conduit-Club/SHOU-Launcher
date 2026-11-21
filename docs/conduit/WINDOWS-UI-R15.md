# Windows R15：缓存、旧成就图标、游戏设置与已有实例

2026-11-07。本轮补齐工具页可重建缓存清理和 39 项旧成就图标，将游戏/下载设置接入共享业务，并通过原生选择器接入真实已有实例的隔离副本，验证设置和目录操作。源码提交：`d46ad675660cbc1c7add97730282e33c860eb82d`（主要实现）、`60df0fe44ca737fb3807e74df1f2d59121c88fe6`（自动内存显示、独立内存继承及导航/可访问名称修正）。

沿用 `features/windows-parity`，跟踪 `origin/features/wpf-parity`；origin 为本社团 SHOU-Launcher 仓库。没有合并 main/dev、推送、切换 Windows 默认入口或正式发布。原工作区 `D:\Code\C#\PCL-CE-SHOU` 的现有修改保留。

**本轮没有启动 Minecraft、执行下载的安装器、真实登录或读取用户账户缓存。** 游戏参数/计划回归、设置持久化与实例文件操作分别记录，不把它们当作主菜单、正版认证或游戏内效果验收。Windows/macOS 共用 Avalonia 页面和 Core；Mac 原生仍待验。

## UI/功能差异与本轮行为

| 原差距 | 本轮行为 | 验证范围 |
| --- | --- | --- |
| 只有游戏日志回收，没有启动器缓存清理 | 新增缓存扫描、文件/大小预览、显式确认、取消和实际清理结果 | Core 受控文件实际删除；Windows 原生扫描/确认门禁，未删除真实用户缓存 |
| 成就只有四个物品，旧数字映射不完整 | 离线补齐旧生成器 1–39 序号及素材，保留名称/数字和自选 PNG | 39 份素材与精确映射回归；数字 2/3 实际预览、另存并打开核对 |
| 启动页隔离/可见性/优先级/IP 等为灰控件 | 接入全局选项、自动/手动内存及受支持高级参数 | 配置、策略/启动计划、会话可见性回归和 Windows 保存/重启；未验证实际游戏窗口 |
| 缺少自动内存，自动值与输入框可能不一致 | 新配置自动估算，旧手动偏好保留；输入框显示实际计算值 | Windows 自动值 10240 MiB；手动 4096 MiB 保存/重启，实例跟随一致 |
| 管理页并发/限速/协议设置不可用 | 接入共享下载预算、HTTPS-only、来源预设、完整清单和安装后选中 | 受控传输回归；原生保存并重启后 3 并发、512 KiB/s 保留 |
| 无直接接入已有游戏目录的流程 | 选择 .minecraft/versions/单版本目录，预检并显式确认接入 | 中文/空格目录，真实 26.2 / Fabric 0.19.3 副本；原始 JSON 不改写 |
| 实例选项不完整，覆盖项易相互影响 | Java、内存、其它启动选项独立跟随/覆盖；保存及重开 | 实例 IPv6、标题、JVM 保留，同时内存仍跟随全局 4096 |
| 未用实际有内容的实例核对页面 | 接入副本，遍历模组/资源/光影/存档/截图/服务器并打开目录 | 模组禁用/恢复，三个目录入口正确；其余内容列表可读，不等于全部操作完成 |

状态放在对应工具卡和原有设置保存区，没有新增全局底部 loading 条。浅/深色、976×636 和 880×560 DIP 的代表状态已检查；完整 DPI/跨屏/无障碍矩阵仍保持待办。

## 缓存与图标的具体范围

缓存只处理当前数据目录内已知可重建内容：`cache/assets`、`authlib`、`catalog`、`club-updates`、`content`、`libraries`、`loaders`、`versions`、`java-metadata`，以及缓存根下 `.json`/`.json.sha256`。临时目录仅处理 `game-`、`loader-`、`installer-` 子树中超过一天的文件；未完成下载的 `.part`/恢复记录/aria2 文件也需超过一天且没有活动下载租约。

扫描有 100000 项上限，不跟随符号链接/reparse point；预览记录相对路径、大小和 SHA-256。删除前先复核整批预览，再逐文件核对句柄、哈希和租约；变化要求重新扫描，取消返回已完成的实际数量。保留导出、未知目录、实例、世界、账户、设置、Java 运行环境和用户选择的素材。清理缓存与 R14 的可恢复游戏日志回收是两个操作，不混称。

Windows 原生使用一个自建缓存文件和一个受保护文件，确认扫描正确、未勾选时清理不可用、勾选后才可用。没有在原生窗口点击清理；真实删除、预览变更拒绝、链接/越界、近期/忙碌文件保护与取消由 Core 文件系统回归验证。

39 张旧成就背景和序号来自 [menzerath/mcgen](https://github.com/menzerath/mcgen) 的 `assets/backgrounds` / `assets/legacy.go`，来源归档 SHA-256 为 `01f0ea544a3c0253d72d69c51bf35f0749b0071bf8201a596d8510e817eab2f8`。**1–39 是旧成就生成器图标序号，不是 Minecraft 数字物品 ID**；例如 2 为钻石、3 为钻石剑。修正了此前把 2 当作草方块的映射，保留额外名称别名和自选 PNG。

图片在本机共享 Avalonia 渲染，不上传文字、不调用远程生成器、不捆绑字体。生成器 MIT 全文、Minecraft 图像归属和来源记录存于 `src/SHOU.Launcher.Desktop/Assets/AchievementIcons/`，候选包带 `licenses/mcgen.txt` 和 `licenses/achievement-icons.md`。字体仍使用平台字体。

## 设置接线与兼容

- 全局隔离支持全部隔离、仅模组实例隔离、共享目录；影响后续新实例目录，不自动搬迁既有数据。显式已有实例保持原目录。
- 可见性支持保留、最小化、隐藏后随会话恢复、关闭启动器；复用已有游戏与启动器退出解耦逻辑。Desktop 使用受控会话信号验证，没有启动游戏。
- 优先级进入平台进程适配层；IPv4/IPv6 转为明确 JVM 属性。标题支持 `{name}` / `{version}`，Windows 窗口适配已接，Mac 自定义游戏标题仍未验证/启用。
- 自动内存由平台实际总量/可用量计算，256 MiB 步长，估算范围 512–32768 MiB；32 位 Java 上限与最小内存另行校验。手动实例 Xmx 覆盖全局；只覆盖其它启动选项时仍可独立跟随全局内存。自动模式保留手动草稿，不将显示值误写成用户手动偏好。
- JVM 允许明确 GC、编码和语言选项，每行一个；游戏尾部参数仅接受受支持的服务器/快速进入等项。拒绝认证、classpath、agent、TLS、数据目录与受保护引导覆盖。前置程序要求绝对可执行路径，独立参数、不开 shell、30 秒超时；失败不继续启动。未在本轮执行用户程序。
- 下载总并发 1–64、总限速 KiB/s（0 为不限速）；并发和限速共享可取消预算。HTTPS-only 跳过 HTTP 源并保留合法 HTTPS 回退，不关闭证书验证。需要策略时使用内部校验下载器，避免 aria2 绕过限速。单文件多段连接尚未补齐。
- 官方/镜像/自定义顺序接现有来源定义；预设不抹掉保存的自定义 JSON。完整官方清单偏好进入目录缓存身份，安装后选中接入原版、Fabric 和整合包登记。
- 新设置采用可选嵌套字段；缺失旧字段保留旧手动内存。未知嵌套字段、实例覆盖与写前 `.bak` 保留；切回全局继承时不丢未知字段。未强行重置旧用户偏好。

主要源文件：Core 的 `Tools/LauncherCacheCleaner.cs`、`Minecraft/GameSettingsPolicy.cs`、`Instances/ExistingInstanceReader.cs` 与 `LauncherService.*`；Desktop 的 `ViewModels/SettingsPageViewModel.Game.cs`、`ToolboxViewModel.Cache.cs`、`InstancesPageViewModel.Existing.cs`、共享 Settings/Tools/Instances 视图及 `Platform/NativeMemoryInfo.cs`。没有读取或复制旧 WPF 视图、样式、控件结构或采集脚本作为实现输入，没有 WPF fallback。

## 已有实例与真实目录验证

使用本项目旧启动器目录中的 `E:\Minecraft\.minecraft\versions\XPlus PerioTable based on Minecraft 26.2 (Fabric)`，仅复制白名单公开版本 JSON、客户端 JAR、89 个 Mod JAR、9 个资源 ZIP、4 个光影 ZIP，共 **104 文件、185397755 字节**。一个非 JAR 模组目录文件未复制。没有读取/复制账户、usercache、私有配置、原世界或其它账号存储。

副本位于任务隔离数据目录的 `本地 实例验收/.minecraft/versions/...`。通过原生文件夹选择器选择、预检、确认，登记 Minecraft 26.2、Fabric 0.19.3 及原版本身份和元数据哈希。登记使用稳定安全 ID，不改写原版本 JSON，不依赖重新下载；原元数据 SHA-256 为 `28fc95f0f14473da5148fcb0d6d6f9339190a740c92a41480ad1edebdeeb1ad2`。

显式确认的已有路径可在预览数据根之外；授权仅对应登记的版本/root，不能将任意外部路径当作可写游戏目录。链接拒绝、重启映射、来源变化重新确认、未登记路径阻挡有 Core 回归。设置、原始来源及未知字段写在启动器侧，不修改原始版本描述。

Windows 实际操作与结果：

- 保存实例 IPv6、`R15 {name}` 和 `-XX:+UseG1GC`；重开候选后仍在。Java/内存分别跟随全局，内存为 4096 MiB。写前备份存在。
- 模组页读出 89 个 JAR；将 `zfastnoise` 禁用再启用，恢复原文件名与 SHA-256 `1dca8c3f392e1699485f7d5fe60ba42b49157d7aef1882596425d27335981e57`。
- 资源包 9、光影包 4；截图读取项目 Logo 夹具。自建合法存档 NBT 显示 1.21.1 / 4189，服务器读取自建 `example.invalid:25565`。这些夹具不是游戏运行生成的证据。
- 实例、存档、模组文件夹按钮均实际交给默认文件管理器 OneCommander，逐次核对到副本根目录、`saves`、`mods`。
- 最终原来源 104 文件与副本 104 文件全部哈希不变；没有打开原游戏、原存档或真实认证。依据见 [复制收据](../../artifacts/windows-main-20261105/reports/r15-real-instance-copy.json) 和 [最终哈希核对](../../artifacts/windows-main-20261105/reports/r15-source-and-copy-final-hashes.json)。

## 构建、测试和首次失败

| 检查 | 最终结果 | 记录 |
| --- | --- | --- |
| Core 全量 | 406 通过、16 显式跳过、0 失败；总计 422 | `r15-core-rc2` TRX |
| Desktop 全量 | 160 通过、0 跳过、0 失败 | `r15-desktop-rc2` TRX |
| Windows Release 自包含 | 干净源码 `60df0fe4` 构建成功 | `r15-tools-settings-rc2-retry` |
| macOS ARM64 交叉构建 | 同一源码构建成功，不代表原生运行 | `r15-macos-final` |
| Windows 原生 | 200% DPI，浅/深色，976×636/880×560，设置重启、图片保存、已有实例操作 | 逐页证据；三次正常关闭 exit0 |

Core 16 项跳过沿用现有平台/外部门禁：8 项 POSIX 链接/Windows reparse 场景、1 项 POSIX 失败 keytool 夹具、4 项 Mac argv/Keychain、2 项未启用的受控 Java 副本门禁、1 项未启用的在线 Java 门禁。未将跳过计为通过。C# 构建为零警告/错误；Java 引导构建仍有三条既有 source/target 8 弃用提示。

新增回归覆盖缓存白名单/实际删除/保护/变更检测、39 素材映射、自动内存与旧偏好、独立覆盖继承、未知字段/备份、已有中文目录/外部显式映射、HTTPS 策略和并发/限速取消。测试在提交前的最终工作树运行，控制记录 HEAD 为前一个提交 `d46ad675`；最终受测改动随后提交为 `60df0fe4`。两个最终平台构建来自干净 `60df0fe4`。

首次测试失败保留：`r15-desktop-expanded` 中 `All39LegacyIconsRenderOfflineWithExactNumericAliases` 一项失败（157 通过）。原因是 headless 平台没有原生绘制后端，导出的是相同占位像素；不能用它证明 39 个图标渲染。改为检查真实素材字节、精确映射，并用 Windows Skia 实际保存钻石/钻石剑验证渲染。原失败输出未删除，见 [首次失败](../../artifacts/windows-main-20261105/reports/r15-first-failure.json)。

`r15-macos-cross`、`r15-tools-settings-rc2` 曾被 35 GiB 磁盘准入门禁在构建前拒绝，未启动编译；核对后仅清理已成功任务的可再生成 bin/obj/publish 中间目录，保留首次失败、TRX、日志、候选和原生证据，再以新 RunId 构建通过。没有提高预算或覆盖全局 SDK。最终候选与证据审计见 [交付审计](../../artifacts/windows-main-20261105/reports/r15-final-delivery-audit.json)。

收尾审计：任务目录 34.570 / 35 GiB，磁盘剩余 278.504 GiB；构建互斥可用，公开证据与本轮文本报告未命中构建环境的敏感值。截图包仅包含经视觉核对的 PNG、逐页说明与哈希清单。

原生三次运行：RC1 `45944`、RC2 `51640`、重启 RC2 `3372`，均正常关闭 exit0、外层超时 false、stderr 0 字节。没有结束用户已有 WPF/R14 窗口；未遗留本轮自有候选进程。

## 候选与证据

- 产品源码：`60df0fe44ca737fb3807e74df1f2d59121c88fe6`；`win-x64` / `Release` / 自包含；SDK `10.0.403`，Avalonia `12.1.5`。
- [Windows 候选 ZIP](../../artifacts/windows-main-20261105/dist/r15-tools-settings-rc2-retry/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.g60df0fe4.zip)：81061657 字节，268 项。
- 候选 SHA-256：`1c5e69836d992c1880f6b25754f80db12243c553426ff5af814c00dbe931e67f`。
- EXE / CoreCLR / Skia 均为 AMD64；无 `.env`、账号、日志、用户实例、缓存、测试夹具等敏感/非交付目录，图标许可已随包保留。构建读取已授权环境文件，仅报告配置是否存在，不输出配置值。
- [逐页截图与说明](../../artifacts/windows-main-20261105/public-evidence-r15/README.md)：12 页分组，18 张窗口截图、2 张实际导出 PNG，逐文件注明 RC1 `d46ad675` / 最终 RC2 `60df0fe4`，不把早期候选冒作最终截图。
- [截图 ZIP](../../artifacts/windows-main-20261105/SHOU-UI-Evidence-R15-20261107.zip)：2781751 字节；SHA-256 `a1e2b452501595828c106e722f23c4c3024147ca9f7902227d176ac544d2db28`。
- 实际另存钻石 PNG SHA-256：`ccb8c5c187ef6416a9f6b488d34fc48da4270d825f23d15ef6d56c3260e1c3ff`；钻石剑 PNG：`aecb5ab5621479e9341182f8dfec28e31712b08b62abd72761e1dd9f8e456c66`，均为 640×112。

## 未完成与下一阶段

完成项已在 [todo.md R15](../../todo.md) 勾选，P10/P12/P22/P50–P52 按实际范围更新，未将整页迁移全勾选。仍有以下边界：

- Mac 原生内存探测、文件选择器/Finder、缓存与 PNG 保存、已有实例及设置复验；Windows 100/125/150/200 全矩阵、跨屏、无障碍和 SMB 中断。
- 旧版渲染器/兼容高级选项、单文件多段连接、完整下载源编辑和更多网络失败/回退原生状态。
- 实例组件/加载器修改、启动脚本导出、服务器状态/快捷连接、模组关系诊断等尚未补齐；存档/截图/资源列表可读不替代完整编辑、回收、排序、更新和失败恢复。
- 实际游戏窗口标题/优先级/IP/内存/可见性、模组和资源效果、原生多人连接等没有本轮游戏证据。继续遵守默认不启动游戏的约定，当前任务明确要求时才做游戏验收。

旧 WPF、旧数据和默认入口保留。源码、候选、报告和截图仅在本机，没有推送、合并或正式发布。

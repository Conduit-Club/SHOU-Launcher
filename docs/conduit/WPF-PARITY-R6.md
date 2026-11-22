# 跨平台迁移 R6 与 Windows 接手检查点

2026-11-05。用户已确认暂停 Mac 侧新增开发，转由 Windows 完成剩余 UI 与功能迁移，并明确授权本地提交、推送当前功能分支。此次仅推送 features/wpf-parity；未合并 dev/main、创建或合并 PR、打标签或正式发布。

## 已验证的候选

实际双端验收包为 R6 RC3，产品源码 b04b51a8802e887e9a5cce9438fba5ddff0eeb1b，干净 detached 构建树，source_dirty=false。.NET SDK 10.0.403 / Avalonia 12.1.5 / Release / 自包含多文件。

| 平台 | ZIP | 字节数 | SHA-256 |
|---|---|---:|---|
| macOS arm64 | SHOU-Launcher-osx-arm64-v1.5.0-dev.gb04b51a8.zip | 48224172 | e87c57fe3045d0fbd0a8f8c60091129b36ee38be71041b1d884b60130f4e0cf6 |
| Windows x64 | SHOU-Launcher-win-x64-v1.5.0-dev.gb04b51a8.zip | 78103814 | 92368674529d7321cb176d9d269e203f6a48f762bb02e389f44546011c2894d0 |

Mac 本地位置：.shou-dev/dist/WPF-PARITY/r6-rc3/ 下的各 RID 目录。Mac ZIP 重新解包后通过 codesign --verify --deep --strict；签名仅为本地 ad-hoc，未公证。Windows 候选未正式签名，通过 SMB 3.1.1 传送并重新校验哈希，位于 Laptop 的 artifacts/mac-validation-r6/candidates/r6-rc3/。

最新接手产品提交为 69fe954（完整 SHA 以 Git 为准），额外修复新配置默认隐藏首页两块社团卡片的问题；显式保存的隐藏偏好仍保留。此修复完整 Desktop 回归通过，但用户随后暂停开发，因此没有构建或声称存在 RC4 原生候选。Windows 应先补验这个新配置默认状态。

## 本轮改动

- 移除首页常驻的大块“账户与安全存储”；动态安全状态保留在现有账户控件提示中，认证与账户管理能力保留。
- Mac 原生关闭/最小化/全屏按钮位于左侧，品牌、Logo、标题右置；Windows 保留左侧品牌。账户悬浮工具条、选择配色和服务器勾选向正式参考图靠齐。
- .zip/.mrpack 可以真实拖入窗口，先预检、显示清单与限制，再由用户明确确认创建独立实例。
- CurseForge 客户端 ZIP 支持 vanilla/Fabric，精确核对项目/文件身份、类别、大小及 SHA-1；资源包放入 resourcepacks。无第三方下载地址时，仅接受 Modrinth 独立发布的同 SHA-1、同大小文件，并额外核验 SHA-512，不猜测 CDN 链接。
- 归档文件仅作为清单指定的必装历史依赖按官方提供的 URL 下载；拒绝已拒审、恶意、删除状态及无官方地址的归档项。
- 仅文件下载可以在既有官方 CDN 白名单内手动跟随最多三跳；API 仍拒绝重定向。保留取消、校验、暂存事务及安全错误上下文。
- 导入后尚未准备游戏本体的实例，现在可以一键准备游戏/加载器并启动；失败或取消不会继续启动。
- 首页去除内部验收用语；新配置默认显示社团介绍和服务器卡片，不覆盖已有明确隐藏偏好。

## 实际验收

完整 Core：345 通过、3 项显式跳过、0 失败；完整 Desktop：115 通过、0 跳过/失败。编译零警告、零错误。Core 三项跳过为需要显式启用的真实 Java/信任库集成测试，不与本轮原生游戏观察混算。最后默认显示修复只改 Desktop 与其测试，Core 源码和测试未变。所有首轮失败、修正与重试记录保留，不累加重复执行的数量。

| 样本 | Mac | Windows |
|---|---|---|
| Fabulously Optimized 6.5 MRpack | Finder 真实拖入；113 文件、48 Mod JAR、3 资源 ZIP，哈希/大小通过；RC2 一键首启、主菜单、Mod Menu、隔离超平坦世界渲染/保存、Quit exit0 | RC1 原生文件选择器导入 113 文件；RC2/RC3 复核原清单和文件不变 |
| Fabulously Optimized 6.5 CurseForge ZIP | RC3 Finder 真实拖入；113 文件、48 Mod JAR、3 资源 ZIP，逐文件双哈希/大小通过；一键首启、主菜单、Mod Menu、隔离超平坦世界渲染/保存、Quit exit0 | RC3 实际导入；113 文件、48 Mod JAR、3 资源 ZIP，独立官方元数据与落盘哈希/大小核验通过 |
| Fabulously Optimized 6.4 CurseForge ZIP | 未另做实际安装 | RC3 实际导入；74 文件、47 Mod JAR、3 资源 ZIP，核验通过 |

Mac 两次游戏均为 Minecraft 1.21.1 / Fabric 0.19.3 / Java 21.0.13+9 arm64。日志加载 151 个模块（含嵌套依赖），主菜单显示 44 Mods，Mod Menu 显示 41 个可列出项；这几种计数含义不同。日志保留了上游版本提示、配置补全及 Steam Deck 检查等警告。没有把短时测试当作任意整合包、长时间游玩、多人或光影兼容保证。

Windows 为 Laptop / Windows 10 Pro 19045 x64 / 200% DPI。R5.1、RC1、RC2、RC3 均实际运行自包含启动器；四项导航、976×636 / 880×560 DIP、最小化/恢复、真实关窗 exit0 有独立 UIA/Win32 证据。RC3 运行约 571 秒后正常关闭，无残留窗口。Windows 本轮没有启动游戏、安装 Java/SDK 或进行真实在线认证。此前隔离 DPAPI 写/读/删除、相同数据根互斥已通过，不代表旧 WPF 凭据兼容已通过。

原生 CUA 遇到窗口绑定/截图/坐标缺失后，用户分别明确授权本轮限定窗口的官方系统接口。Mac 仅操作候选、测试游戏和样本 Finder；Windows 仅操作候选/原生文件选择器。未绕过系统登录或自动批准权限。

## 尚待 Windows 处理

- 用户指出新版 Windows 右上角缺少最大化按钮：现有截图确实只有最小化与关闭。完整窗口行为及图标/字体基线应优先修正。
- RC3 Windows 首页卡片因新配置默认 false 被隐藏；69fe954 已改默认 true 并验证缺文件、空 JSON、单字段与显式 false 保存重读，原生补验待 Windows 继续。
- 按新产品方向移除启动器联机大厅/EasyTier 等专属模块；保留游戏原生多人、服务器数据、认证、下载与代理。
- 将已有 Java 证书检查/独立修复副本能力增加到工具入口，复用服务，不改全局信任或源 Java。
- 完整 54 类视觉/业务对等、未完成加载器/整合包类型、高级设置、世界/数据包/服务器工作流、在线外观与全文语言等仍需逐项登记。
- Windows 真实游戏/正版登录、旧数据往返、旧凭据迁移与旧新入口互斥；这些门禁通过前保留旧 WPF 工程与默认入口。
- Mac 的公证、Windows 正式签名、合并 dev/main 与正式发行均未执行。

## 证据与交接

cleanroom 参考仍为 132 张正式 PNG 和去源码映射的可观察说明，原归档 SHA-256 为 695784a4ca98f6079763220f2971ce8b78fc3ee71fd187298b4f39c27cea6615。未向实现 agent 提供旧 WPF 视图/样式代码、结构映射或采集脚本。

本地完整记录位于 .shou-dev/reports/wpf-parity-r6/，含 TRX、构建信息、样本哈希、Mac 窗口截图、游戏日志、Windows 逐文件官方校验及原失败链。Windows 原始报告位于 D:\Code\C#\PCL-CE-SHOU\artifacts\mac-validation-r6\；Mac 副本的 Windows/ 子目录通过 SMB 逐文件校验。忽略目录中的原始证据、候选和凭据不随 Git 推送。

只回收了 RC1/RC2 已停止的可重复解包副本，保留对应 ZIP、哈希、实例、参考和报告，以维持既有 35 GiB 任务预算。原 .env、未跟踪计划及 worker 的旧共享前置改动保留。Mac 后台候选、游戏和重任务均已结束。

Windows 接手任务见 [WINDOWS-HANDOFF-R6.md](WINDOWS-HANDOFF-R6.md)。归档依赖策略依据：[CurseForge 官方文件类型说明](https://support.curseforge.com/support/solutions/articles/9000197242-file-project-types-and-additional-fields)；该支持说明允许归档文件作为包/Mod 依赖，REST 的 isAvailable=false 与归档状态组合按严格限定范围解释。

# R55：Desktop 发行流程与验收收尾

本轮按用户要求切换双端发行、补 dev/main 受信构建，并准备 `v1.6` 预发布。程序版本为 **1.6.0 / xb**；延续 `features/windows-parity`，不合并 PR、不修改 main/dev，不启动 Minecraft。

## 发行链

`Conduit Desktop Release Draft` 校验标签与源码，复用 `Shared Desktop migration` 的 Mac ARM64 / Windows x64 宿主和相同 Publish/Validate 入口，Windows 额外产出 ARM64 单 EXE。工作流合入相应 dev/main 后，其 push 也走受信签名与凭据专项；PR 不注入 Secrets，临时签名验证继续保留。旧 WPF 回归仍运行，其包不再用于新版发行。

正式版仍要求 `origin/main`、Release、精确三段版本标签；显式预发布可使用用户指定的 `v1.6` 短标签。标签必须指向实际构建源码，不允许混入本地未提交内容。新版收集器只读取 artifact 的 `dist`，先验证三个 RID、版本/代号/源码/模式、SHA-256、Windows PE 与单文件负载、Mac 固定组件与内外构建信息，全部通过后生成原子化的资产目录和统一清单。先建草稿，不自动公开或覆盖已有发行。

## 本机验证

- 发行脚本真实 PowerShell/Git/ZIP 夹具 **21/21**；包括短标签必须显式预发布、main 稳定策略、Actions 嵌套目录、报告假包忽略及拒绝混合源码/缺平台/配置/错误 PE/散落 DLL/变化组件/不一致元数据。不修改真实仓库标签。
- Desktop 完整 Headless **374 通过、3 跳过**（两个 Windows 平台布局用例、一个需显式启用的原生探测），零失败；C# 编译 0 警告/0 错误。9 项纯策略回归覆盖非 Mac 不加载/等待、健康不延迟、瞬态恢复、持续失败有界、错误码脱敏。
- 显式 CoreVideo 探测在锁屏期间 **失败：Create/-6661**；用户手动解锁后复测 **10/10 通过**。失败和成功报告分别保留，不能用后者抹去前者或推定历史 R46 同一根因。
- 新候选以本机 `.env` 明确注入，MS/CF/SHA 三项布尔核验通过；固定凭据组件整文件哈希不变，签名验证通过。此提交前 QA 包的 metadata 明确为 dirty，不用作标签发行资产。
- CUA 已观察新候选隔离首页（账户 0）、设置/软件信息 **1.6.0 / xb** 和原生窗口；Cmd-Q 正常关闭，报告 `normal_close`、`nativeWindowHandleObserved=true`、CoreVideo 一次成功。

## 两项验收更正

1. **Windows 三按钮误判**：重新查看 R50 完整 1952×1272 的 `windows-screenshots/final-mods.png`，右上角最小化、最大化和关闭确实可见。未按误判修改正常标题栏；新增 Windows 专用 880/976 宽度、超长实例名的按钮可见性/边界回归，实际执行由 Windows CI 确认。
2. **个人旧账户回读**：R53 个人安装已在授权后退出重开，两个旧微软项均为持久化已保存，无恢复面板；新 R55 QA 普通窗口也已读到原选微软项和原 XPlus 实例。CUA 绑定发生在异步隔离启动完成前，额外打开了同候选普通数据窗口，核对后已正常关闭，再单独绑定隔离窗口。此过程未录入密码、删除账户或启动游戏。HTTP401 外观响应是过期在线令牌，不等同钥匙串访问失败；最终已提交包的替换回读另列。

## Mac 原生启动

依据 Avalonia 12.1.5 的 [托管渲染时钟](https://github.com/AvaloniaUI/Avalonia/blob/12.1.5/src/Avalonia.Native/AvaloniaNativeRenderTimer.cs)、[macOS RegisterTick](https://github.com/AvaloniaUI/Avalonia/blob/12.1.5/native/Avalonia.Native/src/OSX/PlatformRenderTimer.mm) 和 [平台初始化](https://github.com/AvaloniaUI/Avalonia/blob/12.1.5/src/Avalonia.Native/AvaloniaNativePlatform.cs)，使用同一公开 CoreVideo Create/SetOutputCallback API 在 Avalonia 一次性初始化前探测。健康路径不等待，瞬态错误最多等待约 1.75 秒；不重用半初始化 AppBuilder，不反射替换 Avalonia，不将软件绘图误作原生显示时钟的替代方案。Windows 路径立即返回，不加载 Mac 框架或等待。

持续失败报告只添加阶段、次数与整数原生状态；不复制原始异常消息、参数或令牌。R46 历史报告未保存原生错误码，故**根因仍未确认**，此版属于恢复缓解和诊断补齐。

## 云端结果与剩余边界

代码提交 `d61458e7e33519181d3c20fccb0305f60a613552` 的 [受信 push](https://github.com/Conduit-Club/SHOU-Launcher/actions/runs/43584210751)、[双端 PR](https://github.com/Conduit-Club/SHOU-Launcher/actions/runs/43584216880)、通用与 WPF 回归全部通过。Mac Core 941 通过/7 跳过、Headless 374/3；Windows Core 931/11、Headless 376/1（两项新标题栏用例实际通过）；各套分列，不累加重复检查。固定凭据组件 10 项、真实包更新/故障 17 项通过。

已创建并推送精确 `v1.6` 注释标签（tag object `08589f7a64f95d2c82fb314717dea8094bb348c6`），指向上述源码；[标签发行运行](https://github.com/Conduit-Club/SHOU-Launcher/actions/runs/43584891437) 四个 job 全部通过，先生成 Prerelease 草稿，经资产与最终个人回读核对后已[公开预发布](https://github.com/Conduit-Club/SHOU-Launcher/releases/tag/v1.6)，未设为 Latest，稳定 Latest 仍为 v1.5.0。13 个资产齐套，三包的 GitHub SHA-256 摘要与统一清单、单包旁置校验均一致；Mac ZIP 本机 CRC、内外元数据和固定组件整文件再次一致。标签版 Mac 包已安装到 `/Applications/SHOU Launcher.app`，旧程序和本机 JSON 配置备份位于 `~/Library/Application Support/SHOU-Launcher/backups/app-update-r55-20261114-182917/`；安装前后配置字节一致，实例目录未移动。

| 包 | SHA-256 |
| --- | --- |
| osx-arm64 | `e73f3f88aeb03db4ae534cd49951e2fb013a0d83339a251c3908c14c8f351fb4` |
| win-x64 | `1c38cedd3702f1de9a718256e5a80c21eb9d2d2ff1c015c65d03e0723327a2e2` |
| win-arm64 | `3ee8ee8e1697cde24055513a5688b8c6638002ca99e831d073c9542978a18a9a` |

最终标签包安装后，桌面恢复可用，CUA 已核对两个旧微软项持久化已保存和原 XPlus 实例；Cmd-Q 正常退出重开后再次核对两项均自动回读，无恢复面板或新的钥匙串密码请求，软件信息为 1.6.0 / xb。未更改所选账户，窗口保留在首页。HTTP401 外观提示仍属过期在线令牌，本轮没有刷新登录。普通绘图 6 次、按实际 `local/shou.desktop.v1.json` 启用的软件绘图 6 次，共 12 次冷启均建立原生窗口并按时正常退出，CoreVideo 均一次成功。首轮脚本写错配置路径的所谓“软件”结果已标记无效，不计入这 12 次。Windows ARM64 真机、完整 DPI/跨屏/VoiceOver、旧格式/provider 游戏矩阵继续按 todo 留存。本轮原生与凭据检查不启动游戏，也不替代在线令牌刷新验证。

详细本机证据保存在 `.shou-dev/reports/macos-r55-release/`；最终标签产物源码、SDK、RID、Release 配置及 SHA-256 以随包 BUILD-INFO / RELEASE-MANIFEST 为准。

本轮源代码与流程提交为 `d61458e`；`ef3b4ed` 及后续交付文档提交只更新运行证据与待办，不移动已验证的 v1.6 标签。PR #15 保持打开、未合并，main/dev 引用未变；用户原计划文件和 .env 保留。

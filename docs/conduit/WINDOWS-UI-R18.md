# R18：CurseForge 导出与跨平台加载器文件安装

本轮继续 `features/windows-parity`，产品源码提交 `acad31a4`，最终中文导出标题修正 `da10e1aa`。没有推送、合并 dev/main、发布或更改 Windows 默认入口；原工作区与旧 WPF 保留。**用户明确禁止本轮启动游戏，所有安装、回导和进程验证均未启动 Minecraft。**

## 仍未接齐的功能，供下一轮优先选择

| 范围 | 具体缺口 |
| --- | --- |
| 历史加载器与组合 | Forge 1.13 以前的旧式安装器；LiteLoader 1.12.2 等历史 Jenkins 地址可达性；其它历史版本；Forge+OptiFine 等组合选择/安装。当前同一实例一次选择一个加载器，不能把独立 OptiFine 安装算作组合已实现 |
| Java 与平台 | Mojang 没有本机 windows-x64/jre-legacy Java 8 工件，当前需在 Java 设置明确选择兼容 Java 8；本轮使用隔离的官方 Temurin JRE8 验证，没有把备用自动供给接入产品。历史 LWJGL ARM64 原生库没有可信切片时继续拒绝，不伪造兼容性；Mac 原生安装/回滚全部待验 |
| 第三方安装器边界 | 需要认证的自定义 HTTP 代理尚不支持；LabyMod/LiteLoader 安装器自身缺少上游文件哈希，仍仅采用官方 HTTPS、大小/JAR 结构与本地 SHA-256 收据，不能把客户端哈希冒充安装器官方哈希 |
| 实例修改与导出 | 新实例/包导入事务已补；已有实例切换组件的全部中后期失败时点、共享实例多会话写保护和 Mac 原生仍需补验。启动脚本导出、明确停止脱离 UI 的游戏会话仍未齐。CF/MRpack 对另外五种加载器没有声明可互认的标准身份，使用 SHOU 往返；第三方启动器互认没有实测 |
| 数据包与世界安装 | 数据包下载→选择具体存档→兼容/冲突预检→安装的完整页面链未齐；CF 世界检索/下载已接，但解压并安装到明确存档目标的完整链未齐 |
| 资源管理 | 模组完整更新/批量操作、依赖和冲突关系图、手工 JAR 来源身份；资源包排序/启用/更新；光影兼容/启用/预览；投影原理图解析/材质尺寸预览，以及资源详情与收藏的完整字段/异常状态仍有缺口 |
| 外观与通用设置 | 在线皮肤上传/披风穿戴仍未接为启动器内写操作；旧渲染器/兼容高级选项、全局动画帧率、单文件多段下载、完整来源编辑体验尚未齐 |
| 其它页面与验收 | 内置反馈提交/附件、更多日志检索跳转；Mac 原生、全部 DPI/跨屏/键盘/无障碍、网络/SMB 中断和更多旧配置往返矩阵。已有代表用例通过不代表整个页面和全部平台门禁完成 |

七语补全、视频背景、自动更新和完整许可清单 UI 已按用户决定排除，不能算成已实现功能。中文、图片轮播、设置页手动检查版本与浏览器下载继续保留。更细的未完成项见仓库根 `todo.md` 的 P01–P65 与 G01–G08。

## 本轮接入与修正

| 项目 | 最终行为 |
| --- | --- |
| CF 导出 | 实例页新增 CF 导出按钮，与 MRpack 共用名称、版本、配置和本地资源的显式选择。CF 已登记文件须经官方项目/文件 ID、路径、大小和哈希核对后写入 `manifest.json` 引用；未知来源不猜 ID。本地资源只有明确选中后才进入 overrides |
| 格式身份 | CF/MRpack/SHOU 支持原版、Fabric、Quilt、Forge、NeoForge；完整保留带连字符的版本号。SHOU 另外支持 Cleanroom、Legacy Fabric、OptiFine、LabyMod、LiteLoader。历史 SHOU v1 缺精确清单仍拒绝 |
| 下载与导入 | 下载页使用完整准备后才登记的新实例事务；整合包预检/确认后，可显式勾选“同时下载并安装游戏文件”。默认仍只导入，不隐式启动游戏；失败和取消不注册半成品 |
| 共享目录 | 遵循全局隔离策略；只发布 `.shou`、旧资源映射和 LabyMod 必需附属文件，已有冲突不覆盖。登记失败恢复原安装标记并移除本轮未被外部修改的新增文件，保留用户内容 |
| 安装目录与页面 | Minecraft 配置页补八种附加载器，加 Fabric 共九个带图标横条，自动读取版本与局部加载；版本显示与内部精确身份分开。取消重试不再保留上次安装成功入口。共用导出区标题改为“客户端整合包（Modrinth / CurseForge）” |
| 官方安装处理器 | Forge 接受官方 `ForgeBootstrap` 新主类；Cleanroom 采用官方无界面 client CLI、独立目录、不写官方启动器配置、不自动配置 Java，核对 1.12.2/Java25/基础客户端身份。Legacy Fabric 使用自身官方 metadata/Maven，补旧 Maven native 形式，仍要求仓库校验值 |
| 厂商安装适配 | 受信 bootstrap JAR 内增加安装专用桥接，编译目标 Java8。OptiFine 只调用安装 API；LiteLoader 提取官方内嵌元数据和运行库；LabyMod 只调用数据/文件/ManifestWriter API，避开会关闭用户官方启动器的 `VanillaInstaller.install`。不执行厂商 GUI/main 或游戏入口 |
| 收据与复用 | 精确绑定 loader/game/version，复核安装器、配置、运行库和附属文件哈希；完整收据可离线复用，不先依赖 LabyMod 在线最新版。LabyMod 运行库按完整 commit 分开缓存，避免稳定/快照渠道互相覆盖 |

共享业务在 Core，共用 Avalonia 页面接线；没有新增 WPF fallback 或硬编码 Windows 产品目录。Windows 路径只出现在本机验收控制脚本中。保留平台凭据、代理、数据根互斥、未知配置字段和写前备份。

## Windows 真实文件安装与往返

每条均完成必要文件下载/处理、运行库与客户端准备、重复安装收据复用、表中格式回导后再次完整准备；测试载荷为一份明确的公开 config 夹具。没有账户认证、生成游戏启动计划或启动 Minecraft。

| 加载器 | 实际版本 / Minecraft | 往返格式 |
| --- | --- | --- |
| Quilt | 0.30.2 / 1.21.1 | CF、MRpack、SHOU |
| Forge | 52.1.21 / 1.21.1 | CF、MRpack、SHOU |
| NeoForge | 21.1.265 / 1.21.1 | CF、MRpack、SHOU |
| Cleanroom | 0.7.14-alpha / 1.12.2，Java25 | SHOU |
| Legacy Fabric | 0.20.1 / 1.12.2，真实 Java8 x64 | SHOU |
| OptiFine | J1 / 1.21.1 | SHOU |
| LabyMod | production-49-c228c3a3 / 1.21.1 | SHOU |
| LiteLoader | 1.7.10_04 / 1.7.10，真实 Java8 x64 | SHOU |

LabyMod 本次上游主 JAR 声明大小比实际少 13 字节，但官方 SHA-1 精确一致。只在匹配官方摘要且实际大小受 128 MiB 限制后，为该厂商生成的配置规范化大小；没有放宽 CF/MRpack 的精确大小/哈希校验。安装器和缓存输出另记 SHA-256。

官方 CF 引用实测：公开项目 306612、文件 8786256 导入→仅引用导出→独立回导，最终文件 SHA-512 一致，导出 ZIP 仅含 manifest，不重复嵌入已知 CF JAR。API 凭据只交给当前项目官方 API 主机，没有输出值或交给 CDN。

完整 XPlus 资源实测使用 R17 独立副本，仅打包 mods/resourcepacks/shaderpacks。回导 **717 个资源文件**，逐项 SHA-512 与大小一致；**718 个源文件**再次核对均不变，一份 `.gitignore` 属排除元数据。没有复制原用户账户、存档或完整缓存，也没有再启动该实例。

| 归档 | SHA-256 |
| --- | --- |
| 官方 CF 引用往返 | `98c0bc50cc3a3a468c0555440a6febc67bad8be5935a7484886929fca2c455b8` |
| XPlus 完整资源 CF 服务往返 | `bcf3d9b202ef18ab43dfa838e77b4d9b8b20b50e22265713b5565890aa265364` |
| 原生按钮实际生成的 717 文件 CF 包 | `5add7bb631304a9c4f66f1d3d699d0e226dac80e93f32b88333b9d3c732e7294` |

## 取消、失败和安全边界

- 最终清洁源码执行原版、Fabric 及上述八种加载器的 **48 项**新安装/适用包格式取消与受控失败。中断发生在实际客户端文件安装请求，核对内存实例、磁盘配置 SHA-256 和实例目录集合不变，暂存目录为零。不是伪造成功，也没有把完整可复用共享缓存删掉。
- 另一次真实 Forge JVM 处理器启动后取消：拥有的子进程被取消并等待清理，原 profile 字节不变，没有发布完成收据，暂存为零。
- 受控 Java 安装桥接覆盖成功、目录/版本不匹配和 JVM 中途取消；夹具专门设有游戏 main 哨兵，确认未被调用。新实例失败/取消、坏客户端哈希、状态保存失败、共享目录冲突/原标记恢复均经真实文件系统测试。
- CF/MRpack/SHOU 导出在临时归档已创建后取消，临时归档删除、源文件哈希不变；坏归档哈希和官方 CF 身份不匹配均拒绝。保留数量、大小、物理路径/链接、防覆盖、精确 SHA-1/SHA-512 与平台原生库守卫。
- 有界子进程最长 8 分钟、处理暂存预算 2 GiB；清除安装子进程继承的凭据类环境变量，隔离 home/APPDATA/XDG，不关闭 TLS。外层控制器超时只清理自有进程树。Java8 工具放入本轮 tools，不改全局环境、不要求管理员权限。

48 项覆盖公共文件阶段，真实处理器中途取消覆盖 Forge，桥接中途取消另有受控夹具；不能据此宣称所有厂商处理器的每个晚期失败时点均原生验证。已有实例组件修改的完整晚期回滚仍列待办。

## 回归、原生窗口与首次失败

- **Core：474 通过 / 16 显式跳过 / 0 失败，总计 490。** 16 项仍为 4 项 Mac 原生/Keychain/argv、8 项待独立 Windows 原生夹具的链接/重解析、1 项 POSIX 失败子进程、1 项未启用在线 Java、2 项未启用受控 Java 修复副本。逐项名称/原因保存在 `r18-acceptance-summary.json`，不以跳过代替通过。
- **Desktop：174 通过 / 0 跳过 / 0 失败。** 覆盖九加载器自动读取/图标横条、不隐式选择、事务路由、取消后清除旧成功入口，以及 CF 按钮的共享服务与显式选项。最终文案提交后完整 Desktop 再跑仍全通过；Core 实现未再变化。
- Windows 原生 RC1：1952×1272 像素正常窗口，空账户首次微软登录表单，社团在线数读取为 1；Minecraft 自动目录、局部加载和带图标附加载器横条可见；真实鼠标选择本地资源并点击 CF 导出，717 个文件另行核验；鼠标关闭 exit0。
- 最终 RC2：重新打开同一隔离数据目录，进入实例导出页确认两格式共用标题、按钮和布局；正常窗口关闭消息结束 exit0。该轮鼠标关闭尝试被 Windows 置前台限制拦住，未继续盲点，正常关闭结果有应用自身 `normal_close` 记录。
- Computer Use 两次截图超时；按既有授权使用本机备用截图和输入，操作前检查目标进程/窗口/前台与锁屏，只导航和导出，不触碰认证或游戏。截图仅捕获本轮零账户的应用窗口。不是完整 DPI/最小窗口/Mac 原生矩阵。

首次失败均保留在本机 reports/controller/TRX，不覆盖历史记录：

| 首次失败 | 处理与复验 |
| --- | --- |
| Core 旧测试仍断言 Forge MRpack 不支持 | 改为真实未知 loader 的拒绝用例；保留身份校验，最终 474/16 |
| 新共享事务 5 项夹具失败 | 受控资源 handler 漏掉已配置镜像路径，补精确哈希路径覆盖，未删除断言；共享目录成功、冲突与保存失败回滚通过 |
| 收据测试编译两次失败 | 内部可见性及漏引用，仅修测试夹具；最终全部通过 |
| Desktop 旧计数 4 / 实际 9 | 更新为九种加载器的精确名称、图标、横条宽度断言；最终 174/0 |
| Forge 新主类、Cleanroom CLI 不同 | 核对正式安装器元数据/帮助，增加精确官方主类和 Cleanroom 专用参数后真实安装通过 |
| Legacy Fabric 缺 Java8、旧 Maven native 没 classifiers | 明确选择隔离真实 Java8，增加 Maven native 解析但不跳过仓库校验，实际完整安装通过 |
| OptiFine 安装 API 提前 exit0、Laby 大小/配置形状与预期不同 | 以唯一且严格验证的实际生成 profile 为准；修正精确厂商形状，Laby 只在匹配官方摘要后规范化大小，三种厂商完整安装通过 |
| XPlus 验收最初只数顶层、路径分隔符未规范化，后又包含 .gitignore | 修正验收范围/重用已接入实例，明确单项元数据排除；最终 717 回导与 718 源文件哈希核对通过，没有放宽产品归档过滤 |

## 源码与候选

| 提交 | 内容 |
| --- | --- |
| `acad31a477263546d0d052f28c8a914b352ab18c` | CF 导出、跨格式身份、完整加载器准备、事务发布、桥接及回归 |
| `da10e1aaae1d2bf40d61aec217b94a5572503735` | 共用导出区中文标题和准确支持范围；最终候选源码 |

最终候选为 **win-x64 / 自包含 / Release**，SDK **10.0.403**、Avalonia **12.1.5**，清洁源码构建：

`artifacts/windows-main-20261105/dist/r18-final-rc2/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.gda10e1aa.zip`

- 大小 **81,520,254 字节**，268 项；SHA-256 **`81f6a86f0659d88802f6495eeee5835081352619350ed8e96c19c2c3c13da366`**。
- apphost、CoreCLR、SkiaSharp、HarfBuzz 的 PE 架构均为 AMD64 `0x8664`；必要许可与归属保留。
- 候选不包含 `.env`、账户/凭据目录、实例/缓存/日志、验收夹具或自动更新清单。构建入口验证 Microsoft/CF 项目配置存在，仅输出布尔结果。没有导出用户 token、Cookie 或完整账户缓存。
- 同源码 `r18-mac-final` 的 **osx-arm64 交叉构建通过，0 错误**；没有可用 Mac，本轮没有 Mac 原生候选运行或原生安装验证，不能算两端替代门禁通过。
- 本轮按 35 GiB 任务预算串行重任务；中途保留首次失败、TRX、候选和报告，仅清理已关闭控制器登记的生成 bin/obj。最终任务占用 26.725 GiB、私有验收目录正增长 1.945 GiB，共 **28.670 GiB**，剩余磁盘约 280.375 GiB；本轮候选与 Java 进程均为 0。记录在 `r18-budget-final.json`、`r18-process-final.json`。

复现入口使用 `scripts/dev/Invoke-Dev.ps1` 包装 Core/Desktop 的 `Validate-Desktop.ps1` 和 `Publish-Desktop.ps1`；不构建整个旧解决方案加 RID。Windows 本地验收使用 `artifacts/windows-main-20261105/control/r18-real/` 的隔离 harness，`StartAsync` 明确拒绝游戏，仅允许受限无界面安装/探测。最终官方 CF、717 文件往返和 48 项回滚在清洁 `acad31a4` 上复验；RC2 仅修改上述两条文案。

主要证据位于 `artifacts/windows-main-20261105/reports/`：`r18-acceptance-summary.json`、`r18-real-cf-reference-final.json`、`r18-real-payload-final3.json`、`r18-real-rollback-all-final.json`、`r18-real-processor-cancel-suite.json`、`r18-native-export-audit.json`、`r18-candidate-rc2-audit.json`、两轮 native/exit 记录和 `r18-rc2-export.png`。完整首次失败保存在同目录各 r18 run 日志，以及对应 build reports 的 TRX。

非敏感证据另存为 `artifacts/windows-main-20261105/dist/r18-evidence/SHOU-R18-Windows-evidence.zip`：159 份报告/日志/代表截图/TRX/控制源码，加 SHA-256 清单和说明；不包含 `.env`、账户缓存、JDK、游戏/模组载荷或原始游戏日志。大小 **1,044,125 字节**，SHA-256 **`b9553686f355896c61c7291fff2b8ed9964232d28f743355f3883400efad1e89`**；逐项解包哈希复核通过。

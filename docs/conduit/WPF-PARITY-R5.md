# Mac WPF 对等迁移候选 R5

2026-11-05。本轮继续三个既有功能分支与 cleanroom 分工，仅本地分阶段提交。没有推送、合并 PR、打标签、发布、删除 WPF 或切换 Windows 默认入口。完整替代 WPF 的门禁尚未通过。

## 候选与来源

- 产品提交：`0a1110e582a9212c2c16c7d5dc8ed6bc5172e7e0`，干净 detached 工作树构建，`source_dirty=false`。
- 本地包：`.shou-dev/dist/WPF-PARITY/r5/osx-arm64/SHOU-Launcher-osx-arm64-v1.5.0-dev.g0a1110e5.zip`。
- 大小：48,184,637 字节。SHA-256：`c3e33993008d2e171ff04064a43ee33096d83c3b1a13e4ab6d4e7eca0e4cb6e6`。
- .NET SDK 10.0.403 / Avalonia 12.1.5 / Release / osx-arm64 / 自包含多文件；本地 ad-hoc 签名，未公证。
- ZIP 重新解包后通过 `codesign --verify --deep --strict`；运行环境移除 SDK/PCL 构建输入与工具 PATH，程序目录没有 `.env`。

参考仍为 `UI-Reference-WPF-20261105.zip`，SHA-256 `695784a4ca98f6079763220f2971ce8b78fc3ee71fd187298b4f39c27cea6615`。实现只使用已去源码映射的可观察说明、132 张正式 PNG、新 SHOU 实现及官方协议/工具；未读取旧 WPF 视图、模板或其控件结构 JSON。存档协议另只读核对官方 Minecraft 1.21.1 client 与 mappings 的三个数据类，不涉及旧启动器 UI。

## 本轮行为

- Modrinth 资源可明确选择兼容实例，预检 required 依赖与冲突后安装真实文件，并保存双哈希清单。缓存下载、实例安装各有独立按钮与状态；optional 依赖不自动加入，已有不同文件不会被替换。
- `.mrpack` 支持 vanilla/Fabric 客户端预检、可选文件选择、隔离新实例导入及导出。内容准备和持久化成功后才登记实例；`Installed=false`，游戏本体由首次正常安装/启动补全。下载锁和断点 sidecar 不进入实例；导出只包括清单资源及用户明确选入的本地路径。
- Forge、NeoForge、Quilt 增加官方目录与自动安装接线，保留原版 Minecraft 标识，必须明确选择 loader 版本，与 Fabric 互斥。界面目前仅开放已验证的 Minecraft 1.21.1；其它版本可读目录并明确禁用安装。
- Forge/NeoForge 使用校验后的官方 client CLI 安装器，在独立暂存目录执行；8 分钟超时，取消停止自有进程，运行期间检查整个暂存目录的 2 GiB/10,000 项/24 层限制。主类与父版本正确才提交安装收据，拒绝缓存字面链接与不同内容。处理器内部网络由官方 Java 工具负责，目前不支持带凭据的代理；不声称其全部请求都由共享 HTTP 下载器控制。
- 离线角色支持本地 PNG、Classic/Slim 模型保存、真实头像与可旋转/缩放的 UV 方块模型、外层/披风预览及导出。在线外观只读接口与官网入口已有实现，未执行真实账户线上读写。本地皮肤不会改变原版游戏身份或游戏内皮肤。
- 实例图标、分类、描述、收藏筛选与启动次数持久保存。次数从此版本起统计成功创建游戏进程的尝试，不表示主菜单成功。创建/选择/安装标志均先写盘再发布，失败保留旧状态与未知配置字段。
- 截图支持图片复制、实例内回收与恢复最近删除；同名文件不覆盖。复制以请求序号和串行提交防止旧异步结果覆盖新剪贴板，离页/释放使旧请求失效。此处没有永久删除入口。
- 世界详情增加出生点、世界运行时间；在已选隔离实例、游戏停止且 Java 1.21.1/3955 时，可显式保存作弊、难度及难度锁。数据包格式 48 且无 features 声明时，可批量启停已核实的 `file/*` 包；写前修订校验和原字节备份，未知 NBT 与内建/实验包保留。

## 验证与边界

完整 Core **296 通过、3 显式跳过**；Desktop **96 通过、0 跳过**。编译均零警告、零错误。不累加 worker、重试或重复执行的数量。跳过的是需显式启用的真实 Java 运行时/信任库集成测试；本轮未再次启用。

Core 回归包含真实文件/ZIP/NBT、写盘失败回退、未知字段保持、版本与内容修订冲突、链接/路径/体积限制、错误主类不记成功收据，以及稀疏文件触发处理器整体预算取消。UI 包含实际鼠标打开菜单、3D 拖动/缩放、最小窗口布局、三个剪贴板并发/离页/释放回归及世界草稿取消/冲突。Headless 不代表原生窗口或真实线上认证；剪贴板竞态用真实 PNG 头与转交对象身份验证，不把 Headless 的占位位图尺寸当成真实解码尺寸。

官方实时安装及启动计划：Minecraft 1.21.1 + Forge 52.1.21、NeoForge 21.1.265、Quilt 0.30.2 均通过。最终 R5 ZIP 另移走既有 NeoForge 成功收据，强制重新运行官方处理器，15.3 秒完成，安装/计划 PASS；这不是游戏主菜单证据。

RC1 `0d927f5` 已在 Mac 实窗验证皮肤菜单、原生 PNG 选择保存、真实头像/3D 旋转缩放/复位/Esc、实例描述/分类/收藏、截图复制调用/回收/恢复、本机 MRpack 预检与完整导入；正常关闭 exit0。导入配置字节正确，实例尚未安装游戏本体，且没有混入下载 sidecar。Mac 弹层改在所属窗口内承载，并经真实菜单验证；早期独立原生菜单无法被 CUA 主窗口观察的记录保留。

最终 R5 GUI 首次启动遇到 `AvaloniaNativeRenderTimer.EnsureRegistered`，CUA 随后明确确认 Mac 锁屏。最终世界编辑实窗验收等待用户解锁；原始失败保留，未把它计为通过。CUA 截图仅在对话中显示，不声称已有本地截图文件。外部 Preview 绑定超时，跨应用图片粘贴也未计为通过。

本轮所有重任务使用既有预算/单锁入口，21 项 Git/分支守卫和 4 项生产空间判定边界通过。已有原始计划、`.env`、旧候选、报告和旧工程保留。源码、分支头、TRX、候选、原生操作和未完成项的机器证据位于 `.shou-dev/reports/wpf-parity-r5/`。

## 未完成清单

- Windows 真机、旧数据往返、DPAPI/凭据兼容与双入口互斥；按用户决定先完成 Mac，尚未切默认入口。
- 其它 Minecraft 版本、OptiFine/Cleanroom/LegacyFabric/LabyMod/LiteLoader、独立安装器保存页，以及新加载器主菜单/正常 Quit。
- Forge/NeoForge/Quilt 的 MRpack、CurseForge 包、其它下载托管来源、完整标签与手工 Jar 的项目身份识别。
- 普通数据根的世界/服务器文件操作、其它存档/包格式、数据包更新/删除/排序、游戏内重载、直接进入世界、服务器 ping/快捷连接。
- 真实在线外观读取验收、认证上传与披风穿戴；当前写入入口为官网。
- 完整高级启动选项、全文语言切换、自动更新、工具/联机大厅、内嵌更新历史、全部主题与通用弹窗，以及 54 类页面逐页视觉对等。
- 最终世界实窗验收等待解锁；跨应用图片粘贴未验。

主要一手接口依据：[Modrinth 版本 API](https://docs.modrinth.com/api/operations/getversion/)、[MRpack 格式](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)、[Quilt Meta](https://meta.quiltmc.org/)、[Forge 官方安装器 CLI](https://github.com/MinecraftForge/Installer/blob/2.0/src/main/java/net/minecraftforge/installer/SimpleInstaller.java)。各 worker 的交付 JSON 另记录外观与 NBT 契约依据、原始哈希和首轮失败。

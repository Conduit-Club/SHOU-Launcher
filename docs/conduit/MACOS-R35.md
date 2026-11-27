# R35：macOS 旧版 Minecraft 原生运行兼容

本轮按用户要求恢复 RLCraft 调查，目标为 macOS 上 RLCraft 可玩、McMouser 可用，补丁不得改变 Windows 体验。最终 RC2 已通过本机 RLCraft 2.9.3 / Minecraft 1.12.2 / Forge 14.23.5.2860 的真实主菜单、普通世界创建、创意/生存操作、McMouser、存档读回和正常退出验收；使用原生 ARM64 Java 8，无需 Rosetta。

## 实现范围

- 仅 macOS ARM64 Java 应用已知版本的运行库覆盖，在加载器继承合并后解析。原始 Mojang/Forge JSON 不改写；Windows、Linux、Intel Mac 不应用 ARM64 覆盖。
- 旧 LWJGL 2 bindings/util 与 MinecraftMachina ARM64 natives、JInput Universal、JNA 5.13、text2speech 1.11.3 和 Objective-C bridge 使用固定 URL、大小和哈希；不把 LWJGL 2 升成 LWJGL 3。平台方案进入 Mac 原生缓存指纹，按版本、实际 Java 架构和启动会话隔离。
- 原生库检查 Mach-O / Universal CPU 表，`.jnilib` 保留原文件并建立相同字节的 `.dylib` 别名；架构不符不启动。仅检查 McMouser 自带的 LWJGL 覆盖，不把其它模组内附多平台二进制误判为不兼容。
- macOS LWJGL 2 不再强加 `-XstartOnFirstThread`；现代 LWJGL 3 保留该参数。Windows 的库、参数和原生缓存指纹不变。
- macOS ARM64 的 Mojang `jre-legacy` / Java 8 缺口由 Azul 官方 Zulu 8u512 原生 JDK 补足，安装到启动器管理目录。固定 SHA-256、实际大小、路径/链接边界、解包预算、临时发布、Java 版本/架构探测与复用完整性检查；不改系统 Java 或默认 JAVA_HOME 25。自定义 Mojang catalog、其它系统/架构/Java 版本仍使用原安装器。
- 只在 macOS 接受 Minecraft 1.12.2 / Forge 14.23.5 的已核实 LaunchWrapper 入口，同时核对 profile ID、Forge/LaunchWrapper 坐标、FMLTweaker 和继承身份；仍由经哈希验证的官方 Forge 安装器执行安装，不手造成功收据。Windows 不启用这个新增入口。
- 不自动给用户整合包增加或替换 McMouser；本轮仅在隔离 RLCraft 中加入官方 1.0.3 文件进行验收。

- 首轮实窗发现 CurseForge 导入白名单漏掉 `resources` 与 `structures`，造成菜单缺图并漏掉结构数据。补回这两个游戏数据目录，仍拦截既有宿主脚本/可执行扩展；重新经生产导入服务下载、核验、安装，补回 816 个资源文件和 338 个结构文件。此为两端共享的数据完整性修复，Windows 的运行时、启动参数及原生库策略不变。

## 验证与当前限制

| 项目 | 结果 |
| --- | --- |
| Core 全量 | 765 通过，5 项沿用条件跳过，0 失败 |
| Desktop 全量 | 261 通过，0 失败 |
| 编译 | macOS ARM64 Release，0 警告/错误 |
| Java 8 | 官方归档 SHA-256 校验通过，真实执行为 `1.8.0_512-b09 / Arm64` |
| RLCraft 2.9.3 | 原包 Minecraft 1.12.2 / Forge 14.23.5.2860；187 项 required 全部解析，3,562 项导入清单完成（首轮 2,408 项，遗漏已补），官方 Forge 安装与游戏依赖准备完成 |
| 受限模组 | 复用 R29 浏览器从官方取得的 SRParasites 与 RTG 文件，重新核验官方大小/哈希，只复制到隔离缓存 |
| 原生库 | LWJGL、OpenAL 均 ARM64；JInput 与 McMouser LWJGL 均包含 ARM64 / x86_64 slice；JNI 别名字节一致 |
| 候选启动器 | CUA 真实点击启动隔离实例，游戏交接后恢复“再开一个游戏窗口”；没有全局忙碌锁住 |
| 游戏启动日志 | 最终 Java 进程 PID 22851；McMouser `Everything loaded`、`Enabled LWJGL mouse buttons 4 and 5`；OpenAL 初始化、Apple GPU OpenGL 2.1 初始化 |
| 真实主菜单与单机 | RC2 菜单纹理恢复；普通世界建档，创意放置石头、Ctrl＋左键破坏、移动/跳跃/背包通过；切换生存模式后生命/饥饿/口渴 UI 与操作正常 |
| McMouser 输入 | 真实 macOS 事件验证 Ctrl＋左键、滚轮 1→9、Shift＋滚轮 9→1，控制设置分别识别 Button 4/5；测试后 Jump 恢复 SPACE/57。未宣称已遍历实体鼠标厂商驱动 |
| 存档读回 | 保存后从世界列表重新进入 `R35MacQAQA`，位置、石头/泥土快捷栏和生存状态保留；NBT 核对玩家模式 0、生命 20、14 个 region 文件 |
| 正常退出 | 两次保存世界后回到主菜单，点击 Quit Game；PID 22851 退出 code=0、cancelled=False、timeout=False。QA 启动器 Command–Q，原生结果 normal_close，两个 PID 均消失 |
| Windows | 平台门控及既有全量核心/界面回归通过；本轮未做 Windows 真机、Java 8/RLCraft 原生验证 |

CUA 对 Minecraft 和实际 `com.azul.zulu.java` 均不能绑定；获得用户本轮明确授权后，使用 macOS 官方辅助功能、截图与键鼠接口，仅操作按 PID/创建身份登记的隔离窗口（含重启），没有沿用旧轮授权。验收结束后已关闭窗口并撤销本轮脚本的活动授权标记。

旧模组仍有 module-info 扫描、个别模组纹理/签名/旧网络资源警告，FoamFix 提示旧世界暂留，OpenAL 退出有 effect/filter 清理警告；本轮未见崩溃，读回及退出均正常。这是短时单机与输入验收，不替代长期游玩、着色器或全模组兼容矩阵。自动输入世界名的选择/事件差异仍记入旧待办，未把工具失准认作产品故障。

自动回归还覆盖非 Mac 平台不接收库覆盖、Windows 原始 JNA/URL/线程参数、旧 Forge 合并后解析、现代 Mac 首线程、Universal/错误架构、未知模组不误拦、损坏归档、越界条目/链接、错误 Java、已装文件篡改、锁/状态链接及其它平台原安装器委派。

## 候选与证据

- 产品提交：`0cb94d648990edeab6d7ff1b00d97d2deca5ef01`（前序原生运行时提交 `80e36d6c`，整合包资源修复提交 `0cb94d64`），分支 `features/windows-parity`。
- ZIP：`.shou-dev/dist/MACOS-R35-20261112/rc2/SHOU-Launcher-osx-arm64-v1.5.0-dev.g0cb94d64.zip`
- SHA-256：`c1223412b5d99371515ee62b69ff305a9584edb47c175b72ba91c20c2bf93b20`
- 显式 `.env` 构建，微软/CurseForge 配置与提交身份校验通过，不记录值。ZIP CRC、ad-hoc 签名及 QA/候选 Core、Desktop 程序集一致性通过。RC2 构建 dirty 标记来自待收尾的报告/todo 和已有未跟踪计划文档；产品代码对应上述提交。
- QA 包仅改 bundle 身份和根签名，以免绑定个人应用：`.shou-dev/dist/MACOS-R35-20261112/qa-rc2/SHOU R35 RC2 QA.app`。
- 隔离数据：`.shou-dev/test-data/r35-native`；实例 `274686a781a54cda9a5444cf120d3ec6`。
- 详细测试、归档、运行时、原生架构与窗口证据：`.shou-dev/reports/macos-r35-20261112/`。
- 35 GiB 开发目录门禁曾阻止原生验收启动；核对保留 ZIP/重复文件后回收旧候选解包、重复构建暂存和两次全通过回归生成的 GUID 临时夹具；旧构建完整归档并校验，RC1 失败截图/日志、非模组备份、全部文件哈希与来源均保留，再通过原有门禁。没有提高额度或修改个人应用/存档。

## 未完成与使用边界

- Windows 真机、Intel Mac、其它旧版本/加载器与不同实体鼠标驱动尚未覆盖；本轮不扩展 Windows 的旧 Forge 支持，也不自动安装 Rosetta。
- 已用旧版本导入而遗漏这两个目录的实例，需从原包重新导入；本轮没有自动覆盖用户现有实例或存档。Mac Java 8 安装在本轮隔离数据中，正常用户数据目录由同一安装器按需管理。
- McMouser 本轮只安装到隔离 QA；用户自己的 Forge 1.12.2 实例可从下方官方发布页取得匹配 JAR 后放入 mods。不会暗中改动整合包模组集合。
- 长期内存/世界切换、旧模组告警、游戏 Dock 名称/图标、更多 Mac 组合和现代游戏实测仍在 todo；其它下载/UI 待办继续保留。

## 来源与独立实现

用户提供的 [GPT Pro 分享](https://chatgpt.com/share/6af44d44-d1a4-83ee-9270-65c1938f48e5) 的前轮摘要仅作为调查线索；运行库实际身份按官方数据重新核实。

- [Prism Java 文档](https://prismlauncher.org/wiki/getting-started/installing-java/)，[Prism 固定提交补丁表](https://github.com/PrismLauncher/meta/blob/9cac2ca1cf410146d3a04fbd33752820fefdd70e/meta/common/mojang-library-patches.json)。
- [Prism 启动参数实现](https://github.com/PrismLauncher/PrismLauncher/blob/0786c494b02b93c51d7f401ca5376813f104a7d8/launcher/minecraft/MinecraftInstance.cpp)，用于核对首线程参数的条件。
- [ManyMC ARM64 运行库说明](https://minecraftmachina.github.io/meta-multimc-arm64/)，[JInput 固定提交](https://github.com/r58Playz/jinput-m1/tree/8b2c3a10b4853923efc92f29cbad8e0f304dbd6d)。
- [McMouser 官方 1.12.2 Forge 1.0.3](https://github.com/MinecraftMachina/McMouser/releases/tag/v1.12.2-forge-1.0.3)，JAR SHA-256 `91ce2cec14ce932b44b8428b288431521841f49173e948b3dc4905024637a7e7`。
- [Azul 官方归档](https://cdn.azul.com/zulu/bin/zulu8.98.0.111-ca-jdk8.0.512-macosx_aarch64.tar.gz)，SHA-256 `523a350b96563e1ff406cf3c55e0211a5816b482a67b5f88a5ea9ba799fd32be`，实际大小 103,741,261 字节（元数据 API 的 size 为取整值 103,741,000，不能直接作精确下载上限）。
- [Forge 官方安装器](https://maven.minecraftforge.net/net/minecraftforge/forge/1.12.2-14.23.5.2860/forge-1.12.2-14.23.5.2860-installer.jar)；测试夹具只存官方元数据与出处哈希。

Prism 源码用于行为核对，未复制其 GPL C++/Python 代码；本轮 C# 解析、平台门控、归档安装与验证独立实现。未安装 Rosetta，未自动替换整合包中的模组，也未发布正式版本或合并 PR。

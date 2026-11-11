# Mac R28：整合包后台安装与原生标题栏

本轮延续 `features/windows-parity`，不合并 main/dev，不发布正式版本。用户授权修改、提交与推送，并在当轮允许隔离 Minecraft 主菜单、单机与正常退出；不连接服务器、不读取真实登录凭据。

## 实现

- 整合包版本行直接启动下载、清单解析、独立实例导入和游戏本体/加载器准备。右下角通知进入任务管理；安装期间仍可导航和只读浏览。其它独立资源保留原有下载/安装入口。
- 任务显示真实文件进度、传输速度、剩余文件和各阶段状态；取消遵循现有取消与窗口关闭等待。基础游戏准备失败时保留已导入实例，重试继续准备，不重复导入。未知总数显示“统计中”。
- CurseForge 没有下载地址时，对 FTB 包查询官方公开、完全相同版本的清单。只接受相同 Minecraft/加载器、CF 项目/文件 ID、文件目录、大小与 SHA-1 的独立公开地址，并在下载后校验 SHA-512。拒绝私有版本、身份不符、未知主机、已删除/恶意/不可用文件；不重建 CDN 链接。CF API key 不发给 FTB、Modrinth 或文件 CDN。
- 读取 CF 包的版本与建议内存，导入后将建议内存写入新实例。
- Mac 品牌排列调整为 SHOU、启动器名称、图标。使用 AppKit 标准窗口按钮，调整原生标题栏布局以匹配 48pt 客户区标题栏；保留系统关闭/最小化/全屏行为。真实窗口验收待补。

- 整合包最多四个文件并发下载；取消等待所有文件写入者停止后才回滚暂存。
- 修复任务页数字行高裁切与默认进度条最小宽度越界。
- Fabric 1.21.1 世界的 `fabric` 内建包：目标必须为相同版本的 Fabric 实例，Fabric API/资源加载模块必须列于目标安装清单，SHA-1、SHA-512 与大小未变，JAR 内需有资源加载模块元数据与对应提供类。预检与发布前均重新核验；不删除或改写 NBT 的包身份。其它未知内建包/版本仍阻塞。对应官方定义：[Fabric 1.21.1 ModResourcePackCreator](https://github.com/FabricMC/fabric/blob/1.21.1/fabric-resource-loader-v0/src/main/java/net/fabricmc/fabric/impl/resource/loader/ModResourcePackCreator.java)。

- 下载速度使用跨文件的最近一秒网络字节统计；短文件、并行传输与最后一个数据块均计入，缓存命中与断点偏移不当成新网络流量。后端操作结束时清零。
- macOS 26 的 AppKit 独立布局会重置原生按钮位置。窗口可见时以轻量定时检查维护标准按钮中心，仅在偏移时重排；最小化/全屏跳过，关闭时停止。坐标通过 NSView 官方转换接口核对，原生实测结果待补。

- StoneBlock 原生启动发现 BootstrapLauncher 模块冲突：加载器 profile 名与实际继承的原版 JAR 文件名不同，导致官方 `${version_name}.jar` 忽略项未覆盖原版 JAR。仅对声明该模板的官方 BootstrapLauncher 忽略列表补入实际客户端文件名，保留 profile 身份、其它过滤项和 classpath；不更换或删除整合包模组。Windows/Mac 与重复项/普通主类边界回归通过。参见 [BootstrapLauncher 官方说明](https://github.com/McModLauncher/bootstraplauncher/blob/main/README.md)。首次失败日志另存本轮报告目录，修复后的实际启动待复验。

## 环境与旧实现参考

本机 `.env` 已存在；构建通过现有 EnvFile 流程嵌入配置，只记录配置是否存在，不输出值。无需 SMB 复制。

本轮用户明确允许参考旧实现，因此不再声称本轮是源代码隔离的 cleanroom。实际阅读 `Plain Craft Launcher 2/Pages/PageDownload/Comp/PageDownloadCompDetail.xaml.cs` 的 Install_Click：确认后台下载→安装组合任务及任务通知行为；仅检索 `Plain Craft Launcher 2/Modules/Minecraft/ModModpack.cs` 的入口/阶段。新 Core/Desktop 不引用旧 WPF 项目。

## 验证记录

- 初轮 Core：705 通过，5 显式跳过，0 失败；包含 FTB、四并发取消及 Fabric 世界身份等 26 项新增 FTB 身份/来源/哈希与推荐内存回归。
- Desktop：233 通过，0 失败；新增后台任务、最小窗口任务页、导航、取消/重试、基础游戏准备失败不重复导入与迟到进度保护。
- 最终源码全量结果、候选 SHA-256、原生窗口与游戏证据在完成后补入。

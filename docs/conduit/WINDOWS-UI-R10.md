# R10：Java 工具收口与 Minecraft 目录状态

2026-11-06，继续现有 Avalonia 产品。Windows 本轮范围完成，Mac ARM64 交叉构建通过，Mac 原生待验。**没有启动 Minecraft，没有切换默认入口，没有推送、合并或正式发布。**

## 源码、参考与边界

- 独立工作树：`D:\Code\C#\SHOU-Launcher-Windows`，本地分支 `features/windows-parity`，续接 `origin/features/wpf-parity`。预检取得远端引用，迁移分支仍为 `fb1b483a3373359fe9a257bcc86d498d068864b6`；没有整合 dev/main。
- origin：`https://github.com/Conduit-Club/SHOU-Launcher.git`。原工作目录的 AGENTS.md 修改、参考 ZIP/文件夹及未跟踪 todo.md 保留。
- `v1.5.0` 是带注释标签，目标提交 `8746ad26f9df1eff9f5a3ea57311f82a14ec181b`。比较了限定目录的文件统计、可移植 `PCL.Core/Conduit/Aria2RpcClient.cs` 差异及标签中的 Java 信任文档。新 `src` 在该标签尚不存在，不能把全部新增文件当作逐页功能完成的依据。
- 视觉参考使用正式截图归档中的 Java 设置、自动安装和安装配置 PNG，以及用户截图和可观察说明。没有读取旧 WPF 视图、样式、控件结构 JSON 或采集脚本作为实现输入。
- 旧 Java 文档包含原地修复方案；本轮继续采用现有的独立副本与来源校验。跨平台路径大小写规则及已有下载/凭据隔离保持，不引入旧 WPF fallback。

本地阶段提交：

| 提交 | 内容 |
|---|---|
| `690e2a6cafb695092969e13b28872db53e61a71b` | Java 独立工具流程、持久回退记录、设置草稿协调、Minecraft 缓存及安装草稿、Windows 原子目录发布重试 |
| `42f8f66a0f65c7e354bf6fb71c587dd32985a302` | 去除工具偏好保存后的重复确认；最终候选源码 |

## UI/功能差异与已修项

| 页面/类别 | 此前差距 | R10 结果 |
|---|---|---|
| Java 设置 / 职责 | 修复偏好仍在设置页，工具与设置共用整个草稿 | 设置仅管理默认路径、发现结果和工具跳转；移除证书卡和修复偏好 |
| Java 工具 / 交互 | 修改检查目标也会改变默认 Java 草稿；创建副本直接选用 | 独立检查目标；扫描、生成并核验、明确设为默认分开；来源与检查详情可展开 |
| Java 工具 / 业务 | 回退假定为源 Java，无法恢复先前自动选择；重启丢失回退入口 | 与默认选择同次持久化此前路径或自动选择；回退前检查当前选择，显式路径再探测，避免覆盖后来选择 |
| 设置 / 交互 | 工具选用结果可能被旧设置草稿覆盖 | 未改动的 Java 字段跟随最新保存值，其他草稿保留；两边都改过时明确选择“使用已保存的选择”或“保留我的 Java 草稿”，解决前禁用保存 |
| Minecraft 目录 / 业务 | 重启或离线后没有目录；刷新结果随离页被丢弃 | 15 分钟内复用有效磁盘缓存，手动刷新强制请求；失败保留有日期的缓存，不将失败当新成功；共享结果跨两个 Minecraft 路由复用 |
| 下载 / 交互 | 独立 JAR 文件页与自动安装共用所选版本；返回重置安装名/加载器 | 分开保存两个版本；同会话返回同一未完成版本时保留名称、Fabric 与额外加载器选择，不创建空实例 |
| 通用 / 错误恢复 | Windows 短暂目录占用使 Java 或包原子发布失败 | 仅对 Windows 访问/共享/锁冲突重试，最多约 1.55 秒等待；每次检查路径及目标，不覆盖、不改为复制删除，等待可取消 |
| 工具 / 视觉文案 | 偏好保存成功出现两条相同提示 | RC2 仅保留一条状态说明，原生确认文本数量为 1 |

目录缓存只存公开版本身份与来源主机名，不保存请求凭据或原始来源配置。配置以 SHA-256 区分缓存；有字节/条目/字段/时间及路径边界检查、原子写入。坏缓存不作为成功回退；取消继续向上传播。安装仍通过已有元数据、来源与文件校验链执行。

Java 新增流程位于 Core；两端共享工具页和设置协调逻辑。Windows 重试放在 IO 适配行为中，不改变 Mac 的原子移动语义。正常探测未要求管理员权限。

## 原生与真实 Java 验证

全部使用隔离目录和受控离线角色 `SHOU_UI_Demo`；没有读取真实账户缓存，也没有创建或启动游戏实例。

- RC1 实际鼠标进入工具页，扫描受控 Java 21.0.13 x64；扫描和生成副本后，写盘默认 Java 仍为空。点击“设为默认 Java”才保存副本及回退记录。
- 重启后仍保留副本选择、自动修复偏好及“恢复自动选择”入口；实际回退并再重启后默认 Java 为空、偏好恢复为 true。
- 设置页保留 Java 路径草稿时，从工具页回退触发冲突；原生确认草稿存在、保存禁用，选择已保存值后才能继续保存。
- 健康副本实际只读检查为健康，修复按钮禁用。未知结果及取消/失败不能产生可选副本另由自动化覆盖。
- 通过候选中相同服务执行真实 Windows Java 的生成、显式选用、服务重启重读、重新核验复用、取消和自动回退；不访问网络，不运行 Minecraft。
- 原 JDK **490** 个文件及受控源 **491** 个文件的逐文件 SHA-256 均不变。副本 119 张证书只移除受控夹具命中的 1 张，其余 118 张保持；两份修复收据与备份有效，其他源文件保持，副本文件无硬链接。控制台和 UI 生成的副本分别通过独立核对。

证据：[真实 Java 流程](../../artifacts/windows-main-20261105/reports/r10-java-native-workflow.json)、[独立核对](../../artifacts/windows-main-20261105/reports/r10-java-independent-verification.json)、[UI 副本核对](../../artifacts/windows-main-20261105/reports/r10-java-ui-independent-verification.json)、[生成但未选用](../../artifacts/windows-main-20261105/evidence-r10-shell/11-prepared-visible.png)、[草稿冲突](../../artifacts/windows-main-20261105/evidence-r10-shell/44-java-draft-conflict.png)。运行验证指 Java 探测和 keytool，不能替代 Minecraft 验证。

目录实际读取 **922** 个版本。给隔离启动器配置不可连接的本机 HTTP 代理，并将该测试缓存回溯 20 分钟，验证过期缓存回退及手动刷新失败保留列表与旧时间；没有改系统网络。恢复原配置后，RC2 重读成功并显示新时间。[离线状态](../../artifacts/windows-main-20261105/evidence-r10-shell/33-offline-cache.png)、[恢复连接](../../artifacts/windows-main-20261105/evidence-r10-shell/54-network-restored-catalog.png)。

自动安装选 26.3，名称设为“R10 中文 草稿”，转到独立文件页浏览 26.4-snapshot-7 后返回，名称与原安装版本仍保持。只查询元数据，没有点击安装或下载游戏文件；实例数始终为 0。[草稿记录](../../artifacts/windows-main-20261105/reports/r10-installation-draft.json)。

Windows 本轮实际 DPI 为 **200%**：默认 976×636、最小 880×560，浅/深色、滚动与新增工具按钮原生通过。三个启动器运行均鼠标正常关闭 exit0，未触发超时清理，无本轮残留进程。锁屏期间停止 UI 输入；用户解锁后补齐实际鼠标操作。没有更改全局快捷键或系统 DPI。

## 构建、测试与首次失败

| 检查 | 结果 | 位置（均在 artifacts/windows-main-20261105 下） |
|---|---|---|
| Core 完整回归 | **364 通过、16 显式跳过、0 失败** | `build/main/r10-core-final/.../reports/tests.trx` |
| 最终 Desktop 完整回归 | **133 通过、0 跳过、0 失败** | `build/main/r10-final-ui/.../reports/tests.trx` |
| Windows 自包含 Publish | 通过，干净源码 `42f8f66a` | `build/main/r10-shell-rc2/.../reports/publish.log` |
| 最终 Mac ARM64 交叉 Build | 通过 | `build/main/r10-mac-final/.../reports/build.log` |

Core 回归对应 `690e2a6c`；之后仅删除重复提示，没有 Core 或其它业务变化。最终 Desktop、Windows Publish 与 Mac Build 均对应 `42f8f66a`。使用锁定 SDK **10.0.403**、Avalonia **12.1.5**、Release。Dotnet 编译 0 警告/错误；Java bootstrap 编译器仍有既有的 Java 8 source/target 兼容目标提示。

16 个跳过与 R9 一致：4 个 macOS 原生 Keychain/argv、9 个 POSIX 或需另配 Windows 链接/失败子进程夹具、2 个显式真实 Java 门禁、1 个在线 Java 下载门禁。R10 真实 Java 另由受控候选服务验证，不把这些跳过计为通过。完整名称和原因见[跳过记录](../../artifacts/windows-main-20261105/reports/r10-core-skips.json)。

构建/测试首次失败保留独立日志与报告，没有覆盖；脚本前置失败另记错误类别：

1. `r10-tools-first`：新增测试缺 JavaInstallation.Source 参数，旧布局测试仍引用设置中的旧命令；修正后 `r10-tools-second` 128 全过。
2. `r10-core-workflow`：353 通过/3 失败/16 跳过，均是已有原子 Directory.Move 的 Windows 拒绝访问；定向重跑 25 通过/4 跳过。`r10-core-full2` 又在包发布和 Java 发布出现同类错误，加入有界重试及实际句柄/取消/目标保护测试后，完整回归 364 通过。
3. `r10-catalog-desktop`：新夹具方法 Task 返回类型错误；`r10-catalog-desktop2` 130 通过/1 失败，旧测试假定首次加载使用手动刷新命令的 ExecutionTask。按实际自动加载行为修正后，后续完整 Desktop 通过。
4. `r10-core-full`：360 通过/1 失败/16 跳过，新缓存未捕获 InvalidDataException，损坏缓存阻挡恢复读取；补正确异常分类后坏缓存矩阵通过。
5. 独立证书核对脚本首次缺 cryptography；改用同一已验证 JDK 的只读 keytool 列出公开 CA 指纹，不新增全局依赖，核对通过。[首次错误记录](../../artifacts/windows-main-20261105/reports/r10-java-verifier-first-failure.txt)。
6. 通用递归清理被自动审批拒绝，未执行；后续仅用受控 dotnet clean 回收已成功构建的编译产物，保留首次失败、候选和数据。最终 Mac 首次 1 GiB 预留触发 35 GiB 任务预算拒绝，清理成功产物后按已测构建规模预留 0.5 GiB 重跑通过。没有放宽总预算或磁盘余量门禁。[预算前置失败记录](../../artifacts/windows-main-20261105/reports/r10-mac-rc2-admission-failure.txt)。交付时任务目录约 34.143 GiB、D 盘余量约 299 GiB，无重任务占用锁；[交付状态](../../artifacts/windows-main-20261105/reports/r10-delivery-state.json)。

## 候选与非敏感证据

[Windows x64 自包含 RC2](../../artifacts/windows-main-20261105/dist/r10-shell-rc2/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.g42f8f66a.zip)，**80,833,504 字节**。

SHA-256：`8b8a14fc1ef993f2d3e32b7162790ac9208bd42f55414e1a08696100a4466396`

源码 `42f8f66a0f65c7e354bf6fb71c587dd32985a302`，Release / win-x64 / self-contained，构建时工作区干净。主程序、CoreCLR 与 Skia 本机库均为 AMD64；266 个归档条目未发现配置缓存、凭据、日志、实例、测试数据或 .env。候选未签名，Windows 默认入口仍为旧 WPF。[打包核对](../../artifacts/windows-main-20261105/reports/r10-final-candidate-audit.json)。

- [按页截图文件夹](../../artifacts/windows-main-20261105/public-evidence-r10/README.md)：6 页、17 张截图，每页有说明；主要流程 RC1，最终文案及恢复连接 RC2。
- [证据 ZIP](../../artifacts/windows-main-20261105/SHOU-UI-Evidence-R10-RC2-20261106.zip)：3,652,502 字节，SHA-256 `e5b1be3d64caaa1ad966b4acf00fafb0d285a07c02322d08a1f0216390b4754b`。截图复制前后哈希相同；不含原始 UIA、账户缓存、JDK 或程序。
- 没有向 Mac/SMB 复制候选；跨机器传输时继续核对上述 SHA-256。Mac 本轮只有交叉构建，不能作为原生验收。

## 后续差距

[todo.md](../../todo.md) 仅勾选本轮明确范围。全局字体/图标/控件一致性、125%/150% 与跨屏、Mac 原生、愚人节/特殊历史目录、更多加载器和安装恢复、工具箱其它禁用占位、Java 取消残件的受控清理仍待完善。安装草稿只保留同一会话；没有宣称跨重启恢复，也没有将独立文件浏览算作基础游戏安装完成。完整替代门禁仍未通过，旧 WPF 与默认入口保留。

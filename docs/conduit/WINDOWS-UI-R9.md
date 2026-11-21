# Windows UI R9：外框、社团内容与 Minecraft 版本目录

2026-11-06。本轮只修改和验证启动器外壳及相关功能，**没有启动 Minecraft 游戏，也没有重新执行正版游戏验收**。旧 WPF 与 Windows 默认入口保留；没有推送、合并、发布或改写历史。继续使用独立工作树 `D:\Code\C#\SHOU-Launcher-Windows` 的 `features/windows-parity`，跟踪 `origin/features/wpf-parity`。

## 源码与候选

| 提交 | 内容 |
|---|---|
| `69e5eec2` | 原生圆角与去白边、方形账户按钮、社团 SRV/人数/内嵌日志、本地换衣预览、两条 Minecraft 流程、禁止游戏验证规则及测试。 |
| `2626e5e3` | 去掉旧原生框对默认尺寸、最小尺寸的额外像素补偿。 |
| `b44de00a` | 版本位图实际加载、正式版优先排列、分组铺满内容区及回归。 |
| `e43b3b62` | 账户两行文字在 32 高度内居中，修复 200% 下裁切并增加边界断言。 |
| `4df7700a` | 保留浅色顶栏配色，修复深色白色选中背景上的文字/图标对比度；增加浅深色对比度回归。 |

最终产品源码：`4df7700a8f35f2d72d8d7182fcfce37a4ad904b7`。后续文档提交不改变产品源码。

- [Windows x64 Release 自包含 RC5](../../artifacts/windows-main-20261105/dist/r9-shell-rc5/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.g4df7700a.zip)：80,821,994 字节。
- SHA-256：`4901595138c9cb81dbfa799eb0b8748099ad237d2e49a15602e0f70f43a35523`。
- .NET SDK **10.0.403**、Avalonia **12.1.5**；`source_dirty=false`，AppHost PE Machine `0x8664`。由既有 `Invoke-Dev.ps1` → `Publish-Desktop.ps1` 入口构建，仍是未签名本地候选。
- 发布配置由原项目 `.env` 经既有入口读取，未复制其内容。266 个包内文件中，未发现 `.env`、账户/会话缓存、测试程序集或额外字体文件；[排除检查](../../artifacts/windows-main-20261105/reports/r9-package-exclusions-final.json)。
- macOS ARM64 在相同最终源码上通过构建，0 警告、0 错误；这是交叉构建，**不代表 Mac 原生运行通过**。

## 参考输入与逐页复核

使用用户指定的 `D:\Code\C#\PCL-CE-SHOU\UI-Reference-WPF-20261105.zip`，SHA-256 `695784a4ca98f6079763220f2971ce8b78fc3ee71fd187298b4f39c27cea6615`。仅将其中 PNG 与可观察说明作为 UI 输入，没有读取旧 WPF 视图、样式、控件映射 JSON 或采集脚本。社团日志解析器只复用可移植业务文件 `PCL.Core/Conduit/ClubUpdateNotice.cs`；游戏版本图标直接复用独立位图并记录来源和哈希，不打包字体。

| 参考页面 | 差距与本轮结果 | 新版证据 |
|---|---|---|
| 01 启动首页 | 原先只有内部圆角、外层仍留白框；现在 HWND 客户区覆盖普通窗口，原生区域裁圆角。保留原三钮。 | [最终首页](../../artifacts/windows-main-20261105/evidence-r9-shell/72-final-light-home.png)、[最小窗口](../../artifacts/windows-main-20261105/evidence-r9-shell/60-final-minimum-200.png) |
| 01 账户行 | 原增删按钮狭长且不齐；现在两颗均为 32×32 DIP，图标居中、与下拉框共用上下边缘。两行账户文字分别采用 15/13 行高，原生测量均在边框内。 | [文字边界](../../artifacts/windows-main-20261105/evidence-r9-shell/59-selector-text-bounds.json) |
| 01 社团状态 | 原隔离数据根跳过查询，且固定连接 25565，未使用社团 SRV 的 30000；现首次自动查询、每分钟刷新、可手动重试。三个入口同属一个网关，人数取已知最大值，避免重复相加；未知与零人分开。实测三个入口均报告 1，主页显示 1。 | 最终首页及刷新状态提示；只做 TCP 状态查询，不启动游戏。 |
| 01 社团资源 | Wiki 与入服指南保留有效官网入口，HTTP 200；“社团更新日志”原先打开启动器 Releases，现改为读取社团网站的公开更新。 | [公开地址检查](../../artifacts/windows-main-20261105/reports/r9-club-links.json) |
| 05 更新日志 | 增加内嵌日期列表、展开正文、加载/错误、重试/刷新、可信源校验和有界缓存；只呈现纯文本，不执行远程 HTML。首次展开真实正文，重启后显示缓存，浅深色及 200% 最小窗口可读。 | [缓存重读](../../artifacts/windows-main-20261105/evidence-r9-shell/26-restarted-club-cache.png)、[最小窗口](../../artifacts/windows-main-20261105/evidence-r9-shell/56-history-minimum-loaded.png)、[深色](../../artifacts/windows-main-20261105/evidence-r9-shell/67-final-dark-history-loaded.png) |
| 03 皮肤与披风 | 已有真实 UV 投影能力，本轮从换衣菜单实际进入 3D，并补本地换衣页的同步模型预览。拖动、缩放、复位、外层/披风开关与 Esc 有回归；未上传或修改真实账户。 | [菜单进入预览](../../artifacts/windows-main-20261105/evidence-r9-shell/48-clothing-menu-3d.png)、[本地换衣](../../artifacts/windows-main-20261105/evidence-r9-shell/53-local-clothing-3d.png) |
| 30 Minecraft 自动安装 | 首次进入自动读版本；点击版本后下载并安装基础游戏，可选兼容加载器。消除“创建实例”主流程文案，内部继续使用跨平台统一安装模型。修复草方块/命令方块图标和分组宽度。 | [版本列表](../../artifacts/windows-main-20261105/evidence-r9-shell/44-final-catalog.png) |
| 40 Minecraft 安装包 | 独立于顶部自动安装：浏览版本 → 选择客户端/服务端 → 原生另存 → 文件校验。新增 `IMinecraftPackageService`，这条路径不创建实例、不调用 Java。当前使用校验后的官方 CDN，保留 HTTP 代理且不跟随重定向。 | [文件页](../../artifacts/windows-main-20261105/evidence-r9-shell/33-minecraft-files-loaded.png)、[下载完成](../../artifacts/windows-main-20261105/evidence-r9-shell/35-minecraft-file-download.png) |

头像是受控测试皮肤；截图中的方块颜色用于检查 UV，并非真实用户的头像加载结果。本轮原生验收未读取真实账户缓存或截取真实账户新截图。此前 R7/R8 的正版与头像证据仍为历史证据。

## 原生与文件执行验证

- Windows 100%：默认 976×636、最小 880×560；实际圆角区域排除左上角，普通窗口客户区与外框相等。最小化/还原、最大化/还原及正常关闭通过。
- Windows 200%：对应物理 1952×1272 / 1760×1120；账户按钮为 64×64 物理像素，文字无裁切。真实鼠标标题栏拖动 40×26 DIP 对应移动 80×52 像素；双击最大化、按钮还原通过。
- 最大化客户区精确等于工作区，包括左侧任务栏：200% 下均为 `(146, 0, 3694, 2160)`；屏幕外原生调整框不会变成内容白边。[几何证据](../../artifacts/windows-main-20261105/evidence-r9-shell/62-final-maximized-frame.json)。
- 3D 的原生旋转、滚轮缩放、复位、Esc 已操作；换衣菜单与本地换衣面板在 200% 最小窗口均可操作。首页和新增日志弹层也完成深色检查；RC5 顶栏选中态在两种主题下对比度均达到 4.5，见[最终深色首页](../../artifacts/windows-main-20261105/evidence-r9-shell/70-final-dark-contrast.png)。
- 安装包页实际另存 **Minecraft 26.3 客户端**，41,483,720 字节，SHA-1 `e877b6a07acd633fb3bb475002175cec036e7b87`，与元数据匹配，实例列表前后都是空。只下载文件，没有运行它。[验证记录](../../artifacts/windows-main-20261105/reports/r9-package-download.json)。
- 以上原生流程跨 RC1–RC5 进行：独立文件下载在 RC2 验证，此后 Core 无变化；目录图标/宽度于 RC3 复验，账户行高及最终窗口在 RC4 复验；RC5 补顶栏选中态对比度并复验浅深色首页。七次受控启动器运行均正常关闭 exit0、未触发超时清理，没有留下本轮启动器进程。

## 自动化、跳过与首次失败

| 检查 | 结果 | 证据 |
|---|---|---|
| Core 完整回归 | 351 通过，16 显式跳过，0 失败 | `build/main/r9-core-full/.../reports/tests.trx` |
| Desktop 完整回归 | 126 通过，0 跳过，0 失败 | `build/main/r9-nav-contrast/.../reports/tests.trx` |
| 最终 Windows 发布 | 通过，0 警告/错误，源码干净 | `build/main/r9-shell-rc5/.../reports/publish.log` |
| 最终 macOS ARM64 构建 | 通过，0 警告/错误 | `build/main/r9-mac-rc5/.../reports/build.log` |

上述路径均在 `artifacts/windows-main-20261105` 下。Core 内容在首次产品提交后没有再改；之后新增的是桌面排版修复与相应测试。

16 个跳过项：4 个仅限 macOS 的 Keychain/OS argv；9 个需 POSIX 或另行准备 Windows 链接/失败子进程夹具；2 个显式真实 Java 修复副本门禁未启用；1 个在线 Java 下载门禁未启用。完整名称与原因见 [跳过记录](../../artifacts/windows-main-20261105/reports/r9-core-skips.json)。不把跳过计算为通过，也不为本轮 UI 任务额外开启这些门禁。

首次失败保留，没有覆盖日志：

1. `r9-shell-tests`：3 通过/2 失败。一个新 DNS 夹具错误地编码为空目标，另一个暴露超大页面的 `InvalidDataException` 未被缓存回退捕获；修复夹具与异常分类后 `r9-shell-tests2` 5/5 通过。
2. `r9-desktop-tests`：新测试引用了不存在的属性，编译失败；按实际 API 修正。`r9-desktop-tests2` 随后 123 通过/1 失败，为旧文案断言与新“下载并安装”文案不一致；更新断言后全过。
3. RC1 原生发现最小尺寸额外多出旧框的 16 像素；RC2 修正尺寸。RC2 原生另发现字符串绑定未加载版本图标、折叠组只有标题宽度；RC3 修正，并增加实际图像与宽度断言。
4. 200% 原生复核发现账户文字的继承行高超出 32 高度；RC4 修正为两行均在边框内。鼠标辅助脚本也按实际 DPI 换算后再测，旧未换算坐标的动作不作为标题栏验收证据。

短暂断开的输入会话曾使前台 HWND 为 0。期间使用已授权的原生控件接口完成目录与保存；用户解锁后，已补实际鼠标导航、换衣菜单、拖动和双击。没有改全局快捷键、DPI 或系统设置。

## 后续与交接

默认禁止游戏启动的规则已加入迁移工作树 AGENTS.md，并在原工作目录 AGENTS.md 本地记下；原目录其它既有文件保留。历史游戏授权、门禁和 todo 不能当作新授权。

仍未完成的项目保留在 [todo.md](../../todo.md)：125%/150%、真正跨屏与完整快捷键/贴靠/触屏矩阵；Mac 原生；愚人节与特殊历史版本；独立包更多取消/覆盖/断网场景、镜像优先级与暂存文件体验；在线皮肤上传/披风写入；首页自动入服；Java 修复全部收口工具页等。没有将这些列为迁移完成。

- [按页面整理的证据文件夹](../../artifacts/windows-main-20261105/public-evidence-r9/README.md)，每页含截图及大致说明，不含账户缓存、原始 UIA 树、原生文件选择器截图、日志或程序。
- [非敏感 UI 证据 ZIP](../../artifacts/windows-main-20261105/SHOU-UI-Evidence-R9-RC5-20261106.zip)：7,537,668 字节，SHA-256 `10df46681a8a4aaf08fe62b3da1d3c4d219aa586798b648dfbbf5adb5a78dbc9`。页面文件复制前后哈希一致。
- 后续跨机器复制优先沿用 SMB，并核对 ZIP SHA-256；源码通过 Git。当前没有向 Mac 传输或声称 Mac 原生验收完成。

实现依据采用 Microsoft 的 [自绘窗口框](https://learn.microsoft.com/en-us/windows/win32/dwm/customframe)、[WM_GETMINMAXINFO](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-getminmaxinfo) 与 [SetWindowRgn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn) 文档；Windows 专属行为留在窗口适配层，共享页面和社团/下载业务继续供两端共用。

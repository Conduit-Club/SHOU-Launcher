# R11：跨页字体、图标与控件一致性

2026-11-06。继续同一套 Avalonia 页面和样式，Windows 与 macOS 共用实现。**本轮没有启动 Minecraft，也没有推送、合并、切换默认入口或正式发布。**

## 源码与参考

- 工作树 `D:\Code\C#\SHOU-Launcher-Windows`，分支 `features/windows-parity`；续接迁移分支，没有合并 dev/main。原目录的未提交修改和参考归档保留。
- origin 为 `https://github.com/Conduit-Club/SHOU-Launcher.git`；本轮 fetch 后 `origin/features/wpf-parity` 仍为 `fb1b483a3373359fe9a257bcc86d498d068864b6`。
- 本地阶段提交：`65b7c1f58ded6d709a14325e5f1e56a03b683c53` 统一共享字体、图标和控件；`7fff046e7f7808ec36a8215765bd8cfc9f8bfbe5` 继续收起加载器目录说明并保留实际错误。最终 RC2 源码为后者。
- 对照用户截图，以及正式 WPF 归档中的启动设置、管理与下载、百宝箱、资源搜索、安装配置/Fabric 展开和皮肤预览 PNG。参考图为 200% DPI，按逻辑尺寸核对间距、字号和侧栏图文位置。
- v1.5.0 目标提交 `8746ad26f9df1eff9f5a3ea57311f82a14ec181b`。独立 Lucide SVG 目录在该标签与当前分支之间无差异；只复用许可明确的独立图片，不读取旧 WPF 视图、样式、模板、结构 JSON 或采集脚本。

## 差异与本轮已修

| 范围 | 此前差异 | 本轮结果 |
|---|---|---|
| 共享字体 | 正文、弹出菜单和输入内容行高不一致；中文与 Latin 控件中心不齐 | 正文 13/18、侧栏 14/20、节标题 14/20；输入框内容行高 18。菜单沿用平台中文字体；日志使用平台等宽字体，不新增打包字体 |
| 图标 | 下载、设置、实例、存档页面混用自行画出的路径和字体符号 | 统一为 Lucide 24 单位 SVG，侧栏控件 20×20、图文间距 10、两端共用；选中标记不挤动图文位置；动作补可访问名称 |
| 按钮和输入 | 默认高度、边距及内容对齐各自不同 | 通用按钮、单行输入、下拉与数值输入统一最小高度 28；图标按钮使用正方形；保留首页账户 32×32 与标题栏独立尺寸 |
| 复选框 | Fluent 模板使普通 margin 覆盖无效，文字紧贴方框 | 在启用/禁用状态模板层覆盖，文字间隔 8；保留模板自身键盘与交互状态 |
| 菜单与焦点 | 菜单字体/颜色游离，焦点不够一致 | 共用字体、背景、边框和选择色；按钮与输入沿用共享焦点色，保留框架交互模板 |
| 默认说明 | 设置、工具、下载和实例页常驻迁移说明、重复成功说明；目录额外显示成功计数和来源段落 | 收起默认说明，必要的能力边界放在对应控件的 tooltip；目录来源/更新时间可悬停刷新查看，正常目录与有效名称不再额外出现提示段落 |
| 实际反馈 | 直接按文字内容判断是否隐藏，可能误吞离线提示 | 使用显式显示状态。保留加载、空结果、错误、取消、离线缓存和用户实际操作结果；增加离线通知不是中文时仍显示的回归 |
| 加载器目录 | 展开配置后常驻“点击读取”与成功计数，读取完还可能残留加载中说明 | 默认与成功列表不另加段落；保留加载、空构建、不支持安装、错误与取消，读取不自动选用加载器 |
| 皮肤和日志 | 本地换衣增加额外说明，3D 页底部新增提示项，日志等宽字体依赖 Windows | 本地文件限制移到选择按钮 tooltip，3D 页常驻提示对齐参考；日志 Windows 用 Consolas，macOS 用 Menlo |

涉及共享 Themes、ClubIcon、全部主要页面及少量呈现状态；**Core 与 Windows 窗口适配层没有修改**。字体主族继续由平台提供：Windows `Microsoft YaHei UI`、macOS `PingFang SC`，不依赖 Mac 字体在 Windows 显示。

SVG 共 87 个，新增 79 个；`Assets/Icons/Lucide/SOURCE.txt` 记录原文件与 LF 归一化后的 SHA-256，87 条全部核对通过。ISC/Feather MIT 许可继续保留。没有把旧 WPF 的控件结构带进新版。

仍禁用的高级选项、工具箱功能、加载器下载等没有因此完成。详细缺口继续保留在 [todo.md](../../todo.md)，不将收起说明等同于实现业务。

## 构建与测试

| 检查 | 结果 | 证据（artifacts/windows-main-20261105 下） |
|---|---|---|
| 最终 Desktop 完整回归 | **138 通过、0 跳过、0 失败** | `build/main/r11-loader-presentation/Desktop/Release/win-x64/default/reports/tests.trx` |
| Windows x64 自包含 Publish | 通过，干净源码 `7fff046e` | `build/main/r11-shared-ui-rc2/Desktop/Release/win-x64/default/reports/publish.log` |
| macOS ARM64 交叉 Build | 通过，0 警告/错误，同一源码 `7fff046e` | `build/main/r11-macos-final/Desktop/Release/osx-arm64/default/reports/build.log` |
| 独立 SVG 清单与几何 | 87 条 SHA-256 与视口边界通过 | `SOURCE.txt`、`SharedPresentationTests` |
| Core | 本轮未重跑，代码无改动 | 最近 R10：364 通过、16 显式跳过；不是本轮测试计数 |

锁定 SDK **10.0.403**、Avalonia **12.1.5**、Release。使用现有 Invoke-Dev / Validate-Desktop / Publish-Desktop 入口及 35 GiB 任务预算、20 GiB 余量和超时/清理守卫，重任务串行执行。仅用 `dotnet clean` 回收已成功的旧编译产物，保留首次失败、候选、日志和数据。

新增测试覆盖全部 SVG、十个设置栏目在浅/深色下的图文位置、含中文/Latin 输入高度与复选框间距、默认多余说明隐藏、正常目录成功与离线/错误通知的区别。

首次失败及修复均保留原报告，没有覆盖失败记录：

1. `r11-style-first`：AccountsView 新 SVG 控件缺 views 命名空间，编译失败；补齐后继续。
2. `r11-style-second`：132 通过、1 失败，旧测试仍查找已移入 tooltip 的 Aria2 说明；更新为能力限制仍保留的检查。
3. `r11-style-third`：135 通过、2 失败，新增检查发现单行输入默认行高 15，以及百宝箱遗留常驻说明；修正为 18 并收起说明。
4. `r11-presentation-targeted`：2 通过、2 失败，复选框普通样式被 Fluent 模板 margin 覆盖，以及百宝箱说明；改用状态模板选择器，修正说明。
5. `r11-style-fourth`：136 通过、1 失败，旧结构测试仍要求百宝箱说明常驻；改为检查禁用入口及 tooltip。
6. 随后完整 `r11-desktop-full` 137 通过；新增显式离线状态回归后 `r11-status-final` 与 `r11-presentation-final` 均为 138 全部通过。最后 `r11-loader-presentation` 在提交前工作区验证最终加载器提示变化，仍为 138 全部通过；之后只提交和更新报告，没有再改产品代码。

## Windows 原生核对

隔离数据目录 `native-r11-ui` 只含受控离线角色 SHOU_UI_Demo、演示皮肤和两个未安装实例，无真实账户凭据。没有点击启动、安装基础游戏或准备并启动。

RC1、最终 RC2 均实际打开自包含候选并取得 Windows 原生首页截图：200% DPI、976×636；圆角、无白边、首页在线人数、账户下拉与 32×32 增删按钮边缘保持。原生 UIA 读到下拉框及两个按钮 y=795、height=64 像素，两个按钮宽均为 64 像素。演示皮肤为受控色块，不是真实账户头像。

原生逐页与输入验证待续：自动置前台被 Windows 拒绝，鼠标守卫没有发送点击；UIA Invoke 也返回 Unsupported Pattern。后续焦点明确变为 LockApp，已停止界面输入并请求用户解锁、手动置前台。不能将自动化布局测试代替全部原生鼠标、菜单、DPI 和键盘验收。

RC1 对受控自有进程发送正常关闭请求后 exit0；RC2 经 90 秒应用自身定时退出 exit0，均未触发外层超时强制清理。RC2 stderr 为空，没有本轮残留启动器进程。没有向 LockApp 发送输入，也没有改系统快捷键或 DPI。[RC1 退出](../../artifacts/windows-main-20261105/reports/r11-native-ui1-exit.json)、[RC2 退出](../../artifacts/windows-main-20261105/reports/r11-native-rc2-exit.json)。

## 候选

[Windows x64 自包含 RC2](../../artifacts/windows-main-20261105/dist/r11-shared-ui-rc2/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.g7fff046e.zip)，**80,853,898 字节**。

SHA-256：`e9518f3ff8b7d9728b4af01b69f033b26cc08c514559efa5ec1676b961bc9ffb`

源码 `7fff046e7f7808ec36a8215765bd8cfc9f8bfbe5`，Release / win-x64 / self-contained，构建工作区干净。主程序、CoreCLR、Skia 本机库为 AMD64；266 个条目未发现 .env、账户缓存、实例、日志、密钥或测试数据。[打包核对](../../artifacts/windows-main-20261105/reports/r11-final-candidate-audit.json)。未签名，本机候选，默认入口仍为旧 WPF。

[按页截图与说明](../../artifacts/windows-main-20261105/public-evidence-r11/README.md)只有本轮最终 RC2 首页一页、一张，不能代替其余原生页面。[截图 ZIP](../../artifacts/windows-main-20261105/SHOU-UI-Evidence-R11-RC2-20261106.zip)为 985,066 字节，SHA-256 `40ba50422eee12f08df9254dc6560452468098211568dba1e9b673b94a211915`；复制前后哈希一致，不含原始 UIA、账户缓存、日志或程序。

macOS 只有交叉构建，**原生排版、弹层、菜单、窗口与键盘复验待有 Mac 时完成**。本轮没有传送候选到 SMB/Mac，没有游戏或登录新证据。

# R11：跨页字体、图标与控件一致性

2026-11-06。继续同一套 Avalonia 页面和样式，Windows 与 macOS 共用实现。**本轮没有启动 Minecraft，也没有推送、合并、切换默认入口或正式发布。**

## 源码与参考

- 工作树 `D:\Code\C#\SHOU-Launcher-Windows`，分支 `features/windows-parity`；续接迁移分支，没有合并 dev/main。原目录的未提交修改和参考归档保留。
- origin 为 `https://github.com/Conduit-Club/SHOU-Launcher.git`；本轮 fetch 后 `origin/features/wpf-parity` 仍为 `fb1b483a3373359fe9a257bcc86d498d068864b6`。
- 本地阶段提交：`65b7c1f58ded6d709a14325e5f1e56a03b683c53` 统一共享字体、图标和控件；`7fff046e7f7808ec36a8215765bd8cfc9f8bfbe5` 收起加载器目录说明；`5a1622339b9c6b8128e1d3e8634f7ddb9d23dc96` 修正原生数值框、滑块与展开布局；`23f56a15198639f13c64f5b523dbd2ae6ee5de44` 修正工具选中图标与内存标签；`e26e2f996ff1f4302f22c355f23e944f5538c723` 收起实例重复说明、分开日志加载/空结果并补实例侧栏可访问名称。**最终 RC5 源码为 e26e2f99。**
- 对照用户截图，以及正式 WPF 归档中的启动设置、管理与下载、百宝箱、资源搜索、安装配置/Fabric 展开和皮肤预览 PNG。参考图为 200% DPI，按逻辑尺寸核对间距、字号和侧栏图文位置。
- v1.5.0 目标提交 `8746ad26f9df1eff9f5a3ea57311f82a14ec181b`。独立 Lucide SVG 目录在该标签与当前分支之间无差异；只复用许可明确的独立图片，不读取旧 WPF 视图、样式、模板、结构 JSON 或采集脚本。

## 差异与本轮已修

| 范围 | 此前差异 | 本轮结果 |
|---|---|---|
| 共享字体 | 正文、弹出菜单和输入内容行高不一致；中文与 Latin 控件中心不齐 | 正文 13/18、侧栏 14/20、节标题 14/20；输入框内容行高 18。菜单沿用平台中文字体；日志使用平台等宽字体，不新增打包字体 |
| 图标 | 下载、设置、实例、存档页面混用自行画出的路径和字体符号 | 统一为 Lucide 24 单位 SVG，侧栏控件 20×20、图文间距 10、两端共用；选中标记不挤动图文位置；动作补可访问名称 |
| 按钮和输入 | 默认高度、边距及内容对齐各自不同 | 通用按钮、单行输入、下拉与数值输入统一最小高度 28；图标按钮使用正方形；保留首页账户 32×32 与标题栏独立尺寸 |
| 数值与滑块 | 原生滑块占高 50，数值框随行拉高；不透明度编辑区被两个旋钮按钮挤掉 | 滑块与数值输入 28 高；旋钮按钮各 16 宽，数值编辑区至少 40 宽；不透明度完整显示 100；内存标签单行呈现 |
| 展开按钮 | 默认宽度与箭头位置游离，外套卡片重复边框 | 折叠栏填满内容宽度，高 38；箭头、主题边框和卡片间距一致，保留原生焦点与展开行为 |
| 复选框 | Fluent 模板使普通 margin 覆盖无效，文字紧贴方框 | 在启用/禁用状态模板层覆盖，文字间隔 8；保留模板自身键盘与交互状态 |
| 菜单与焦点 | 菜单字体/颜色游离，焦点不够一致 | 共用字体、背景、边框和选择色；按钮与输入沿用共享焦点色，保留框架交互模板 |
| 工具选择态 | 深色下选中文字变色，图标仍为白色或原色 | 图标跟随所属选项前景色；两个工具栏目原生确认 |
| 默认说明 | 设置、工具、下载和实例页常驻迁移说明、重复成功说明；目录额外显示成功计数和来源段落 | 收起默认说明，必要的能力边界放在对应控件的 tooltip；目录来源/更新时间可悬停刷新查看，正常目录与有效名称不再额外出现提示段落 |
| 实际反馈 | 直接按文字内容判断是否隐藏，可能误吞离线提示 | 使用显式显示状态。保留加载、空结果、错误、取消、离线缓存和用户实际操作结果；增加离线通知不是中文时仍显示的回归 |
| 加载器目录 | 展开配置后常驻“点击读取”与成功计数，读取完还可能残留加载中说明 | 默认与成功列表不另加段落；保留加载、空构建、不支持安装、错误与取消，读取不自动选用加载器 |
| 皮肤和日志 | 本地换衣增加额外说明，3D 页底部新增提示项，日志等宽字体依赖 Windows | 本地文件限制移到选择按钮 tooltip，3D 页常驻提示对齐参考；日志 Windows 用 Consolas，macOS 用 Menlo |
| 社团更新状态 | 读取中同时出现“尚无记录”；正常结果常驻缓存使用说明 | 加载与真正空结果分别显示；正常结果收起额外说明，实际离线/错误继续提示 |
| 实例页面 | 正常存档与服务器页反复显示内部范围说明；UIA 读出侧栏记录对象字段 | 正常说明移至读取按钮 tooltip，受限原因仍显示；十二个栏目提供正常名称，鼠标与方向键导航保留 |

涉及共享 Themes、ClubIcon、全部主要页面及少量呈现状态；**Core 与 Windows 窗口适配层没有修改**。字体主族继续由平台提供：Windows `Microsoft YaHei UI`、macOS `PingFang SC`，不依赖 Mac 字体在 Windows 显示。

SVG 共 87 个，新增 79 个；`Assets/Icons/Lucide/SOURCE.txt` 记录原文件与 LF 归一化后的 SHA-256，87 条全部核对通过。ISC/Feather MIT 许可继续保留。没有把旧 WPF 的控件结构带进新版。

数值框、滑块与展开栏调整依据锁定版本的框架模板核对，沿用框架控件及交互语义：[ButtonSpinner](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/5825bf8c66fba0c11e67c980d8ece6444576bffa/src/Avalonia.Themes.Fluent/Controls/ButtonSpinner.xaml)、[Slider](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/5825bf8c66fba0c11e67c980d8ece6444576bffa/src/Avalonia.Themes.Fluent/Controls/Slider.xaml)、[Expander](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/5825bf8c66fba0c11e67c980d8ece6444576bffa/src/Avalonia.Themes.Fluent/Controls/Expander.xaml)。

仍禁用的高级选项、工具箱功能、加载器下载等没有因此完成。详细缺口继续保留在 [todo.md](../../todo.md)，不将收起说明等同于实现业务。

## 构建与测试

| 检查 | 结果 | 证据（artifacts/windows-main-20261105 下） |
|---|---|---|
| 最终 Desktop 完整回归 | **141 通过、0 跳过、0 失败** | `build/main/r11-native-hints-recheck/Desktop/Release/win-x64/default/reports/tests.trx` |
| Windows x64 自包含 Publish | 通过，干净源码 `e26e2f99` | `build/main/r11-shared-ui-rc5/Desktop/Release/win-x64/default/reports/publish.log` |
| macOS ARM64 交叉 Build | 通过，0 警告/错误，同一源码 `e26e2f99` | `build/main/r11-macos-rc5/Desktop/Release/osx-arm64/default/reports/build.log` |
| 独立 SVG 清单与几何 | 87 条 SHA-256 与视口边界通过 | `SOURCE.txt`、`SharedPresentationTests` |
| Core | 本轮未重跑，代码无改动 | 最近 R10：364 通过、16 显式跳过；不是本轮测试计数 |

锁定 SDK **10.0.403**、Avalonia **12.1.5**、Release。使用现有 Invoke-Dev / Validate-Desktop / Publish-Desktop 入口及 35 GiB 任务预算、20 GiB 余量和超时/清理守卫，重任务串行执行。仅用 `dotnet clean` 回收已成功的旧编译产物，保留首次失败、候选、日志和数据。

交付核对时任务占用 **34.902 GiB / 35 GiB**，磁盘空闲 **277.613 GiB**，无重任务所有者和本轮残留启动器进程。[交付状态](../../artifacts/windows-main-20261105/reports/r11-final-delivery-state.json)。

新增测试覆盖全部 SVG、十个设置栏目在浅/深色下的图文位置、含中文/Latin 输入高度与复选框间距、滑块/数值旋钮/展开栏实际尺寸、工具选中颜色、默认多余说明隐藏、正常目录成功与离线/错误通知的区别。受控异步测试验证读取中不显示空日志；原有存档写保护回归确认正常说明收起后，非隔离或运行中原因仍保留。完整页面夹具核对十二个侧栏的可访问名称。

最终测试在提交前工作区执行，控制报告中的 HEAD 为 `23f56a15`；测试通过后将同一产品与测试内容提交为 `e26e2f99`，随后以干净源码构建 RC5，未再修改产品代码。Core 本轮未变，不重复计算历史 Core 成绩。

首次失败及修复均保留原报告，没有覆盖失败记录：

1. `r11-style-first`：AccountsView 新 SVG 控件缺 views 命名空间，编译失败；补齐后继续。
2. `r11-style-second`：132 通过、1 失败，旧测试仍查找已移入 tooltip 的 Aria2 说明；更新为能力限制仍保留的检查。
3. `r11-style-third`：135 通过、2 失败，新增检查发现单行输入默认行高 15，以及百宝箱遗留常驻说明；修正为 18 并收起说明。
4. `r11-presentation-targeted`：2 通过、2 失败，复选框普通样式被 Fluent 模板 margin 覆盖，以及百宝箱说明；改用状态模板选择器，修正说明。
5. `r11-style-fourth`：136 通过、1 失败，旧结构测试仍要求百宝箱说明常驻；改为检查禁用入口及 tooltip。
6. 随后完整 `r11-desktop-full` 137 通过；新增显式离线状态回归后 `r11-status-final`、`r11-presentation-final`、`r11-loader-presentation` 均为 138 全部通过。解锁后增加原生发现的尺寸回归，`r11-native-layout`、`r11-native-presentation-final`、`r11-resumed-final` 均为 140 全部通过。
7. `r11-tools-colors-final`：新增测试缺 `ToggleButton` 命名空间，CS0246 编译失败；补导入后 `r11-tools-colors-recheck` 140 全部通过。
8. `r11-native-hints-final`：140 通过、1 失败；新增侧栏检查误放入仅挂载存档子页的夹具，找不到侧栏。移至完整页面夹具后，最终 `r11-native-hints-recheck` **141 全部通过**。
9. RC5 首次 Publish 在执行构建前被 35 GiB 预算守卫拒绝；保留 `reports/r11-rc5-admission-first.json`。只对已成功的编译产物执行 `dotnet clean` 后重试成功，没有清理失败报告、候选包或数据，也没有降低预算。

## Windows 原生核对

隔离数据目录 `native-r11-ui` 只含受控离线角色 SHOU_UI_Demo、演示皮肤和两个未安装实例，无真实账户凭据。没有点击启动、安装基础游戏或准备并启动。

用户解锁后已续完代表状态走查。Computer Use 可定位窗口与读取控件，但点击仍报 `coordinate input geometry is unavailable`；沿用用户已明确允许的 UIA 读取、Win32 鼠标/键盘及本地截图备用接口。每次输入前核对进程、可执行路径、窗口归属和前台；没有向 LockApp 或其它应用发送输入，没有改系统快捷键或 DPI。重任务进行时个别前台核对未通过，守卫没有点击；重任务结束重新核对后继续。

| 原生范围 | 实际结果与限制 |
|---|---|
| 首页 | 200% DPI、976×636 与 880×560，浅/深色圆角和无白边保持；真实只读查询显示社团在线人数，受控头像、账户下拉与 32×32 增删按钮对齐；不涉及真实账户认证 |
| 十个设置栏目 | 浅色常用尺寸、深色最小窗口逐页核对；不透明度 100 完整，数值框高 28、旋钮不挤掉数值，内存标签单行，滑块紧凑；滚动可达底部保存栏 |
| 主题下拉与持久化 | 鼠标展开、Down/Enter 选择深色、保存；RC4 重启仍为深色。随后 Up/Enter 恢复浅色并保存，RC5 重启仍为浅色；只改隔离数据偏好 |
| Java 工具与百宝箱 | 深色最小窗口实际切换，选中图标与文字同色，侧栏填满宽度；未扫描或修复 Java，未执行百宝箱占位动作 |
| Minecraft 与下载 | 自动加载目录、分组宽度、配置展开箭头；中文名称输入，Tab 到 Forge 展开按钮、Shift+Tab 返回。模组只读搜索 sodium 获得真实资源列表；整合包重复说明已收起。未创建实例、下载游戏或安装资源 |
| 账户菜单与 3D | 深色菜单实际打开，Esc 关闭；进入真实 UV 模型预览并缩为最小窗口，Esc 正常关闭。皮肤为色块夹具，未上传皮肤或修改在线账户 |
| 社团更新日志 | RC4 深色最小窗口实际展开/收起日期与阅读正文；RC5 浅色复验加载中不出现空记录，读取后显示正文且无常驻缓存说明；未外发内容 |
| 十二个实例栏目 | 鼠标进入设置，方向键遍历修改、导出、存档、截图、模组、资源包、光影包、投影原理图、服务器、模组关系；图文与空态可见。RC5 UIA 读到十二个正常栏目名称，存档/服务器正常重复说明已收起；未保存、安装、导出或启动 |
| 窗口与退出 | RC4 真实点击最小化后恢复，常用/最小尺寸可切换；标准三钮保留。续验 RC2、RC3、RC4、RC5 均点击关闭后 exit0，无外层超时清理，stderr 均为空 |

截图来源按 RC3/RC4/RC5 的真实源码区分。RC4 在 RC3 上仅修正工具选中图标和内存标签；RC5 在 RC4 上仅收起重复说明、区分日志加载/空结果及补侧栏名称。共享控件及其它页面沿用同一实现。完整页图见下方证据包，每页附说明，不将空页面走查当作全部业务完成。

[续验 RC2 退出](../../artifacts/windows-main-20261105/reports/r11-native-resumed-exit.json)、[RC3 退出](../../artifacts/windows-main-20261105/reports/r11-native-rc3-exit.json)、[RC4 退出](../../artifacts/windows-main-20261105/reports/r11-native-rc4-exit.json)、[RC5 退出](../../artifacts/windows-main-20261105/reports/r11-native-rc5-exit.json)。此前锁屏与置前台阻挡已解除，原阻挡报告保留为历史记录。

## 候选

[Windows x64 自包含 RC5](../../artifacts/windows-main-20261105/dist/r11-shared-ui-rc5/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.ge26e2f99.zip)，**80,854,659 字节**。

SHA-256：`eac1bb03eb4cfed72daba215c37de2f4f4e7c919b4e2588d60f939bb27f39efd`

源码 `e26e2f996ff1f4302f22c355f23e944f5538c723`，Release / win-x64 / self-contained，构建工作区干净。主程序、CoreCLR、Skia 本机库为 AMD64；266 个条目未发现 .env、账户缓存、实例、日志、密钥或测试数据。[打包核对](../../artifacts/windows-main-20261105/reports/r11-rc5-candidate-audit.json)。未签名，本机候选，默认入口仍为旧 WPF。RC1–RC4 包及对应核对记录保留，不作为最终交付来源。

[按页截图与说明](../../artifacts/windows-main-20261105/public-evidence-r11-final/README.md)共 **33 个页面文件夹、39 张截图**；每张标记真实源码、DPI、逻辑尺寸、采集方式和 SHA-256。[截图 ZIP](../../artifacts/windows-main-20261105/SHOU-UI-Evidence-R11-RC5-20261106.zip)为 **7,518,052 字节**，SHA-256 `698019faa685510e67963bc479c70912cbb762e16d184b97008debdcbfb22573`。复制前后哈希一致、ZIP 完整性通过；白名单只包括人工查看过的 PNG、说明和清单，不含原始 UIA、账户缓存、日志内容、程序或 Java。[归档核对](../../artifacts/windows-main-20261105/reports/r11-final-public-evidence.json)。此前 RC2 仅首页的证据包保留。

macOS 只有交叉构建，**原生排版、弹层、菜单、窗口与键盘复验待有 Mac 时完成**。本轮没有传送候选到 SMB/Mac，没有游戏或登录新证据。

## 仍待完善

- N04/N04e：已填充的资源与存档详情、其它下载类别及全部弹层状态还需逐项核对；完整焦点、屏幕阅读器名称、系统强调色和高对比度矩阵尚未完成。
- N05/G01：本轮原生主要为 200% DPI；125%/150%、跨显示器、系统贴靠/快捷键、触屏及 macOS 原生待验，不用布局测试或交叉构建替代。
- 原逐页业务缺口继续保留在 todo.md；语言、更新安装链、其它加载器、在线皮肤/披风写操作等没有因统一样式而完成，也没有通过隐藏入口来勾选。
- 未启动 Minecraft，未验证游戏、登录、自动入服或任何游戏内效果。后续只有用户在当前任务明确要求，才可启动游戏验收。

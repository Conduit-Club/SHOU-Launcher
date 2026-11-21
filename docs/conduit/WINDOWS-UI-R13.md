# R13：下载来源、愚人节分组与独立安装器

2026-11-07。产品提交 **a77f75f7d129b9a2abbb2db06a28f37ae6e9b6aa**，分支 `features/windows-parity`。本轮依据用户新截图和要求续作迁移分支；没有合并 dev/main、推送、切换默认入口或正式发布。旧 WPF 和原工作区已有改动保留。没有启动 Minecraft，也没有执行下载的安装器。

## 差异与本轮结果

| 用户指出的差异 | R13 结果 | 验证边界 |
|---|---|---|
| Minecraft 缺少愚人节版 | 九个实际 ID 单独分组；不改变原始 kind/安装能力，不伪造缺失条目 | Core 回归、实际 Windows 目录 |
| 来源只有 Modrinth，世界搜不到 | 接入全部/CurseForge/Modrinth；世界使用 CF classId 17，另五类对应各自官方分类 | CF 六类各返回 30 项并读取文件；原生切换来源和世界下载 |
| 排序应改回默认 | UI 初始/重置为“默认”；空关键词仍展示热门，输入关键词后按提供方默认相关性读取 | Desktop 回归、原生初始与切换 |
| OptiFine 空页面 | BMCLAPI 502 项正式/预览目录、实际文件名校对、官方文件另存及 SHA-1 验证 | 正式 J1、历史预览 J1_pre15 真实保存和复用；候选界面另存 K2_pre5 |
| Cleanroom 空页面 | 官方 Maven 73 项目录、1.12.2 分组、独立安装器及官方 SHA-1 | 0.7.14-alpha 实际保存，文件哈希与官方发布摘要一致 |
| LabyMod 空页面 | 官方稳定/快照 manifest，48.4/49 和真实发布日期；另存官方 Java 安装器 | 实际保存 529637 字节；上游没有提供此安装器哈希 |
| LiteLoader 空页面 | 官方历史清单 16 组、独立图标和安装器地址 | 1.7.10 实际保存；1.12.2 Jenkins HTTPS 在本机超时，未冒认成功 |
| 推荐/最新版埋在列表里 | 每组推荐、最新版置顶，其下全部版本；同一行可同时标记；仅一两项时直接排列 | Forge 26.3 实窗：推荐 66.1.0、最新 66.1.5、全部 18 项；Desktop 布局回归 |

本次目录实际显示 103 个正式版、749 个普通预览版、61 个远古版和 9 个愚人节版。没有为了与旧图计数相同而修改版本类型或添加假条目。愚人节 ID 明确列出，包括 1.RV-Pre1；同日普通正式版 26.1.1 不误分。特殊历史名称的安装限制保留，独立分组不等于所有历史版本都可安装。

## 共享实现与下载边界

- 数据协议、CF 检索/文件/依赖接口、安装器目录与另存放在 Core；页面、图标、按钮和筛选继续由同一套 Avalonia 实现承担。没有增加 Windows 业务路径或 WPF fallback。
- 仅参照旧业务模块中的元数据协议和下载地址，以及用户截图；没有读取旧 WPF 视图、样式、模板或结构映射。Cleanroom/LabyMod/Egg 位图来自已有独立素材目录，来源与 SHA-256 记在 Assets/Minecraft/README.md。
- CF 项目/版本身份使用 `cf-` 前缀，和已有 Modrinth 收藏隔离；旧收藏缺少 Source 字段时仍按 Modrinth 读取。来源变化会重新读取，草稿/缓存按页面保留；本轮不宣称搜索草稿跨重启持久化。
- “全部”每页从两个提供方分别读取 15 项，合并展示；单来源读取 30 项。热门/下载量在当页排序，不能视作跨两个独立站点所有页面的统一全局排行榜。选择提供方后按该站点完整分页。
- CF key 仅附在固定 `https://api.curseforge.com/v1/` 请求，使用禁止自动跳转的专用传输。图标和文件请求不带 key；现有 CF 官方 CDN 逐跳白名单、哈希/大小验证复用。未改 MRpack 的 Modrinth 下载域名边界、CF 整合包精确哈希替代规则、实例隔离与回滚。
- 作者未提供第三方下载地址，或文件已不可用时，不拼接 CDN 地址；展示版本及项目页入口和原因。真实例子 Fresh Animations 返回 30 个版本，但当前均无第三方地址，不能算“可直接下载 30 个文件”。
- OptiFine 的 BMCLAPI 预览记录以 `HD_U_J1 + pre15` 等形式表示，不能仅接受 `HD_U`。自然数字排序避免 pre9 排到 pre10 前面。官方文件不可用时只访问固定 BMCLAPI 所委派的、带预期 SHA-1 的单个 HTTPS 节点，不跟随后续跳转、不发送凭据；拒绝字面 IP/本机/私网目标，兼容系统代理的 198.18/15 fake-IP 映射。不会关闭 TLS 校验。
- Cleanroom 使用官方 Maven 的 SHA-1。LabyMod 清单 sha1 属于客户端，LiteLoader 清单 md5 属于加载器本体，**都不冒用于独立安装器**。这两个安装器仅做官方 HTTPS、有效大小/有界完整传输和 JAR 结构检查；本地 SHA-256 用于交付核对，不是上游签名或发布者哈希。
- 四类新增下载先落入独立暂存，经检查后通过目标旁临时文件发布；失败/取消保留原目标并清理本次暂存。下载的 JAR 不自动运行、不创建实例。独立安装器当前上限 64 MiB，有界请求/取消继续有效。

协议依据：[CurseForge 官方 API](https://docs.curseforge.com/rest-api/)、[BMCLAPI 维护者接口文档](https://bmclapidoc.bangbang93.com/)、[Cleanroom 官方 Maven](https://maven.cleanroommc.com/com/cleanroommc/cleanroom/maven-metadata.xml)、[LabyMod 官方 manifest](https://releases.r2.labymod.net/api/v1/manifest/production/latest.json)、[LiteLoader 官方清单](https://dl.liteloader.com/versions/versions.json)。实际响应与下载结果另存为脱敏记录，没有保存 API key、账户缓存或认证响应。

## 验证与首次问题

项目锁定 .NET SDK **10.0.403**、Avalonia **12.1.5**；通过已有 Invoke-Dev、Validate-Desktop、Publish-Desktop 串行构建测试。

| 验证 | 结果 | 原始记录 |
|---|---|---|
| Core Release / win-x64 | **383 通过，0 失败，16 显式跳过** | [TRX](../../artifacts/windows-main-20261105/build/main/r13-core-rc1/Desktop/Release/win-x64/default/reports/tests.trx) |
| Desktop Release / win-x64 | **148 通过，0 失败，0 跳过** | [TRX](../../artifacts/windows-main-20261105/build/main/r13-desktop-rc1/Desktop/Release/win-x64/default/reports/tests.trx) |
| Windows 自包含候选 | 干净源码 a77f75f7，Release/win-x64；EXE/CoreCLR/Skia 均 AMD64 | [包审计](../../artifacts/windows-main-20261105/reports/r13-rc1-candidate-audit.json) |
| macOS ARM64 交叉构建 | 成功，0 warning/error；**未做 Mac 原生运行** | [控制记录](../../artifacts/windows-main-20261105/reports/r13-macos-rc1-Build-osx-arm64-default-controller.json) |
| Windows 原生 UI | 200% DPI，976×636 与 880×560 DIP；三来源、自动读取、世界文件、四类目录/另存、推荐区、深色、最小化恢复、重启收藏 | [逐页证据](../../artifacts/windows-main-20261105/public-evidence-r13/README.md) |

16 项跳过沿用已有门禁：8 项 POSIX 链接/Windows reparse 场景、1 项 POSIX 失败 keytool 夹具、4 项 Mac argv/Keychain、2 项受控 Java 副本门禁未启用、1 项在线 Java 门禁未启用。没有将跳过算作通过，也没有为外壳修改开启游戏测试。

新增回归覆盖愚人节与普通发布的区分、CF 世界分类/编码/来源身份/凭据作用域/拒绝跳转/缺 key、文件项目身份/哈希/CDN/无第三方地址、四类目录、OptiFine 预览自然排序与真实校验、拒绝私网节点、坏哈希/HTML/取消保留旧文件和清理暂存，以及来源控件/世界路由/默认排序/推荐区和 CF 收藏重读。

本轮自动构建与测试首轮均通过；真实联网检查暴露并修正的记录保留：

1. 最初只接受 OptiFine `HD_U`，185 项目录漏掉预览类型；修正后为 502 项，增加预览字段回归。
2. [首次历史预览另存](../../artifacts/windows-main-20261105/reports/r13-real-core-final.json)失败：官网只返回缺文件文本，大小检查拒绝，未发布错误文件。
3. [首次镜像回退](../../artifacts/windows-main-20261105/reports/r13-real-preview-fixed.json)被代理 fake-IP 映射误拦；修正后保留真实私网限制，[最终 J1_pre15](../../artifacts/windows-main-20261105/reports/r13-real-preview-rc1.json)两次保存同 SHA-256、暂存 0。
4. 原生另存辅助脚本的早期文件名输入常量及前台守卫曾失败，原始记录保留；最终用实际系统选择器和候选状态/输出文件核对结果。两个安装器保存在既有受控 package-download-r9 目录，另外两个保存在 r13-native-save；未覆盖或移动旧验收资料，不将输入异常伪报为产品失败或通过。

[首次问题索引](../../artifacts/windows-main-20261105/reports/r13-first-observations.json) 汇总上述问题、修正和剩余上游阻塞。

测试在提交前的开发工作树完成，控制记录的 HEAD 为 2d44f8d2，包含待提交的 R13 源码；随后提交为 a77f75f7，候选和 Mac 交叉构建均来自该干净提交。没有因版本标识变化而重复整套测试。

## 实际文件与原生证据

[六类 CF 及四类安装器首轮联网](../../artifacts/windows-main-20261105/reports/r13-real-core-first.json)、[候选原生下载收据](../../artifacts/windows-main-20261105/reports/r13-native-downloads.json)。这里只核对文件，不执行安装器或解压世界。

| 文件 | 实际字节数 | 检查 |
|---|---:|---|
| OptiFine 26.2 K2_pre5 安装器 | 7,849,368 | 原生另存，预期 SHA-1 |
| Cleanroom 0.7.14-alpha 安装器 | 15,327,470 | 原生另存，官方 SHA-1 |
| LabyMod 48.4 安装器 | 529,637 | 原生另存，HTTPS/大小/JAR；无上游安装器哈希 |
| LiteLoader 1.7.10 安装器 | 2,896,561 | 原生另存，HTTPS/大小/JAR；无上游安装器哈希 |
| Oneblock v3.4.8 世界 ZIP | 29,945,532 | 候选实际下载，官方 SHA-1 `155a1adac41d25c7b3da20101756065973dd0b64` |

真实 Core 重复保存还包括 OptiFine 1.21.1 J1/J1_pre15、Cleanroom、LabyMod 和 LiteLoader 1.7.10，文件一致，暂存均为 0。每个文件的完整 SHA-256 见下载收据/联网记录。

候选两次正常关闭均 exit0，stderr 为 0，未触发外层超时清理；重启后深色及 CF 收藏仍可见。重启窗口恢复默认 976×636，不能当作窗口尺寸持久化通过。收藏页缩略图仍为占位，已保留 P39。

## 交付与剩余项

- [Windows RC1 ZIP](../../artifacts/windows-main-20261105/dist/r13-download-rc1/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.ga77f75f7.zip)：**80,930,095 字节**，Release / win-x64 / self-contained，未签名本地候选。
  SHA-256：`4221af2004a59793d5d257290e3fc323ada0bf94f14cd5b2e09a80ca63060e32`。
- [逐页证据 ZIP](../../artifacts/windows-main-20261105/SHOU-UI-Evidence-R13-20261107.zip)：13 页/19 图及说明，**4,310,086 字节**；不含原始 UIA、运行数据、日志或下载文件。
  SHA-256：`18a0b5e9a74851fd54adfac316fecc90f3671b1ee5b103f9f582cb389f6d3010`。
- 候选包 266 项，架构与 SHA-256 已核对，未包含 .env、账户/凭据缓存、私钥、日志、测试夹具或验收下载。API 构建配置继续由 Core 拥有，未在报告或截图输出值。
- [最终审计](../../artifacts/windows-main-20261105/reports/r13-final-delivery-audit.json)：任务目录 **33.728 / 35 GiB**，磁盘剩余 **277.537 GiB**，20 GiB 预留满足；重任务锁可正常取得，两个候选进程均已退出。仅用 dotnet clean 回收已成功运行的编译输出；报告、失败记录、候选和验收数据均保留。公开截图/文本还对本机构建值做了不输出内容的检查，未发现泄漏。

未完成：LiteLoader 1.12.2 等 Jenkins HTTPS 源的可达性；LabyMod/LiteLoader 上游安装器哈希；完整系统另存取消/断网/覆盖/历史版本矩阵；CF 世界/数据包明确目标与解压安装链；CF 资源依赖装入真实实例的原生验证；更多标签中文名、收藏缩略图、窗口尺寸持久化及完整 UI 对齐；Mac 原生、更多 DPI/跨屏和无障碍。新增四类入口是独立安装器下载，不代表游戏实例安装或运行链迁移完成。

已完成项见 [todo.md](../../todo.md) 的 D09a–D09e；D06/D07、D09f 与各页剩余业务仍未勾选。旧 WPF 与默认入口继续保留。

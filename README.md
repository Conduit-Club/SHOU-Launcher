**简体中文** | [English（上游原版说明）](README-EN.md) | [繁體中文（上游原版說明）](README-ZH_TW.md)

<div align="center">

<img src="Plain%20Craft%20Launcher%202/Images/Conduit/club-logo.png" alt="潮涌核心社标志" width="96" height="96">

# Plain Craft Launcher（PCL）· 潮涌核心社版

**PCL-CE-SHOU** · 为潮涌核心社 Minecraft 网络定制的第三方启动器

[社团主页](https://conduit-club.github.io/) · [服务器档案](https://conduit-club.github.io/server/) · [项目仓库](https://github.com/Conduit-Club/PCL-CE-SHOU) · [社团 Releases](https://github.com/Conduit-Club/PCL-CE-SHOU/releases)

</div>

> **v1.0.0（zst）**：社团首发版本。Windows x64 自包含下载包与 SHA-256 校验文件见[社团 Releases](https://github.com/Conduit-Club/PCL-CE-SHOU/releases)，功能边界与验证说明见[首发说明](docs/conduit/RELEASE-v1.0.0.md)。

## 五湖四海之士，汇于东海之滨

潮涌核心社希望把校园、记忆与新的创作汇聚在同一片 Minecraft 空间。社团服务器包含长期建设的 **SMP 多人生存服**、用于地皮与大型作品制作的 **Create 创造建筑服**，以及承载校园还原作品的 **SHOU 建筑展示服**。启动器的首页、服务器入口和更新提醒都围绕这套真实运行的社团网络设计。

服务器规则、作品与更新记录请以[潮涌核心社官方服务器档案](https://conduit-club.github.io/)为准。

## 服务器入口

| 网络或后端 | 用途 | 社团当前配置 | Minecraft 入口 |
| --- | --- | --- | --- |
| Velocity 代理网络 | 统一认证、跨服转发与版本接入 | 支持 1.7.10–26.1 客户端接入 | `smp.shoumc.com`、`create.shoumc.com`、`shou.shoumc.com` |
| SMP 生存服 | 多人生存、社区建设与扩展玩法 | **1.21.1** | `smp.shoumc.com` |
| Create 创造建筑服 | 地皮、创造建筑与作品制作 | **1.21.11** | `create.shoumc.com` |
| SHOU 建筑展示服 | 参观校园还原作品与 BlueMap 地图 | **1.21.11** | `shou.shoumc.com` |

*注：表内后端版本按社团当前配置整理，可能覆盖旧官网页面中的版本记录；入口和玩法说明请参见当前社团公告与[官方服务器档案](https://conduit-club.github.io/)。*

从任一公开入口进入代理网络后，可以使用 `/server smp`、`/server create` 或 `/server shou` 切换后端。Velocity 的版本范围描述的是代理层接入能力，不代表所有 Minecraft 版本、客户端或模组组合都能直接兼容；具体以后端配置和实际测试为准。

详细说明： [Velocity 代理服](https://conduit-club.github.io/servers/velocity/) · [SMP 生存服](https://conduit-club.github.io/servers/smp/) · [Create 创造建筑服](https://conduit-club.github.io/servers/create/) · [SHOU 建筑展示服](https://conduit-club.github.io/servers/shou/)。

## 账户与认证

启动器提供三类账户来源的添加、切换和管理入口：

| 账户来源 | 说明 |
| --- | --- |
| Microsoft | 原版 Microsoft 账户。账户页首次先显示此入口，但社团应用审核尚未完成，当前明确提示未开通并阻止授权。 |
| MUA Union | MUA Union 第三方账户。首次使用时在 MUA 皮肤站注册并验证邮箱；随后在启动器选择 MUA Union，填写该皮肤站的邮箱和密码。旧 Union 地址档案若无法登录，请移除后通过 MUA 入口重新添加。Union 聚合地址用于服务器端认证配置，不是启动器账号登录 API，也无需填入 Minecraft 服务器地址栏。 |
| LittleSkin | LittleSkin 第三方账户。启动器支持按登录页提供的密码登录路线操作，不需要额外的 app ID；若使用 OAuth 设备授权，则需要符合 LittleSkin 的应用 ID 与白名单要求。 |

> **验证边界**：LittleSkin 与 MUA 的皮肤站邮箱密码登录路线已由用户实际确认可用；Microsoft 授权仍因社团 API 审核未完成而禁用，令牌刷新、角色切换与 Minecraft 端联机不在本次密码登录确认范围内。遇到登录问题时，请先查看[社团服务器认证说明](https://conduit-club.github.io/servers/velocity/)。

## 三步开始游玩

1. **获取启动器**：从[社团 Releases](https://github.com/Conduit-Club/PCL-CE-SHOU/releases)下载 v1.0.0 Windows x64 自包含包，并使用旁侧的 SHA-256 文件校验完整性。
2. **添加账户**：在启动器中添加并选择 Microsoft、MUA Union 或 LittleSkin 账户；MUA Union 用户先在皮肤站注册并验证邮箱，再填写皮肤站邮箱和密码。
3. **准备实例并选择服务器**：先下载或选择一个游戏实例（推荐 **1.21.1**），再在主页选择 SMP、Create 或 SHOU 后启动游戏；若只想进入游戏再手动选服，可选择“只启动游戏”，进入代理后使用 `/server smp`、`/server create` 或 `/server shou`。

## 启动器定制

- **账户来源计数与选择**：账户面板按 Microsoft、MUA Union、LittleSkin 等来源分组并显示数量，可直接选择要使用的来源和档案。
- **启动后进入服务器**：从主页选择 SMP、Create 或 SHOU 后，启动游戏即可按所选目标进入对应入口；也可以选择“只启动游戏”，暂不自动进服。
- **嘉然主题**：社团版默认使用嘉然配色，同时保留主题切换能力。
- **官方实景轮播**：首页默认展示社团服务器截图，柔化轮播可在“设置 → 个性化”中关闭。
- **社团日志提醒**：启动后默认检查社团网站更新日志并提示新的记录，可在“设置 → 个性化”中关闭；检查失败时不会阻塞启动。
- **手动更新社团程序**：更新页手动查看社团 GitHub Releases，下载按钮打开发行页面，由用户自行选择版本和下载，不在后台覆盖启动器程序。

## 社团实景

以下图片来自潮涌核心社官方服务器档案，用于展示启动器所服务的真实空间：

| 校园湖畔 | SMP 出生点广场 | Create 出生点雕塑 |
| --- | --- | --- |
| ![SHOU 校园湖畔](Plain%20Craft%20Launcher%202/Images/Conduit/campus-lake.jpg) | ![SMP 出生点广场](Plain%20Craft%20Launcher%202/Images/Conduit/spawn-plaza.jpg) | ![Create 出生点雕塑](Plain%20Craft%20Launcher%202/Images/Conduit/spawn-statues.jpg) |

## 下载与开发

v1.0.0 是 Conduit-Club 基于 PCL-CE 的独立第三方发行版；请从[社团 Releases](https://github.com/Conduit-Club/PCL-CE-SHOU/releases)获取正式包，不要把上游 PCL-CE 的版本号、下载统计或发行包当作潮涌核心社版本。

社团发行目标为 **Windows x64 自包含程序**，运行时无需另外安装 .NET Runtime，但启动 Minecraft 仍需要可用的 Java 运行时。若要从源码构建，请先阅读[社团构建说明](docs/conduit/BUILD.md)；构建使用 **.NET 10 SDK**，入口为 [`scripts/conduit/Publish.ps1`](scripts/conduit/Publish.ps1)。

## 反馈与联系

- 启动器代码、构建问题与已知问题：[GitHub Issues](https://github.com/Conduit-Club/PCL-CE-SHOU/issues)
- 社团 QQ 群：**756155087**
- 服务器入口、规则和更新：[潮涌核心社官方站点](https://conduit-club.github.io/)

## 许可、署名与鸣谢

PCL-CE-SHOU 是 Conduit-Club 基于 [PCL-Community/PCL-CE](https://github.com/PCL-Community/PCL-CE) `dev` 分支独立维护的第三方版本，不是 PCL、PCL-CE、Mojang 或 Microsoft 的官方发行物。项目保留 PCL 原作者与 PCL Community 的署名、原有许可证和相关第三方声明：

- PCL 原作者：[龙腾猫跃](https://github.com/Meloong-Git/PCL)，原作者赞助入口：[爱发电](https://ifdian.net/a/LTCat)。
- `Plain Craft Launcher 2/` 使用仓库内的[PCL 分发有限许可](Plain%20Craft%20Launcher%202/LICENCE)，并保留 PCL 原作者要求的署名和说明。
- 其他目录使用仓库根目录的 [Apache License 2.0](LICENSE)，其中包含 PCL Community 的版权与 CE 版本声明。

社团版开发与维护：**Moeary / Codex**。特别鸣谢[社团指导老师-ZRY](https://xxxy.shou.edu.cn/_t2275/2023/0706/c17034a320081/page.psp)与[空间博士-ZST](https://xxxy.shou.edu.cn/_t2275/2023/0707/c17036a353973/page.psp)；关于页也保留了相应说明。

本页的服务器信息以[官方首页](https://conduit-club.github.io/)、[Velocity 文档](https://conduit-club.github.io/servers/velocity/)、[SMP 文档](https://conduit-club.github.io/servers/smp/)、[Create 文档](https://conduit-club.github.io/servers/create/)和[SHOU 文档](https://conduit-club.github.io/servers/shou/)为来源。MUA Union 与 LittleSkin 的认证流程请分别参考[MUA Union 官方开发文档](https://docs.mualliance.cn/dev/union/auth)和[LittleSkin OAuth 设备授权文档](https://manual.littleskin.cn/advanced/oauth2/device-authorization-grant)。

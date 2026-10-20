# 社团微软正版登录配置

PCL-CE 上游工作流从自己的 GitHub Secrets 读取 `CLIENT_ID` 并注入二进制；社团构建使用已通过 Microsoft 审核的公开应用标识。Client ID 本身是公开标识，但它代表应用所有者的注册与审核身份，不能把它视为任意项目通用的登录服务。本项目没有使用其他启动器的标识，也不需要用户提交微软密码或 Client Secret。

## 注册步骤

1. 使用具有应用注册权限的账户登录 [Microsoft Entra 管理中心](https://entra.microsoft.com/)。微软当前的注册快速入门列出 Azure 订阅、租户及至少 Application Developer 权限等前提；学校租户若禁止应用注册，需要联系管理员或使用自己可管理的租户。
2. 打开 **Entra ID → App registrations → New registration**，名称填写 `PCL 潮涌核心社版`（用户授权时会看到此名称）。
3. Supported account types 选择 **Personal accounts only**，或包含 **Personal Microsoft accounts** 的选项。不要只允许学校组织账户：Minecraft 玩家通常使用个人 Xbox/Microsoft 账户。
4. 注册完成后复制 **Application (client) ID**，不是 Directory ID、Object ID 或 Client Secret。
5. 在应用 **Authentication** 设置启用 **Allow public client flows**。本程序使用 `consumers/oauth2/v2.0/devicecode` 设备代码流程，运行时请求 `XboxLive.signin offline_access`，不使用 Client Secret，不需要为此流程提供重定向 URI。
6. 检查 [Minecraft Java Edition Game Service API 审核说明](https://help.minecraft.net/hc/en-us/articles/16254801392141) 及 [应用审核表单](https://aka.ms/mce-reviewappid)。社团应用审核已获准；实际授权仍应使用测试账户确认账户、地区和 Minecraft 服务状态。
7. 在启动页点击 **微软登录配置**，填写获得的 Client ID；点击 **微软正版** 后在微软官网完成设备代码授权。若实际授权遇到 `403` 或 `Invalid app registration`，请记录完整错误并核对应用注册与 Minecraft 服务权限。

Client ID 保存在本机配置 `ConduitMicrosoftClientId`。此入口用于开发测试；获得社团正式可用的 ID 后，应作为公开应用配置随社团发行物统一提供，避免让普通社员逐个注册应用。更换 ID 后可能需要重新登录旧的微软账户。

社团构建现在也支持根目录 `.env` 的 `PCL_MS_CLIENT_ID` 或 GitHub Secret `CLIENT_ID`，详见 `BUILD.md`。个人本机覆盖配置的优先级高于构建内置值。已审核的应用支持个人 Microsoft 账户；截至 2026-10-03，应用 ID `17d4b212-f29e-4a51-9ef7-71232f89a34c` 的门户 **Allow public client flows** 已启用并保存成功，用户已确认使用真实账户完成微软登录。

## 官方参考

- [注册应用](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [公共客户端与机密客户端](https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-applications)
- [设备代码流程](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)

MUA Union 与 LittleSkin 使用各自的 Yggdrasil / OAuth 服务，不依赖上述 Microsoft Client ID；可以先在首页独立添加和切换。

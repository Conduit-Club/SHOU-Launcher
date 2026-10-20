# 社团第三方认证说明

## 登录入口

社团启动器保留传统 Yggdrasil 用户名/密码登录。输入内容只提交给当前选择的认证服务，不会提交到社团服务器或其他认证服务。打开登录页后，密码登录是默认入口；OAuth 只有在认证服务公开可用的设备授权配置和客户端标识时才显示，用户需要主动选择。

MUA Union 的服务端聚合地址用于 Minecraft 服务器接入，不是玩家直接登录的地址。启动器的 MUA 登录入口使用成员皮肤站 API：

```text
https://skin.mualliance.ltd/api/yggdrasil
```

旧版本曾保存过 `https://skin.mualliance.ltd/api/union/yggdrasil` 的档案。启动器仍将这类档案识别为 MUA Union，并保留原地址；添加新账户时使用成员皮肤站入口，不会把旧档案的密码静默迁移到其他主机。MUA 文档中的 [联合认证说明](https://docs.mualliance.cn/zh/dev/union/auth) 将 Union 地址定义为服务器使用的 Yggdrasil API Root，并说明玩家应在成员皮肤站注册后在启动器登录。

LittleSkin 的传统 Yggdrasil API 仍可用于用户名/密码登录，且与 OAuth 并行提供；详见 [OAuth for Yggdrasil](https://manual.littleskin.cn/feature/oauth-for-yggdrasil)。启动器不再硬编码其他应用的 OAuth Client ID。LittleSkin 设备代码流要求应用先申请白名单，需按 [设备授权授予说明](https://manual.littleskin.cn/advanced/oauth2/device-authorization-grant) 注册并提交工单；没有社团自己的客户端标识和白名单时，应使用密码入口或由社团后续配置自有 OAuth 应用。

## 错误处理

认证端点可能因网关超时或维护返回 HTML/504，而不是 Yggdrasil JSON。启动器会依据 HTTP 状态显示可读的失败原因，不把响应正文、密码、访问令牌或异常堆栈展示给用户；标准 JSON 错误仍会保留认证服务返回的错误代码和描述以便修正账号或服务配置。

元数据探测在后台执行，只更新 OAuth 按钮的可见性，不会覆盖用户已经选择的登录方式或切换中的认证来源。真实登录、令牌刷新和游戏联机仍需要使用测试账户人工验证；本项目的自动化测试只使用本地伪造 HTTP 响应，不向认证端点提交凭据。


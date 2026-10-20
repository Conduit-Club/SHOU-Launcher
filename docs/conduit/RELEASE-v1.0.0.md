# Conduit-Club v1.0.0（zst）

Conduit-Club v1.0.0 是面向社团 Minecraft 网络的独立第三方 Windows x64 自包含启动器。它保留 PCL-CE 与 PCL Community 的原作者、许可证和上游归属，不代表 PCL、Mojang 或 Microsoft 官方发行。

## 本版功能

- 提供 SMP 生存、Create 创造和 SHOU 校园展示三个社团入口，并保留“只启动游戏”选项。
- 三个入口的当前游戏版本分别为 SMP 1.21.1、Create 1.21.11、SHOU 1.21.11。
- 按 Microsoft、LittleSkin、MUA Union 和其他来源分类管理账户，支持已有档案切换、来源标识和启动前状态提示。
- LittleSkin 使用皮肤站密码登录路线；MUA 使用成员皮肤站的 Yggdrasil 接口。旧 Union 地址账户若无法登录，请移除后通过 MUA 入口重新添加。
- 提供嘉然配色、社团首页与官方实景轮播，并在关于页展示社团版本和发行代号。
- 更新页只手动检查社团 GitHub Releases；更新日志提醒默认开启，可在“设置 → 个性化”中关闭，检查失败时不阻塞启动。
- 发行目标为 Windows x64 自包含单文件程序，运行时无需另外安装 .NET Runtime；启动 Minecraft 仍需要可用的 Java 运行时。
- ZIP 内含启动器、许可证和构建信息；对应的 SHA-256 校验文件作为 ZIP 旁的独立 Release 资产提供。

## 首发限制

- 社团 Microsoft API 申请与配置尚未完成，微软入口在界面中明确标记为暂未开通并阻止授权请求。已有公开 Client ID 不代表 Minecraft API 审核已经通过；请先使用 LittleSkin 或 MUA。
- 首次进入账户页会先显示微软正版入口，但该入口会明确提示尚未开通；LittleSkin 与 MUA 可独立使用。
- LittleSkin OAuth 设备授权仍须使用其允许的自有应用 ID 与白名单；本版密码登录不要求用户申请 connect ID。
- LittleSkin 与 MUA 的皮肤站邮箱密码登录路线已由用户实际确认可用；Microsoft 授权、令牌刷新、角色选择和 Minecraft 联机不在本次密码登录确认范围内。
- 程序更新需要用户从社团 Releases 手动下载并替换，不会在后台自动覆盖现有启动器；Minecraft 版本和模组组合仍需按对应社团后端的支持范围适配。

## 发布信息

- 版本：`v1.0.0`
- 发行代号：`zst`
- 正式标签：`v1.0.0`
- 直接 PCL-CE 基线：`2.15.1`
- 元数据上游字段：`2.12.1`（保留仓库原有上游声明）
- 构建配置：Windows x64、Release、自包含

## 构建与验证

- ClubRelease 定向测试：19/19 通过。
- 离线核心测试：290 项，289 项通过、0 项失败；其中 1 项 ChaCha20 硬件支持测试因平台不支持而未执行。
- 另有 6 项真实网络或 Windows Toast 集成测试明确未执行，因此本版不宣称全部全量测试通过。
- 本版修复了缓存空文件、SQLite 释放与过期清理、事件总线、编码检测和路径校验问题。
- `BUILD-INFO.txt` 记录源码提交、SDK 版本和构建配置；测试数字记录在本页，TRX 测试报告将随测试报告 ZIP 作为 Release 资产提供。ZIP 旁的 `.sha256` 资产用于校验下载文件完整性。

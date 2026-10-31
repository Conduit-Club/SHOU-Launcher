# 客户端凭据构建配置

2026-10-31 按维护者最新决定，继续将 CurseForge 项目 API Key 编入客户端，不采用服务端中转。Key 可从分发程序中提取；不能将这一方式视为只有服务端持有秘密的方案。

## GitHub 配置

- 仓库：`Conduit-Club/SHOU-Launcher`。
- 仓库级 Actions Secrets：`CLIENT_ID` 和 `CURSEFORGE_API_KEY`。后者已从本机 `.env` 中现有的 Key 配置，未输出明文或写入命令行参数。
- 设置入口：[Actions Secrets](https://github.com/Conduit-Club/SHOU-Launcher/settings/secrets/actions)。临时创建的 `curseforge-server` 环境已撤销。
- GitHub Secrets 用于存储构建输入，相关规则见 [GitHub 官方说明](https://docs.github.com/en/actions/reference/security/secrets)。本地 `.env` 不自动同步为仓库 Secret。

## 注入与核对

`Conduit Build` 和 `Build (Reusable)` 均将仓库 Secret 映射为 `PCL_CURSEFORGE_API_KEY`，通过源码生成器写入 `PCL.Core.dll`。手动草稿发行复用 `Conduit Build`，沿用同一路径。

源码生成器只允许 `PCL_MS_CLIENT_ID`、`PCL_CURSEFORGE_API_KEY` 和 `PCL_GITHUB_SHA` 三项值进入客户端，排除其他 `PCL_*` 服务令牌与私钥。本地打包会清理旧编译结果并关闭共享编译；通用 CI 在全新检出中关闭共享编译。

编译完成后，两个入口调用 `scripts/conduit/Assert-ClientCredentials.ps1`，在最终核心程序集内查找已配置值的 UTF-16 字符串。缺少已配置值即使构建成功也会失败；输出只包含是否已配置、是否注入，不回显值。没有仓库 Secrets 的 Fork PR 仍可编译，检查不会把缺失的值表述为已注入。

本地构建的 `BUILD-INFO.txt` 额外记录 `CurseForge API key embedding verified`。此结果核对实际产物，不能用于证明 Key 无法被提取。

## 验证范围

源码生成器回归使用伪造值，验证三项已配置内容会进入生成源码及程序集、无关服务令牌不会进入，以及关闭生成时不捕获环境变量。真实 Key 的产物检查只报告布尔结果。

本机验证：核心离线测试 374 通过、1 项平台能力跳过、0 失败；WPF / 模型回归 8 项通过；配置解析 13 组检查通过。产物核对器的成功、缺失值且不回显、无 Secret 三种情况通过。Release 自包含打包的三项实际注入检查均为 `True`，`BUILD-INFO.txt` 中的 CurseForge 注入核对也为 `True`。

原先的账户布局、3D 预览和 Mod 下载优先级保持原有实现，继续等待 PR #12 审查合并。

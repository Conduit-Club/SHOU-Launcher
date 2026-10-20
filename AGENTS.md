# 社团启动器开发约定

## 项目定位与历史

- 本仓库是 Conduit-Club 独立维护的 **SHOU-PCL**，历史上基于 PCL-Community/PCL-CE 开发。
- `origin` 为 `https://github.com/Conduit-Club/SHOU-PCL.git`。`main` 是默认稳定分支，`dev` 是开发与候选发行分支；通过 `dev → main` PR 审查整合。
- 2026-10-20 最后一次纳入的 PCL-CE `dev` 源码基线为 `f00b458e7bcf1368ea4e00148cf5db09bd1f5133`。此后不自动 fetch、同步或合并原上游，也不要求配置 `upstream` 远程。
- 按维护者决定，旧历史已迁移为由 Moeary 创建的源码快照；快照署名表示导入者，不表示原有代码均为其原创。来源、原作者与许可证继续保留，详见 `docs/conduit/HISTORY.md`。

## 开发前检查

1. 阅读本文件、`CONTRIBUTING.md` 及相关模块文档，检查 `git status --short --branch` 和 `git remote -v`。
2. 保留任务无关的现有改动；需要联网同步时仅默认使用本社团的 `origin`。
3. 功能分支默认使用 `features/` 前缀。未经用户要求，不提交、推送、改写历史、合并 PR、部署或正式发布。
4. 排查和评审默认只分析。不要执行破坏性删除、强推或 `git reset --hard`，除非用户明确授权相应操作。

## 开发与验证

- 使用原生 Windows / PowerShell 7，优先使用 `rg` / `rg --files`。
- .NET 10 SDK 位于 `D:\Programs_Dev\dotnet`；NuGet 缓存位于 `D:\DevCache\nuget`。
- 构建方式见 `docs/conduit/BUILD.md`，发行构建入口为 `scripts/conduit/Publish.ps1`。
- 社团配置、资源和逻辑优先放入独立模块，避免无关重命名、全仓格式化及批量换行修改。
- 保留原有许可证、作者署名、赞助链接及第三方声明；社团发行物明确标注独立第三方版本。
- 发布只使用社团仓库的目标与凭据，不启用原 CE 专用签名、MirrorChyan 或其他上游发行服务。
- 不提交密钥、缓存或构建产物；缺少外部服务凭据时如实报告限制。
- 修改后运行相称的构建与测试；构建通过不代表真实账户登录、下载或 Minecraft 启动已验证。
- 默认中文沟通，自称“吾”、称用户“指挥官”，技术说明准确简洁。

## 交付

- 报告修改范围、冲突处理、验证结果和已知限制。
- 发布时记录源码提交、SDK 版本、架构、构建配置及 SHA-256；直接基于 `dev` 的发行默认作为预发布，除非用户明确要求正式发行。

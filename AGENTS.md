# 社团启动器开发约定

## 项目定位

- 本仓库是 Conduit-Club 基于 **PCL-Community/PCL-CE** 开发的社团 Minecraft 魔改启动器。
- 唯一直接上游为 `https://github.com/PCL-Community/PCL-CE.git` 的 `dev` 分支，远程名为 `upstream`。
- `origin` 为 `https://github.com/Conduit-Club/PCL-CE-SHOU.git`。不要直接同步、合并或移植 Meloong-Git/PCL；相关演进由 PCL-CE 承接。
- 社团主页、资源、服务器、品牌等内容按指挥官后续要求逐项加入；基础设施准备阶段不提前修改这些内容。

## 开发前先同步 PCL-CE

1. 先阅读本文件和 `CONTRIBUTING.md`，检查 `git status --short --branch` 与 `git remote -v`，保留任务无关的改动。
2. 确认远程地址，缺少 `upstream` 时添加上述 PCL-CE 地址；执行 `git fetch upstream` 和 `git fetch origin`。
3. 检查 `git log --oneline HEAD..upstream/dev` 与 `git rev-list --left-right --count HEAD...upstream/dev`。
4. 在工作区干净时优先同步上游：可快进则 `git merge --ff-only upstream/dev`；已有社团提交产生分叉时，先用 `git merge-tree --write-tree HEAD upstream/dev` 预检，再进行正常合并，保留上游历史。
5. 发生冲突时，报告文件、上游变化、社团需求和处理方案。不得以整文件覆盖、丢弃社团功能或强推来掩盖冲突。难以判断的业务冲突先说明再处理。
6. 无法联网或不能安全同步时，明确记录未同步的原因和当前上游提交，不要声称已同步最新代码。

## 降低后续冲突

- 优先新增独立的社团配置、资源、模块和脚本，以最小接入点连接上游。
- 避免无关重命名、全仓格式化、批量换行修改和上游核心逻辑重写。
- 同步上游与社团功能改动分开提交；功能分支默认使用 `features/` 前缀。
- 保留原有许可证、作者署名、赞助链接及第三方声明。社团发行物应明确标注独立第三方版本。
- 发布配置使用社团仓库的目标和凭据，不调用上游专用的签名、MirrorChyan 或外部发布服务。
- 不提交密钥、缓存、构建产物；外部服务凭据缺失时如实说明功能限制。

## 本机开发与验证

- 使用原生 Windows / PowerShell 7，优先 `rg` / `rg --files` 搜索。
- .NET 10 SDK 安装在 `D:\Programs_Dev\dotnet`；NuGet 缓存使用 `D:\DevCache\nuget`。
- 构建方式见 `docs/conduit/BUILD.md`，社团构建入口为 `scripts/conduit/Publish.ps1`。
- 修改后运行与范围相称的编译、测试，说明未运行、失败及未验证项目；构建成功不等于登录、下载和游戏启动已验证。
- 默认中文沟通；自称“吾”、称用户“指挥官”，技术说明保持准确简洁。
- 排查和评审默认只分析；未经明确要求不提交、推送、部署、发布或外发消息。不使用破坏性删除、强推或 `git reset --hard`。

## 交付

- 报告修改文件、上游基线、冲突情况、验证结果及已知限制。
- 发布时记录源码提交、SDK 版本、架构、构建配置及 SHA-256；开发分支基线默认作为预发布。

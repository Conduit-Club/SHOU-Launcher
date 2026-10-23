# 遗漏清理、下载源与 CI 产物调整

基于 `dev` 的 `18ad0e73`，本轮不处理已有编译 warning，也不提交、推送或正式发布。

## 下载源

实测 BMCLAPI 与 Mojang 各返回 920 个版本，SHOU-MCMirror 返回 11 个版本（2026-10-23）。没有复现“BMCLAPI 正常返回后又被社团源覆盖”；已通过失败回归测试复现：首选源报错时，原逻辑立即接受 SHOU 精选清单，跳过仍可用的 Mojang 完整清单。

现在版本浏览先按优先级尝试完整清单，再回退到精选清单；已指定版本的元数据和文件下载仍按用户设置的源顺序处理。不按条目数量判断清单有效性，也不合并不同来源的版本元数据哈希。保存设置会中止旧清单请求并清除缓存，避免旧排序下的请求完成后继续被复用。日志记录最终来源、版本数及各源失败原因。

BMCLAPI 的公开 Minecraft 下载接口不需要申请 API Key。CurseForge 是单独的服务，需要项目自己的授权 Key。

## 联机与隐私

- 自定义节点实际接入 EasyTier 启动参数，新增“仅使用自定义节点”设置；移除动态公共节点列表读取。
- 删除 Natayark 登录及自动令牌刷新入口，昵称使用本地设置或当前游戏账户。
- 网络协议改用进程内随机会话标识；本地硬件识别码与账户加密兼容处理保持原用途。
- EasyTier 从官方 GitHub Releases 下载，移除旧 CE 镜像地址。
- 删除未使用的 CE 更新／公告实现，更新操作链接与请求头品牌，修订四种语言的隐私文案。
- 旧 CE 公告预设自动迁移到默认主页。其他第三方主页预设仍由用户选择使用。

## 构建与发行

版本从 `metadata.json` 读取，并与 `ClubRelease.CurrentVersion` 校验。ZIP、SHA-256 文件与 Actions artifact 使用相同的版本标识：

- 开发：`SHOU-Launcher-win-x64-v1.3.0-dev.<run>.g<sha>.zip`。
- 通用 CI：`SHOU-Launcher-win-x64-v1.3.0-ci.<run>.g<sha>`，仍是非自包含编译产物。
- 正式：`SHOU-Launcher-win-x64-v1.3.0.zip`，须显式指定与源码匹配且属于 `origin/main` 的版本标签。

社团 ZIP 在所有回归通过后上传。手动 `Conduit Release Draft` 工作流校验已有标签、复用完整构建测试，再创建草稿发行；不会自动公开发行、移动标签或覆盖已有 Release。删除原 CE 的发行与 MirrorChyan 工作流。

`CURSEFORGE_API_KEY` 映射为 `PCL_CURSEFORGE_API_KEY`，打包脚本仅允许微软 Client ID、获准的 CurseForge Key 和源码 SHA 注入；其他环境变量仍被隔离。Key 缺失时仍允许编译，不使用占位值。批准前不声称官方 CurseForge 功能已完成联调。该 API Key 会进入客户端二进制，不能作为只有服务端才可持有的秘密使用；不要注入 OAuth Client Secret、个人 GitHub Token 或私钥。

隐私说明见 [PRIVACY.md](PRIVACY.md)。真实两端联机、登录、完整游戏下载与运行，以及未提交 workflow 的 GitHub Actions 执行，仍需后续验证。

## 本机验证结果

- 先运行失败回归，确认首选完整清单出错后，旧逻辑误选 SHOU 精选清单；修复后对应测试通过。
- 离线核心测试：355 通过、1 项因平台不支持 ChaCha20 跳过。
- WPF 编译 XAML 与交互回归：1 通过，覆盖新增节点设置、无原仓库标签的社团反馈条目、保存排序时旧请求完成的缓存竞态。
- 在线清单测试：1 通过，来源 BMCLAPI，920 个版本。
- x64 Beta 自包含 ZIP 成功，旁置 SHA-256 与文件实算值一致；ARM64 CI 编译 0 错误。没有进行 ARM64 真机运行验证。
- actionlint、PowerShell 语法、错误构建后缀／标签拒绝检查及本地化 XML 检查通过。新的 GitHub Actions 尚未执行。

测试报告保存在本机 `artifacts/cleanup-source-tests/`；候选包在 `artifacts/cleanup-publish-final/`。这些产物来自未提交工作区，不是正式发行。

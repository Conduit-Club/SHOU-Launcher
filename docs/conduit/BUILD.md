# Conduit-Club 构建

当前发行入口是共享 Avalonia Desktop：Windows x64、Windows ARM64 和 Apple Silicon macOS。旧 WPF 工程与构建脚本保留作回归和历史参考，不再供新版 Release 取包。开发基线为 `dev`，稳定分支为 `main`；当前目标为 **1.6.0 / xb**。

## Desktop 本地构建

使用仓库锁定的 .NET SDK 10.0.403、PowerShell 7 和 Python 3；Java 21 用于不启动游戏的加载器/协议回归。任务工具和依赖缓存放入 `.shou-dev`，使用 [任务入口](../../scripts/dev/Invoke-Dev.ps1) 和现有工具注册。

```powershell
./scripts/dev/Invoke-Dev.ps1 -TaskRoot <已注册任务目录> -RunId desktop-tests -Action Test -EntryProject tests/SHOU.Launcher.Desktop.Tests/SHOU.Launcher.Desktop.Tests.csproj -RuntimeIdentifier win-x64 -Configuration Release
./scripts/dev/Invoke-Dev.ps1 -TaskRoot <已注册任务目录> -RunId candidate -Action Publish -RuntimeIdentifier win-x64 -Configuration Release
```

macOS 将 RID 改为 `osx-arm64`；Windows ARM64 使用 `win-arm64`。`Invoke-Dev.ps1` 通过任务门禁执行 `Validate-Desktop.ps1` / `Publish-Desktop.ps1`，保存构建记录、TRX 和包旁元数据；`Initialize-CI.ps1` 仅用于临时 GitHub runner，不能在本机运行或替换现有控制器。已有本机工具注册和空间策略应继续复用，不能因打包绕过空间门禁。

Windows ZIP 只含一个自包含 `SHOU.Launcher.Desktop.exe`，无需用户复制散落 DLL 或安装 .NET。macOS ZIP 含签名的 `SHOU Launcher.app` 和固定凭据组件。普通候选名带源码短 SHA；每包旁置 SHA-256 与 `BUILD-INFO.json`，记录完整源码、版本、代号、SDK、RID、配置、凭据注入布尔结果和 Windows 内嵌负载信息。ARM64 交叉编译不代替 ARM64 真机验收。

## 凭据与 Mac 签名

复制 `.env.example` 为根目录 `.env`，填写公开的 Microsoft Client ID 与项目获准的 CurseForge API Key；也可用进程环境，后者优先。`Publish-Desktop.ps1 -EnvFile <路径>` 明确指定本机文件。解析器只读取受支持键，不执行命令、插值或表达式，保留 Key 中的 `$`；不注入 Client Secret、GitHub Token 或服务私钥。`.env` 不提交、不上传为产物，也不自动同步云端。

Actions 的仓库 Secrets 使用 `CLIENT_ID`、`CURSEFORGE_API_KEY`、`MACOS_SIGNING_CERTIFICATE_BASE64` 和 `MACOS_SIGNING_PASSWORD`。PR（包括同仓 PR）不注入秘密或固定签名，仍运行双端编译/测试，Mac 使用显式临时签名。受信 push / 手动 / 标签构建注入客户端配置；Mac 临时导入固定证书，检查稳定凭据组件和真实包跨更新回读，最后清理 CI 钥匙串。证书和 P12 不进入 artifact。

项目 Key 会随客户端分发，GitHub Secrets 保护构建输入，不能使客户端内的 Key 保密。详细边界见 [凭据构建说明](CLIENT-CREDENTIALS-20261031.md)。Mac 固定证书、冻结组件与首次旧项授权见 [Mac 签名说明](MACOS-SIGNING.md)。当前没有 Apple Developer ID 公证或 Windows Authenticode；完整性哈希不等于平台认证。

## CI 与发行

- `Shared Desktop migration`：面向 dev/main 的 PR、dev/main 及保留的移植分支 push 都构建双端；受信构建启用固定 Mac 签名。执行 Core、Headless、发行策略/收集回归，产出三个 RID；Windows 同时检查单 EXE 与内嵌负载。
- `Legacy WPF regression`：保留旧工程验证包，不参与新版 Release 资产选择。
- `Conduit Desktop Release Draft`：版本标签 push，或手动选择已有标签；复用同一 Desktop 构建工作流，只有两个宿主的编译、测试、签名与打包全部通过才收集资产。

稳定包必须 `Release`、精确 `v<三段版本>` 标签指向所构建源码，且源码属于 `origin/main`。预发布显式使用 `-Prerelease`，允许同版本的 `v1.6`（仅源码补丁号为 0）或带后缀标签，不要求提前合并 main。`v1.6` 对应程序版本 **1.6.0 / xb**；GitHub 必须标为 Prerelease，不能设为 Latest。用户本轮要求暂不合并 PR，因此此标签从移植功能分支创建。

`Collect-ReleaseAssets.ps1` 只从 Actions 产物的 `dist` 收集包，拒绝测试夹具、缺平台、混合源码/配置、散落 DLL、错误 PE 架构、损坏哈希、变化的固定凭据组件或不一致的 Mac 构建信息。全部验证后生成三包、旁置构建/签名信息、`SHA256SUMS.txt` 与 `RELEASE-MANIFEST.json`。先创建**草稿**，不覆盖已有发行、不自动公开；维护者核对资产后再将预发布公开，正式发行仍遵循 dev → main 审查流程。

工作流进入分支的代码不代表分支已推送或被验证。首次运行结果和未完成原生验收见 [R55](MACOS-R55.md)、[v1.6.0 说明](RELEASE-v1.6.0.md) 与 [todo](../../todo.md)。不启动 Minecraft 的编译、Headless、凭据专项不代替完整游戏/真机矩阵。

## 旧 WPF 构建（历史参考）

```powershell
./scripts/conduit/Publish.ps1 -Configuration Release -Dotnet <dotnet.exe> -OutputRoot artifacts/legacy-verification
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Beta -p:Platform=x64
```

旧脚本继续保留原元数据、凭据注入校验和 main 正式标签策略。原 PCL-CE 来源和迁移历史仅见 [HISTORY.md](HISTORY.md)，不再跟踪或同步其上游。

## 首次本机验证（v1.0.0，2026-10-02 历史记录）

- Windows x64 自包含 Beta 发布成功，EXE 约 187 MiB。上游存在 NU1510、可空引用等编译警告，本次未批量修改上游代码来消除警告。
- 全量单元测试共 247 项：199 通过、48 失败、0 跳过。失败包含 SQLite 数据库清理文件占用、语言资源/字体、路径校验、网络及 EventBus 等；这是未修改业务代码的 CE 基线实测结果，尚未逐项修复或确定全部根因。
- 测试报告保存在本机 `artifacts/test-results/conduit-tests.trx`；云端工作流保留测试失败状态，同时在测试前上传成功编译的产物，并单独上传测试报告。
- 本次仅用于验证构建与发布链路，不作为社团正式可用版本；未进行 GUI 和 Minecraft 端到端测试。

## v1.1.0（sxj）候选验证

- 当前 HEAD 的离线全量测试为 315 项：314 项通过、0 项失败、1 项跳过；跳过项为 `TestChaCha20Simple`，原因是测试平台不支持所需的 ChaCha20 硬件能力。
- 此前专项回归为 40 项，其中 39 项 Conduit 测试与 1 项 McPing 测试，全部通过；该统计不把它们表述为认证测试数量。
- LittleSkin 与 MUA 密码登录路线已由用户确认可用；账户刷新、令牌刷新、角色切换和 Minecraft 联机仍未完成全量真实服务联调。
- 更新历史使用现有 `PanMsg` 原生弹层承载内容，不创建额外系统窗口；按钮和启动后提示路径已完成运行验证。

## v1.2.0（zyf）发行

正式包基于合并后的 `main`，使用 Release 配置；`dev` 用于开发与验证。当前仓库为 `Conduit-Club/SHOU-Launcher`；产物命名为 `SHOU-Launcher-win-x64.zip` 及旁置的 `.sha256`。构建信息包含版本、代号、源码 SHA、SDK、架构和配置。发布前应确认 `BUILD-INFO.txt` 中源码状态为 clean，且标签指向实际构建的 main 提交。

## 当前开发构建与手动草稿发行

打包默认使用带版本、构建号和源码短 SHA 的候选名；显式 `-Configuration Release -ReleaseTag v<版本>` 才生成正式文件名，并校验标签指向当前源码且属于 `origin/main`。`Conduit Release Draft` 手动工作流在测试通过后创建草稿，不自动公开或覆盖已有发行。当前源码已删除原 CE / MirrorChyan 发行工作流；上文关于原发行链路的描述保留为历史。详细命名、凭据与验证边界见 [MAINTENANCE-20261023.md](MAINTENANCE-20261023.md)。

# Conduit-Club 构建

本项目是 Conduit-Club 基于 PCL-CE 独立维护的第三方社团 Minecraft 启动器。当前已加入第一阶段社团定制，详见 CUSTOMIZATION.md；微软登录配置见 MICROSOFT-LOGIN.md。

## 上游

- 只同步 `PCL-Community/PCL-CE` 的 `dev`，不直接同步 PCL。
- 2026-10-02 基线：`a42a7699948aebd1e2563df025274af255c1f559`，相对原仓库快进 13 个提交，无合并冲突。
- 直接 PCL-CE 基线版本：`2.15.1`；社团首发元数据版本为 `1.0.0`，正式发行标签使用 `v` 前缀，同时兼容读取旧 `conduit-v` 标签，不冒充 CE 官方发行版。

## 环境

本机 .NET SDK 10.0.401 / Windows Desktop Runtime 10.0.12 位于 `D:\Programs_Dev\dotnet`。
SDK 通过微软官方 `https://dot.net/v1/dotnet-install.ps1` 安装，使用 `-Channel 10.0 -Quality GA -Architecture x64 -InstallDir D:\Programs_Dev\dotnet`。
用户 PATH 和 DOTNET_ROOT 指向该目录；更改后请新开终端。NuGet 缓存位于 `D:\DevCache\nuget`。

```powershell
./scripts/conduit/Publish.ps1 -Dotnet D:\Programs_Dev\dotnet\dotnet.exe
./scripts/conduit/Publish.ps1 -Configuration Release -Dotnet D:\Programs_Dev\dotnet\dotnet.exe -OutputRoot artifacts/conduit-release
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Beta -p:Platform=x64
```

脚本默认构建 Windows x64、Beta、自包含单文件程序；正式首发可传入 `-Configuration Release`，输出到 `artifacts/conduit`（或显式指定的新目录），ZIP 内含 EXE、许可文件及构建信息。无需额外安装 .NET 运行时，不是 MSI/Setup 安装向导。可通过 `-OutputRoot artifacts/conduit-club1` 指定新输出目录；保留旧目录可避免混入旧文件。可使用 `-Architecture ARM64` 交叉编译，但不能替代 ARM64 真机验证。

## 发布

### 本地配置与 GitHub Secrets

复制 `.env.example` 为仓库根目录 `.env`，填写 `PCL_MS_CLIENT_ID=应用程序GUID`。`.env` 及其变体被 Git 忽略，不能放入提交或上传为 artifact。`Publish.ps1` 自动读取它，进程环境中的同名变量优先；不执行文件内的命令、变量插值或表达式。

当前构建入口只允许注入公开的 Microsoft Client ID 和源码 SHA。脚本会临时隔离其他 `PCL_*` 环境变量并在退出时还原，避免上游生成器把无关环境凭据带入客户端；不要填写 Client Secret、GitHub Token 或服务器私钥。每次构建会 clean 并关闭共享编译，防止环境配置变化后沿用旧生成代码。

GitHub Actions 使用仓库 Secret `CLIENT_ID` 映射为 `PCL_MS_CLIENT_ID`。社团工作流在 PR、`dev` push 和手动运行时构建，分开运行社团回归与全量测试；全量测试失败仍保留失败状态和 TRX 报告。Fork PR 无法读取仓库 Secrets，仍可执行无 Client ID 编译。

2026-10-02 已配置 `CLIENT_ID`。其他上游 Secrets（CurseForge、Natayark、联机、遥测等）没有社团自己的可用值，未配置假值，也未复制上游凭据。原 CE GPG / MirrorChyan 发行工作流在社团仓库保持禁用。微软应用注册及注入成功不等于 Minecraft API 审核通过，仍需完成审核与实际登录验证。

`Conduit Build` 工作流可在 GitHub Actions 手动运行，会上传 x64 构建产物，不使用上游密钥或镜像服务。正式 v1.0.0 发布前，先完成 root 代理统一编译、专项测试和实际授权确认；随后再通过 `gh release create` 发布 ZIP 和 SHA-256 文件。当前尚未创建正式 tag 或 Release，不能把开发构建链接当作正式下载地址。

原 CE Release 工作流仅在 `PCL-Community/PCL-CE` 仓库运行，避免重写社团 Release 说明、要求 CE 签名密钥或触发上游 MirrorChyan 上传。这是对上游工作流的少量必要差异，未来同步时需保留并检查。

## 当前限制

- 微软公共 Client ID 可由上述构建入口注入；CurseForge、Natayark/联机等依赖凭据的功能尚不可视为可用，后续应配置社团自己的合法凭据。
- 社团 UI、服务器入口和更新源已完成首轮接入；更新页手动追踪社团 GitHub Releases。
- 未使用上游 GPG 私钥，也未做 Authenticode 签名；SHA-256 仅用于核对文件完整性。
- 编译和单元测试不能替代登录、下载、安装游戏、启动 Minecraft 的端到端验证。

## 首次本机验证（2026-10-02）

- Windows x64 自包含 Beta 发布成功，EXE 约 187 MiB。上游存在 NU1510、可空引用等编译警告，本次未批量修改上游代码来消除警告。
- 全量单元测试共 247 项：199 通过、48 失败、0 跳过。失败包含 SQLite 数据库清理文件占用、语言资源/字体、路径校验、网络及 EventBus 等；这是未修改业务代码的 CE 基线实测结果，尚未逐项修复或确定全部根因。
- 测试报告保存在本机 `artifacts/test-results/conduit-tests.trx`；云端工作流保留测试失败状态，同时在测试前上传成功编译的产物，并单独上传测试报告。
- 本次仅用于验证构建与发布链路，不作为社团正式可用版本；未进行 GUI 和 Minecraft 端到端测试。

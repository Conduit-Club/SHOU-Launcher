# R50：个人 Mac 更新、Windows 单文件与旧数据接续

本轮用户明确要求更新已安装 Mac 应用，并让 Laptop 的现有工程编译/测试 Windows；Windows 应为单个 EXE，放回旧目录后自动沿用实例与可恢复登录凭据。不启动 Minecraft，不合并 PR 或正式发行。

## Mac 当前安装

已将 `/Applications/SHOU Launcher.app` 从源码 e16683c7 经 R49 候选 912888c1 更新到 R50 钥匙串补丁 8cb5cb48，正常路径启动并建立原生窗口。更新时先正常退出旧进程，保存完整旧应用 ZIP 和原配置备份，逐文件哈希/ZIP CRC 核对后原子替换。安装期间 3 个账户、1 个 XPlus 实例及配置均保留。

两次完整应用和配置备份分别位于 `~/Library/Application Support/SHOU-Launcher/backups/app-update-r50-20261113-110159`、`app-update-r50-20261113-115553`；配置备份权限为 0600、目录为 0700，未导出钥匙串秘密。用户应用与实例没有用于整合包测试。

原生启动后旧登录项读取失败，适配器的后台读取强制禁止交互。显式恢复触发系统授权并在用户完成后成功读回，确认本次确有授权衔接问题。恢复入口补丁已增加：普通后台启动仍不会弹窗；用户明确点击“重新授权钥匙串”时，仅为读取原有同作用域项启用系统交互，随后恢复进程原交互状态。读取后校验账户身份、清除临时字节并恢复内存会话，不为此重写原记录或轮换 token。拒绝、取消、数据不符时保留原记录；未完成全部读取时仍保护账户持久写入。该能力仅由 macOS 适配器提供，不改变 Windows DPAPI 行为。

用户先完成系统授权；首次重开发现另一个登录项仍需授权，补齐该项的“始终允许”后再次正常重开，两项 Microsoft 凭据均自动读取，恢复提示消失。随后通过安装包内置账户菜单真实刷新所选 Microsoft 登录，令牌有效期推进、皮肤 3D 预览恢复可用，未重新登录、未启动游戏。安装与只读恢复期间配置哈希不变；明确点击刷新后由正常流程更新所选账户元数据和受保护令牌，3 个原账户 ID 与 1 个实例记录保持。未导出任何钥匙串秘密。

当前候选仍为临时本地签名。未来更换签名的构建可能再次触发系统对已有项目的确认；本轮不通过放宽钥匙串 ACL 或绕过系统授权来掩盖此情况。

Apple 的 [kSecUseAuthenticationUIFail 说明](https://developer.apple.com/documentation/security/ksecuseauthenticationuifail)明确指出：需要用户认证时，该策略返回交互不允许错误。补丁为用户操作提供系统授权入口，没有修改钥匙串 ACL、关闭证书校验或导出凭据。

## Windows 并行验证

复用 Laptop 的“整理各页面 UI 截图”会话、工程 `D:/Code/C#/SHOU-Launcher-Windows`。起点为 origin/features/windows-parity 的 5268c28。父任务未修改 Windows 启动、打包与旧数据迁移文件，Mac 修复单独保留待接入。

共享 `smb://100.127.0.5/PCL-CE-SHOU` 已通过 Finder 挂载到 `/Volumes/PCL-CE-SHOU`；本轮交接目录为 `artifacts/windows-r50-20261113/handoff`。源码 bundle、候选和脱敏报告通过共享交换；真实账户秘密只在 Laptop 原用户上下文使用。

Laptop 交接已核对：34 个实例、6 个账户，DPAPI 可保存和重读，1 个 Microsoft 账户完成实际刷新、所有权及 profile 验证；原 Profile 与 UserKey.bin 哈希不变。Windows 200% DPI 实窗走查首页、版本目录、实例模组/资源包、设置、工具和崩溃分析；两版候选四次正常退出均 exit0，重开时 4 个配置/实例源文件哈希不变。真实旧数据验证在原 Windows 用户的隔离服务宿主完成；原生 UI 使用虚构账户和隔离实例，未覆盖真实旧 WPF 启动器。

单文件实现按 [Microsoft 单文件部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file)处理：分发 EXE 内包含应用和运行时依赖，原生库可在运行时解压到缓存；目录定位需使用单文件入口所在目录，不能依赖空的 Assembly.Location。分发目录和 ZIP 运行载荷均只有 `SHOU.Launcher.Desktop.exe`，无散放 DLL。实际原生库从受控解压缓存载入，新的缓存目录及不存在的 DOTNET_ROOT 下四个独立命令均通过。EXE 内嵌图片、三语、Java bootstrap 和许可材料；本轮没有更改 Mac 应用包形式。

## 本轮证据

`.shou-dev/reports/macos-r50-20261113/` 保存安装前后信息、原配置哈希、Mac 凭据恢复/取消/原生交互范围测试与桌面回归。Windows 的 170 项日志/TRX/原生证据与 25 张截图位于 `windows-r50-evidence.zip`；交接文件大小和 SHA-256、ZIP CRC、包内单 EXE 与裸 EXE 一致性均已由 Mac 复核，未带用户凭据数据。

Mac 安装源提交 8cb5cb48 的 Core 最终全量 910 通过、5 个平台/环境条件跳过；恢复/原生交互专项 9 项通过。Desktop 全量 364 项通过。首次编译修复和原始报告均保留。

Mac 安装 ZIP：`SHOU-Launcher-osx-arm64-v1.5.0-dev.g8cb5cb48.zip`，54,072,077 字节；SHA-256 `9cf4326798345f6ebf79a83bd24187d4f237203b0f3d8d0005d400700dca5582`。构建使用 .NET SDK 10.0.403、Release/osx-arm64，带现有编译环境配置。打包时源码无未提交的产品修改，BUILD-INFO 的 source_dirty=true 来自保留的未跟踪计划及本轮报告草稿。安装包继续留在 `.shou-dev/dist/MACOS-R50-20261113/rc1/`。

## 两端整合与交付

Windows 源码从基点 5268c283 通过 27,938 字节增量 bundle 交回，头为 aec07e1a；包含 e1286dc4 产品源码及后续测试/报告。Mac 的 8cb5cb48 与其在当前功能分支整合为 `7002883febf7e8be0fc1c80d7cd546c991d7b1ab`，无冲突、无改写历史，未触及 dev/main 或合并 PR。整合后 Mac Core 全量 917 通过/6 条件跳过、Desktop 364 全通过；同一整合源码的 Windows x64 交叉编译通过，0 警告/错误（不替代 Laptop 原生证据）。实际游戏不在本轮授权内。

Windows 候选源为 `e1286dc4e07e2ec19a458cb1867cd8f144f427e6`；Mac 安装源为上方 8cb5cb48，两份候选分别对应已验收的原生构建，不能将两者误写成整合提交的同一构建。Windows EXE 为 118,917,066 字节，SHA-256 `321e6329e41342ca72027c8e2eade040dd74df5ed82416e505964b61dfe5fc80`，位于 SMB 交接根的 `candidate/SHOU.Launcher.Desktop.exe`；ZIP SHA-256 `fb41fbd87e33c8b65dd8f96f9bb8c527be99e603466a92cb9ee0fdb86ef048fe`。Windows Core 全量 899 通过/18 条件跳过、最后路径回归 7 项通过、Desktop 363 全通过。更多说明见 [Windows R50](WINDOWS-R50.md)。

截图复核发现：`screens/final-mods.png` 的详情页右上角未见三按钮，而 `screens/final-home.png` 首页三按钮可见。当前代码仍定义三按钮，首页实际最小化/最大化/还原已验；需另以 Windows 实窗核实详情页是否隐藏/越界或捕获差异，不能以 UIA Invoke 正常关闭代替可见性验收。本轮记录为待办，未当作已修复。

剩余重点：Windows 详情页三按钮可见性；正式签名及新机器/ARM64；更多历史缓存、第三方 provider、失效凭据/断网与旧目录布局。Mac 临时签名变化可能再次需系统授权。R49 的整合包样本与实际游戏门禁继续保留。

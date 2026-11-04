# Windows/macOS 共用 UI 迁移

状态：RUNNING，S0/S1 B0 已完成首轮编译与生成器测试，正在分发。真实基线 `b97b565678cbcdc8f602b57973d0837d3ac556a2`；integration 分支 `features/macos-arm64`，所有修改尚未提交。

本轮使用同一 .NET 10.0.403 + Avalonia 12.1.5 Desktop；保留 WPF 默认入口。所有权见 ../../OWNERSHIP.md，接口见 ../../CONTRACTS.md。工具与重任务门禁由 `.shou-dev/control/dev.py` 提供。

预检已通过，但预检 fixture 不作为产品候选。当前原生 Windows/WPF/DPAPI 门禁 BLOCKED（无主机），远程 CI 未授权，真实账户/社团服与签名公证单列。不得把本地交叉编译当成 Windows 实窗通过。

当前动作：建立共享工程、生成器/metadata 契约、构建路径核验及统一发布入口；B0 patch 核查后允许两个 worker 写入。总进度、真实命令和交接见 `.shou-dev/reports/night/PROGRESS.json` 与 role handoffs。

## B0 检查点（2026-11-04）

- 主agent已建立同一 Core/Desktop、生成器、共享 DTO、Program/Bootstrap 和统一构建脚本；API 以 CONTRACTS.md 为准。
- Desktop Release/osx-arm64 编译：PASS，0警告0错误；Core真实测试：PASS，3/3，含metadata精确资源名、生成注册和无WPF程序集依赖。
- 真实命令：`control/dev.py --worker main --job b0-build … -- pwsh -File scripts/dev/Validate-Desktop.ps1 -Action Build …`；报告：`.shou-dev/build/main/b0/Desktop/Release/osx-arm64/default/reports/`。Core测试报告在对应 `Tests/…/reports/tests.trx`。
- B0仍是共享架构检查点，六页业务/平台/游戏链尚未接入，不能当成产品候选。UI、platform只读梳理均完成；之后按精确patch并行实现。
- 权限已更新Full Access/never；没有任何Git提交或远程写授权。三棵工作树HEAD仍为审计基线，dirty内容单独以patch+hash记录。

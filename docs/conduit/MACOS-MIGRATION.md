# Windows/macOS 共用 UI 迁移

状态：RUNNING，B1–B4 UI/平台增量已顺序整合，Core正式业务链与Host已接通。真实基线 `b97b565678cbcdc8f602b57973d0837d3ac556a2`；integration 分支 `features/macos-arm64`，B0本地提交为 `53d31370c47e76deee7cf1644b22b8e6a6e1d3b0`；后续源码与精确dirty检查点见总报告。

本轮使用同一 .NET 10.0.403 + Avalonia 12.1.5 Desktop；保留 WPF 默认入口。所有权见 ../../OWNERSHIP.md，接口见 ../../CONTRACTS.md。工具与重任务门禁由 `.shou-dev/control/dev.py` 提供。

预检已通过，但预检 fixture 不作为产品候选。当前原生 Windows/WPF/DPAPI 门禁 BLOCKED（无主机），远程 CI 未授权，真实账户/社团服与签名公证单列。不得把本地交叉编译当成 Windows 实窗通过。

当前动作：完成集成回归，从统一入口发布实际产品候选，然后验证原生窗口与隔离Minecraft。总进度、真实命令和交接见 `.shou-dev/reports/night/PROGRESS.json` 与 role handoffs。

## B0 检查点（2026-11-04）

- 主agent已建立同一 Core/Desktop、生成器、共享 DTO、Program/Bootstrap 和统一构建脚本；API 以 CONTRACTS.md 为准。
- Desktop Release/osx-arm64 编译：PASS，0警告0错误；Core真实测试：PASS，3/3，含metadata精确资源名、生成注册和无WPF程序集依赖。
- 真实命令：`control/dev.py --worker main --job b0-build … -- pwsh -File scripts/dev/Validate-Desktop.ps1 -Action Build …`；报告：`.shou-dev/build/main/b0/Desktop/Release/osx-arm64/default/reports/`。Core测试报告在对应 `Tests/…/reports/tests.trx`。
- B0仍是共享架构检查点，六页业务/平台/游戏链尚未接入，不能当成产品候选。UI、platform只读梳理均完成；之后按精确patch并行实现。
- 权限为Full Access/never；指挥官现已允许三功能分支内的本地阶段提交，仍不推送、合并、改写历史或发行。初始基线保持审计SHA，当前HEAD与dirty内容另列；交接继续使用精确patch+hash，主agent顺序应用后独立提交。

当前下一步：完成metadata/download逻辑回归，接库/assets/LaunchPlan，顺序复测统一发布；UI/platform在独立工作树实现B1。测试失败及脚本修复见PROGRESS.json，未将失败标为通过。

## B1 接线（进行中）

主agent已实现metadata/rules/继承/libraries/assets/校验下载/LaunchPlan、配置sidecar、认证协议与生产启动编排；Core集成实测46/46通过（0警告/错误）。平台B1的12个文件与UI B1的26个文件均已按冻结patch应用并逐文件hash核对。UI独立Headless18/18通过，集成复测进行中。账户/网络模块正由UI worker只读审查；平台worker已审查游戏Core，三项P1均已修并纳入46项回归。

平台完整工厂、runtime安装、Keychain与DPAPI尚在B2；真实窗口/游戏主菜单尚未验收，Windows默认入口为NOT_READY。所有数量按各次报告记录，未将worker与集成重叠测试累加。

## B2 实窗前门禁（进行中）

平台B2已顺序整合（9文件hash匹配），含真实Keychain/DPAPI新store、官方runtime安装器和组合工厂；worker37/37通过。UI B2角色选择与取消26/26通过，等待父分支复测。账户审查4项P1与3项P2均接受并修复：单角色token绑定、保护身份来源、下载task归属、设置事务、原子网络快照、进程代际、Xuid。

Windows x64统一发布/最终owner校验通过，归档在dist/B1-architecture，仅为架构包；Bootstrap尚未接通时不算功能候选。Mac打包依赖检查已修，PE DLL子组件签名需求已用独立fixture验证；正式重跑在后续记录。真实产品实窗、游戏主菜单仍NOT_RUN；旧Windows数据接管NOT_READY。

## B1–B4 集成检查点（2026-11-05 00:47 SGT）

Core集成83/83及新增官方1.21.1回归后的86/86均实测通过，0警告0错误；精确命令与退出码在reports/night/MVP/core-run.json及official-core-run.json。相同Core中间根复用，未用新obj掩盖回归。后一次TRX覆盖同路径，前次结果保留在独立命令日志。官方fixture来自Mojang原始metadata与assets索引，核对SHA-1，生产resolver与独立预期比对63库、16个普通native artifact、Java21/runtime-delta、3888个去重assets；这些测试不表示真实游戏已启动。

平台B3只读Windows旧凭据兼容读取与B4原生macOS路径规范化已整合；B4三文件SHA-256精确匹配。UI B2的26项Headless正在集成分支复测。新增构建/打包脚本PowerShell语法检查11/11通过；旧WPF发布仅可做本地交叉验证，仍需Windows真机。Mac逐文件签名、bundle签名和ditto归档结构fixture已经通过，正式产品包尚待本节之后的实际记录。

## 产品与配置验证（2026-11-05 01:35 SGT）

集成实现提交为`c9b9e094a4cf3103abec8290261be1b7622b88c8`，快速退出状态竞态及许可目录布局修复提交为`30fa63f10f1ba7a81706db7719a451797849c8d4`。Core87/87、共用UI Headless26/26已实测。正式Mac ARM64归档位于任务根`dist/MVP-fix1/Desktop/Release/osx-arm64/default`，附SHA-256、BUILD-INFO与native依赖报告。该包经统一restore→clean→publish→实际owner校验→完整许可→逐文件/bundle ad-hoc签名→归档，最终zip用ditto解压到中文空格路径后严格验签通过。它是本地未公证候选。

清除构建PCL变量并移除SDK/pwsh所在PATH后，最终包diagnostics启动成功，加载自身.NET10.0.14与新Core，真实发现并probe Temurin21 ARM64。首次原生Avalonia启动在`AvaloniaNativeRenderTimer.EnsureRegistered`失败，尚未创建App/VM；系统同时报告内置屏幕休眠，与[上游休眠显示定时器问题](https://github.com/AvaloniaUI/Avalonia/issues/18895)一致，但未将相关性冒充最终根因证明。失败命令、脱敏方法栈与CUA尝试均在`reports/night/MVP`。当前L3原生窗口为条件BLOCKED，不使用应用自报窗口状态替代真实观察。

同一正式候选通过生产服务真实安装固定原版1.21.1（client+63库+3888去重assets），生成脱敏LaunchPlan，并启动Java进程至音频引擎和纹理图集初始化。六个实例native库为ARM64，临时Cocoa库含ARM64 slice；所有这些可写文件位于隔离根。CUA没有可用的Java app绑定，未观察可交互主菜单；180秒测试时限后停止自有进程，exit137/cancelled，启动命令exit1。此结果不算L4或正常退出通过，日志/计划/native文件hash保留。

平台B5测试增量已整合：worker真实调用生产runtime安装器和真实probe，官方Java21.0.7+6-LTS ARM64下载约104MB，145文件/205链接逐项校验，第二次只刷新catalog、0 payload复用。在线专项1/1通过；默认禁联网专项明确Inconclusive，无在线目录创建。它使用标明的测试HTTPS适配器，不能代替最终包下载器或游戏主菜单验收。

构建专项实测发现PowerShell把.NET方法的null字符串参数转为空串，原清除方法使存在敏感的PCL_WRITE_SECRET仍保留；共享入口已改Env provider显式移除，缺失与空串分别还原。B05/B06四种开关、白名单及转义，B11四配置runtime读取边界、Debug内置优先，以及源码SHA禁止runtime覆盖共九项实际编译/owner消费已通过（同一compiler中间根）。反射验证器改用实际owner的hash一致独立副本，避开CLR同路径PE映射复用；未更换obj。原失败与后续PASS均保留在`reports/night/BUILD-CONTRACT`。B07完整Desktop A→B→none仍待执行，不在此提前标通过。

新增独立跨平台workflow，保留Windows旧入口gate；reusable legacy构建改走同一Publish链，SDK统一10.0.403。标准runner规格仅列14GB SSD（[GitHub官方资料](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)），因此仅全新ephemeral GitHub任务根显式登记8GiB保留量/10GiB停止线；现有本机night controller仍保持20GiB保留量、10GiB停止线、35GiB任务上限。CI按真实free决定是否放行，不使用收费runner，不绕本机控制器。新增脚本语法与合成空间fixture通过，云端未获运行授权，状态NOT_RUN。

当前下一步：集成Core/Headless回归、构建输入异常/还原检查、本地阶段提交；再冻结同一源码执行B07，随后生成当前Windows x64/ARM64候选。Windows原生、旧数据导入/往返、DPAPI及默认接管仍未完成。

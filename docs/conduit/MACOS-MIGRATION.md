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

## B07 源码不变门禁与恢复目标修正

首轮B07的A包已完成真实owner检查，但正常restore将Core/生成器锁文件从无RID改为win-x64目标，源码指纹检查正确拒绝该轮，未继续把它记为三步通过。保留原ArtifactsRoot与A归档，没有删除obj。新SHOU工程统一声明osx-arm64/win-x64/win-arm64及仅供可移植测试的linux-x64恢复目标；五份锁文件现在均含同一目标集合，避免切换入口/RID时再改写源码锁文件。产品Publish仍只接受Windows/macOS三RID，Linux没有产品宿主支持声明。

修正后Core89通过/1项在线opt-in跳过，Headless26/26通过，均0警告/错误；独立TRX保存于reports/night/P0-GATES。UI B3单文件增量已按hash整合，Application.Name在XAML加载前来自真实metadata/provider，未手写品牌默认值；worker和集成各26项，数量不累加。下一条实际动作是从新干净本地检查点，以原B07中间根和新的fixture输出目录重跑完整A→B→none。

## B07 通过与 P1 接线（2026-11-05 02:55 SGT）

B07完整三步已在`69080e24c9b14f90f53e4d03e98ab7b52ba9e918`实测通过，A/B/none使用同一Desktop、Release、win-x64、同一有效中间根及完全相同源码指纹`f966185089ea73d79d7b53850163f7c87efc871a4a4ab0d1dd0813f41d771a40`。三份实际owner只含对应本轮假配置、none不含A/B，环境精确还原。失败阶段保留原ArtifactsRoot；固定RID目标后SDK使用稳定RID pivot，正式三步没有人为清空或逐次更换obj。报告在`reports/night/B07/stable`，其归档明确仅为配置fixture。

同一干净SHA的真实配置Windows x64与ARM64候选均已生成于`dist/N1/Desktop/Release/<RID>/default`，附SHA-256/BUILD-INFO；实际publish与最终owner均校验通过，Windows真机仍BLOCKED。Mac30fa最终归档的独立进程凭据write/read/delete三步全部PASS（仅唯一隔离假key），不表示跨版本签名连续性通过。

P1已把目标main与全部game args移到受控anonymous stdin。随包Java bootstrap以Java8字节码运行在原main线程，JVM公开argv不含游戏凭据；不用argfile、明文参数文件或raw-argv降级。Java helper由统一构建使用注册JDK编译、hash作为Desktop元数据/嵌入资源身份；初始化获lease后校验并原子提取到缓存，运行时不需要javac。没有helper的真实账户启动明确拒绝。主编排/共享DTO/资源构建由主亲写；平台B6传输/protocol五文件精确patch整合，10项真实Java与传输专项通过。

现代Fabric0.16.14+1.21.1已接生产接口、现有Loader来源fallback、metadata继承与Maven校验；六库采用官方profile声明的SHA1/size，另外两库按来源顺序获取checksum sidecar。UI提供原版/Fabric选择并显示loader，未知loader明确拒绝。既有DownloadSources/ModDownloadSources及CF凭据host边界测试直接复用，无旧WPF/PCL.Core程序集引用。

P1完整Core127通过、1项默认在线opt-in跳过；首轮只有路径负例断言类型错误，生产拒绝行为正确，测试修正后通过。集成Headless28/28通过。平台只读复审指出helper类可被前置库遮蔽及等号classpath覆盖，主已修为受信helper首位并拒绝替代入口/路径形式；后续五项保护回归通过，含真实JVM同名恶意类负例。详细报告在`reports/night/P1`。JDK21编译release8产生三条目标过时兼容提示，与.NET0警告分开记录。下一步是P1正式候选包及从最终归档执行保护后的原版/Fabric安装与进程验证；当前不预报主菜单通过。


## N2/N3 候选与边界验证（2026-11-05）

N2从干净的`853c41e28dd346a4598019b81f7e1f7e273e0ed1`经统一入口生成同一Desktop的Mac ARM64 Beta/Release、Windows x64/ARM64四份候选，BUILD-INFO与SHA-256随包。Mac Release由最终zip重新ditto解包后严格验签；移除构建变量及SDK/pwsh PATH后的diagnostics、同一产物Keychain假键跨进程write/read/delete、真实原版1.21.1与Fabric0.16.14安装/计划全部exit0，证据在`reports/night/N2`。

两种真实Java游戏进程均通过私有stdin引导至游戏渲染/音频初始化，实际OS argv/HotSpot检查不含游戏凭据参数，ARM64由活进程os.arch与加载native佐证。Fabric首轮检查器把官方profile公开的`-DFabricMcEmu= net.minecraft.client.main.Main`误判为实际KnotClient泄漏；首轮FAIL保留，修正为检查所选target后重跑PASS，产品未为此改动。60秒观察上限由启动器停止进程，命令exit1，不能算正常Quit或L4。CUA无Java/Minecraft绑定，显示条件与原生AvaloniaRenderTimer阻塞仍单列；未宣称可交互主菜单。

N3顺序应用UI `90c9706`和platform `de23f5d`的精确增量：联机后端尚未实现，所有非none目的地均禁用启动；aria2仅支持本机RPC，界面与真实Core一致。通用Java保护断言与Mac OS argv专项分开，避免其他OS整项skip。CI固定Temurin21并在临时任务根登记同一JDK别名，wrapper注入JAVA_HOME，helper不再回退系统javac；setup-java Maven输出也在runner.temp，空间计量跳过POSIX链接与Windows reparse。仅本机CI工具注册/alias/gate/编译fixture通过，Actions未运行。

吾亲写的下载修复把aria2输出、控制sidecar、cache命中纳入路径门禁与独占锁，单次捕获网络配置；无哈希目录走内部新请求。内部下载在HTTP await之后、任何截断前复查路径，resume以CreateNew临时文件原子替换，新写来源身份为URL摘要且兼容旧明文身份。阻塞真实HTTP期间替换.part或.resume链接的负例保护独立fixture字节不变。此边界不等同于抵御拥有目录写权限的恶意进程所有原子级双重TOCTOU。RPC是本地协议fixture，未冒充真实aria2二进制。

集成全量Core最终137 passed、1显式OnlineJava opt-in skipped、0 failed；Headless30/30通过，.NET编译零警告/错误。Java8兼容helper由Java21编译仍有3条目标8过时提示，未隐去。不可变TRX、真实命令/退出码与两次worker只读审查在`reports/night/N3`及各handoff目录。后续进入受控Java trust派生副本；绝不原地修复官方runtime后继续宣称原manifest不变。


旧WPF回退已实际交叉发布x64和ARM64：`reports/night/LEGACY/publish-x64-fix1-run.json`与`publish-arm64-run.json`均exit0，实际PCL.Core owner及最终单文件exe均验证三项配置嵌入布尔值，zip/hash/BUILD-INFO齐全。首轮x64编译/publish成功后owner定位失败，发现SDK引用项目实际移除RID；统一脚本现分别评估入口RID与引用RID-less上下文。旧generator去掉无意义nullable tuple转换；旧WPF原有大量告警仍如实保留。此交叉构建不证明WPF/DPAPI/Windows界面能在真机运行，W01–W10仍有主机阻塞与配置导入/往返内部缺口，旧入口继续为默认。


## B8 独立Java信任副本

三角色已整合完整独立派生内核与真实设置入口。源JavaHome只读复制并保留安全相对链接/权限，hash覆盖路径/type/文件bytes/link文本/mode；不复用源硬链接。只允许策略命中的信任文件与精确备份/receipt发生变化，重新扫描保留其他证书。源/输出/provenance全部重核，失败/不完整变体不可被发现或执行；生产公开probe在执行前验证，自身发布probe只在已验树内使用显式runner，未开放公共staging bypass。

新增官方`System.Security.Cryptography.Pkcs 10.0.14`用于纯内存PKCS12 Decode/VerifyMac/Decrypt，拒绝私钥/未知bag与无界KDF声明，仅将X509 CertBag裸DER交给证书读取器。macOS EphemeralKeySet导入与OCTET封装问题的前两轮失败已保留；没有改用DefaultKeySet，也没有把PFX导入临时/持久Keychain。第三轮定点→完整fixture→真实B5 JKS/同副本keytool PKCS12的子命令均exit0。包装器误按MSTest错误notExecuted counter断言导致外层exit1，原记录保留；只读assessment按UnitTestResult确认实际PASS与默认2项SKIPPED，不重写退出码。

主亲写扫描/派生/持久设置编排、CLI隔离入口与事务回归。自动修复偏好默认true，只为新下载官方Java创建派生副本；不写隐藏实例覆盖。无显式Java时优先匹配metadata的完成变体，显式原路径可以选回，既有实例显式覆盖保持。派生变体即使auto=false也必须验provenance，字面路径/实际home逃逸不降成普通Java；观察性日志失败不把已提交切换变成假失败，日志链接不写独立fixture。

集成Core181 PASS、3显式SKIPPED（OnlineJava1、trust interop默认2）、0FAIL；Headless34/34，.NET编译0warning/error。真实副本互操作由worker另表，不与集成重复相加。证据在`reports/night/B8`及platform B8 handoff。已发现macOS Path.GetRelativePath默认大小写比较不适合严格卷边界，B9将独立硬化；本节不宣称已验证case-sensitive卷。最终候选重包与GUI/Windows/外部账户门禁仍按真实层级单列。


## B9 路径与日志边界收束

Java/Platform内不再用macOS默认忽略大小写的Path.GetRelativePath作root门禁。共享JavaPathBoundary复用B4固定arity getattrlist取得现存祖先的实际case/Unicode，再用明确POSIX Ordinal边界；未知后缀不折叠。Core.SafePath委托同一实现，不新增第二份Mac PInvoke；隔离游戏根、派生home/exe返回、literal逃逸归属均重核。当前FS同inode大小写别名、内外链接与相邻前缀已实测；独立case-sensitive卷仍NOT_RUN。

观察性Log在append前拒绝symlink/reparse/父逃逸，也复用单regular+nlink==1身份检查；真实ln负例证明独立保护fixture不会被日志追加。磁盘/权限/PInvoke不可用只保留内存日志，不把已完成配置事务显示为失败。主新增测试首次因漏using无法编译，失败日志保留；仅补导入后全量通过，没有降低生产拒绝语义。平台B9旧broken-link异常断言同样保留首轮FAIL后精确修正。

最终集成Release Core188 passed、3显式skipped（OnlineJava1/trustInterop默认2），Headless34/34；无Secrets的本地CI配置Core187/187并真实编译同一Desktop，均0.NET warning/error。重叠worker专项不累加。证据`reports/night/B9`与两worker只读review/handoff。功能范围到此冻结，仅做同源码B07、最终统一候选、产品包实际验证和交付报告。

## N4 最终候选与分层结论（2026-11-05）

最终产品源码为干净的本地提交`5f99195b95b26cb6c96af8ca67c67775ca44a6ea`，真实基线仍为`b97b565678cbcdc8f602b57973d0837d3ac556a2`。同一Desktop经统一入口依次生成Windows x64 Release、Windows ARM64 Release、Mac ARM64 Beta及Release；全部restore→clean→publish→实际owner程序集验证→打包通过，原`.env`仅以外部只读`-EnvFile`引用，未复制进工作树或报告。归档、SHA-256与BUILD-INFO位于任务根`dist/N4/Desktop/<Configuration>/<RID>/default`。后续文档提交与候选源码SHA分别记录，不把文档HEAD冒充构建来源。

| 候选 | SHA-256 |
|---|---|
| Mac ARM64 Release | `edd3ddba491611fbc6103f21a43176866cfccfbbfc7a797a7781708857875f2f` |
| Mac ARM64 Beta | `cb87ba45402578722b20ff1d371fd57ae7e8d3055eecc4160d20a616e311d7a5` |
| Windows x64 Release | `cadf341f275d0af9f260541d540058b660735cfbcf2177d5c4728b64973b1e14` |
| Windows ARM64 Release | `730a33185d2e6f26c0e731cbb93035bff5064e8df535af090177daf926bf02f1` |

四包均为自包含多文件产物，关闭single-file/trimming/AOT；包含正式图标、资源及23个依赖包许可。Mac采用本地ad-hoc签名，Release最终zip在中文空格目录重新ditto解包后严格验签通过；没有Developer ID或公证。两个Windows包各19个原生PE文件分别匹配x64/ARM64，另有204个managed PE；它们是交叉构建候选，未在Windows执行，未签名。旧WPF x64/ARM64回退包另有较早精确检查点与dirty补丁记录，不能与N4源码身份混用。

B07在最终源码再次完整通过A→B→none三步，仍使用最初登记的`build/main/BUILD-CONTRACT/Desktop/Release/win-x64/B07/B07-Desktop`有效中间根；三步源码指纹均为`6cb952171031ed10f36ac50d3197277a9d45af1d4031621eb03705ae133de42f`，环境精确还原，没有换obj或人工删除缓存来掩盖残留。B07归档仅是假配置fixture，不列为产品候选。

最终Mac Release归档的生产程序在清除PCL/build别名、SDK/pwsh PATH后，通过diagnostics、唯一假键Keychain跨进程write/read/delete、JKS独立副本创建/复用/重验、纯内存PKCS12读取与副本keytool修复。原PKCS12源`cacerts`仍为1814字节，SHA-256保持`491a44284c39806db780678e08d0667911ee25e38f576cd4013d0bee38df433c`；源JDK不被修改。完整来源与输出树hash、provenance和命令保存在`reports/night/N4`。

同一最终包选用其已验证的Microsoft Java21.0.7 ARM64派生副本，真实安装并运行Minecraft1.21.1原版及Fabric0.16.14。两次活进程均见`os.arch=aarch64`、9个任务内native库及受信bootstrap，OS argv/HotSpot command不含目标main、游戏参数或离线档案名；Fabric官方公开兼容property另列，不误判成私有target泄漏。真实游戏日志含loader、音频和纹理图集初始化。每次60秒观察上限由启动器停止自有Java，exit137/cancelled、命令exit1；原始FAIL payload保留，不能算主菜单或正常Quit通过。

原生GUI首轮在显示器休眠时于Avalonia render timer初始化失败。最终一次限时`caffeinate -u`用户活动断言未改持久电源设置或解锁；显示器查询的asleep字段消失，不能单凭字段缺失断言已醒。相同N4包随后自报窗口句柄并按150秒定时exit0，但CUA连接该存活程序的确切.app路径仍返回`cgWindowNotFound`，未取得实际窗口交互或截图，故L3仍BLOCKED。旧CUA记录的写盘时刻晚于游戏退出，已移除不能独立复现的“观察时仍存活”表述，保留时间不确定性。

分层结果为L0编译/包、L1 Core、L2 Headless通过；L3原生GUI、L4可交互游戏主菜单及正常Quit、L5真实账户/入服仍未通过。Windows W01–W10因缺原生主机保持BLOCKED；旧配置/实例/禁用Java往返与生产根双入口接管还存在内部实现缺口，不能全归因于主机缺失。selected-server生产路径未完成，UI明确禁用非none目的地；Forge/NeoForge、完整mod/modpack内容管理及旧Java/LWJGL2兼容矩阵尚未迁移。旧WPF继续为Windows默认入口。

本轮仅在三个指定功能分支进行普通本地阶段提交，未推送、合并、创建PR、打标签、调用远程Actions或发行。原工作区仍为main。最终交付索引在任务根`reports/night/DELIVERY.md`和`delivery.json`，包含六份实际归档的hash、BUILD-INFO、真实测试命令/退出码、L/B/W/D门禁、三角色handoff、精确patch/源码hash与可执行续跑步骤。迁移整体仍未完成，`all_executable_work_done`不得据此改为true。

# R26：在线外观、选择器与窗口关闭

日期：2026-11-10。继续 `features/windows-parity`，不合并 main/dev、不推送、不改变 Windows 默认入口、不启动 Minecraft。

## 尚未接齐的功能与待验范围

1. Microsoft 在线外观已接入共享代码，真实角色上传/披风往返仍待使用用户明确选定的角色、PNG 和披风验收。本轮仅使用合成图片、虚构账户及受控 HTTP，不改真实账户。
2. LittleSkin/MUA 公开外观读取已有实现；写入仍打开对应官网。不能将 Yggdrasil 登录令牌直接当作站点 OAuth 写入凭据。外观备份保留本地原皮肤、模型及披风 ID，直接恢复界面尚未实现。
3. macOS 原生选择器、Cmd+W/退出、关闭写盘与重启状态仍需 Mac 验收。Windows 交叉构建不能替代 AppKit 与 Keychain 的原生行为。
4. Windows 新候选原生文件选择/另存/取消、界面保存设置后的关闭回读，当前被锁屏阻挡；测试进程只使用隔离夹具，没有真实凭据。已完成独立原生进程的非交互关闭重开冒烟，不能替代鼠标和选择器验收。
5. 既有清单中仍有脚本向运行中启动器投递、程序移动重定位、世界导出、模组批量更新/冲突关系、资源包排序/更新、光影实际启用及历史格式兼容缺口。完整 DPI/跨屏/无障碍/SMB 中断矩阵仍未完成。

## 本轮变化

### 在线皮肤与披风

- 新增独立外观写入接口，复用当前用户的受保护会话；凭据只发往固定 Minecraft Services 地址，纹理下载不携带凭据，生产客户端关闭重定向。
- Microsoft 换衣菜单打开共享 3D 预览：选择 PNG、选择经典/纤细模型，然后明确点击“上传到此角色”。64×32 旧皮肤不接受纤细模型；拒绝坏 PNG、超限、动画及异常路径。
- 披风选择只改变预览；“穿戴所选披风”和“卸下当前披风”才写入，只允许官方最新 profile 中拥有的披风。
- 写前核对 UUID，保存旧外观，再次检查取消；相同外观不重复写入。上传后回读皮肤像素/模型或活动披风，核对后才报成功。
- 远端操作不具备本地文件事务语义。请求发出后的断线/取消、5xx、重定向及成功后暂时读不到结果，均保留“结果待核对”状态，不自动重发；必须刷新后才能再次操作。
- 本地备份位于隔离/普通数据根的 `account-appearance/<账户标识哈希>-online-<随机标识>.json`，包含原皮肤、模型、活动/拥有披风 ID 及哈希，不含 token、密码或原始响应。每角色最多 64 份，达到上限先整理再修改，不静默删除。

接口依据为本轮匿名读取的 [Minecraft 官方角色页面脚本](https://www.minecraft.net/msaprofilejs/4b0ceb1d0f5e001c558e_03112026_1141/5031.chunk.ebe410810c8685ecbb97.js)：皮肤使用 multipart `variant`/`file`，披风使用活动披风接口。未读取浏览器账户存储或 Cookie。

### 选择器与导出

- 使用 Avalonia 平台 StorageProvider；PNG 过滤同时声明扩展名、MIME 与 Apple `public.png` UTI，检查平台的打开/另存能力。
- 选图前捕获角色、模型和代次，返回后再次核对；换号、取消和迟到结果不能给新角色上传。
- 导出前捕获所选皮肤字节；另存只接受本机路径，先创建独立暂存、写入并刷新，再核对目录/文件身份并原子替换。坏图、取消、只读和链接目标不会截断原图。云端虚拟文件项不当作可原子替换的本地文件处理。

### 窗口关闭

- 关闭时取消并等待主操作，以及工具、设置和组件任务；30 秒仍未结束则保留窗口并提示重试，不强杀进程。
- 启动器设置草稿提供保存、放弃和返回；验证/保存失败时保留窗口和草稿。
- 实例、运行参数、服务器、存档设置和数据包排序草稿提示返回相应页面保存，避免“保存设置”误写其它实例或世界。未保存草稿放弃只影响当前内存，不删除文件。
- 窗口真正关闭后再释放界面订阅。Bootstrap 仍负责服务、数据互斥和待写盘资源，不向窗口关闭流程添加游戏停止操作。

## 验证记录

- SDK 10.0.403，Avalonia 12.1.5，Release；第一轮 Windows Desktop 构建成功。
- Core 首轮与最终完整回归均为 **652 通过、16 沿用平台跳过、0 失败**。最终包含 5xx 未知结果分类和输入大小前置检查，构建 0 警告/0 错误；首轮新增测试的 3 个过时 DataTestMethod 警告已消除。
- Desktop 首轮：226 通过、1 失败、0 跳过。失败为旧 `ClosingWindowFinishesProfileWaitAndLeavesHostServiceOwnershipIntact` 在取消认证完成后立即检查订阅数，早于新增关闭等待的后续步骤；改为有界等待实际窗口关闭，继续检查挑战结束、订阅清零及服务归属。首失败 TRX/日志保留；R2 完整重跑 **228 通过、0 跳过、0 失败**（补入实例草稿关闭验证），构建 0 警告/0 错误。
- Computer Use：窗口控件可读，截图 `FrameArrived timed out`，点击 `coordinate input geometry is unavailable`；按既有授权尝试 Windows 备用接口，检测到 LockApp 后立即停止输入，已请求解锁。没有点击真实账户授权或外观写入。
- 最终 Windows 候选两个独立进程 PID 47996、3180：原生窗口句柄出现、Ready、Process.CloseMainWindow 正常关闭、exit0；同隔离目录重开，配置 SHA-256 不变。没有鼠标/键盘输入或真实账户写入。初次验收辅助脚本因正反斜杠比较拒绝路径，在任何关闭前退出；将两端都规范为绝对路径并严格匹配后通过，拒绝记录保留。这是辅助脚本问题，不是候选失败。

锁屏输入处理遵循本会话的 [Computer Use 技能](D:/DevCache/Codex/plugins/cache/openai-bundled/computer-use/26.1027.51404/skills/computer-use/SKILL.md)所引用 guidance.md：“If the Windows desktop is locked, stop immediately and ask the user to unlock the desktop.” 同时继续不依赖桌面的构建、测试和交付检查。

## 源码、候选与证据

- 实现提交：`073d31854e13e2ffba33041f77072483c5e03daa`；最终候选源码：`99ef4f2b8c4e1e89c80bba2b0d22dfc99cbe2a22`（补测试空行及最终回归记录，运行时代码相同）。仅本地提交，未推送、合并或发布。
- Windows：win-x64、自包含、Release、SDK 10.0.403、Avalonia 12.1.5；268 个归档/磁盘文件逐一哈希匹配，apphost/CoreCLR/Skia/HarfBuzz 为 AMD64；源码干净，Windows 默认入口仍为 legacy WPF，敏感路径为 0。
- 候选：`artifacts/windows-main-20261105/dist/r26-final-win/Desktop/Release/win-x64/default/SHOU-Launcher-win-x64-v1.5.0-dev.g99ef4f2b.zip`，81,730,672 字节。
- 候选 SHA-256：`e0b7037f8340c5c04aece2113beecbe27d1f439907d8070a078a9d182af0efe8`。
- macOS：同源 `99ef4f2b` 的 osx-arm64 Release 交叉构建成功，0 警告/0 错误；apphost 为 Mach-O ARM64，SHA-256 为 `2f0c280741cd62dcd7565dbdab68abd8d3f35a0652e57f91ec5fd16eef5d3d1c`。未构建/签名 Mac 原生 .app，未在 Mac 运行。
- 非敏感证据：`artifacts/windows-main-20261105/dist/r26-evidence/SHOU-R26-validation-evidence.zip`，512,411 字节、37 项、0 张截图；含控制器、日志、TRX、首次失败/辅助脚本拒绝、原生进程重启、文件哈希、跳过原因和验收源码。SHA-256：`3e41c28caf9089d01788aa9363149a98911a6adf85914ea448a95eff40297cf1`。实际 .env 配置值匹配为 0，值不输出。
- 证据首次预检被扫描器源码中的自身 PEM 检查字面量误拦，归档尚未创建；改为匹配独立 PEM 头行，仍保留真实配置值与凭据形状检查，重跑通过。拒绝记录保留，没有忽略实际密钥匹配。
- 最终归档前任务 30.243 GiB、私有验收正增长 2.000 GiB，合计 32.243 GiB，35 GiB 预算内；证据归档另增不足 0.001 GiB。磁盘可用 238.761 GiB，重任务均已结束。保留本轮全部报告、失败记录、验证数据与候选，无需删除旧版工程或用户数据。

所有 R26 首失败、控制器和 TRX 位于 `artifacts/windows-main-20261105/reports` 与对应 `build/main/r26-*/.../reports`。敏感数据和真实账户缓存不进入证据包；当前只有隔离测试角色，PNG 和备份载荷不放入证据。

Mac 后续使用独立测试角色与 PNG 验证 Finder 打开/另存/取消、换号时迟到选择器结果、Cmd+W/系统退出、草稿保存失败和关闭重开；只在用户指定角色、皮肤和披风后操作真实在线外观。候选和非敏感证据跨机器优先 SMB，复制前后核对 SHA-256，源码经 Git 传递。不启动游戏。

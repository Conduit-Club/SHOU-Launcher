# R50：Windows 单文件与旧数据衔接

2026-11-13。`features/windows-parity` 从 `e6354d9a` 干净快进到 Mac 交接头 `5268c283` 后续作。未合并 dev/main、推送、发布或覆盖真实旧启动器。原工作目录的 AGENTS.md 修改、参考资料和旧 stash 保留。本轮没有启动 Minecraft，也没有改账户皮肤、披风或密码。

## 结果与候选

最终候选源码：`e1286dc4e07e2ec19a458cb1867cd8f144f427e6`。后续报告提交不是候选源码。

- Windows x64 / Release / self-contained single-file；.NET SDK 10.0.403、运行时 10.0.14、Avalonia 12.1.5。源码干净，未签名。
- EXE：`SHOU.Launcher.Desktop.exe`，118,917,066 字节。SHA-256：`321e6329e41342ca72027c8e2eade040dd74df5ed82416e505964b61dfe5fc80`。
- ZIP：`SHOU-Launcher-win-x64-v1.5.0-dev.ge1286dc4.zip`，SHA-256：`fb41fbd87e33c8b65dd8f96f9bb8c527be99e603466a92cb9ee0fdb86ef048fe`。归档与分发目录均只有 EXE。
- 本机任务根：`D:/Code/C#/SHOU-Launcher-Windows/artifacts/windows-main-20261105`；最终输出 `dist/r50-final`。
- SMB 交接根：`PCL-CE-SHOU/artifacts/windows-r50-20261113`，Mac 已挂载为 `/Volumes/PCL-CE-SHOU/artifacts/windows-r50-20261113`。源码用 `windows-r50-source.bundle`，候选在 `candidate`，脱敏证据另放 ZIP；不搬运用户凭据。

## 实现

Windows 发布使用 .NET singlefilehost，运行时自包含；Skia、HarfBuzz 与 GLES 内嵌，启动时由运行时解压至缓存。托管调试信息内嵌，原生 PDB 不放入分发。图片、图标、现有三语文本、Java bootstrap 和 26 个运行时依赖的许可材料均保留。没有新增字体包或许可清单界面。Mac 继续原应用包入口。

`Assert-DesktopBundle.ps1` 使用锁定 SDK 的 Bundler 检查格式，核对 212 项清单、包内 Core 与新构建 owner 的哈希、自包含 runtimeconfig、所有原生 PE 架构及敏感文件名排除；分发目录强制恰好一个 EXE。程序集来源报告改为程序集名称，不依赖 single-file 下为空的 Assembly.Location。Java bootstrap 从程序集资源释放并校验，仍用私有 stdin 传递敏感启动参数。

许可证先由精确 deps 清单收集后内嵌；保留原依赖锁。Windows 发布关闭会额外引入隐式 ILLink 包的 single-file 分析器，不做 trimming/AOT，改由上述实际包校验及冷启动验收把关。[.NET single-file 设计](https://github.com/dotnet/designs/blob/main/accepted/2020/single-file/design.md)和 SDK Manifest/FileEntry 是包布局依据。

Windows 未指定 `--data-dir` 时，先保留已经存在的生产 sidecar；其次保留旧预览数据根及其安全存储。没有新版状态才启用旧数据首次接入。因此把新版更新放在旧 WPF 旁边不会抛弃已有新版设置；已有 sidecar 时不会自动合并或重新导入用户已移除的账户。

首次接入读取同目录及立即子目录的 versions、旧 LaunchFolders、PCL/config.v1.yml 和 Setup.ini 的公共目录选择字段；实例复用既有预检、继承链、图标及隔离规则，不移动游戏文件。账户只读当前 Windows 用户 PCLCE 的 Profile 与 UserKey.bin，复用现有旧字段解密器，支持可识别的微软、离线及 Authlib 档案。密码、rawJson 与无关账户存储不迁移；未知 provider 不冒充微软或普通 Yggdrasil。

可恢复凭据写入当前用户 DPAPI 安全存储；已有新凭据不被旧缓存覆盖。先完成安全存储再原子提交非敏感 sidecar；取消或提交失败回滚本轮新建的凭据记录，不删除既有记录。凭据不可恢复时保留旧文件并记录原因类别，仍可正常重新认证。恢复缓存不代表已通过在线验证，后续沿用正常刷新和所有权检查。

## 验证

| 范围 | 结果 |
|---|---|
| Windows Core 全量 | 899 通过、18 条件跳过、0 失败，共 917；真实 aria2 1.35.0 传输/取消/续传通过 |
| Windows Desktop 全量 | 最终 363 通过、0 跳过/失败；构建 0 警告/错误 |
| 最后路径优先级修复 | 源码 e1286dc4 针对回归 7/7，含既有预览、旧目录同址更新、原凭据不覆盖、保存/重读、取消/失败回滚 |
| macOS ARM64 | e1286dc4 交叉构建通过，0 警告/错误；不作为原生 Mac 验收 |
| 真实旧数据原生服务验收 | 6 账户、34 实例，当前用户 DPAPI 保存后重读通过；1 个微软账户完成真实刷新、所有权与 profile 校验；源配置及 UserKey.bin 字节未变 |
| 最终单文件命令进程 | diagnostics、DPAPI write/read/delete 四个独立进程均 exit0；新解压目录，DOTNET_ROOT 指向不存在目录 |
| 原生窗口 | 200% DPI（192），仅 EXE 的新分发目录冷启动；首页、下载、实例模组/资源包、设置、工具和 OOM 分析可操作，有逐页截图 |
| 窗口状态与重启 | 9c2f8855 代表候选最小化恢复、最大化还原、正常关闭重开各 exit0；e1286dc4 最终候选重新核对页面、最大化还原和 exit0，配置与实例源文件重启哈希核验保留 |

真实账户验收运行在测试宿主的隔离数据根；真实旧目录只读，没有直接将候选覆盖到真实 WPF 安装目录。单文件宿主另验原生 DPAPI。截图仅含虚构账户和隔离实例，没有真实账户、缓存或授权代码。没有浏览器 Cookie/账号扫描。原生截图/鼠标/键盘回退在本轮重新取得用户明确授权后才使用；关闭重开的部分步骤使用实际窗口按钮的原生 UIA Invoke。

18 项跳过主要为 Mac Keychain/Java/原生库、需要符号链接权限的矩阵和显式启用的真实旧账户测试；后者本轮已单独通过。完整原因见证据中的 `r50-core-skips.json`，不与通过数重复累计。Java bootstrap 的 javac --release 8 仍有工具链的三条旧目标提示；C# 构建均无警告/错误。

## 首次失败与修复

- 迁移回滚夹具在 Windows 返回 UnauthorizedAccessException，原断言只接受 IOException；修正为两个确定的文件系统类别，凭据回滚及无暂存残件断言保留。
- 新增凭据保留测试一度缺 Configuration 命名空间，已补齐；首败编译日志保留。
- 状态协议夹具只读握手首字节就关闭，Windows 会因未读数据重置连接；改为完整读完请求，在线人数/图标断言不变。
- aria2 存活检查写死 Mac 的 1.37.0；改为核对操作前后同一实际 daemon 版本，下载字节、哈希、取消/续传及未停止 daemon 的断言保留。
- Desktop 日志夹具混合路径分隔符，以及设置导航断言忽略 R42 的 350 ms 自动保存。分别规范化路径、改为核对所有页面的值保持且最终确实持久保存。
- 首次发布在子作用域追加属性时丢失已有 artifact 参数，已固定为独立完整参数数组，并在 publish 前再次验证输出边界。误生的项目 obj 已移至本轮失败夹具目录，锁文件变更留差异证据后还原。
- 包检查最初错误要求静态内置运行时有独立 coreclr/clrjit 项，且原生 NuGet PDB 形成旁文件；按 SDK 实际 singlefilehost 与 runtimeconfig 核验，PDB 在发布项阶段排除，所有必需运行库仍核验。
- 测试工具将带正斜杠、尚不存在的 Windows 解压目录传给 .NET 原生宿主时，命令在托管入口前以 0xC00000FD 退出；控制矩阵定位到该环境变量，换为 GetFullPath 的 Windows 规范路径后，新目录冷启动及四命令均通过。不是通过改 Java、凭据或 TLS 掩盖失败。

各轮日志/TRX、首败和修复后结果保留；只清理了已完成构建的 bin/obj，未删候选、源文件、用户数据或旧工程。

## 仍需后续验收

- Mac 主任务已在本机安装 8cb5cb48，完成 Keychain 恢复、重开自动读取与真实微软刷新；详见 [Mac R50](MACOS-R50.md)。此 Windows 会话自身仅做 Mac 交叉编译，完整原生选择器矩阵仍保留。
- Windows ARM64、签名、新机器依赖、完整 DPI/跨屏/键盘/无障碍与虚拟文件选择器矩阵。
- 更多老版仅 Setup.ini 账户缓存、未知 provider、失效凭据/断网与不同旧目录布局；本轮不宣称全部历史格式无感登录。
- LittleSkin/MUA 本轮仅恢复与安全保存，没有逐账户在线刷新或外观写入；微软本轮只验证一个真实账户。
- `todo.md` 既有 R49-NEXT、R48-NEXT、R46-BOOT 和完整替代门禁继续保留。未启动游戏，不能据此宣称游玩、入服或主菜单门禁通过。

Mac 接回复核：final-mods 等详情页截图右上角未见三按钮，首页三钮可见。当前记录为需要 Windows 实窗复现的可见性问题，尚未确定是布局/隐藏还是捕获差异；正常 UIA Invoke 关闭与首页最大化通过不能覆盖此项。见 todo 的 R50-WIN-CAPTION。

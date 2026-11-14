# Mac 固定本地签名与 CI

## 当前方案

Mac 候选使用同一份自签代码签名证书；应用标识仍为 `club.conduit.shou.launcher`。
应用的 designated requirement 同时绑定应用标识与证书 SHA-1 指纹，不再绑定每次编译变化的代码哈希。
证书并非 Apple Developer ID，包仍未公证，不代表通过 Gatekeeper 的默认外部分发检查。
Windows 单 EXE 打包及签名行为保持原样。

本机初始化：

```sh
python3 scripts/macos/manage_signing.py init
```

默认保存于 `~/Library/Application Support/SHOU-Launcher-Signing/`：

- `identity.p12`：加密的证书与私钥，可用于恢复或导入 CI。
- `password.txt`：该导出的密码，也是专用签名钥匙串的密码。
- `certificate.pem`：公开证书。
- `signing.json`：公开指纹与本机专用钥匙串路径。
- `signing.keychain-db`：仅供本项目签名使用的钥匙串。

目录为 0700，私钥导出、密码及钥匙串为 0600；不可加入 Git、SMB 交接或构建 artifact。
请在安全位置保留私钥导出和密码的恢复副本。后续初始化会复用原身份，不会每次生成证书。
更换电脑时应导入这份原身份；重新创建证书会改变应用身份。
签名只在操作期间加入专用钥匙串搜索路径，成功或失败均还原原搜索列表，不更换默认钥匙串、不添加系统信任。

正常使用原 `Publish-Desktop.ps1` / `Invoke-Dev.ps1` 构建入口即可；它们读取本机配置并解锁专用签名钥匙串。
也可以显式传入 `SHOU_MACOS_SIGNING_IDENTITY`（证书 SHA-1）和 `SHOU_MACOS_SIGNING_KEYCHAIN`。
没有固定身份时会报错；仅 PR/测试包可以显式设置 `SHOU_MACOS_ALLOW_ADHOC=1`，构建信息会标明临时签名。
`BUILD-INFO.json`、`SIGNING-INFO.json` 和原生依赖报告均记录实际签名方式，不宣称已公证。

## GitHub Actions

`Shared Desktop migration` 已接入相同身份的导入、签名、原生诊断与清理。
需要仓库维护者配置以下两个 Actions Secrets：

| Secret | 内容 |
| --- | --- |
| `MACOS_SIGNING_CERTIFICATE_BASE64` | 本机 `identity.p12` 的 Base64 内容 |
| `MACOS_SIGNING_PASSWORD` | 对应导出密码 |

不要在聊天、命令日志或提交中粘贴这些值。通过 GitHub Secrets 网页或 `gh secret set` 的标准输入添加。
2026-11-14 经维护者明确授权，已将本机同一身份的加密 P12 和导出密码存入本仓库上述 Actions Secrets；私钥不进入源码、构建日志或 artifact。
本地已验证导入成功/密码错误的清理与搜索列表恢复。

### 云端验证（2026-11-14）

代码提交 `a001ff141eda590ac8f367dba267d7211e1e3d6a` 的
[可信推送构建](https://github.com/Conduit-Club/SHOU-Launcher/actions/runs/43546419732)
和 [PR 桌面检查](https://github.com/Conduit-Club/SHOU-Launcher/actions/runs/43546424831) 均通过；
同一提交的 Build (CI) 与 Conduit Build 也通过。

- macOS：Core 941 通过 / 7 条件跳过，Desktop 365 通过；固定证书导入、隔离原生签名测试、候选打包、临时私钥清理均成功。
- Windows：Core 931 通过 / 11 条件跳过，Desktop 365 通过；x64 和 ARM64 单 EXE 候选打包成功，Mac 签名步骤按平台跳过。
- PR 构建明确跳过签名私钥导入和签名诊断，仍可生成临时签名测试包；可信 push 使用固定签名。
- 已下载可信 Mac artifact 并独立复核 ZIP CRC / SHA-256、完整应用的 `codesign --verify --deep --strict`、构建来源与配置注入布尔记录。
  云端包的证书指纹与本机均为 `BBFC2DA005E308768F21B86F4BA73D5B691305C9`，DR 绑定证书与应用标识。
  artifact 与内层候选均无 P12、密码或私有钥匙串；未启动该云端应用、未替换个人安装。

可信候选 `SHOU-Launcher-osx-arm64-v1.5.0-dev.8.ga001ff14.zip`：54,563,625 字节，
SHA-256 `4ea28e796e41543c04d74a2ad026b021fb02b255ea6aa19c944b7b31a1afdd0b`。
这是未公证的开发候选，不是正式发布，也不表示下方真实登录钥匙串的无感更新问题已解决。

可信的手动/分支构建把原 P12 导入 runner 临时钥匙串；结束时 `always()` 清理钥匙串、P12 和临时证书。
PR 构建不读取签名 Secrets，继续生成明确标注的临时签名测试包，避免将私钥交给 PR 代码。
可信构建缺少上述 Secrets 时明确失败，不能默默生成另一张自签证书或临时签名来冒充固定身份。
当前工作流由 `features/macos-arm64` / `features/windows-parity` 推送及面向 dev/main 的 PR 触发。
签名能力通过当前功能分支的 push 验证；`workflow_dispatch` 须等工作流进入默认分支后再使用。
尚未把 Mac 包接入正式 Release 发布。

本机和 CI 必须复用同一个 P12。单纯给两张证书起相同名字，不能保持应用身份。
公证需要以后切换到 Apple Developer ID；不需要为每款软件另买会员。

## 钥匙串验证的实际边界

固定证书可以保持代码签名身份，但**不能保证当前 macOS 的旧文件钥匙串更新后不再授权**。
2026-11-14 的原生验证发现：当前用户登录钥匙串里严格绑定测试程序的模拟项除应用 DR 外，还被系统附加了 `cdhash:` 分区。
两版程序 DR 相同、代码哈希不同，更新版无交互读取返回 `-25293`；旧版本可读取，无关签名同样被拒绝。
实际完整候选也通过 LaunchServices 启动，使用唯一隔离数据目录及模拟凭据：旧版写入/读取成功，改变 bundle 哈希并用同一证书签名后的新版读取失败，旧版删除成功。
没有改真实登录项、放宽 ACL、信任根证书或关闭系统保护。当前个人安装保持 R51 原样，未为了尝试这个方案再触发用户授权。

`scripts/macos/test_signing.py --directory <新测试目录>` 会编译三个小型原生探针，验证 DR 稳定性、不同代码哈希、
无关签名拒绝，并记录模拟项实际分区与跨版本读取结果；当前脚本在单独的临时测试钥匙串内操作，禁用交互，结束删除整个测试钥匙串。
这项专用钥匙串测试在本机和同一 P12 的模拟 CI 导入后均通过，但它不能替代上述当前登录钥匙串的真实候选结果。
如果签名稳定但系统仍阻止读取，报告 `PASS_WITH_KEYCHAIN_LIMITATION` 并在 CI 发出 warning，不能记作凭据无感更新通过。
对当前个人安装与真实账户的首次签名切换/后续更新授权仍需单独验收，不能承诺换成本地证书就只再授权最后一次。

参考：[Apple TN2206](https://developer.apple.com/library/archive/technotes/tn2206/)、
[GitHub macOS runner 证书导入](https://docs.github.com/en/actions/how-tos/deploy/deploy-to-third-party-platforms/sign-xcode-applications)。

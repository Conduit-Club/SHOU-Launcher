# R53：Mac 更新后的钥匙串授权

## 结果与范围

普通启动器更新改为经稳定的原生凭据组件访问钥匙串。主程序的代码哈希变化不再改变实际钥匙串调用者。
Mac 本机已用真实签名候选验证更新前后无交互读取、更新及删除；保留旧版本回读能力。
Windows 继续使用原 DPAPI 适配器和单 EXE 流程，没有加入这个组件。

旧登录项仍在原钥匙串、原 service/account 下，凭据格式不变，不要求再次登录微软或皮肤站。
**第一次从旧版切换时，每个旧登录项仍可能需要一次系统授权。** 在启动器现有的“重新授权钥匙串”入口触发后，
由用户在系统提示中审阅并选择“始终允许”，可让该组件以后继续读取该项。
提示可能显示 `SHOU.CredentialBroker`。组件或签名身份更换、用户撤销访问或钥匙串本身锁定，仍可能要求授权。
这不是绕过 macOS 授权，也不是取消密码保护。

本轮没有替换 `/Applications/SHOU Launcher.app`，没有读取、导出、改写或删除个人登录项，没有启动 Minecraft。
真实账户的首次切换授权留待安装时由用户亲自完成；不将隔离测试描述为个人账户迁移已通过。

## 实现

- `MacCredentialBrokerClient` 使用私有匿名管道，并在发送数据前核实运行中的原生子进程。
- 原生组件要求同证书、精确的启动器标识，验证父进程完整代码和资源；收到请求后重验。
  更换或修改托管程序集也会被拒绝，不只检查 .NET apphost。
- 冻结的原生签名二进制随每个候选原样复制，不随主程序 SDK、版本或构建机重编/重签。
  主程序和原生组件相互验证身份，组件启用 hardened runtime，无第三方动态库或 JIT 例外。
- 原 service/account 和 Core 凭据恢复逻辑保留。后台禁止弹授权，显式恢复读取才允许系统交互。
  没有新增登录按钮、凭据明文缓存、后台 daemon 或全局钥匙串设置。
- 组件缺失、损坏、冒充、父程序篡改时拒绝，不静默回退到旧存储。
  30 秒普通请求和 5 分钟授权请求上限，取消或父进程退出不会留下无限等待的组件。

组件 SHA-256：`753543641f62113e3ad3f5d237c2978965ac2a87a4fcc559de43f9c617cb25ce`。
cdhash：`b52770a28e3a962111114fccf06023cdbc08793f`。
签名证书：`BBFC2DA005E308768F21B86F4BA73D5B691305C9`。
源码、冻结产物、编译器/SDK 与构建说明一起保留于 [原生组件目录](../../src/SHOU.Launcher.Desktop/Native/MacOS/README.md)。

## 本机验证

- macOS ARM64 Core：949 通过，7 既定条件跳过；Desktop：365 全通过。C# 构建均无警告/错误。
- Windows x64 交叉编译通过，C# 无警告/错误；Java bootstrap 仍有既有 source/target 8 过时提示。
- 原生签名测试 10 项通过：两个不同主程序 cdhash 共用冻结组件读写，不同签名、同证书不同应用、父资源篡改及无关 service 均拒绝。
- 完整候选升级/故障验证：更新前后读写、回读、删除与恢复；父资源/托管程序集篡改拒绝；组件缺失无旧后端回退；冒充组件收到零字节。
  测试仅使用当前登录钥匙串中的唯一隔离命名空间，删除模拟项，保留输入候选原字节。
- 提交前 RC2 使用显式 `.env` 发布载荷；修正 native 组件后仅重打包，C# 产品代码与通过测试的载荷一致。
  提交后的最终候选以随包 `BUILD-INFO.json` 和 SHA-256 旁文件记录源码；云端可信 push 另执行 Mac 15 的同样原生及完整候选检查。

## 保留事项

- 个人旧登录项首次授权，以及授权后实际普通更新的个人账户回读，仍需用户安装时验收；本轮不自动触发系统密码提示。
- Apple Developer ID/公证和 Windows 签名不在本次修复中，现有本地自签身份保持。
- ad-hoc PR 包及未打包开发宿主继续使用旧适配器；验证/分发固定组件效果须使用维护者签名的可信 Mac 候选。

背景依据：[Apple TN3137](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains)、
[Apple Security 的分区身份实现](https://github.com/apple-oss-distributions/Security/blob/main/securityd/src/clientid.cpp)、
[分区与应用 ACL 校验](https://github.com/apple-oss-distributions/Security/blob/main/securityd/src/acls.cpp)。

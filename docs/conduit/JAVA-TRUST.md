# Java 证书检查与修复

入口：**设置 → Java → Java 证书检查与修复**。

打开页面会检查启动器已发现的 JDK/JRE；“重新扫描”先刷新 Java 安装列表。结果显示完整 Java 版本、发行商、证书库路径和命中的 SHA-256 指纹。“受影响”指发现本规则撤信任的证书，“需加固”指缺少本次事件的中间证书阻止名单，“未能检查”不表示安全。

已有 Java 由用户选择单项或批量修复。启动器新下载的 Java 默认自动修复，可关闭并保存设置。自动检查是文件下载后的阻塞步骤，全部可检查项处理完成后才刷新 Java 列表并报告任务完成；失败会显示错误并保留下载目录，不把修复失败当作成功，也不清理已下载的 Java。每个下载加载器单独保存目标目录。

## 规则与依据

内置规则版本：`emudhra-2026-10-22`。不从网络下载规则，不按证书名字、别名、国别或 Java 版本号批量删除证书。

- 移除 `emSign ECC Root CA - G3`：证书 DER SHA-256 为 `86A1ECBA089C4A8D3BBE2734C612BA341D813E043CF9E8A862CD5C57A36BBE6B`。
- 如果 8 张泄露的中间证书及其 8 张交叉签名证书被直接装入信任库，也按 DER 指纹移除。
- 向 Java 自身的 `blocked.certs` / Java 8 `blacklisted.certs` 追加上述 16 张中间证书的 SHA-256。这样可以阻止公告中已知证书经其他受信任根建立的链；保留厂商原有阻止名单与算法。
- 不移除公告明确保留的 G1，也不因为交叉签名删除整个 AAA/Sectigo 根。

一手依据：[Mozilla 事件与 16 张证书清单](https://bugzilla.mozilla.org/show_bug.cgi?id=2083449)、[Mozilla 撤信任决定](https://bugzilla.mozilla.org/show_bug.cgi?id=2083449#c18)、[NSS 根证书移除与序列号](https://bugzilla.mozilla.org/show_bug.cgi?id=2084101)。根证书指纹及测试证书来自保留 Mozilla NSS 证书数据的 [Dart SDK 固定版本](https://chromium.googlesource.com/external/github.com/dart-lang/sdk/+/refs/tags/3.8.0-7.0.dev/third_party/fallback_root_certificates/certdata.pem)。

Java 文件格式与阻止名单实现依据：[OpenJDK JKS](https://github.com/openjdk/jdk/blob/master/src/java.base/share/classes/sun/security/provider/JavaKeyStore.java)、[现代 Java 阻止名单](https://github.com/openjdk/jdk/blob/master/src/java.base/share/classes/sun/security/util/UntrustedCertificates.java)、[Java 8 阻止名单](https://github.com/openjdk/jdk8u/blob/master/jdk/src/share/classes/sun/security/util/UntrustedCertificates.java)。

## 文件与恢复

- 检查 JDK/JRE 的 `lib/security/cacerts`、JDK 8 的 `jre/lib/security/cacerts`，以及优先使用的 `jssecacerts`。扫描不执行 Java。
- JKS v1/v2 直接解析并验证默认 `changeit` 密码的完整性摘要；保留条目的字节、时间戳与别名。含涉事证书的私钥条目拒绝自动处理。
- PKCS12 用 .NET 只读检查；需要修改时调用该 Java 自带的 `keytool`，先改临时副本，再验证剩余全部证书。缺少 `keytool`、含私钥、特殊密码、损坏格式或不支持的阻止名单算法会报告人工处理。
- Java 8 根据已有名单、PE 版本信息或 `release` 文件选择名单位置；现代 Java 使用 `blocked.certs`。
- 每次写入前，原文件备份到同目录的 `文件名.shou-backup-随机ID`，并记录 `文件名.shou-trust-repair.json`，包含规则 ID 和前后文件 SHA-256。
- 临时文件落盘后原子替换，写入前再次检查原文件。写入失败保留原文件和恢复信息；每个文件单独报告结果，批量操作可能部分完成。
- 恢复仅在当前文件仍与该次修复的 SHA-256 一致、备份也通过校验时执行，避免覆盖后续更新。修复时新建的名单恢复到原先不存在的状态；备份与记录保留供核对。
- 符号链接、目录联接点及链接祖先一律拒绝写入。不会执行聊天引文中的 Linux 批量替换命令，也不会把自带 Java 的证书库链接到系统路径。

## 使用边界与验证

修复后需重启正在运行的 Java/游戏。撤信任后，由被移除 CA 签发的站点可能无法连接，站点维护者需要迁移证书。检查范围不包括 `-Djavax.net.ssl.trustStore` 等 JVM 参数指定的自定义文件、应用自己加载的证书、关闭校验的代码或未来重新签发的其他证书。规则按已知 DER 指纹阻止证书，不是公钥级全局撤销服务；应同时安装 Java 厂商后续更新。链接到系统证书库的 Linux Java 应通过发行版包管理器处理，本启动器是 Windows 应用。

验证全部使用临时目录与运行时副本，不修改本机安装的 Java、游戏数据和信任设置。专项覆盖 JKS v1/v2、重名误匹配、重复别名、损坏、不同密码、备份/恢复、更新冲突、名单保留、链接保护、Java 8 与 `jssecacerts`、自动修复关闭/重复调用；真实 JDK `keytool` 验证修复后的 JKS，并完成真实 PKCS12 的删除、保留其他证书和恢复。WPF 回归加载证书设置控件与 Java 页面编译 XAML，验证按钮委托签名、配置保存以及下载加载器的阻塞检查顺序。

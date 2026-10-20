# 独立维护与历史迁移

2026-10-20，维护者决定将 SHOU-PCL 改为独立维护，停止自动同步 PCL-CE。默认分支为 `main`，开发分支为 `dev`，通过 `dev → main` PR 审查后进入稳定分支。

## 源码来源

本项目历史上基于 [PCL-Community/PCL-CE](https://github.com/PCL-Community/PCL-CE)，最后纳入的源码基线为 `f00b458e7bcf1368ea4e00148cf5db09bd1f5133`（2026-10-15）。此前社团版本使用的基线为 `a42a7699948aebd1e2563df025274af255c1f559`。

历史迁移采用源码快照，快照的作者和提交者为 `Moeary <MinecraftYYXX@gmail.com>`。这表示维护者执行了导入和整理，不表示历史代码全部由其原创。PCL 原作者龙腾猫跃、PCL Community 及各依赖的署名、赞助链接、许可证和第三方声明仍保留。

## 迁移范围

远程已有的三个历史标签与两个功能分支迁移到新的快照历史，源码树与原引用逐一校验一致：

| 引用 | 原始源码提交 |
| --- | --- |
| `conduit-v2.15.1-beta.1-baseline.20261002` | `3fe5079546b9fa040568c559748e517c97326490` |
| `v1.0.0` / `features/conduit-launcher` | `8e7b0ac52471bf4690b352c3309d7039ad223b0d` |
| `v1.1.0` | `25a2f27652e3b0c0ea550fa2e35686ca6e399ae0` |
| `features/v1.1.0-sxj` | `df50ce9160053af068a9d137f680f5de8cbae7f2` |
| 同步前 `dev`，新的 `main` 源码基线 | `7248a8d4e84aab9e50ecbcc532cbd566da0ccc2f` |

原始完整 Git 历史、引用映射及旧发行元数据保存在迁移机器的 `.git/shou-migration-20261020/`，不上传原历史备份。历史发行附件不重新构建，已有二进制中的源码 SHA 仍指向原提交；重写标签不会改变附件的 SHA-256。

旧克隆应先备份本地工作，再重新克隆本仓库；不要把旧分支合并回新历史。新贡献从 `dev` 开始，并向 `dev` 提交功能 PR。

## 许可证

`Plain Craft Launcher 2/` 保留其 [PCL 分发有限许可](../../Plain%20Craft%20Launcher%202/LICENCE)，其余目录继续使用 [Apache License 2.0](../../LICENSE)，第三方组件各自的许可证与版权声明保持有效。独立维护、Git 历史重写与 GitHub Fork 关系解除均不改变上述源码来源和许可约束。

# 2026-11-05 Mac 原生验收

N5 Mac Release已通过真实启动器六页导航、Minecraft1.21.1原版主菜单与Options交互、Quit Game正常退出，以及随后关闭启动器。活动游戏期间触发启动器定时退出存在进程残留问题，首轮失败完整保留。整体迁移尚未完成。

## 当前分支

- 开发分支：`features/macos-native-validation`，主仓库目录直接使用此分支。
- 基线：本次fetch确认`origin/dev=b97b565678cbcdc8f602b57973d0837d3ac556a2`。
- 主集成来源：`features/macos-arm64@7decdd17e91d7a48764f96e07968af5a3811530c`，快进纳入。
- UI来源：`features/macos-arm64-ui@8e5fc6f87d2110a372d3baf0885098b28c3135a2`，历史整合提交`df517d95af14283c1daea3ae8a74e2118605bea8`。
- 平台来源：`features/macos-arm64-platform@de6ebe869e7fd1b3fb786bb3268ee65901b17068`，历史整合提交`6b716785795efe7e24f9dd1346aed3a170b4e7d4`。

两worker的实现此前已按增量集成。合并前检查各自负责的全部已提交路径，无缺失、实现字节一致；较旧共享依赖不替换后续集成修复。因此两次非快进合并使用`--strategy=ours`，合并后完整树与`7decdd17`相同，三个源头均为祖先，源引用不变。worker未提交共享前置文件保留原处并备份，未作为新产品变更重复纳入。

本次最终文档提交不改变产品源码。旧integration worktree保留为detached检查点；旧UI/platform工作树及原始计划文件保留。`main`、`dev`和远程引用未被推进，未推送、创建/合并PR、运行远程Actions、打标签或发布。

## 候选与输入

| 项目 | 实际值 |
|---|---|
| 候选 | `dist/N5/Desktop/Release/osx-arm64/default/SHOU-Launcher-osx-arm64-v1.5.0-dev.g573ce8e7.zip` |
| 源码 | `573ce8e74b9abf429aa6b882b055ebc16c123832`，原构建时干净 |
| SHA-256 | `5656227d1f0564049d0e1678db2c01271a679a5b19b96fb44209940a885d6ac2`，本次重新核对一致 |
| 宿主 | Apple Silicon，macOS26.6，进程Arm64 |
| 发布方式 | 自包含多文件，single-file/trimming/AOT关闭；本地ad-hoc签名，未公证 |
| 解包 | `test-data/白天候选验证/N5-release/SHOU Launcher.app`，重新ditto解包并严格验签 |
| 隔离数据 | `test-data/day-native-20261105`，从原离线fixture克隆并重定位；原fixture保留 |
| 游戏 | 原版Minecraft1.21.1；离线`SHOUNightTest`；只启动、不入服 |
| Java | 官方Temurin21.0.13+9 ARM64，2048MiB |

表中`dist/`、`test-data/`和下文`reports/`均位于本机任务根`.shou-dev/`。启动子进程清除了PCL/build输入及SDK/pwsh PATH，程序目录无`.env`。本次没有新增真实账户授权、Keychain ACL变更或服务器连接。

## 本次实际观察

| 验证 | 结果与边界 |
|---|---|
| 启动器实窗 | CUA取得真实标题、版本、Release及源码SHA；实际截图可见品牌首页 |
| 六页导航 | 首页、实例与启动、下载任务、账户、设置、日志均实际打开 |
| 游戏启动 | 从启动器`LaunchGameButton`点击启动，非仅生成计划 |
| 首次引导 | 独立截图确认Continue后，以限定PID键盘事件继续 |
| 主菜单 | 独立窗口截图可见Minecraft1.21.1、Singleplayer、Options、Quit Game |
| 交互 | 打开Options，Escape返回主菜单，再选择Quit Game |
| 游戏退出 | `Stopping!`，exit0、cancelled=False、timeout=False，PID57421消失 |
| 启动器退出 | 游戏结束后关闭窗口，`normal_close`、Ready，PID57405进程exit0，无timeout |
| 清理 | 自有测试进程均结束；临时辅助功能列表项移除，PID授权receipt关闭 |

Java无bundle ID，当前CUA无法绑定，用户明确授权了限于本次游戏窗口的原生替代方式及辅助功能。重试更换PID后再次确认目标授权。原生工具仅针对授权PID查询窗口、截图、发送Tab/Return/Escape；点击因窗口onscreen标志为false而被工具门禁拒绝，没有绕过该门禁。选中按钮的截图与随后界面变化独立佐证实际输入生效。

辅助功能开关关闭后的一次即时原生查询仍报告trusted=true；随后已从系统设置移除工具项，并关闭工具自身的PID授权receipt，此后没有发送原生输入。没有更改ChatGPT/Codex已有授权。

## 保留的失败与待修复项

首轮启动器PID56568、游戏PID56693在授权设置期间达到900秒测试上限。应用报告`exit_after`，游戏exit137/cancelled=True，不能算正常Quit。启动器进程在窗口关闭后残留；保存1秒进程采样后，仅对该自有PID发送SIGTERM，外层记录exit-15、无外层timeout。

重试上限为3600秒，实际289秒内完成主动退出，游戏与启动器均exit0。两个路径分开记账。活动游戏关闭启动器时的进程残留尚待最小复现、定位和修复；应优先核查ProcessRunner停止/释放过程是否捕获已停止的UI同步上下文，该推测尚未验证。

## 测试与剩余范围

本次合并保持原集成产品树，后续仅新增文档，候选没有重包。沿用同一冻结源码的夜间证据：Core196通过、3显式跳过；Headless34通过；本地无Secrets的CI Core192通过；B07同中间根A→B→none通过。套件有重叠，不累加；这些套件本轮没有重复运行。

已补齐N5 Release的L3实窗/导航与原版1.21.1的L4交互/正常Quit。仍未完成：

- 活动游戏期间关闭启动器的进程残留修复与回归。
- Fabric主菜单/正常Quit及更多原生业务流。
- Windows真机W01–W10；旧配置、实例、禁用Java导入与旧→新→旧往返、双入口接管。
- selected-server生产启动路径；真实Microsoft/MUA/LittleSkin账户及入服。
- Developer ID/公证、Windows签名、Keychain跨配置与后续更新身份连续性。
- Forge/NeoForge、完整mod/modpack内容管理、旧Java/LWJGL2兼容矩阵及真实aria2二进制端到端。
- 大小写敏感卷上的AppPaths/凭据namespace/完整trust服务；已有六项JavaPathBoundary/SafePath通过不推广到全链路。

## 证据索引

`.shou-dev/reports/day-native/`保存：

- `delivery.json`、`tests.json`、`unfinished.json`：当前交付与未完成清单；原夜间报告保留为历史。
- `merge-result.json`、`merge-plan-and-preservation.json`、`preserved-worker-inputs/`：合并、归属核对与原文件备份。
- `native-inputs.json`、`gui-observations.json`：候选身份与外部观察。
- `vanilla-main-menu-confirmed.png`、`vanilla-options.png`、`vanilla-quit-game-focused.png`：真实游戏窗口截图。
- `game-normal-quit.json`、`minecraft-normal-quit.log`、`launcher-after-normal-game-quit.log`：游戏正常退出。
- `ui-retry-process-run.json`、`ui-retry-native-report.json`：启动器正常关闭与进程退出0。
- `first-attempt-assessment.json`、`ui-process-run.json`、`first-attempt-exit-sample.txt`：首轮失败及定向恢复。
- `native-ui-grant-first-attempt.json`、`native-ui-grant.json`：授权范围和完成后关闭状态。

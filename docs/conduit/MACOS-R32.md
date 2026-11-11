# Mac R32：外部 Aria2 端口优先

本轮接续 `features/windows-parity`；用户更正设计为“默认 localhost:6800/jsonrpc，用户提前启动 aria2/GUI，连接成功就用，否则内置”。不启动 Minecraft，不覆盖个人应用/实例，不创建服务、不修改全局 shell 配置；正常本地提交/功能分支推送沿用授权，不合并或发布。

## 行为与配置

- 默认 RPC 地址 `http://localhost:6800/jsonrpc`，保留可选密钥与现有优先使用开关；设置 → 管理中可保存后测试连接。只支持本机 RPC，共享输出路径无须映射。已保存的开关/端口保持原值，不覆盖用户配置。
- 启动器不自动发现 `aria2c`、不安装、不开进程、不启停 GUI/daemon。Homebrew aria2 **1.37.0** 已按最初要求安装于 `/opt/homebrew/bin/aria2c`，未启用后台常驻。Windows 无需 winget 或 PATH 中存在 aria2。
- 下载前以 `aria2.getVersion` 探测，2 秒连接探测时限；RPC 直连本机，不走系统代理、不跟随重定向。成功结果短时复用 10 秒，失败 3 秒，避免整包离线时每文件等待；显式测试立即重新探测。配置修订使缓存失效。
- 未连接、密钥错误、RPC/传输异常或最终大小/哈希不符时走内置。用户取消时只暂停对应 GID，不回退重新下载。RPC 错误仅显示代码/类型，不展示响应正文、密钥或签名来源 URL。
- 每个外部任务的来源/输出路径/分段数单独传入。代理按当前设置配置到新任务，不改 daemon 全局选项；不同系统代理规则不能准确表达时使用内置。控制 RPC 始终本机直连。

若自行在终端启动，可使用下列前台命令，另开终端保留它运行；也可在 GUI 中开启相同 RPC 端口。密钥如由 GUI 设置，在启动器设置填写同一值，不要放进报告或截图。

```sh
/opt/homebrew/bin/aria2c --enable-rpc=true --rpc-listen-all=false --rpc-listen-port=6800
```

## CF/FTB 通道

保持 `CurseForgePackResolver` 的官方 CDN 允许列表与无 API 密钥重定向传输。为 Aria2 暴露生命周期受限的固定资源中继：只绑定 `127.0.0.1` 随机端口、随机能力路径，限定单资源 GET/HEAD、Host/请求头/单 Range；无任意 URL 转发入口。上游仍经过原受检 HTTP 传输与共享限速，Aria2 每段占用共享连接预算。离线回退重新指向原始允许来源，中继和请求随操作释放。

普通下载的严格 HTTPS 或全局限速尚不能由裸 RPC 完整约束，仍走内置并显示原因；不声称所有网络字节均走外部引擎。FTB 接口复用该通道，本轮没有下载真实 FTB 整包。

## 取消与恢复修正

真实 Homebrew 1.37.0 在取消后直接 `unpause` 可触发 `Piece::initWrCache` 断言并 exit134；先前中间回归保留于忽略目录，未作为通过结果。移除运行时 `changeOption` 仍能复现，因此不将原因归结于代理选项。

修正为暂停并等待空闲，保留 `.aria2-part` / `.aria2`，仅对 GID 与输出路径均匹配的本启动器任务移除旧请求记录，再以相同输出/新来源重新登记，由 daemon 侧车恢复分片。兼容暂停队列移除后直接不存在 GID 的行为。此方式也允许 CF 中继重建时安全更新端口。外部服务全局设置与其它任务不动；不调用 shutdown/pauseAll/removeAll。

## 验证与限制

聚焦回归 **15 通过 / 0 失败**，包含真实 `/opt/homebrew/bin/aria2c`：镜像失败切源、共享并发/最终哈希、取消后续传、CF 受检传输与限速/HTTPS、禁止重定向不覆盖原文件、新中继恢复；连接失败缓存、手动重新连接、错误/不可达端口回退及 RPC 重定向阻止亦通过。所有 daemon 由测试夹具启动在临时端口并只在夹具结束清理，不属于产品自动启动。

Core 全量 **737 通过 / 5 沿用跳过 / 0 失败**，Desktop 最终全量 **244 通过 / 0 失败**，均为 0 构建警告/0 错误。新增控件回归覆盖相同设置的后台快照不重建来源行、不清除连接结果且保持滚动位置。未启动游戏，未触碰个人存档；未做 Windows 本轮原生测试。持续大包聚合指标、内置自适应分段、任意文件完整暂停/恢复 UI、长时间断网及真实 CF/FTB 大包矩阵保留在 `todo.md`。

参考：[aria2 官方 RPC 文档](https://aria2.github.io/manual/en/html/aria2c.html)、[Homebrew aria2](https://formulae.brew.sh/formula/aria2)。验收依据为本机受控回归，不把官方文档当作产品测试结果。

## 最终候选与原生验收

- 源码 `224d61450b885f1ce040ff891292ec8069b96e32`；前一实现提交 `1d86c0de`。SDK 10.0.403 / macOS arm64 / Release / self-contained / Avalonia 12.1.5，显式读取原 `.env`，构建只记录 Microsoft/CF 凭据存在与提交匹配，未输出值。
- 候选 `/Users/moear/Code/SHOU-Launcher/.shou-dev/dist/MACOS-R32-20261111/rc2/SHOU-Launcher-osx-arm64-v1.5.0-dev.g224d6145.zip`，51,919,457 字节；SHA-256 `c2ec7b114924dcfb5e02af68dde781abfda0debb17f308c283b10514b0af75bb`。ZIP 全条目 CRC、原始 app 与 QA 副本 `codesign --verify --deep --strict` 通过。本机 ad-hoc 候选，未公证、未正式发布；个人 `/Applications` 应用未替换。
- CUA 真实窗口：管理页默认地址 `http://localhost:6800/jsonrpc`，开启优先外部并保存，连接到预先启动的 Homebrew 1.37.0；改为 `localhost:1` 并保存后显示“未连接…将使用内置下载器”，恢复 6800 后重连成功。这里离线提示为实窗结果，实际失败转内置传输另由核心回归核实。
- RC1 实窗发现操作/后台快照重载会清空提示并跳位。RC2 采用设置页自身可取消读操作，未改变的设置不重复 Load；连接测试前后滚动位置 882 保持，结果不被普通快照清除。保存修改本身仍可能重建来源列表并跳位，单列 `R32-UI02`，不宣称整个设置页滚动完全修好。
- Command–Q 后原生结果为 `normal_close` 且窗口句柄确已观察；夹具核对外部 daemon 仍存活，再在夹具清理阶段终止测试 PID。应用代码无 aria2 进程发现/启动/停止。QA 副本仅更改 bundle 身份/签名，Core/Desktop 程序集与原包一致。
- 首轮 Mac 锁屏导致 Avalonia 原生渲染初始化失败；用户解锁后同候选启动恢复，锁屏失败记录仍保留，不将首次失败当作通过。未尝试自动解锁。

忽略目录证据：`.shou-dev/reports/macos-r32-20261111` 下 `core-full.json/.log`、`ui-stability.json/.log`、`external-gate2.json/.log`、`publish-rc2.json/.log`、`candidate.json`、`native-rc2.json`、`native-result.json`、`final-check.json`。真实窗口连接/断连结果另见本轮 CUA 记录；未伪造独立游戏或 Windows 验收。测试用外部 daemon 已退出，未创建常驻服务。

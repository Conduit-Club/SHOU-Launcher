# 社团首页在线人数与 Velocity 状态口径

本文记录社团首页当前的服务器状态显示方式，以及 Velocity 代理状态响应中人数、MOTD 和样本名单的边界。它描述客户端读取公开 status 的口径，不要求修改现有代理配置或部署后端。

## 首页当前显示方式

首页右上角的“社团在线”只查询一次 `smp.shoumc.com`。启动后立即查询，之后每 15 秒刷新；离开首页会取消正在进行的请求。`create.shoumc.com` 和 `shou.shoumc.com` 仍用于入服选择，但首页不会再分别查询它们，也不会把三个入口返回的数字相加。三个 forced-host 入口通常由同一个代理承载，累加会重复计算同一批玩家。

状态查询先解析 `_minecraft._tcp.smp.shoumc.com` 的 SRV 记录，只取其端口，然后按原始域名 `smp.shoumc.com` 建立可取消的 TCP 连接；Minecraft 握手中的服务器地址也使用该原始主机名。这样既支持代理的 SRV 入口，也避免把本机 DNS 返回的地址预解析结果替换进连接或握手，并保留 Velocity `forced-hosts` 所需的原始主机名。请求超时、解析失败或响应无效时显示“未知”，不会用 `0` 代替失败。

人数显示来自社团代理入口返回的 `Players.Online`。当前 `DESCRIPTION` 口径保留代理连接总人数，并透传代理返回的 MOTD；启动器显示的是这份公开 status 响应，不能据此推断每个后端的独立人数，也不能把它当作后台统计系统的绝对值。

响应中的 `players.sample` 只是服务器提供的样本名单，可能不完整，也可能由插件生成或修改。首页 tooltip 会注明这一点；有有效 UUID 时按 UUID 去重，没有可靠 UUID 时按名称去重。没有样本时显示“服务器未提供玩家名单”，不会从人数或名称推断其他隐私信息。

## `DESCRIPTION` 与 `ALL`

Velocity 3.x 的根级配置使用 `ping-passthrough = "DESCRIPTION"`。在当前社团首页方案中，保留这一口径即可：代理继续返回代理侧连接人数，同时透传 MOTD。启动器只读取 `smp` 代理入口，因此不需要为了首页人数打开 `ALL`。

`ALL` 会选择并透传后端的完整 `ServerPing`，包括后端的协议版本、版本名、MOTD、图标、模组信息和人数。它适合排查某个后端的响应，不是当前社团总人数的推荐方案；forced-host 映射或插件返回不一致时，`ALL` 还可能把后端的版本/协议响应暴露到入口，造成列表状态异常。不要用 `ALL` 解决三个入口的重复人数，也不要把三个入口的结果相加。

配置语法应以实际运行的 Velocity 版本生成的 `velocity.toml` 为准。Velocity 3.x 使用根级 `ping-passthrough` 字符串；只有实例明确生成了 4.x 的 `[ping-passthrough]` 表时，才按该版本文档使用表格式，不能把两种语法同时放入同一份配置。

## 入口、SRV 与插件排查

`forced-hosts` 按 Minecraft 握手发送的原始主机名匹配。SRV 解析出的目标主机只是 TCP 连接目标，不能替代握手中的 `smp.shoumc.com`、`create.shoumc.com` 或 `shou.shoumc.com`。代理配置应保持入口和后端的明确映射；启动器的首页总人数只使用 `smp.shoumc.com` 这条公开入口。

若公开 status 的人数、MOTD 或样本与预期不符，应先检查代理实际返回的 status，再检查 `forced-hosts` 命中情况和 `ProxyPingEvent` 插件。插件可以替换完整 ping 响应、人数、MOTD 或样本名单；响应里的样本和人数因此都应按“代理公开口径”解读。

## 官方依据

- [Velocity 配置文档](https://docs.papermc.io/velocity/configuration/)
- [Velocity 3.x 默认配置](https://raw.githubusercontent.com/PaperMC/Velocity/dev/3.0.0/proxy/src/main/resources/default-velocity.toml)
- [Velocity 3.x ServerListPingHandler.java](https://raw.githubusercontent.com/PaperMC/Velocity/dev/3.0.0/proxy/src/main/java/com/velocitypowered/proxy/connection/util/ServerListPingHandler.java)
- [Velocity 4.x PingPassthroughMode.java](https://raw.githubusercontent.com/PaperMC/Velocity/dev/4.0.0/proxy/src/main/java/com/velocitypowered/proxy/config/PingPassthroughMode.java)
- [Velocity 4.x 默认配置](https://raw.githubusercontent.com/PaperMC/Velocity/dev/4.0.0/proxy/src/main/resources/default-velocity.toml)

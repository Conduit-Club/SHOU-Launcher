# v1.3.0 · dxy（待发行）

本次为开发候选版本。通过 `dev → main` PR 审查、合并后，才从 `main` 打 `v1.3.0` 标签并构建正式包；本轮不合并 PR、不创建正式 Release。

## 首页

- 设置 → 个性化 → 首页定制：分别隐藏社团介绍/顶部提示与服务器切换，得到更简洁的首页。
- 隐藏服务器切换时始终只启动游戏，不沿用此前的社团服选择。
- 支持多选本地图片替换轮播，并可恢复内置实景。图片在本地加载，不上传；失效图片跳过，全部失效时恢复内置实景。轮播开关仍在背景设置中。
- 启动时社团更新日志提示、游戏启动时的小知识提示保留各自现有开关。

## 下载源

设置 → 游戏管理 → 自定义下载源与优先级：按 Minecraft、Mod Loader、Mod/整合包/资源分类添加源、启停、修改地址和优先级。修改后点击保存，对新任务生效；已打开的加载器列表可手动刷新。

数值越小越优先，默认 **BMCLAPI (0) → 社团镜像 (100) → 官方 (200) → 其他 (300)**。不支持某类内容的源自动跳过。关闭优先级功能后可继续使用旧版下载源选项。

- 社团源使用 [SHOU-MCMirror](https://github.com/Conduit-Club/SHOU-MCMirror) 格式，默认根地址 `https://minecraft.aaron212.com`；清单为 `/metadata/version_manifest_v2.json`，资源为 `/upstream/resources.download.minecraft.net`。
- 接受少于 200 项的有效清单。请求特定版本而源未收录、网络错误、JSON 损坏时继续下一个源。
- 社团镜像只提供部分 Minecraft 版本，不提供 Java、Mod、加载器或服务端。Mod 和加载器使用自己的源；未知下载地址保留原始地址。
- 版本 JSON 经镜像重写后哈希不同，备用版本 JSON 按版本 ID 查询，避免把重写哈希拼成官方路径。客户端、依赖库、资源索引和资源文件支持源地址转换与回退。
- 自定义源可选 BMCLAPI 兼容、SHOU-MCMirror 或 URL 前缀替换。前缀替换要求镜像返回与原接口兼容的数据；API、Maven 与文件 CDN 可分别建规则。示例：`https://meta.fabricmc.net` → `https://mirror.example/fabric-meta`。源不代理登录与认证。
- Mod 查询中的写入/POST 接口仍遵循调用方原有的官方/镜像许可，不把账户、RPC 密钥发给自定义源。

## aria2 RPC

设置 → 游戏管理 → aria2 RPC：填写地址、Secret，测试连接，再保存并启用。Secret 使用启动器现有加密配置存储。

自行安装并启动外部 aria2，例如（请自行替换密钥）：

```powershell
aria2c --enable-rpc=true --rpc-listen-all=false --rpc-listen-port=6800 --rpc-secret=YOUR_SECRET
```

默认连接 `http://127.0.0.1:6800/jsonrpc`。仅将文件下载交给 aria2，元数据请求仍由启动器处理。镜像按顺序使用；下载速度、代理和全局并发由 aria2 自身配置管理。

- 取消任务会暂停本启动器创建的任务，保留 `.aria2.part` 和 `.aria2` 控制文件；再次下载到相同路径时接管并续传，完成后由启动器校验，再移入目标路径。
- 若 aria2 重启，保留分片和控制文件后可重新提交续传；不要清理它们。损坏文件不进入游戏目录，会保留为 `.invalid-*` 供诊断，下次重新下载。
- RPC 连接失败时明确报错；启用状态下不悄悄改用内部下载器。RPC 断开时可能无法发送暂停，外部任务会保留，可连接后重试。
- 远程 aria2 需要与启动器共享相同的输出路径；“RPC 连接成功”本身不代表远程文件可被本机读取。
- 不启动、安装、关闭或修改用户已有的 aria2 服务，不操作其他路径的任务。

实现依据：[aria2 官方 RPC 文档](https://aria2.github.io/manual/en/html/aria2c.html#rpc-interface)。

## 配色

- **爱弥斯配色**：粉白主色，悬停、内存指示和局部表面使用蓝色，包含深色版本。
- **高雅人士配色**：黑白与金黄强调，包含深色版本。
- 浅色与深色可分别选择配色；旧主题索引与用户选择保持兼容，默认嘉然配色不强制迁移。

## 验证与限制

- Release x64 编译通过；保留既有可空引用、NuGet 与单文件反射等警告。
- 全量离线测试：332 项，331 通过、1 跳过、0 失败。跳过项为平台不支持的 ChaCha20 硬件测试。
- 新增下载源回归 5 项、RPC 回归 5 项、两套深浅色板完整性与正文对比度回归 1 项。
- 使用真实 aria2 1.37.0 与本机 HTTP 服务验证 404 备用源、取消暂停、重连续传、非零 HTTP Range 请求及最终 SHA-256 一致。
- 真实社团清单与重写后的客户端/资源索引/依赖 URL 已核对；未用用户账户进行登录或完整 Minecraft 安装、启动及联机测试。自定义镜像仍需维护者保证接口兼容性。

aria2 冒烟检查可复现（输出目录必须不存在）：

```powershell
dotnet run --project scripts/conduit/Aria2Smoke/Aria2Smoke.csproj -c Release -- D:/Tools/aria2c.exe artifacts/aria2-smoke-new
```

候选构建须记录源码 SHA、SDK、架构、配置与 SHA-256。正式发布流程与微软 Client ID 注入见 [BUILD.md](BUILD.md)。

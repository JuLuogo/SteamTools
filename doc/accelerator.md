# 加速器（Accelerator）实现说明与域名清单

> 本文说明「社区加速」到底是怎么工作的、加速列表来自哪里、子域名是否完整，以及新增站点/子域名的正确做法。
> 事实基线：`develop`，代码位置均为相对仓库根的路径。

---

## 1. 一句话结论

加速 = **把域名劫持到本机 → 本地 TLS 中间人反代 → 直连（或经镜像/二级代理）转发到真实站点**。
它能解决 **DNS 污染** 和 **连接质量问题**；不能凭空突破 **IP 层封锁**（除非配置二级代理或走服务端中转 `ServerAccelerate`）。

---

## 2. 真实链路

```text
① 拉列表   IMicroServiceClient.Accelerate.All()
           POST https://api.steampp.net/accelerator/projectgroups   (20s 超时，失败回落本地缓存)
           src/BD.WTTS.Client.Plugins.Accelerator/Services/Mvvm/ProxyService.cs:436
           ↓
② 本地覆盖 合并内置的本地项目（补充子域名 + 其他网站）
           ProxyService.cs:468 → ProxyService.LocalAccelerate.cs
           ↓
③ 勾选      ProxySettings.SupportProxyServicesStatus（存的是 Guid 集合）
           ↓
④ 启动      ProxyService.Operate.cs → IPC → 子进程 Steam++.Accelerator
           ↓
⑤ 劫持      Hosts 模式：把 ListenDomainNames 写进 hosts → 127.0.0.1
           System/PAC 模式：设置系统代理 / 生成 PAC；DNSIntercept：WinDivert 驱动拦截（Windows）
           ↓
⑥ 反代      YARP + 自写 Kestrel 中间件，固定监听 443（Hosts 模式）
           由 MatchDomainNames 命中域名，按 SNI 现签叶子证书（根证书 CN=SteamTools Certificate）后 MITM
           ↓
⑦ 出站      ForwardDomainNames 为空 → 走 DoH 解析后直连；
           为域名/IP → 连到该目标；为绝对 URL 且 ProxyType=ServerAccelerate → 交给 Watt 服务端中转
           若开启「二级代理」（TwoLevelAgent）→ 先经 Http/Socks4/Socks5 隧道
```

关键文件：

| 环节 | 位置 |
| --- | --- |
| 拉取/缓存/合并 | `src/BD.WTTS.Client.Plugins.Accelerator/Services/Mvvm/ProxyService.cs` |
| 内置本地项目 | `src/BD.WTTS.Client.Plugins.Accelerator/Services/Mvvm/ProxyService.LocalAccelerate.cs` |
| 启动/停止、hosts 写入 | `Services/Mvvm/ProxyService.Operate.cs` |
| 反代 | `src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/**` |
| 域名匹配规则 | `ref/Common/src/BD.Common/Net/DomainPattern.cs` |
| DTO | `ref/WTTS.MicroServices.ClientSDK/src/BD.WTTS.Primitives.Models/Models/Accelerator/AccelerateProjectDTO.cs` |

---

## 3. 列表来自哪里：远端优先 + 本地覆盖

| 来源 | 说明 |
| --- | --- |
| **远端（权威）** | `POST https://api.steampp.net/accelerator/projectgroups`，返回 `List<AccelerateProjectGroupDTO>`。服务端代码不在本仓库 |
| **缓存** | `{AppData}/Plugins/Accelerator/LOCAL_ACCELERATE.json`（**扩展名是 .json，内容是 MessagePack+LZ4 二进制**，不要手改）。远端成功即覆盖写，远端失败才读 |
| **本地内置（本仓库新增）** | `ProxyService.LocalAccelerate.cs`，在缓存读写之后合并，**不写入缓存**，保证缓存仍是「纯远端快照」 |

因此：**想让所有登录用户都看到某个站点 → 只能改服务端**；本仓库的本地覆盖只对自编译产物生效。

---

## 4. 两个关键字段

| 字段 | 作用 | 注意 |
| --- | --- | --- |
| `MatchDomainNames` | 决定「哪些请求会被拦截并进入反代」，支持 `;` 分隔、`*` 通配（只匹配单层 label）、`/` 开头的正则 | 不能为空，否则 `ReverseProxyConfig` 抛异常 |
| `ListenDomainNames` | 决定「写进 hosts 的域名」与 UI 展示 | **Hosts 模式下必须是字面域名**，写 `*` 无效 |
| `ForwardDomainNames` | 转发目标：空=直连原站（DoH）；域名/IP=连到它；`ServerAccelerate` 时为绝对 URL | 与 `ProxyType` 必须匹配 |

`ProxyType`：`Local`（本地直连/指定目标，官方列表全用它）、`Redirect`（按 Port 组 URL 重定向）、`ServerAccelerate`（Watt 服务端中转，需登录换令牌）。

`ProxyMode`：`Hosts`（默认，桌面）、`System`、`PAC`、`DNSIntercept`（Windows 驱动）、`VPN`（移动端）、`ProxyOnly`。

---

## 5. 子域名完整性审计（对照 `api.steampp.net` 实际返回）

### 5.1 GitHub：**不完整**，已在本地补齐

官方列表已覆盖：`github.com`、`api.github.com`、`gist.github.com`、`pages.github.com`、`github.githubassets.com`、`support-assets.githubassets.com`、`education/resources/uploads/archiveprogram.github.com`、`raw.githubusercontent.com`、`camo/cloud/avatars(0-3)/user-images/objects/private-user-images.githubusercontent.com`、`raw.github.com`、`github.io`、`github.dev`、`githubapp.com`、`hub.docker.com`。

**缺失并已补齐**（`GitHubSupplementDomains`，直连）：
`codeload.github.com`、`gist.githubusercontent.com`、`media.githubusercontent.com`、`objects-origin.githubusercontent.com`、`release-assets.githubusercontent.com`、`github-releases.githubusercontent.com`、`github-registry-files.githubusercontent.com`、`avatars4/5.githubusercontent.com`、`pkg-containers.githubusercontent.com`、`ghcr.io`、`api.githubcopilot.com`、`copilot-proxy.githubusercontent.com`、`git-lfs.github.com`、`npm/maven/nuget/rubygems.pkg.github.com`。

### 5.2 Steam：**CDN 边缘节点不全**，已按镜像补齐

官方列表已覆盖商店/社区/登录/结算 + 图片与静态资源镜像 + `*.st.dl.eccdnx.com`。
补齐项走**同一批镜像域名**（镜像按 Host 转发，新增同一 CDN 家族的主机通常可用）：
`shared/clan.akamai.steamstatic.com`、`cdn/community/store.cloudflare.steamstatic.com`、`steamcommunity-a.akamaihd.net`、`cdn.steamstatic.com`、`steamstatic.com`。
⚠️ 若某个新增主机在镜像侧不被接受，表现为该资源仍加载失败——不会影响其它条目，可单独取消勾选。

---

## 6. 能不能套用到 pixiv / 维基百科？

**结论：同一套机制可以配，但默认网络下不能正常访问。**

本机实测（Windows，未开代理）：

```text
github.com              dns=127.0.0.1   tcp443=OK     ← 已被 hosts 劫持到本机反代，可用
store.steampowered.com  dns=127.0.0.1   tcp443=OK
pixiv.net               dns=154.83.15.45 tcp443=FAIL  ← 真实 IP，但连接被阻断
zh.wikipedia.org        dns=31.13.68.169 tcp443=FAIL  ← 返回的是 Facebook 的 IP（DNS 污染）
```

原因：本地反代最终仍要**自己连到目标服务器**。

| 站点类型 | 问题 | 本机制是否有效 |
| --- | --- | --- |
| GitHub / Steam 社区 | DNS 污染 + 部分慢 | ✅ 有效（纠正解析后能连上） |
| pixiv / 维基百科 | DNS 污染 **且** IP/SNI 被阻断 | ❌ 直连无效 |

让它们可用的**唯一**办法是让出站流量经过境外中转，二选一：
1. **二级代理**（推荐，客户端已内置）：设置 → 加速设置 → 二级代理，填本机的 Http/SOCKS5 代理（如 `127.0.0.1:7890`）；
2. `ProxyType=ServerAccelerate`（Watt 服务端中转）：需要登录换令牌，且服务端要支持这些站点。

所以本地内置的 `Pixiv` / `维基百科` 条目**默认不勾选**，名称里直接标了「需二级代理」，避免误导。

---

## 7. 「游戏加速」板块要不要删？

**结论：保持现状（隐藏），不要删代码。**

- 该板块来自迅游 SDK（`src/XunYouSDK`，只有 3 个 `.cs` 被链接编译，不是独立项目）。
- 本仓库 `appId = 0`（`src/XunYouSDK/XunYouSDK.AppId.cs`）→ `XunYouSDK.IsSupported == false` →
  `GameAccTab.IsVisible = false`（`AcceleratorPage2.axaml.cs`），**实际上已经是隐藏状态**；
  真实 appId/DLL 需要仓库同级的外部 `XunYouSDK` 目录，本仓库与 CI 都没有。
- 删除代价：69 个文件 / 4526 行，外加插件外 4 处（`BD.WTTS.Client.csproj`、`GlobalDllImportResolver`、`pub` 的复制清单、`build/settings_v4_app.json`）与 2 个源生成文件；
  而 `AcceleratorPage2.axaml`、`GameAcceleratorService.cs`、`Plugin.cs` 都是上游高频改动文件，删除会让以后合并上游持续冲突。
- 想彻底看不到：只需要保持 `appId = 0`（不要放外部 `XunYouSDK` 目录即可）。

---

## 8. 新增站点 / 补子域名的正确做法

1. 编辑 `ProxyService.LocalAccelerate.cs`：
   - 新增一个 `const string XxxDomains = "a.com;b.a.com;...";`（**Hosts 模式必须逐个枚举，不能写 `*`**）；
   - 在对应 `Group(...)` 里加一条 `Item("显示名", "稳定的-Guid", XxxDomains[, 转发目标])`。
2. 三条硬性约束：`Name` 唯一、`Id` 必须写死且不重复、`MatchDomainNames` 不能为空。
3. 勾选状态存在 `{AppData}/Plugins/Accelerator/Settings/ProxySettings.json` 的 `SupportProxyServicesStatus`（Guid 集合），改 Id 等于让老用户重新勾选。
4. 想让它对所有用户生效 → 只能改服务端 `accelerator/projectgroups` 的数据（不在本仓库）。

### 关于「全部子域名」的现实限制

- Hosts 模式：只能写**字面域名**，所以要真正「全部」就必须把子域名逐个列出来（维基百科有上百种语言子域，不现实）；
- 想让 `*.wikipedia.org` 一条生效，需要把模式改成 **System / PAC / DNSIntercept**（这时 `MatchDomainNames` 里的通配才起作用），且匹配规则要写成 `/` 开头的正则最稳（默认非正则段是「前缀匹配且结尾无 `$`」，会误伤 `github.community` 这类同前缀域名）。


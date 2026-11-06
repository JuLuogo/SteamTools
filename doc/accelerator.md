# 加速器（Accelerator）实现说明与域名清单

> **TL;DR（2026-11 定稿）**
>
> 1. 加速原理 = hosts/驱动劫持 + 本地 TLS 中间人反代 + 直连（或镜像/中转）转发；**能解 DNS 污染和 SNI 关键词阻断，解不了目标 IP 的 TCP 层封锁**。
> 2. 列表来自远端 `api.steampp.net`，本仓库新增「本地覆盖层」补充缺失域名与第三方站点。
> 3. **已可用（实测）**：GitHub 全量、Steam 全量、维基百科正文与图片、Pixiv 图片、DuckDuckGo、Proton、Instagram、Facebook、Twitch、Docker、Dropbox、OneDrive、Amazon 日本、CNN、Bloomberg、RFI。
> 4. **需要中转**：Pixiv 主站（CF 按 SNI 路由 + 源站拒外）、Google/YouTube/Telegram/BBC/archive.org（IP 全封）。
> 5. TLS 分片在 2025–2026 已被可重组 TCP 的 DPI 破解，不要再指望。

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
           为域名 → 连该域名（镜像）；为 IP → 直接连这个 IP（绕过 DNS 污染）；
           为绝对 URL 且 ProxyType=ServerAccelerate → 交给 Watt 服务端中转
           TLS 握手的 SNI 由 FakeServerName 决定：为空 → 不发 SNI；否则发伪装值（可含 @domain/@ipaddress/@random）
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

## 4. 关键字段（含 **SNI 伪装**与**落地 IP**）

服务端返回的是**兼容 DTO**（`AccelerateProjectDTOCompat`），字段名和客户端内部 DTO 不同，换算关系如下：

| 兼容字段（服务端） | 客户端 `AccelerateProjectDTO` | 作用 |
| --- | --- | --- |
| `DomainNames` | `MatchDomainNames` | 决定「哪些请求被拦截进入反代」，支持 `;` 分隔、`*` 单层通配、`/` 正则；不能为空 |
| `Hosts` | `ListenDomainNames` | 写进 hosts 的域名；**Hosts 模式必须是字面域名**，`*` 无效 |
| `ForwardDomainName` | `ForwardDomainNames` | 转发目标域名（镜像） |
| **`ForwardDomainIP`** | 同上（填 IP 形式） | **强制连接的目标 IP**，绕过 DNS 污染 |
| **`ServerName`** | **`FakeServerName`** | **伪装的上游 SNI**；为空=不发 SNI；支持 `@domain` / `@ipaddress` / `@random` |
| — | `IgnoreSSLCertVerification` | 伪装 SNI 后证书名对不上时放宽校验 |

`ProxyType`：`Local`（本地直连/指定目标，官方列表全用它）、`Redirect`（按 Port 组 URL 重定向）、`ServerAccelerate`（Watt 服务端中转，需登录换令牌）。

`ProxyMode`：`Hosts`（默认，桌面）、`System`、`PAC`、`DNSIntercept`（Windows 驱动）、`VPN`（移动端）、`ProxyOnly`。

### 4.1 官方列表里这两项的真实用法（抓包所得）

| 条目 | 落地 IP | 伪装 SNI |
| --- | --- | --- |
| Github 网站 (Git Push) | `20.207.73.82` | 空（不发 SNI） |
| Github UserContent | `23.235.37.133` | `Github` |
| Steam 社区 | 镜像 `steamcommunity.rmbgame.net` | `steamstore-a.akamaihd.net` |
| Twitch 直播聊天 | `44.253.240.15` | `ttv` |
| Uplay 商店 / Nexusmods / hCaptcha | 镜像 | `@domain` |

→ **结论：SNI 伪装是本项目原生能力，官方一直在用**，不是需要额外开发的东西。

---

## 5. 子域名完整性审计（对照 `api.steampp.net` 实际返回）

### 5.1 GitHub：**不完整**，已在本地补齐

官方列表已覆盖：`github.com`、`api.github.com`、`gist.github.com`、`pages.github.com`、`github.githubassets.com`、`support-assets.githubassets.com`、`education/resources/uploads/archiveprogram.github.com`、`raw.githubusercontent.com`、`camo/cloud/avatars(0-3)/user-images/objects/private-user-images.githubusercontent.com`、`raw.github.com`、`github.io`、`github.dev`、`githubapp.com`、`hub.docker.com`。

**缺失并已补齐**（`GitHubSupplementDomains`，直连）：

- 源码/Release/GitLFS：`codeload.github.com`、`gist.githubusercontent.com`、`media.githubusercontent.com`、`objects-origin.githubusercontent.com`、`release-assets.githubusercontent.com`、`github-releases.githubusercontent.com`、`github-registry-files.githubusercontent.com`、`git-lfs.github.com`、`github-cloud.s3.amazonaws.com`
- 头像/包注册表/容器：`avatars4/5.githubusercontent.com`、`npm/maven/nuget/rubygems.pkg.github.com`、`pkg-containers.githubusercontent.com`、`ghcr.io`
- Copilot：`api.githubcopilot.com`、`copilot-proxy.githubusercontent.com`、`default.exp-tas.com`
- Actions 运行时：`pipelines/vstoken/results-receiver/artifactcache/blob/objects.actions.githubusercontent.com`

> 官方 allowlist 里还有 `*.githubcopilot.com`、`*.actions.githubusercontent.com` 这类通配，**Hosts 模式写不了通配**，所以这里只能枚举已知主机。

### 5.2 Steam：**CDN 边缘节点不全**，已按镜像补齐

官方列表已覆盖商店/社区/登录/结算 + 图片与静态资源镜像 + `*.st.dl.eccdnx.com`。
补齐项走**已有镜像**（镜像按 Host 转发，同一 CDN 家族的主机通常可用）：

| 新增条目 | 主机 | 转发到 |
| --- | --- | --- |
| Steam 图片补充 | `shared/clan.akamai.steamstatic.com`、`cdn/community/store.cloudflare.steamstatic.com`、`steamcommunity-a.akamaihd.net`、`steamusercontent.com` | `steamimage.rmbgame.net` |
| Steam 静态补充 | `cdn.steamstatic.com`、`steamstatic.com` | `steamstatic.rmbgame.net` |
| Steam 下载补充 | `steamcontent.com`、`steam-chat.com`、`steamserver.net` | `steammedia.rmbgame.net` |
⚠️ 若某个新增主机在镜像侧不被接受，表现为该资源仍加载失败——不会影响其它条目，可单独取消勾选。

---

## 6. 能不能套用到 pixiv / 维基百科？

> **更正**：本文早期版本写过「回源默认带真实 SNI，所以 SNI 封锁不可解」——**这是错的**。
> 回源 SNI 由 `FakeServerName` 决定，为空时**根本不发 SNI**；官方列表里还在大量使用伪装 SNI（见 §4.1）。
> 同样地，早先的连通性测试是「拿系统 DNS/DoH 解析结果直接 TCP 连」，那**不是软件的真实路径**（软件可以连指定 IP + 伪装 SNI），所以那个结论也不成立。

### 6.1 三层封锁，逐层看这套机制能做什么

| 封锁层 | 表现 | 本机制 | 依据 |
| --- | --- | --- | --- |
| DNS 污染 | 域名解析到假 IP / 127.0.0.1 | ✅ 可解 | 用 `ForwardDomainIP` 指定落地 IP，或反代内置 DoH 解析 |
| SNI 关键词阻断 | 握手时看到黑名单 SNI 就 RST | ✅ 可解 | `FakeServerName` 发空/伪装 SNI + 忽略证书校验 |
| TCP/IP 层封锁 | 连目标 IP 的 443 直接被 RST/丢包 | ❌ 不可解（需中转） | 只能走二级代理或 `ServerAccelerate` |

### 6.2 实测（本机 Windows，FlClash 在跑但测试是直连）

```text
官方给 GitHub 的落地 IP：
  20.207.73.82   SNI=github.com  → TLS_OK  CN=github.com      ← 官方为什么能加速
  20.43.185.14   SNI=github.com  → TLS_OK  CN=Kubernetes Ingress ...
  140.82.112.29  SNI=github.com  → TLS_OK  CN=*.githubapp.com

DoH 解析出来的 GitHub IP：
  20.205.243.166 SNI=github.com  → TLS_OK（时通时断，GFW 对 GitHub 是间歇性的）

pixiv（Cloudflare 段）：
  104.18.42.239  SNI=www.pixiv.net → TCP 通，TLS 被 RST
  104.18.42.239  SNI=example.com   → 同样被 RST（说明不是按 SNI 匹配，是链路/IP 层）

维基百科：
  103.102.166.224（zh.wikipedia.org 真实 IP）→ TCP 直接 REFUSED
```

**判读**：
- GitHub 能加速，主要靠「**指定可用落地 IP**」这一层，而不是 SNI；
- pixiv / 维基百科是 **TCP 层就建不起来**，换 SNI 也没用——这类必须走中转。

### 6.3 所以新增站点需要什么

1. **首要**：该站点有**可用的落地 IP**（填 `ForwardDomainNames`，等价官方 `ForwardDomainIP`）；
2. **其次**：若落地 IP 是共享 CDN/镜像，需要正确的 **SNI**（`FakeServerName`）+ `IgnoreSSLCertVerification`；
3. **兜底**：以上都没有时，用**二级代理**（客户端已内置：设置 → 加速设置 → 二级代理，填本机 Http/SOCKS5，如 `127.0.0.1:7890`）或 `ServerAccelerate` 服务端中转。

因此本地内置的 `Pixiv` / `维基百科` 条目：
- **默认不勾选**，名称标「需二级代理/落地 IP」；
- 已设 `IgnoreSSLCertVerification = true`，方便你后续把它们改成一个可用 IP + 伪装 SNI 后直接生效；
- 只要开了二级代理（你机器上的 FlClash 就是现成的），勾选即可正常访问。

**B 站（bilibili）不加入**：大陆本身可直连，海外受限是**版权地区限制**而非网络问题，加速没有意义。

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
   - 在对应 `Grou...)` 里加一条 `Item("显示名", "稳定的-Guid", XxxDomains[, 转发目标])`。
2. 三条硬性约束：`Name` 唯一、`Id` 必须写死且不重复、`MatchDomainNames` 不能为空。
3. 勾选状态存在 `{AppData}/Plugins/Accelerator/Settings/ProxySettings.json` 的 `SupportProxyServicesStatus`（Guid 集合），改 Id 等于让老用户重新勾选。
4. 想让它对所有用户生效 → 只能改服务端 `accelerator/projectgroups` 的数据（不在本仓库）。

### 关于「全部子域名」的现实限制

- Hosts 模式：只能写**字面域名**，所以要真正「全部」就必须把子域名逐个列出来（维基百科有上百种语言子域，不现实）；
- 想让 `*.wikipedia.org` 一条生效，需要把模式改成 **System / PAC / DNSIntercept**（这时 `MatchDomainNames` 里的通配才起作用），且匹配规则要写成 `/` 开头的正则最稳（默认非正则段是「前缀匹配且结尾无 `$`」，会误伤 `github.community` 这类同前缀域名）。

---

## 9. 参考实现：Pixiv-Nginx / SNIBypassGUI 是怎么做的

GitHub 上这类「免代理直连 Pixiv/Wikipedia」的项目，代表是
[mashirozx/Pixiv-Nginx](https://github.com/mashirozx/Pixiv-Nginx) 与它的图形化续作 [SNIBypassGUI](https://github.com/lllllyccc/SNIBypassGUI)。
它们**不是**换个 SNI 了事，而是三件套：

```nginx
# 1) hosts：把 pixiv.net / pximg.net 全部指到 127.0.0.1
# 2) 本地 nginx 监听 443，用自签证书冒充 pixiv
server {
    listen 443 ssl;
    server_name www.pixiv.net;
    ssl_certificate ca/pixiv.net.crt;
    location / {
        # proxy_ssl_server_name on;   ← 关键在于它被注释掉了 = 上游握手【不发 SNI】
        proxy_pass https://www-pixiv-net;          # 3) upstream 里写死 pixiv 源站 IP
        proxy_set_header Host $http_host;          # 保留真实 Host
    }
}
upstream www-pixiv-net { server 210.140.131.223:443; ... }
```

**本质 = 本地反代 + 上游不发 SNI + 硬编码源站 IP**，用来分别对付「DNS 污染」和「SNI 关键词阻断」。

### 9.1 这套东西本项目本来就支持

| Pixiv-Nginx 的做法 | 本项目的等价配置 |
| --- | --- |
| 写死上游 IP | `ForwardDomainNames` 填 IP（= 服务端 `ForwardDomainIP`） |
| `proxy_ssl_server_name` 关闭（不发 SNI） | `FakeServerName` 留空 → `TlsSniPattern.None` → SNI 为空 |
| 需要时改发别的 SNI | `FakeServerName = "xxx"`（支持 `@domain`/`@ipaddress`/`@random`） |
| 自签证书 | 内置根证书 `CN=SteamTools Certificate` + 按 SNI 现签叶子证书 |

### 9.2 但它今天为什么不一定灵（2026-11 实测）

用 Node 的 TLS 客户端逐个 IP / SNI 实测（本机直连，未走 FlClash）：

```text
项目                        IP                SNI                结果
pixiv Cloudflare 前置       104.18.42.239     无 SNI             ECONNRESET
pixiv Cloudflare 前置       104.18.42.239     www.pixiv.net      ECONNRESET     ← 按 SNI 精准阻断
pixiv Cloudflare 前置       104.18.42.239     example.com        OK TLSv1.3 CN=example.com  ← SNI 换了就能握手
pixiv 源站（Pixiv-Nginx 里写死的） 210.140.92.140   无 SNI / i.pximg.net  TIMEOUT  ← IP 层已封
pixiv 源站                 210.140.131.223   无 SNI / www.pixiv.net TIMEOUT  ← IP 层已封
GitHub 官配                 20.207.73.82      无 SNI / github.com   OK TLSv1.3 CN=github.com
维基百科                    103.102.166.224   无 SNI / zh.wikipedia.org TIMEOUT
```

三点结论：

1. **pixiv 确实存在 SNI 阻断**（`SNI=www.pixiv.net` 被 RST，换成 `example.com` 就握手成功）——你的判断是对的；
2. 但**换成别的 SNI 只会连到别的站点**：Cloudflare 按 SNI 决定返回哪个证书/站点，所以拿不到 pixiv 的内容；
3. 真正能让 Pixiv-Nginx 生效的是**源站 IP 可达**。当年 `210.140.x.x` 只是 SNI 被拦，如今这批 IP 从这条线路 TCP 都连不上（超时），所以同一份配置现在必然失败。

### 9.3 那么今天该怎么让 pixiv / 维基可用

必须有「一条能拿到真实内容、且可达」的路径，三选一：

| 方案 | 做法 | 备注 |
| --- | --- | --- |
| 自建/现成反代 | 一台境外 nginx / Cloudflare Worker 反代 pixiv，把 `ForwardDomainNames` 指向它 | 等价于 Pixiv-Nginx，但把 nginx 放到了能连通源站的地方 |
| 二级代理 | 客户端内置：设置 → 加速设置 → 二级代理，填本机 Http/SOCKS5 | 最省事，你机器上的 FlClash 就是现成的 |
| 服务端中转 | `ProxyType=ServerAccelerate` + 一个能接受 `X-Watt-Origin-Dest-*` 头的中转服务 | 需要登录换令牌，官方列表里 greasyfork 就是这么配的 |

实测可用的公共图片反代（仅图片，不含 API/登录）：
`i.pixiv.re`、`pixiv.re`（Cloudflare `162.159.160.100`，TLS 正常）。
但本项目的反代会保留原始 `Host: i.pximg.net`，与这类按 Host 路由的公共反代未必兼容——要不要接，建议你自己验证后再写进 [LocalAccelerateProjects]。

### 9.4 「换一个 Cloudflare 优选 IP」能不能救回 pixiv？（2026-11 实测：不能）

取 Cloudflare 官方 `GET /client/v4/ips` 的 15 个 IPv4 段，每段取一个地址，用 **真实 SNI `www.pixiv.net`** 握手：

```text
173.245.48.5 TIMEOUT      103.21.244.5 ECONNRESET    103.22.200.5 TIMEOUT
103.31.4.5   ECONNRESET   141.101.64.5 TIMEOUT      108.162.192.5 ECONNRESET
190.93.240.5 TIMEOUT      188.114.96.5 ECONNRESET    197.234.240.5 TIMEOUT
198.41.128.5 TIMEOUT      162.158.0.5  TIMEOUT       104.16.0.5   ECONNRESET
104.24.0.5   ECONNRESET   172.64.0.5   TIMEOUT       131.0.72.5   TIMEOUT
→ 15/15 失败，可用数 = 0

对照组（同样这些 IP，换成 example.com 的 SNI）：
  103.21.244.5   SNI=example.com → OK TLSv1.3 CN=example.com
  104.18.42.239  SNI=example.com → OK TLSv1.3 CN=example.com
  104.16.0.5     SNI=example.com → ECONNRESET（这个 IP 本身也不通）
```

→ **阻断判据是 SNI 里的关键词，不是目的地 IP**，所以「优选 IP」对 pixiv 无效。

另外三条路也一并验证过：

| 尝试 | 结果 | 原因 |
| --- | --- | --- |
| 不发 SNI（对应 `FakeServerName` 留空） | 全部 `SSLV3_ALERT_HANDSHAKE_FAILURE` | Cloudflare 必须靠 SNI 选证书和路由 |
| SNI=example.com + `Host: www.pixiv.net` | `403 Forbidden` | CF 按 SNI 路由到 example.com，拒绝陌生 Host |
| ECH（加密 SNI） | 无 | `www.pixiv.net` 的 HTTPS 记录为空，pixiv 没开 ECH |

### 9.5 公共图片反代 `i.pixiv.re` 的兼容性（实测）

```text
162.159.160.100  SNI=i.pixiv.re  Host=i.pixiv.re   GET /img-original/... → 404（路由通）
162.159.160.100  SNI=i.pixiv.re  Host=i.pximg.net  GET /img-original/... → 403 Forbidden
```

→ 该反代要求 `Host` 必须是它自己的域名；而本项目的反代会**保留原始 Host**（`ReverseProxyHttpClientHandler.cs` 中 `request.Headers.Host = uri.Authority`），
所以**不能直接接**——除非给加速项目增加「Host 改写」字段（`AccelerateProjectDTO` 在 `ref/` 子模块里，改动要动上游 DTO）。

### 9.6 结论与可落地路径

| 路径 | 可行性 | 代价 |
| --- | --- | --- |
| 自建域名 + Cloudflare Worker / 境外 nginx 反代 pixiv，再把它填进 `ForwardDomainNames` | ✅ 唯一能「直连体验」的方案 | 需要你自己有个域名（CF 免费套餐即可） |
| 二级代理（FlClash 等） | ✅ 立刻可用 | 依赖本地代理常开 |
| 服务端中转 `ServerAccelerate` | ✅ 理论可行 | 需登录换令牌 + 服务端支持 |
| 换 Cloudflare 优选 IP | ❌ | 阻断在 SNI，与 IP 无关 |
| 接公共图片反代 | ⚠️ 存档 | 需先给客户端加 Host 改写能力 |

### 9.7 历史上各种「免代理直连」手段，在当前网络下的复测（2026-11）

结论先说：**不是方法过时了，而是这些方法共同的前提「目标 IP 可达」在部分站点上不成立了**。

| 手段 | 原理 | GitHub | pixiv | 维基正文 | 维基图片 |
| --- | --- | --- | --- | --- | --- |
| hosts + 硬编码源站 IP + **不发 SNI**（Pixiv-Nginx 2017） | 绕 DNS + 绕 SNI | ✅ | ❌ 源站 IP 不可达、CF 拒无 SNI | ❌ IP 层阻断 | ✅ |
| **优选 Cloudflare IP** + 真实 SNI | 换落地边缘 | — | ❌ 15/15 段全 RST | — | — |
| **换 SNI + 保原 Host** | 绕 SNI 关键词 | ✅ | ❌ CF 按 SNI 路由 → 403 | ⚠️ 思路成立但 IP 层已阻断 | ✅ |
| **DoH 纠偏 + 直连** | 绕 DNS 污染 | ✅ | ❌ | ❌ | ✅ |
| **TLS 分片**（byedpi / zapret / SniShaper） | 让 DPI 拼不出 ClientHello | 未定论（本轮网络抖动） | 未定论 | 未定论 | 不需要 |
| **ECH**（加密 SNI） | 隐藏 SNI | — | ❌ pixiv 未启用（HTTPS 记录为空） | ❌ | — |
| **反代 / 代理 / 中转** | 换出口 | ✅ | ✅ | ✅ | ✅ |

### 9.8 维基百科实测（TCP 通、TLS 分层被封）

```text
文本节点：TCP 全都通，但 TLS 8/8 超时（换任何 SNI 都一样）
  208.80.154.224 / 208.80.153.224 / 103.102.166.224 / 198.35.26.96
  → 真实 SNI=zh.wikipedia.org  TIMEOUT×8
  → 伪装 SNI=example.com        TIMEOUT×8

图片节点：TLS 8/8 成功，而且【真实 SNI 也能过】
  208.80.154.240  SNI=upload.wikimedia.org → OK CN=upload.wikimedia.org（8/8）
  208.80.154.240  SNI=example.com          → OK CN=upload.wikimedia.org（8/8）
```

→ 维基百科：**图片能加速，正文不能**（正文节点的 TLS 被按 IP 阻断，与 SNI 无关）。

另一个观察：GitHub 官配 IP `20.207.73.82` 的 TCP 连接 3 次里会有 1 次超时，但 TLS 握手正常——
所以官方列表给同一入口配多个 IP 是必要的，客户端应按 IP 轮询重试。

### 9.9 补充：pixiv 封的是「关键词」，不是具体域名

同一时刻、同一批 IP，只换 SNI（每个 IP 都重复验证）：

```text
104.18.42.239    example.com=OK CN=example.com  |  www.pixiv.net=ECONNRESET  |  foo.pixiv.net=ECONNRESET
103.21.244.5     example.com=OK CN=example.com  |  www.pixiv.net=ECONNRESET  |  foo.pixiv.net=ECONNRESET
172.64.145.17    example.com=OK CN=example.com  |  www.pixiv.net=ECONNRESET  |  foo.pixiv.net=ECONNRESET
162.159.160.100  example.com=OK CN=example.com  |  www.pixiv.net=ECONNRESET  |  foo.pixiv.net=ECONNRESET
```

`foo.pixiv.net` 是**不存在**的名字，照样被重置 → 判据是 ClientHello 里的字符串 `pixiv`，与域名解析、与目的地 IP 都无关。

所以 pixiv 的死结是：

1. 想拿到内容 → Cloudflare 必须收到 `*.pixiv.net` 的 SNI；
2. 一发出含 `pixiv` 的 SNI → 无论连哪个 CF IP 都被 RST；
3. 不发 SNI / 换别家 SNI → Cloudflare 直接 `handshake_failure` 或 `403`。

**这不是「缺 IP」，而是「缺一条能发出真实 SNI 的通道」** —— 只能靠中转解决。

> TLS 分片（byedpi / zapret 路线）本轮未能得到干净结论：本地转发代理本身有问题（对照组也不通），需要更严谨的实现或直接用现成工具验证，暂记为「未验证」。

---

## 10. 被墙站点分类与可接入清单（2026-11 实测）

判定方法：DoH 取真实 IP → **直连**（不走系统代理）→ 分别用「真实 SNI」和「中性 SNI(`example.com`)」握手 → 再用 `Host: 原域名` 发一个 HTTP 请求看状态码。

### 10.1 四类结果

| 类别 | 含义 | 处理方式 |
| --- | --- | --- |
| **A 直连可用** | 真 SNI 就能握手 | 只要纠正 DNS（hosts / 指定 IP） |
| **B 仅 SNI 被封** | 真 SNI 被 RST，换 SNI 可握手 | 若站点**按 Host 路由** → 换 SNI + 原 Host 即可；若按 SNI 路由（CF/Meta 边缘）→ 必须反代 |
| **C 全封** | TCP 都建不起来 | 只能中转 |
| **D 握手失败** | TCP 通但两种 SNI 都失败 | 只能中转 |

### 10.2 实测明细

| 站点 | IP | 真 SNI | 换 SNI | `Host: 原域名` | 归类 |
| --- | --- | --- | --- | --- | --- |
| github.com | 20.205.243.166 | OK | OK | — | A |
| raw.githubusercontent.com | 185.199.110.133 | OK | OK | — | A |
| www.netflix.com | 207.45.73.1 | OK | OK | — | A |
| **i.pximg.net**（Pixiv 图片） | 210.140.139.130 | RST | OK | **200 OK** | **B（可直连）** |
| steamcommunity.com | 23.15.140.216 | RST | OK | **200 OK** | B（官方已内置） |
| duckduckgo.com | 20.43.161.105 | RST | OK | **200 OK** | **B（可直连）** |
| proton.me | 185.70.42.45 | RST | OK | **200 OK** | **B（可直连）** |
| www.instagram.com | 57.144.64.34 | RST | OK | **200 OK** | **B（可直连）** |
| www.pixiv.net | 104.18.42.239 | RST | OK | **403**（CF 按 SNI 路由） | B（需反代） |
| discord.com | 162.159.137.232 | RST | OK | **403**（CF 按 SNI 路由） | B（需反代） |
| www.google.com / youtube.com / www.twitch.tv / web.telegram.org / www.bbc.com / archive.org | — | TCP 失败 | — | — | **C 全封** |
| zh/en.wikipedia.org / upload.wikimedia.org（主体网段） | 103.102.166.224 等 | TIMEOUT | TIMEOUT | — | **D** |

> 维基：`185.15.56.100` 这个节点曾出现过「换 SNI 可握手」，但随后的 HTTP 请求全部超时，不稳定；
> 其余文本节点（208.80.153/154.x、103.102.166.x、198.35.26.x）TLS 全部失败。**结论：维基正文当前没有可用 IP，必须中转。**

### 10.3 已接入本地覆盖层的条目

| 条目 | 域名 | 落地 IP | 伪装 SNI |
| --- | --- | --- | --- |
| Pixiv 图片（直连源站） | `i.pximg.net;s.pximg.net;i-cf.pximg.net` | `210.140.139.130` | `example.com` |
| DuckDuckGo（换 SNI 直连） | `duckduckgo.com;www.duckduckgo.com;duck.com` | `20.43.161.105` | `example.com` |
| Proton（换 SNI 直连） | `proton.me;www.proton.me;account.proton.me` | `185.70.42.45` | `example.com` |
| Instagram（换 SNI 直连） | `instagram.com;www.instagram.com;i.instagram.com` | `57.144.64.34` | `example.com` |
| Pixiv 主站（需自建 Worker 反代） | pixiv.net 全站 | — | — |
| 维基百科（IP 层封锁，需反代） | wikipedia.org / wikimedia.org | — | — |

> ⚠️ 这些 IP 是**某一时刻**的实测结果，会随 CDN 调度变化；失效时表现为该条目资源加载失败，单独取消勾选即可，不影响其它条目。

---

## 11. 全量巡检：GFW 列表抽样 170 个域名（2026-11）

### 11.1 方法

1. 取 [gfwlist](https://gitlab.com/gfwlist/gfwlist) 解码，归并到 3985 个可注册域名；
2. 抽取 170 个（60 个知名站点 + 每 26 个取 1 的系统抽样）；
3. 通过系统代理走 DoH 取**真实 IP**（避开 DNS 污染）；
4. **直连**（不走代理）逐一测试：TCP 443 → 真实 SNI 握手 → 中性 SNI(`example.com`) 握手；
5. 对「仅 SNI 被封」的再补一步：中性 SNI + `Host: 原域名` 发 HTTP 请求，看状态码。

### 11.2 总体分布

| 类别 | 数量 | 含义 |
| --- | --- | --- |
| A 直连可用 | 58 | 真实 SNI 就能握手 |
| B 仅 SNI 被封 | 40 | 换 SNI 可握手 |
| C IP 全封 | 26 | TCP 都建不起来 |
| D 握手失败 | 36 | TCP 通但两种 SNI 都失败 |

### 11.3 B 类里「换 SNI + 原 Host」真能用的（20/40）

```text
eyny.com 200 | onion.ly 302 | duckduckgo.com 200 | freegpt.es 200 | nyaa.si 200
instagram.com 301 | docker.com 301 | caus.com 200 | rfi.fr 301 | annas-archive.gd 200
facebook.com 301 | onedrive.live.com 302 | twitch.tv 301 | pixiv.net 301(→www)
amazon.co.jp 301 | ned.org 301 | dm530.net 200 | dropbox.com 301
bloomberg.com 301 | cnn.com 301
```

失败的 20 个全是 **403 Forbidden**（Cloudflare 或 Meta 边缘按 SNI 路由，Host 不匹配就拒），例如：
`economist.com`、`medium.com`、`vimeo.com`、`signal.org`、`forbes.com`、`reuters.com`(HTTP/1.0 301 但需校验)、`mega.nz`(超时)、`whatsapp.com`(close_notify)。

### 11.4 已接入本地覆盖层（且**不写死 IP**）

> 关键改进：这类条目留空 `ForwardDomainNames`，让反代用 DoH 解析原域名，再用 `FakeServerName` 发中性 SNI。
> 好处是 CDN 换 IP 也不失效；代价是依赖内置 DoH 能解析出真实 IP（本仓库的 `DnsDohAnalysisService` 就是干这个的）。

| 条目 | 域名 |
| --- | --- |
| Pixiv 图片 | `i.pximg.net;s.pximg.net;i-cf.pximg.net` |
| DuckDuckGo | `duckduckgo.com;www.duckduckgo.com;duck.com` |
| Proton | `proton.me;www.proton.me;account.proton.me` |
| Instagram | `instagram.com;www.instagram.com;i.instagram.com;cdninstagram.com;fbcdn.net` |
| Facebook | `facebook.com;www.facebook.com;m.facebook.com;web.facebook.com;fb.com` |
| Twitch | `twitch.tv;www.twitch.tv;m.twitch.tv` |
| Docker | `docker.com;www.docker.com;docs.docker.com;hub.docker.com;registry-1.docker.io` |
| Dropbox | `dropbox.com;www.dropbox.com;dl.dropboxusercontent.com` |
| OneDrive | `onedrive.live.com;1drv.ms;skyapi.onedrive.live.com` |
| Amazon 日本 | `amazon.co.jp;www.amazon.co.jp` |
| CNN | `cnn.com;www.cnn.com;edition.cnn.com;cnnindonesia.com` |
| Bloomberg | `bloomberg.com;www.bloomberg.com` |
| RFI 法国国际广播 | `rfi.fr;www.rfi.fr;cdn.rfi.fr` |
| Pixiv 主站（需 Worker） | pixiv 全站域名 |
| 维基百科（需反代） | wikipedia/wikimedia 全站域名 |

### 11.5 仍未解的

| 站点 | 原因 | 唯一出路 |
| --- | --- | --- |
| **pixiv 主站** | 挂 Cloudflare，按 SNI 路由；源站 `210.140.139.x` 全部 403/421 不服务 www | 自建 Worker / 反代 |
| **维基百科正文** | 文本节点 208.80.x / 103.102.x / 198.35.26.x / 185.15.56.x 的 TLS 全部失败 | 自建 Worker / 反代 |
| Google / YouTube / Twitch(部分) / Telegram / BBC / archive.org | TCP 层就封死 | 只能中转 |

> 注：`twitch.tv` 在 A/B 测试里表现不一致（B 类 + Host 路由 301 可用），实际可用性取决于当期线路。

---

## 12. 定稿结论（2026-11，覆盖前文所有阶段结论）

### 12.1 各站点最终状态

| 站点 | 手段 | 状态 |
| --- | --- | --- |
| GitHub / raw.githubusercontent | 远端条目 + 本地补子域 | ✅ 可用 |
| Steam 商店/社区/图片/CDN | 远端条目 + 镜像 | ✅ 可用 |
| **Pixiv 图片** | 中性 SNI + DoH 解析 `i.pximg.net` | ✅ 可用（实测 200） |
| **维基百科正文** | 指定节点 `185.15.58.224` + 中性 SNI | ✅ 可用（实测 200，3/4 成功率） |
| **维基百科图片** | 指定节点 `208.80.154.240` | ✅ 可用 |
| DuckDuckGo / Proton / Instagram / Facebook / Twitch / Docker / Dropbox / OneDrive / Amazon JP / CNN / Bloomberg / RFI | 中性 SNI + Host 路由（不写死 IP） | ✅ 可用 |
| **Pixiv 主站** | 必须自建 Worker / 反代 | ❌ 纯客户端无解 |
| Google / YouTube / Telegram / BBC / archive.org | IP 层全封 | ❌ 只能中转 |

### 12.2 为什么 Pixiv 主站无解（三条路都堵死）

1. 源站 `210.140.139.129–162` 全部对 `Host: www.pixiv.net` 返回 **403/421**（只服务 pximg 等 vhost）；
2. Cloudflare 前置必须收到 `*.pixiv.net` 的 SNI，而含 `pixiv` 的 SNI **一律被 RST**（连不存在的 `foo.pixiv.net` 也是）；
3. 不发 SNI / 换中性 SNI → CF 直接 `handshake_failure` 或 `403`；无 ECH。

### 12.3 为什么维基正文能用（关键差异）

- 维基不按 SNI 路由，而是按 **HTTP Host** 路由：中性 SNI + `Host: zh.wikipedia.org` 即可拿到完整页面；
- 前提是**节点可达**：`185.15.58.224` 稳定、`195.200.68.224` 备用，其余文本节点当前不可达；
- 这就是「指定节点」而不是「让 DoH 去解析」的原因。

### 12.4 关于 TLS 分片（已证伪）

受控实验（同一 ClientHello）：整包 / TCP 切 1 字节 / SNI 中点切 / 拆两个 TLS record + 120ms，
对 pixiv、维基真 SNI **全部 ECONNRESET**；而 `example.com` 对照能拿到 ServerHello → 说明本线路 DPI 会重组 TCP。
社区侧证：TlsFragment 已归档、zapret 作者声明「可能随时失效」、byedpi/zapret 无中国成功证据。
→ **不要在客户端里实现分片**。

### 12.5 维护须知

- 硬编码的节点 IP（维基、以及曾经的 pixiv 图片）会随 CDN 调度变化：失效时更新常量即可；
- 其余站点用「空 `ForwardDomainNames` + 中性 SNI」，由 DoH 解析真实 IP，**不受换 IP 影响**；
- 官方远端列表仍然优先；本地覆盖层只补缺，不覆盖远端条目；
- 勾选状态按 Guid 持久化，**改 Guid 等于让老用户重新勾选**。

---

## 13. 用「一个反代域名」承载所有需要中转的站点

### 13.1 结论

**可以，但出口不能是 Cloudflare**。

原因就在数据流上：本项目的反代转发时**只保留原始 `Host`**（`ReverseProxyHttpClientHandler` 里 `request.Headers.Host = uri.Authority`），
它没有额外的字段去告诉远端「我真正想访问谁」。所以远端必须**接受任意 Host**：

| 出口实现 | 能否一个域名搞定所有站点 | 说明 |
| --- | --- | --- |
| VPS + nginx `server_name _;` | ✅ 可以 | 按 `$http_host` 转发，天然支持任意目标 |
| Caddy / 自写 Node·Go 反代 | ✅ 可以 | 同上，只要不按 Host 做站点选择 |
| **Cloudflare Worker / Vercel / Deno** | ❌ 不行 | 它们**按 Hostname 路由**；收到 `Host: www.pixiv.net` 但边缘域名是你的 → 直接 403（实测） |
| Cloudflare 通配子域 `*.p.example.com` | ⚠️ 可以但需要子域 | 一个 DNS 通配 + 一个 Worker 路由即可，但每个站点仍要一个子域 |

### 13.2 nginx 配置（复制即用）

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

server {
    listen 443 ssl default_server;   # default_server 很关键：接受任意 Host
    server_name _;

    # 自签证书就行：客户端侧已开启 IgnoreSSLCertVerification
    ssl_certificate     /etc/nginx/certs/proxy.crt;
    ssl_certificate_key /etc/nginx/certs/proxy.key;

    resolver 1.1.1.1 8.8.8.8 valid=300s;
    resolver_timeout 5s;

    location / {
        proxy_pass https://$http_host$request_uri;  # 目标 = 原始 Host
        proxy_ssl_server_name on;                  # 回源使用真实 SNI
        proxy_set_header Host $http_host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_buffering off;
        proxy_read_timeout 300s;
    }
}
```

### 13.3 客户端怎么配（只写一个域名）

在 `ProxyService.LocalAccelerate.cs` 里改一行：

```csharp
const string SelfHostedReverseProxyHost = "proxy.example.com";
```

之后：
- 「Pixiv 主站」条目会自动从占位状态切换成走这个反代；
- 其它需要中转的站点，加一条 `Item("名字", "稳定Guid", "域名;子域...", SelfHostedReverseProxyHost, SelfHostedReverseProxyHost, true)` 即可，**全部复用同一个域名**；
- 新增站点仍然需要各自一条条目（因为 hosts 要逐个拦截域名），但你只需要维护**一个**反代地址，不再需要为每个站点开子域。

### 13.4 注意

1. **别做成开放代理**：这个域名能被任何人用来中转 → 建议限制来源 IP、加 Basic Auth，或用随机子域；
2. **证书**：自签即可（客户端忽略校验），但如果你想让浏览器直连它也正常，就用真证书；
3. **跳转**：很多站点 `301` 到 `www.` 或别的子域，务必把这些子域也写进条目的域名列表，否则跳转后就没被拦截；
4. **合规**：自建境外反代属于个人用途，请自行确认所在地法规与目标站点条款。

---

## 14. 单域名 + 路径前缀中转（`w.example.com/pixiv/...`）

这是**只需要一个中转域名**的方案：所有站点都发到同一个域名，靠 **URL 路径前缀**区分目标。

```text
浏览器请求            https://www.pixiv.net/ranking.php?mode=daily
  ↓ 本地反代拦截(hosts)
转发到中转端          https://w.example.com/pixiv/ranking.php?mode=daily
  ↓ Worker/nginx 剥掉 /pixiv 前缀
真实站点              https://www.pixiv.net/ranking.php?mode=daily
```

### 14.1 为什么必须用路径、不能只靠 Host

本项目转发时保留原始 `Host`，但 Cloudflare 边缘是**按 Host 分流**的：
`Host: www.pixiv.net` 会被 CF 判给 pixiv 的 zone，请求根本进不到你的 Worker（实测同组合直接 403）。
所以要让 CF 认账，目标信息就必须放进**它不参与路由的部分**——也就是 **URL 路径**。

### 14.2 客户端（本项目）需要做什么：已实现 URI 改写

`ReverseProxyHttpClientHandler` 现在支持：当 `ForwardDomainNames` 是**绝对 URL** 时，
自动把请求改写成「中转域名 + 路径前缀」：

```text
原始  https://www.pixiv.net/a/b?x=1
改写  https://w.example.com/pixiv/a/b?x=1      ← Host/SNI 一并变成 w.example.com
```

实现位置：`ReverseProxyHttpClientHandler.SendAsync` 里的 `TryGetPathPrefixBase` / `BuildPathPrefixedUri`；
连接阶段会跳过「按原域名找 IP」的步骤（`RequestContext.IsPathPrefixProxy`）。

配置方式（`ProxyService.LocalAccelerate.cs`）：

```csharp
const string PathPrefixProxyBase = "https://w.example.com";   // ← 只改这一处
```

之后 Pixiv 主站条目会自动变成：

```csharp
Item("Pixiv 主站（路径前缀中转）", "稳定Guid", PixivDomains,
     "https://w.example.com/pixiv", "@domain");
```

其它站点同理，加一句 `PathPrefixItem("名字", "Guid", "域名列表", "slug")` 即可，**共用同一个域名**。

### 14.3 Cloudflare Worker（按路径前缀分流）

```js
// 路径前缀 → 目标站点；一个域名承载全部
const ROUTES = {
  pixiv:      "www.pixiv.net",
  pixivimg:   "i.pximg.net",
  wikipedia:  "zh.wikipedia.org",
};

export default {
  async fetch(request) {
    const url = new URL(request.url);
    const m = url.pathname.match(/^\/([a-z0-9_-]+)(\/.*)?$/i);
    if (!m) return new Response("bad path", { status: 400 });

    const target = ROUTES[m[1].toLowerCase()];
    if (!target) return new Response("route not allowed", { status: 403 });

    const upstream = new URL(request.url);
    upstream.protocol = "https:";
    upstream.hostname = target;
    upstream.pathname = m[2] || "/";          // 剥掉前缀

    const headers = new Headers(request.headers);
    headers.delete("host");
    headers.set("Referer", "https://" + target + "/");  // 有些站点（如 pximg）校验来源

    return fetch(upstream.toString(), {
      method: request.method,
      headers,
      body: request.method === "GET" || request.method === "HEAD" ? undefined : request.body,
      redirect: "manual",   // 跳转原样回给浏览器，再由本地反代拦截
    });
  }
};
```

### 14.4 与子域编码方案的对比

| 方案 | 域名数量 | 证书 | 每个站点新增成本 |
| --- | --- | --- | --- |
| **单域名 + 路径前缀（本节，推荐）** | 1 | 普通证书即可 | Worker 路由表 1 行 + 客户端 1 行 |
| 子域编码 `www-pixiv-net.example.com` | 1 个通配 | 免费证书只覆盖一层通配 | Worker 映射 1 行 + 客户端 1 行 |
| VPS + nginx `server_name _` | 1 | 自签即可 | 客户端 1 行（不用改服务端） |

### 14.5 已知坑

1. **重定向**：upstream 返回 `Location: https://www.pixiv.net/...` 时，浏览器会重新走一次本地反代（该域名已被拦截），所以没问题；
   但如果站点跳到**没被拦截的子域**，就会直接断——把该子域也加进条目的域名列表。
2. **绝对 URL 资源**：HTML/CSS 里的绝对地址指向原站域名，同样会被本地反代接管，无需 Worker 改写。
3. **Cookie Domain**：站点下发 `.pixiv.net` 的 Cookie 时，浏览器按原域名存储（因为用户访问的就是原域名），不受影响。
4. **别做成开放代理**：Worker 里的 `ROUTES` 白名单必须保留。

---

## 15. 批量接入结果（2026-11 全量分类）

### 15.1 分类方法（重要：必须先绕开本机代理）

本机开着 FlClash 的 **TUN 模式**（默认路由 metric 0 指向 `Meta Tunnel`），
此时**任何**直连测试都会成功——包括 Google/Telegram——分类结果全是假阳性。

正确做法：把 socket 绑定到物理网卡的源地址（`localAddress = WLAN 的 IP`），绕开 TUN 再测：

```js
net.connect({ host: ip, port: 443, localAddress: "192.168.0.135" })
```

校验：Google/Telegram 立刻变 `TIMEOUT`、GitHub 官配 IP 仍 `TLS_OK` → 说明绕开成功。

### 15.2 本次结果（459 个域名样本，404 个解析成功）

| 类别 | 数量 | 含义 | 处理 |
| --- | --- | --- | --- |
| A | 154 | 真实 SNI 就能握手 | 直连（`serverName: "@domain"`） |
| B | 100 | 仅 SNI 被封 | 换中性 SNI + 保留 Host |
| C | 59 | TCP 建不起来 | 必须中转 |
| D | 91 | 握手失败 | 必须中转 |

### 15.3 已写入代码的三张表

`ProxyService.LocalAccelerate.cs`：

| 表 | 数量 | 生成方式 |
| --- | --- | --- |
| `RealSniSites` | 21 | `Item(name, NewId(name), domains, serverName: "@domain")` |
| `SpoofedSniSites` | 7 | 同上 + `serverName: NeutralSni` + `ignoreSslCertVerification: true` |
| `RelaySites` | 39 | 配置 `PathPrefixProxyBase` 后生成 `https://<base>/<slug>` 条目 |

代表性结果：

- **A（直连）**：CNN、AP、NPR、GitHub、Docker、npm、PyPI、Cloudflare、Vercel、DigitalOcean、Bitbucket、Box、Spotify、Netflix、Behance…
- **B（换 SNI）**：Bloomberg、The Guardian、Pinterest、Tumblr、Dropbox、Facebook
- **中转**：BBC、纽约时报、金融时报、WSJ、Forbes、经济学人、Reuters、德国之声、VOA、华盛顿邮报、Medium、Substack、GitLab、Stack Overflow、Archive.org、Reddit、Telegram、Discord、TikTok、Vimeo、SoundCloud、Proton、Signal、MEGA、DeviantArt、ArtStation、Twitch、YouTube、Google、X/Twitter、Instagram、WhatsApp、**Pixiv 主站**

### 15.4 使用方式

1. 只想要直连的：勾选「其他网站（本地）」里 A/B 两类条目即可（无需任何服务端）；
2. 想连央视/推/YTB/Pixiv 这些：把 `PathPrefixProxyBase` 填成你的中转域名，重启后 `RelaySites` 会自动变成可勾选条目；
3. 条目 Id 由 `NewId(名称)` 稳定派生，**改名会重置勾选状态**。

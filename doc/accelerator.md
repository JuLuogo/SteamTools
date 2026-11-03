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

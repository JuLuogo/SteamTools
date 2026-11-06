using System.Linq;

// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services;

partial class ProxyService
{
    /// <summary>
    /// 内置（本地）加速项目。
    /// <para>远端 <c>accelerator/projectgroups</c> 是加速列表的唯一权威来源；本类只做两件事：</para>
    /// <list type="number">
    /// <item>补全远端列表遗漏、但同样需要加速的子域名（hosts 模式必须逐个列出字面域名，无法用通配符）；</item>
    /// <item>提供「其他网站」分组：勾选某一个站点即启用该站点的全部已知域名。</item>
    /// </list>
    /// <para>约束（改动时务必遵守）：</para>
    /// <list type="bullet">
    /// <item>组名是 <see cref="ProxyDomains"/> 的唯一主键，必须与远端组名不同，否则会互相覆盖；</item>
    /// <item>Id 必须稳定（写死在代码里），勾选状态靠它持久化；随机 Guid 会导致重启后勾选丢失；</item>
    /// <item>只有叶子节点（组内的子项）会被持久化，所以每个站点都要是「组内的一个 Item」；</item>
    /// <item><c>MatchDomainNames</c> 不能为空，否则 <c>ReverseProxyConfig</c> 会抛异常；</item>
    /// <item>Hosts 模式下 <c>ListenDomainNames</c> 只能写字面域名，不能写 <c>*</c>，所以这里必须逐个枚举子域名。</item>
    /// </list>
    /// <para>关于「其他网站」：本工具的反代是「本地反代 + 直连」，只能解决 DNS 污染/连接质量问题；
    /// 对 IP 层被封锁的站点（如 pixiv、维基百科），需要用户在「设置 → 二级代理」里配置一个可用的上游代理才生效。</para>
    /// </summary>
    static class LocalAccelerateProjects
    {
        const ushort DefaultPort = 443;

        /// <summary>
        /// 创建一个加速项目项（全部走本地直连或指定目标域名）。
        /// </summary>
        /// <param name="name">显示名称。</param>
        /// <param name="id">稳定的 Guid 字符串（勾选状态持久化用）。</param>
        /// <param name="domains">分号分隔的域名列表，同时用作匹配规则与 hosts 条目。</param>
        /// <param name="forwardDomainNames">
        /// 转发目标：可为域名（走镜像）或 <b>IP</b>（直接连该 IP，绕过 DNS 污染，对应官方的 ForwardDomainIP）。
        /// </param>
        /// <param name="serverName">
        /// 伪装的上游 SNI（对应官方的 ServerName）；为空表示不发 SNI。
        /// 仅用于绕过 SNI 关键词阻断或适配镜像证书，对 IP 层封锁无效。
        /// </param>
        /// <param name="ignoreSslCertVerification">伪装 SNI 时通常要同时放宽证书校验。</param>
        static AccelerateProjectDTO Item(
            string name,
            string id,
            string domains,
            string forwardDomainNames = "",
            string? serverName = null,
            bool ignoreSslCertVerification = false)
            => new()
            {
                Name = name,
                Id = new Guid(id),
                Port = DefaultPort,
                ProxyType = ProxyType.Local,
                MatchDomainNames = domains,
                ListenDomainNames = domains,
                ForwardDomainNames = forwardDomainNames,
                FakeServerName = serverName,
                IgnoreSSLCertVerification = ignoreSslCertVerification,
            };

        static AccelerateProjectGroupDTO Group(string name, long order, params AccelerateProjectDTO[] items)
            => new()
            {
                Name = name,
                Order = order,
                Items = items.ToList(),
            };

        /// <summary>
        /// 本地补充的 GitHub 域名（官方列表中缺失的常见子域名，全部直连）。
        /// </summary>
        const string GitHubSupplementDomains =
            "codeload.github.com;" +
            "gist.githubusercontent.com;" +
            "media.githubusercontent.com;" +
            "objects-origin.githubusercontent.com;" +
            "release-assets.githubusercontent.com;" +
            "github-releases.githubusercontent.com;" +
            "github-registry-files.githubusercontent.com;" +
            "avatars4.githubusercontent.com;" +
            "avatars5.githubusercontent.com;" +
            "pkg-containers.githubusercontent.com;" +
            "ghcr.io;" +
            "api.githubcopilot.com;" +
            "copilot-proxy.githubusercontent.com;" +
            "git-lfs.github.com;" +
            "npm.pkg.github.com;" +
            "maven.pkg.github.com;" +
            "nuget.pkg.github.com;" +
            "rubygems.pkg.github.com;" +
            "default.exp-tas.com;" +
            "github-cloud.s3.amazonaws.com;" +
            // GitHub Actions 的运行时代理与制品缓存（官方 allowlist 里是 *.actions.githubusercontent.com，hosts 模式无法写通配）
            "pipelines.actions.githubusercontent.com;" +
            "vstoken.actions.githubusercontent.com;" +
            "results-receiver.actions.githubusercontent.com;" +
            "artifactcache.actions.githubusercontent.com;" +
            "blob.actions.githubusercontent.com;" +
            "objects.actions.githubusercontent.com";

        /// <summary>
        /// 本地补充的 Steam 图片类 CDN（沿用官方列表里已有的图片镜像域名）。
        /// </summary>
        const string SteamImageSupplementDomains =
            "shared.akamai.steamstatic.com;" +
            "clan.akamai.steamstatic.com;" +
            "cdn.cloudflare.steamstatic.com;" +
            "community.cloudflare.steamstatic.com;" +
            "store.cloudflare.steamstatic.com;" +
            "steamcommunity-a.akamaihd.net;" +
            "steamusercontent.com";

        /// <summary>
        /// 本地补充的 Steam 静态资源 CDN（沿用官方列表里已有的静态镜像域名）。
        /// </summary>
        const string SteamStaticSupplementDomains =
            "cdn.steamstatic.com;" +
            "steamstatic.com";

        /// <summary>
        /// 本地补充的 Steam 下载/CDN 根域（沿用官方列表里已有的下载镜像域名）。
        /// </summary>
        const string SteamDownloadSupplementDomains =
            "steamcontent.com;" +
            "steam-chat.com;" +
            "steamserver.net";

        /// <summary>
        /// 实测「换中性 SNI + 保留原 Host」可直接拿到内容的站点（2026-11 批量巡检）。
        /// <para>⚠️ 这类条目【不写死 IP】：留空 ForwardDomainNames，让反代用 DoH 解析原域名，
        /// 再用 FakeServerName 发中性 SNI；站点按 Host 路由，因此能拿到正确内容，且不受 CDN 换 IP 影响。</para>
        /// </summary>
        const string NeutralSni = "example.com";

        /// <summary>Pixiv 图片源站（实测 200 OK，无需 Worker）。</summary>
        const string PixivImageDomains = "i.pximg.net;s.pximg.net;i-cf.pximg.net";

        const string DuckDuckGoDomains = "duckduckgo.com;www.duckduckgo.com;duck.com";
        const string ProtonDomains = "proton.me;www.proton.me;account.proton.me";
        const string InstagramDomains = "instagram.com;www.instagram.com;i.instagram.com;cdninstagram.com;fbcdn.net";
        const string FacebookDomains = "facebook.com;www.facebook.com;m.facebook.com;web.facebook.com;fb.com";
        const string TwitchDomains = "twitch.tv;www.twitch.tv;m.twitch.tv";
        const string DockerDomains = "docker.com;www.docker.com;docs.docker.com;hub.docker.com;registry-1.docker.io";
        const string DropboxDomains = "dropbox.com;www.dropbox.com;dl.dropboxusercontent.com";
        const string OneDriveDomains = "onedrive.live.com;1drv.ms;skyapi.onedrive.live.com";
        const string AmazonJpDomains = "amazon.co.jp;www.amazon.co.jp";
        const string CnnDomains = "cnn.com;www.cnn.com;edition.cnn.com;cnnindonesia.com";
        const string BloombergDomains = "bloomberg.com;www.bloomberg.com";
        const string RfiDomains = "rfi.fr;www.rfi.fr;cdn.rfi.fr";

        /// <summary>
        /// Pixiv 主站域名。⚠️ 主站挂在 Cloudflare 上，按 SNI 路由，
        /// 换 SNI 只会拿到 403，因此必须用一个自己的 Worker / 反代域名来承接（见 doc/accelerator.md §9.6）。
        /// </summary>
        const string PixivDomains =
            "pixiv.net;www.pixiv.net;accounts.pixiv.net;source.pixiv.net;sketch.pixiv.net;" +
            "dic.pixiv.net;comic.pixiv.net;novel.pixiv.net;fanbox.pixiv.net;payment.pixiv.net;" +
            "oauth.secure.pixiv.net";

        /// <summary>
        /// 维基百科正文。⚠️ 维基的文本节点默认不可达，必须指定实测可用的落地节点 + 中性 SNI。
        /// <para>实测：185.15.58.224 稳定可用（3/4 成功）、195.200.68.224 备用（1/4）；
        /// 185.15.59.224 与 103.102.166.x / 208.80.153-154.x 当前不通。</para>
        /// <para>图片走 <c>upload.wikimedia.org</c> 单独一条（节点不同，该节点对它返回 421）。</para>
        /// </summary>
        const string WikipediaIp = "185.15.58.224";

        const string WikipediaDomains =
            "wikipedia.org;www.wikipedia.org;zh.wikipedia.org;en.wikipedia.org;ja.wikipedia.org;" +
            "zh.m.wikipedia.org;en.m.wikipedia.org;" +
            "wikimedia.org;commons.wikimedia.org;meta.wikimedia.org;" +
            "api.wikimedia.org;login.wikimedia.org;donate.wikimedia.org;species.wikimedia.org";

        /// <summary>维基共享资源图片节点（实测对任意 SNI 都返回真实内容）。</summary>
        const string WikipediaImageIp = "208.80.154.240";
        const string WikipediaImageDomains = "upload.wikimedia.org;maps.wikimedia.org";

        /// <summary>
        /// 单域名 + 路径前缀的「中转基地址」，例如 <c>https://w.peroe.com</c>。
        /// <para>配好后：访问 <c>https://www.pixiv.net/a</c> 会被改写成 <c>https://w.peroe.com/pixiv/a</c>，
        /// 由中转端（Cloudflare Worker / nginx / 自写服务）按【路径前缀】分流到真实站点。</para>
        /// <para>好处：只需要一个域名，不需要为每个站点开子域；Cloudflare Worker 也能用（它按路径匹配，不看 Host）。</para>
        /// <para>要求：中转端从 <c>/&lt;slug&gt;/...</c> 里剥掉前缀再转发，并把响应原样返回。</para>
        /// <para>留空表示不启用，Pixiv 主站条目保持「需中转」的占位状态。</para>
        /// </summary>
        const string PathPrefixProxyBase = "";

        /// <summary>创建一个走「路径前缀中转」的条目；未配置基地址时返回 null。</summary>
        static AccelerateProjectDTO? PathPrefixItem(string name, string id, string domains, string slug)
        {
            if (string.IsNullOrEmpty(PathPrefixProxyBase))
                return null;
            var forward = PathPrefixProxyBase.TrimEnd('/') + "/" + slug;
            // serverName 用 @domain：改写后的 uri.Host 就是中转域名，SNI 自然等于它
            return Item(name, id, domains, forward, "@domain");
        }

        /// <summary>
        /// 需要合并进 <see cref="ProxyDomains"/> 的本地加速项目组（顺序即 UI 中的排序）。
        /// </summary>
        public static IReadOnlyList<AccelerateProjectGroupDTO> Groups { get; } = CreateGroups();

        static IReadOnlyList<AccelerateProjectGroupDTO> CreateGroups()
            => new AccelerateProjectGroupDTO[]
            {
                Group("补充加速（本地）", 9001,
                    Item("GitHub 补充子域名（直连）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9001", GitHubSupplementDomains),
                    Item("Steam 图片补充（走镜像）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9002", SteamImageSupplementDomains, "steamimage.rmbgame.net"),
                    Item("Steam 静态补充（走镜像）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9003", SteamStaticSupplementDomains, "steamstatic.rmbgame.net"),
                    Item("Steam 下载补充（走镜像）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9004", SteamDownloadSupplementDomains, "steammedia.rmbgame.net")),
                CreateOtherSitesGroup(),
            };

        /// <summary>
        /// 「其他网站（本地）」分组。
        /// <para>列在这里的条目：要么被实测证明可「换中性 SNI」直连，要么依赖可用的落地节点；
        /// Pixiv 主站是唯一必须走中转的（见 <see cref="PathPrefixProxyBase"/>）。</para>
        /// </summary>
        static AccelerateProjectGroupDTO CreateOtherSitesGroup()
        {
            var items = new List<AccelerateProjectDTO>
            {
                Item("Pixiv 图片（直连源站）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9101", PixivImageDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("DuckDuckGo", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9104", DuckDuckGoDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Proton", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9105", ProtonDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Instagram", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9106", InstagramDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Facebook", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9107", FacebookDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Twitch", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9108", TwitchDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Docker", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9109", DockerDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Dropbox", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910a", DropboxDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("OneDrive", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910b", OneDriveDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Amazon 日本", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910c", AmazonJpDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("CNN", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910d", CnnDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Bloomberg", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910e", BloombergDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("RFI 法国国际广播", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910f", RfiDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("维基百科正文（指定节点）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9103", WikipediaDomains, WikipediaIp, NeutralSni, true),
                Item("维基百科图片（指定节点）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9110", WikipediaImageDomains, WikipediaImageIp, NeutralSni, true),
            };

            // Pixiv 主站只能走中转：配了 PathPrefixProxyBase 就用「单域名 + 路径前缀」，否则留占位条目。
            items.Add(PathPrefixItem("Pixiv 主站（路径前缀中转）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9102", PixivDomains, "pixiv")
                ?? Item("Pixiv 主站（需自建中转：配 PathPrefixProxyBase）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9102", PixivDomains, ignoreSslCertVerification: true));

            return Group("其他网站（本地）", 9002, items.ToArray());
        }
    }
}

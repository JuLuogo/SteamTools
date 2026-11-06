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

        /// <summary>中性 SNI：用于绕过「SNI 关键词阻断」，站点按 Host 路由时不影响内容。</summary>
        const string NeutralSni = "example.com";

        // —— 必须写死落地节点的条目（DoH 解析到的节点不可达） ——

        /// <summary>Pixiv 图片源站（实测 200 OK）。</summary>
        const string PixivImageDomains = "i.pximg.net;s.pximg.net;i-cf.pximg.net";

        /// <summary>维基百科正文节点：185.15.58.224 稳定、195.200.68.224 备用。</summary>
        const string WikipediaIp = "185.15.58.224";
        const string WikipediaDomains =
            "wikipedia.org;www.wikipedia.org;zh.wikipedia.org;en.wikipedia.org;ja.wikipedia.org;" +
            "zh.m.wikipedia.org;en.m.wikipedia.org;" +
            "wikimedia.org;commons.wikimedia.org;meta.wikimedia.org;" +
            "api.wikimedia.org;login.wikimedia.org;donate.wikimedia.org;species.wikimedia.org";

        /// <summary>维基共享资源图片节点。</summary>
        const string WikipediaImageIp = "208.80.154.240";
        const string WikipediaImageDomains = "upload.wikimedia.org;maps.wikimedia.org";

        const string DuckDuckGoDomains = "duckduckgo.com;www.duckduckgo.com;duck.com";
        const string OneDriveDomains = "onedrive.live.com;1drv.ms;skyapi.onedrive.live.com";
        const string AmazonJpDomains = "amazon.co.jp;www.amazon.co.jp";

        // —— 分类结果（2026-11 实测，已绕过本机 TUN/proxy 的直连路径） ——

        /// <summary>直连可用且【不需要伪装 SNI】的站点（A 类：用真实 SNI 就能握手）。</summary>
        static readonly (string Name, string Domains)[] RealSniSites =
        {
            ("CNN", "cnn.com;www.cnn.com;edition.cnn.com"),
            ("AP News", "apnews.com"),
            ("NPR", "npr.org"),
            ("Ameba 博客", "ameblo.jp"),
            ("Naver", "naver.com"),
            ("GitHub", "github.com"),
            ("Docker", "docker.com"),
            ("npm", "npmjs.com"),
            ("PyPI", "pypi.org"),
            ("Cloudflare", "cloudflare.com"),
            ("Vercel", "vercel.com"),
            ("DigitalOcean", "digitalocean.com"),
            ("Bitbucket", "bitbucket.org"),
            ("SourceForge", "sourceforge.net"),
            ("Box", "box.com"),
            ("Spotify", "spotify.com"),
            ("LastPass", "lastpass.com"),
            ("1Password", "1password.com"),
            ("Pixiv 图片域名", "pximg.net"),
            ("Behance", "behance.net"),
            ("Netflix", "netflix.com"),
        };

        /// <summary>直连可用但【SNI 被关键词阻断】的站点（B 类：换中性 SNI + 保留原 Host 即可）。</summary>
        static readonly (string Name, string Domains)[] SpoofedSniSites =
        {
            ("Bloomberg", "bloomberg.com;www.bloomberg.com"),
            ("The Guardian", "theguardian.com;www.theguardian.com"),
            ("Pinterest", "pinterest.com;www.pinterest.com;i.pinimg.com"),
            ("Tumblr", "tumblr.com;www.tumblr.com"),
            ("Dropbox", "dropbox.com;www.dropbox.com;dl.dropboxusercontent.com"),
            ("Facebook", "facebook.com;www.facebook.com;m.facebook.com;fb.com"),
        };

        /// <summary>必须走中转的站点（C/D 类，以及被反代按 SNI 路由拒绝的 B 类）。slug 即路径前缀。</summary>
        static readonly (string Slug, string Name, string Domains)[] RelaySites =
        {
            ("bbc-com", "BBC", "bbc.com;www.bbc.com;bbc.co.uk;www.bbc.co.uk"),
            ("nytimes-com", "纽约时报", "nytimes.com;www.nytimes.com"),
            ("ft-com", "金融时报", "ft.com;www.ft.com"),
            ("wsj-com", "华尔街日报", "wsj.com;www.wsj.com"),
            ("forbes-com", "Forbes", "forbes.com;www.forbes.com"),
            ("economist-com", "经济学人", "economist.com;www.economist.com"),
            ("reuters-com", "Reuters", "reuters.com;www.reuters.com"),
            ("dw-com", "德国之声", "dw.com;www.dw.com"),
            ("rfi-fr", "法国国际广播", "rfi.fr;www.rfi.fr"),
            ("voanews-com", "美国之音", "voanews.com;www.voanews.com"),
            ("washingtonpost-com", "华盛顿邮报", "washingtonpost.com;www.washingtonpost.com"),
            ("medium-com", "Medium", "medium.com;www.medium.com;miro.medium.com"),
            ("substack-com", "Substack", "substack.com;www.substack.com"),
            ("singtao-com", "星岛", "singtao.com;www.singtao.com"),
            ("udn-com-tw", "联合报", "udn.com.tw;www.udn.com.tw"),
            ("ok-ru", "OK.ru", "ok.ru;www.ok.ru"),
            ("gitlab-com", "GitLab", "gitlab.com;www.gitlab.com"),
            ("stackoverflow-com", "Stack Overflow", "stackoverflow.com;www.stackoverflow.com"),
            ("archive-org", "Internet Archive", "archive.org;www.archive.org;web.archive.org"),
            ("reddit-com", "Reddit", "reddit.com;www.reddit.com;old.reddit.com"),
            ("telegram-org", "Telegram", "telegram.org;web.telegram.org;t.me;telegram.me"),
            ("discord-com", "Discord", "discord.com;www.discord.com;cdn.discordapp.com;media.discordapp.net"),
            ("tiktok-com", "TikTok", "tiktok.com;www.tiktok.com"),
            ("vimeo-com", "Vimeo", "vimeo.com;www.vimeo.com"),
            ("soundcloud-com", "SoundCloud", "soundcloud.com;www.soundcloud.com"),
            ("proton-me", "Proton", "proton.me;www.proton.me;mail.proton.me;account.proton.me"),
            ("signal-org", "Signal", "signal.org;www.signal.org"),
            ("mega-nz", "MEGA", "mega.nz;www.mega.nz"),
            ("deviantart-com", "DeviantArt", "deviantart.com;www.deviantart.com"),
            ("artstation-com", "ArtStation", "artstation.com;www.artstation.com"),
            ("twitch-tv", "Twitch", "twitch.tv;www.twitch.tv"),
            ("youtube-com", "YouTube", "youtube.com;www.youtube.com;m.youtube.com;i.ytimg.com;yt3.ggpht.com"),
            ("google-com", "Google", "google.com;www.google.com;accounts.google.com;apis.google.com;gstatic.com;www.gstatic.com"),
            ("x-com", "X (Twitter)", "x.com;www.x.com;pbs.twimg.com;abs.twimg.com"),
            ("twitter-com", "Twitter", "twitter.com;www.twitter.com"),
            ("instagram-com", "Instagram", "instagram.com;www.instagram.com;i.instagram.com;cdninstagram.com"),
            ("whatsapp-com", "WhatsApp", "whatsapp.com;www.whatsapp.com;web.whatsapp.com"),
            ("pixiv", "Pixiv 主站", "pixiv.net;www.pixiv.net;accounts.pixiv.net;source.pixiv.net;sketch.pixiv.net;dic.pixiv.net;comic.pixiv.net;novel.pixiv.net;fanbox.pixiv.net;oauth.secure.pixiv.net"),
        };

        /// <summary>由名称派生稳定 Guid（勾选状态持久化依赖它，改名等于重置勾选）。</summary>
        static string NewId(string name)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes("BD.WTTS.Accelerator." + name));
            return new Guid(bytes).ToString();
        }

        /// <summary>
        /// 单域名 + 路径前缀的「中转基地址」，例如 <c>https://w.peroe.com</c>。
        /// <para>配好后：访问 <c>https://www.pixiv.net/a</c> 会被改写成 <c>https://w.peroe.com/pixiv/a</c>，
        /// 由中转端按【路径前缀】分流到真实站点。</para>
        /// <para>留空时，<see cref="RelaySites"/> 里的站点不会出现在列表中（因为它们没有可用的直连路径）。</para>
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

        /// <summary>需要合并进 <see cref="ProxyDomains"/> 的本地加速项目组（顺序即 UI 中的排序）。</summary>
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
        /// <list type="number">
        /// <item>写死节点的条目：Pixiv 图片、维基正文/图片（DoH 解析不到可达节点）；</item>
        /// <item><see cref="RealSniSites"/>：真实 SNI 即可直连（A 类）；</item>
        /// <item><see cref="SpoofedSniSites"/>：仅在 SNI 上被封，换中性 SNI + 保留 Host 即可（B 类）；</item>
        /// <item><see cref="RelaySites"/>：必须中转（C/D 类与按 SNI 路由拒绝的 B 类），需配置 <see cref="PathPrefixProxyBase"/>。</item>
        /// </list>
        /// </summary>
        static AccelerateProjectGroupDTO CreateOtherSitesGroup()
        {
            var items = new List<AccelerateProjectDTO>
            {
                Item("Pixiv 图片（直连源站）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9101", PixivImageDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("DuckDuckGo", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9104", DuckDuckGoDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("OneDrive", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910b", OneDriveDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("Amazon 日本", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a910c", AmazonJpDomains, serverName: NeutralSni, ignoreSslCertVerification: true),
                Item("维基百科正文（指定节点）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9103", WikipediaDomains, WikipediaIp, NeutralSni, true),
                Item("维基百科图片（指定节点）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9110", WikipediaImageDomains, WikipediaImageIp, NeutralSni, true),
            };

            foreach (var (name, domains) in RealSniSites)
                items.Add(Item(name, NewId(name), domains, serverName: "@domain"));

            foreach (var (name, domains) in SpoofedSniSites)
                items.Add(Item(name, NewId(name), domains, serverName: NeutralSni, ignoreSslCertVerification: true));

            foreach (var (slug, name, domains) in RelaySites)
            {
                var relayItem = PathPrefixItem(name, NewId(name), domains, slug);
                if (relayItem != null)
                    items.Add(relayItem);
            }

            return Group("其他网站（本地）", 9002, items.ToArray());
        }
    }
}

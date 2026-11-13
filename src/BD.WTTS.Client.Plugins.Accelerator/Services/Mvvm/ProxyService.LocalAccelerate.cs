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
            ("CNN", "cnn.com;www.cnn.com;edition.cnn.com;cnnindonesia.com;media.cnn.com;cdn.cnn.com;i2.cdn.turner.com;lite.cnn.com"),
            ("AP News", "apnews.com;www.apnews.com;assets.apnews.com"),
            ("NPR", "npr.org;www.npr.org;media.npr.org;static.npr.org;assets.npr.org"),
            ("Ameba 博客", "ameblo.jp;www.ameblo.jp;assets.ameba.jp;stat.ameba.jp;ameba.jp"),
            ("Naver", "naver.com;www.naver.com;nid.naver.com;ssl.pstatic.net;s.pstatic.net;mail.naver.com"),
            ("Docker", "docker.com;www.docker.com;docs.docker.com;hub.docker.com;registry-1.docker.io;production.cloudflare.docker.com;desktop.docker.com"),
            ("npm", "npmjs.com;www.npmjs.com;registry.npmjs.org;registry.npmjs.com;static.npmjs.com"),
            ("PyPI", "pypi.org;www.pypi.org;files.pythonhosted.org;pypi.python.org"),
            ("Cloudflare", "cloudflare.com;www.cloudflare.com;developers.cloudflare.com;dash.cloudflare.com;static.cloudflareinsights.com"),
            ("Vercel", "vercel.com;www.vercel.com;assets.vercel.com"),
            ("DigitalOcean", "digitalocean.com;www.digitalocean.com;cloud.digitalocean.com;assets.digitalocean.com"),
            ("Bitbucket", "bitbucket.org;www.bitbucket.org;api.bitbucket.org;bytebucket.org;cdn.bitbucket.org"),
            ("SourceForge", "sourceforge.net;www.sourceforge.net;downloads.sourceforge.net;master.dl.sourceforge.net"),
            ("Box", "box.com;www.box.com;account.box.com;api.box.com;cdn01.boxcdn.net"),
            ("Spotify", "spotify.com;www.spotify.com;open.spotify.com;api.spotify.com;i.scdn.co;cdn.spotify.com"),
            ("LastPass", "lastpass.com;www.lastpass.com;assets.lastpass.com"),
            ("1Password", "1password.com;www.1password.com;start.1password.com;my.1password.com"),
            ("Pixiv 图片域名", "pximg.net;www.pximg.net"),
            ("Behance", "behance.net;www.behance.net;assets.behance.net;mir-s3-cdn-cf.behance.net"),
            ("Netflix", "netflix.com;www.netflix.com;assets.nflxext.com;codex.nflxext.com"),
        };

        /// <summary>直连可用但【SNI 被关键词阻断】的站点（B 类：换中性 SNI + 保留原 Host 即可）。</summary>
        static readonly (string Name, string Domains)[] SpoofedSniSites =
        {
            ("Bloomberg", "bloomberg.com;www.bloomberg.com;assets.bwbx.io;cdn1.bbend.net"),
            ("The Guardian", "theguardian.com;www.theguardian.com;i.guim.co.uk;assets.guim.co.uk;static.guim.co.uk"),
            ("Pinterest", "pinterest.com;www.pinterest.com;i.pinimg.com;assets.pinterest.com;widgets.pinterest.com"),
            ("Tumblr", "tumblr.com;www.tumblr.com;assets.tumblr.com;64.media.tumblr.com"),
            ("Dropbox", "dropbox.com;www.dropbox.com;dl.dropboxusercontent.com;content.dropboxapi.com;api.dropboxapi.com"),
            ("Facebook", "facebook.com;www.facebook.com;m.facebook.com;web.facebook.com;fb.com;connect.facebook.net;static.xx.fbcdn.net;scontent.xx.fbcdn.net"),
        };

        /// <summary>必须走中转的站点（C/D 类，以及被反代按 SNI 路由拒绝的 B 类）。</summary>
        static readonly (string Name, string Domains)[] RelaySites =
        {
            ("BBC", "bbc.com;www.bbc.com;bbc.co.uk;www.bbc.co.uk;news.bbc.co.uk;ichef.bbci.co.uk;static.bbci.co.uk;feeds.bbci.co.uk;nav.files.bbci.co.uk;m.files.bbci.co.uk;news.files.bbci.co.uk;session.bbc.co.uk;account.bbc.com;bbcmedia.akamai.net"),
            ("纽约时报", "nytimes.com;www.nytimes.com;static01.nyt.com;g1.nyt.com;graphics8.nytimes.com;i.nyt.com;int.nyt.com;myaccount.nytimes.com"),
            ("金融时报", "ft.com;www.ft.com;static.ft.com;next-media.ft.com;cdn.ft.com;on.ft.com;registration.ft.com"),
            ("华尔街日报", "wsj.com;www.wsj.com;static.wsj.net;images.wsj.net;s.wsj.net;art.wsj.net;video-api.wsj.com;partner.wsj.net"),
            ("Forbes", "forbes.com;www.forbes.com;i.forbesimg.com;thumb.forbesimg.com;blogs-images.forbes.com"),
            ("经济学人", "economist.com;www.economist.com;cdn.economist.com;web.economist.com;social.economist.com"),
            ("Reuters", "reuters.com;www.reuters.com;static.reuters.com;graphics.reuters.com;images.reuters.com"),
            ("德国之声", "dw.com;www.dw.com;static.dw.com;p.dw.com"),
            ("法国国际广播", "rfi.fr;www.rfi.fr;cdn.rfi.fr;static.rfi.fr"),
            ("美国之音", "voanews.com;www.voanews.com;static.voanews.com"),
            ("华盛顿邮报", "washingtonpost.com;www.washingtonpost.com;static.washingtonpost.com;arc-anglerfish-washpost-prod-washpost.s3.amazonaws.com"),
            ("Medium", "medium.com;www.medium.com;miro.medium.com;cdn-images-1.medium.com"),
            ("Substack", "substack.com;www.substack.com;substackcdn.com;substack-post-media.s3.amazonaws.com"),
            ("星岛", "singtao.com;www.singtao.com"),
            ("联合报", "udn.com.tw;www.udn.com.tw"),
            ("OK.ru", "ok.ru;www.ok.ru"),
            ("GitLab", "gitlab.com;www.gitlab.com;assets.gitlab-static.net;gitlab-static.net"),
            ("Stack Overflow", "stackoverflow.com;www.stackoverflow.com;i.stack.imgur.com;cdn.sstatic.net;stackexchange.com"),
            ("Internet Archive", "archive.org;www.archive.org;web.archive.org;ia801504.us.archive.org"),
            ("Reddit", "reddit.com;www.reddit.com;old.reddit.com;i.redd.it;preview.redd.it;styles.redditmedia.com;www.redditstatic.com;redditmedia.com;oauth.reddit.com"),
            ("Telegram", "telegram.org;www.telegram.org;web.telegram.org;t.me;telegram.me;td.telegram.org;core.telegram.org;cdn.telegram.org"),
            ("Discord", "discord.com;www.discord.com;discordapp.com;cdn.discordapp.com;media.discordapp.net;gateway.discord.gg;discord.gg"),
            ("TikTok", "tiktok.com;www.tiktok.com;sf16-website-login.neutral.ttwstatic.com;p16-sign.tiktokcdn.com"),
            ("Vimeo", "vimeo.com;www.vimeo.com;f.vimeocdn.com;i.vimeocdn.com;player.vimeo.com"),
            ("SoundCloud", "soundcloud.com;www.soundcloud.com;i1.sndcdn.com;i2.sndcdn.com;cf-media.sndcdn.com"),
            ("Proton", "proton.me;www.proton.me;mail.proton.me;account.proton.me;api.protonmail.ch"),
            ("Signal", "signal.org;www.signal.org"),
            ("MEGA", "mega.nz;www.mega.nz;mega.io;g.api.mega.co.nz"),
            ("DeviantArt", "deviantart.com;www.deviantart.com;images-wixmp-ed30a86b8c4ca887773594c2.wixmp.com"),
            ("ArtStation", "artstation.com;www.artstation.com;cdna.artstation.com;cdnb.artstation.com"),
            ("Twitch", "twitch.tv;www.twitch.tv;m.twitch.tv;static.twitchcdn.net;assets.twitch.tv;clips.twitch.tv"),
            ("YouTube", "youtube.com;www.youtube.com;m.youtube.com;youtubei.googleapis.com;www.youtube-nocookie.com;i.ytimg.com;s.ytimg.com;yt3.ggpht.com;ytimg.com"),
            ("Google", "google.com;www.google.com;accounts.google.com;apis.google.com;gstatic.com;www.gstatic.com;ssl.gstatic.com;fonts.gstatic.com;recaptcha.net;www.recaptcha.net"),
            ("X (Twitter)", "x.com;www.x.com;api.x.com;pbs.twimg.com;abs.twimg.com;video.twimg.com;t.co"),
            ("Twitter", "twitter.com;www.twitter.com;api.twitter.com"),
            ("Instagram", "instagram.com;www.instagram.com;i.instagram.com;cdninstagram.com;fbcdn.net;scontent.cdninstagram.com;graph.instagram.com"),
            ("WhatsApp", "whatsapp.com;www.whatsapp.com;web.whatsapp.com;static.whatsapp.net;mmg.whatsapp.net"),
            ("Pixiv 主站", "pixiv.net;www.pixiv.net;accounts.pixiv.net;source.pixiv.net;sketch.pixiv.net;dic.pixiv.net;comic.pixiv.net;novel.pixiv.net;fanbox.pixiv.net;payment.pixiv.net;oauth.secure.pixiv.net"),
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
        /// <para>配好后：访问 <c>https://www.pixiv.net/a</c> 会被改写成 <c>https://w.peroe.com/www.pixiv.net/a</c>，
        /// 由中转端按【路径前缀】分流到真实站点。</para>
        /// <para>留空时，<see cref="RelaySites"/> 里的站点不会出现在列表中（因为它们没有可用的直连路径）。</para>
        /// </summary>
        const string PathPrefixProxyBase = "";

        /// <summary>
        /// 创建一个走「路径前缀中转」的条目；未配置基地址时返回 null。
        /// <para>转发目标只写基地址（不带路径），反代会自动把原始主机名编进第一段路径，
        /// 因此中转端不需要为每个站点配规则。</para>
        /// </summary>
        static AccelerateProjectDTO? PathPrefixItem(string name, string id, string domains)
        {
            if (string.IsNullOrEmpty(PathPrefixProxyBase))
                return null;
            // serverName 用 @domain：改写后的 uri.Host 就是中转域名，SNI 自然等于它
            return Item(name, id, domains, PathPrefixProxyBase.TrimEnd('/'), "@domain");
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

            foreach (var (name, domains) in RelaySites)
            {
                var relayItem = PathPrefixItem(name, NewId(name), domains);
                if (relayItem != null)
                    items.Add(relayItem);
            }

            return Group("其他网站（本地）", 9002, items.ToArray());
        }
    }
}

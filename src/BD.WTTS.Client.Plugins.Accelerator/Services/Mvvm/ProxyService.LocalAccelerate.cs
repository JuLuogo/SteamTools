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
        /// <param name="forwardDomainNames">转发目标（域名或 IP）；为空表示直连原站，走 DoH 解析。</param>
        static AccelerateProjectDTO Item(string name, string id, string domains, string forwardDomainNames = "")
            => new()
            {
                Name = name,
                Id = new Guid(id),
                Port = DefaultPort,
                ProxyType = ProxyType.Local,
                MatchDomainNames = domains,
                ListenDomainNames = domains,
                ForwardDomainNames = forwardDomainNames,
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
            "rubygems.pkg.github.com";

        /// <summary>
        /// 本地补充的 Steam 图片类 CDN（沿用官方列表里已有的图片镜像域名）。
        /// </summary>
        const string SteamImageSupplementDomains =
            "shared.akamai.steamstatic.com;" +
            "clan.akamai.steamstatic.com;" +
            "cdn.cloudflare.steamstatic.com;" +
            "community.cloudflare.steamstatic.com;" +
            "store.cloudflare.steamstatic.com;" +
            "steamcommunity-a.akamaihd.net";

        /// <summary>
        /// 本地补充的 Steam 静态资源 CDN（沿用官方列表里已有的静态镜像域名）。
        /// </summary>
        const string SteamStaticSupplementDomains =
            "cdn.steamstatic.com;" +
            "steamstatic.com";

        /// <summary>
        /// Pixiv 全部已知域名。⚠️ 中国大陆网络下需要二级代理，否则直连会被 IP 封锁。
        /// </summary>
        const string PixivDomains =
            "pixiv.net;www.pixiv.net;accounts.pixiv.net;source.pixiv.net;sketch.pixiv.net;" +
            "dic.pixiv.net;comic.pixiv.net;novel.pixiv.net;fanbox.pixiv.net;payment.pixiv.net;" +
            "oauth.secure.pixiv.net;i.pximg.net;s.pximg.net;i-cf.pximg.net";

        /// <summary>
        /// 维基百科/维基媒体的常用域名。⚠️ 同 Pixiv，需要二级代理。
        /// <para>说明：hosts 模式无法写通配，因此这里枚举常用语种与共享资源域名；
        /// 其它语种（如 fr/ru/es）可在 System/PAC 模式下由 <c>*.wikipedia.org</c> 规则覆盖。</para>
        /// </summary>
        const string WikipediaDomains =
            "wikipedia.org;www.wikipedia.org;zh.wikipedia.org;en.wikipedia.org;ja.wikipedia.org;" +
            "zh.m.wikipedia.org;en.m.wikipedia.org;" +
            "wikimedia.org;commons.wikimedia.org;upload.wikimedia.org;meta.wikimedia.org;" +
            "api.wikimedia.org;login.wikimedia.org;donate.wikimedia.org";

        /// <summary>
        /// 哔哩哔哩常用域名（对海外访问更有意义；国内用户一般不需要勾选）。
        /// </summary>
        const string BilibiliDomains =
            "bilibili.com;www.bilibili.com;api.bilibili.com;passport.bilibili.com;" +
            "space.bilibili.com;live.bilibili.com;t.bilibili.com;bangumi.bilibili.com;" +
            "data.bilibili.com;message.bilibili.com;" +
            "hdslb.com;" +
            "i0.hdslb.com;i1.hdslb.com;i2.hdslb.com;s1.hdslb.com";

        /// <summary>
        /// 需要合并进 <see cref="ProxyDomains"/> 的本地加速项目组（顺序即 UI 中的排序）。
        /// </summary>
        public static IReadOnlyList<AccelerateProjectGroupDTO> Groups { get; } = new AccelerateProjectGroupDTO[]
        {
            Group("补充加速（本地）", 9001,
                Item("GitHub 补充子域名（直连）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9001", GitHubSupplementDomains),
                Item("Steam 图片补充（走镜像）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9002", SteamImageSupplementDomains, "steamimage.rmbgame.net"),
                Item("Steam 静态补充（走镜像）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9003", SteamStaticSupplementDomains, "steamstatic.rmbgame.net")),
            Group("其他网站（本地）", 9002,
                Item("Pixiv（需二级代理）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9101", PixivDomains),
                Item("维基百科（需二级代理）", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9102", WikipediaDomains),
                Item("哔哩哔哩", "8f3a1c62-1a1e-4c9b-9d1f-2b6f0f3a9103", BilibiliDomains)),
        };
    }
}

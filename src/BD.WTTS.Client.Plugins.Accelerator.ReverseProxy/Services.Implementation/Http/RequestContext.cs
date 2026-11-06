// https://github.com/dotnetcore/FastGithub/blob/2.1.4/FastGithub.Http/RequestContext.cs

// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services.Implementation;

/// <summary>
/// 请求上下文
/// </summary>
sealed class RequestContext
{
    /// <summary>
    /// 获取或设置是否为 HTTPS 请求
    /// </summary>
    public bool IsHttps { get; }

    /// <summary>
    /// 获取或设置 Sni 值
    /// </summary>
    public TlsSniPattern TlsSniValue { get; }

    /// <summary>
    /// 是否为「路径前缀中转」请求。
    /// <para>当 <c>ForwardDomainNames</c> 是绝对 URL（如 <c>https://w.peroe.com/pixiv</c>）时置为 true，
    /// 此时请求 URI 已被改写为中转域名 + 路径前缀，不再使用原域名的转发目标。</para>
    /// </summary>
    public bool IsPathPrefixProxy { get; }

    public RequestContext(bool isHttps, TlsSniPattern tlsSniValue, bool isPathPrefixProxy = false)
    {
        IsHttps = isHttps;
        TlsSniValue = tlsSniValue;
        IsPathPrefixProxy = isPathPrefixProxy;
    }
}
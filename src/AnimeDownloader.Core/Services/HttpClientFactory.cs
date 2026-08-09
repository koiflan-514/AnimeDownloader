using System.Net;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// 创建应用内共享的 <see cref="HttpClient"/>。
/// 统一 User-Agent、超时，并应用系统代理或用户手动代理。
/// </summary>
public sealed class HttpClientFactory
{
    private const string UserAgent = "AnimeDownloader/0.2";

    private readonly AppSettings _settings;
    private readonly HttpMessageHandler? _handlerOverride;

    public HttpClientFactory(AppSettings settings, HttpMessageHandler? handlerOverride = null)
    {
        _settings = settings;
        _handlerOverride = handlerOverride;
    }

    /// <summary>创建一个新的 HttpClient（图源间共享即可，不必每次新建）。</summary>
    public HttpClient CreateClient()
    {
        var handler = _handlerOverride ?? BuildHandler();
        var timeoutSeconds = Math.Clamp(_settings.RequestTimeoutSeconds, 5, 120);
        var client = new HttpClient(handler, disposeHandler: _handlerOverride is null)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, image/avif, image/webp, image/*;q=0.9, */*;q=0.8");
        return client;
    }

    private HttpClientHandler BuildHandler()
    {
        var handler = new HttpClientHandler();
        if (_settings.UseNoProxy)
        {
            handler.UseProxy = false;
            return handler;
        }

        if (!string.IsNullOrWhiteSpace(_settings.ProxyUrl))
        {
            handler.Proxy = new WebProxy(_settings.ProxyUrl);
            handler.UseProxy = true;
            return handler;
        }

        var detected = ProxyDetector.Detect();
        if (detected.Count > 0)
        {
            handler.Proxy = new WebProxyFromDict(detected);
            handler.UseProxy = true;
        }

        return handler;
    }

    /// <summary>将 "http=x;https=y;no_proxy=z" 字典包装为 WebProxy 的最小实现。</summary>
    private sealed class WebProxyFromDict : IWebProxy
    {
        private readonly IReadOnlyDictionary<string, string> _proxies;

        public WebProxyFromDict(IReadOnlyDictionary<string, string> proxies)
        {
            _proxies = proxies;
        }

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            var scheme = destination.Scheme.ToLowerInvariant();
            if (_proxies.TryGetValue(scheme, out var proxy) && proxy.Length > 0)
            {
                return new Uri(proxy);
            }

            return null;
        }

        public bool IsBypassed(Uri host)
        {
            if (!_proxies.TryGetValue("no_proxy", out var noProxy) || noProxy.Length == 0)
            {
                return false;
            }

            foreach (var entry in noProxy.Split(','))
            {
                var item = entry.Trim();
                if (item.Length == 0)
                {
                    continue;
                }

                if (host.Host.Equals(item, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (item.StartsWith('.') && host.Host.EndsWith(item, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

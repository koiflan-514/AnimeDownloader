using System.Net;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// 创建应用内共享的 <see cref="HttpClient"/>。
/// 统一 User-Agent、超时，并应用系统代理或用户手动代理。
/// 代理隧道基于 <see cref="ProxyTunnelConnector"/>：支持 HTTP(S) CONNECT 与 SOCKS5
/// （.NET 的 WebProxy 不支持 socks5://），自动检测的系统代理会先探测 SOCKS5、
/// 协议不符时回退 HTTP CONNECT，节点传输故障时再回退直连；no_proxy 旁路与
/// 代理认证（user:password@）均受支持。
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

    private HttpMessageHandler BuildHandler()
    {
        var connectTimeout = TimeSpan.FromSeconds(Math.Clamp(_settings.RequestTimeoutSeconds, 5, 120));
        if (_settings.UseNoProxy)
        {
            return new SocketsHttpHandler { ConnectTimeout = connectTimeout };
        }

        var detected = _settings.ProxyUrl is null ? ProxyDetector.Detect() : null;
        var proxyUrl = _settings.ProxyUrl
            ?? detected?.GetValueOrDefault("https")
            ?? detected?.GetValueOrDefault("http")
            ?? detected?.GetValueOrDefault("socks");
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return new SocketsHttpHandler { ConnectTimeout = connectTimeout };
        }

        var proxy = ParseProxyUrl(proxyUrl);
        if (proxy is null)
        {
            return new SocketsHttpHandler { ConnectTimeout = connectTimeout };
        }

        var bypass = ProxyBypassList.Parse(detected?.GetValueOrDefault("no_proxy"));
        // 自动检测的系统代理：协议可探测回退，节点传输故障时可旁路直连。
        // 用户手动指定代理说明有意全量走代理，协议与路径严格遵从。
        var autoDetected = _settings.ProxyUrl is null;
        // 自动模式下给代理尝试的连接超时减半（代理黑洞挂起时仍留一半预算给直连回退）。
        var requestTimeout = Math.Clamp(_settings.RequestTimeoutSeconds, 5, 120);
        var tunnelConnectTimeout = autoDetected
            ? TimeSpan.FromSeconds(Math.Clamp(requestTimeout / 2.0, 5, 15))
            : connectTimeout;
        var tunnel = BuildTunnelHandler(
            proxy, bypass, allowProtocolFallback: autoDetected && !IsSocksScheme(proxy.Scheme), tunnelConnectTimeout);
        if (!autoDetected)
        {
            return tunnel;
        }

        return new DirectFallbackHandler(
            tunnel,
            new SocketsHttpHandler { ConnectTimeout = connectTimeout },
            TimeSpan.FromSeconds(Math.Clamp(requestTimeout / 2.0, 5, 15)));
    }

    /// <summary>
    /// 构建代理隧道 handler。<para />
    /// 关键：ConnectCallback 接管全部连接建立，必须 UseProxy=false，否则 handler 会把
    /// "连接到代理本身"也交给 ConnectCallback（形成通过代理连代理的套娃隧道）。
    /// </summary>
    private static SocketsHttpHandler BuildTunnelHandler(
        Uri proxy,
        ProxyBypassList? bypass,
        bool allowProtocolFallback,
        TimeSpan connectTimeout)
    {
        return new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = connectTimeout,
            ConnectCallback = (context, cancellationToken) => ProxyTunnelConnector.ConnectAsync(
                proxy,
                context.DnsEndPoint,
                bypass,
                allowProtocolFallback,
                cancellationToken),
        };
    }

    internal static bool IsSocksScheme(string scheme) =>
        scheme.Equals("socks", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("socks5h", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析代理地址；缺省 scheme 时补 "http://"（与 WebProxy 的宽容行为一致）。
    /// "socks://" 视为 "socks5://"。
    /// </summary>
    private static Uri? ParseProxyUrl(string value)
    {
        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "http://" + normalized;
        }
        else if (normalized.StartsWith("socks://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "socks5://" + normalized["socks://".Length..];
        }
        else if (normalized.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "socks5://" + normalized["socks5h://".Length..];
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && uri.Host.Length > 0
            && uri.Port > 0
            ? uri
            : null;
    }

    /// <summary>
    /// 自动代理模式下的韧性层：给代理尝试一个独立时间预算（默认为总超时的一半）。
    /// 代理在预算内没拿到响应头（节点掉线 / TLS 挂起 / 数据极慢）就取消并直连重试一次；
    /// 响应头到达后解除预算，保证 ResponseHeadersRead 的流式下载不被误杀。
    /// HTTP 层错误（4xx/5xx）不重试。幂等 GET 才回退。
    /// </summary>
    private sealed class DirectFallbackHandler : DelegatingHandler
    {
        private readonly SocketsHttpHandler _direct;
        private readonly HttpMessageInvoker _directInvoker;
        private readonly TimeSpan _proxyAttemptBudget;

        public DirectFallbackHandler(SocketsHttpHandler tunnel, SocketsHttpHandler direct, TimeSpan proxyAttemptBudget)
            : base(tunnel)
        {
            _direct = direct;
            _directInvoker = new HttpMessageInvoker(direct, disposeHandler: false);
            _proxyAttemptBudget = proxyAttemptBudget;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_proxyAttemptBudget);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (!cancellationToken.IsCancellationRequested &&
                      ex is HttpRequestException or IOException
                          or System.Security.Authentication.AuthenticationException
                          or OperationCanceledException)
            {
                // 代理隧道传输层故障或预算耗尽：直连重试一次。
                return await _directInvoker.SendAsync(CloneRequest(request), cancellationToken)
                    .ConfigureAwait(false);
            }

            // 响应头已到达：解除预算计时（保留流式响应体），并脱离外部令牌的注册。
            attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
            return response;
        }

        /// <summary>克隆无内容的 GET 请求（.NET 不允许重发同一请求实例）。</summary>
        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _directInvoker.Dispose();
                _direct.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

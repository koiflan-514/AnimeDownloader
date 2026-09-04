// 联网冒烟测试：逐个图源做连通性探测 + 真实拉取 + 图片字节下载。
// 三条网络路径：直连、应用同款客户端（HttpClientFactory 自动代理）、可选的手动代理 URL。
// 用法：dotnet run --project tools/SourceSmoke [-- <manual-proxy-url> | dmoe | socks <url>]
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

if (args.Length > 0 && args[0] == "dmoe")
{
    await DmoeProbe.RunAsync();
    return;
}

if (args.Length > 2 && args[0] == "rawtls")
{
    await RawTlsProbe.RunAsync(args[1], int.Parse(args[2]));
    return;
}

if (args.Length > 0 && args[0] == "rawsocks")
{
    await RawSocksProbe.RunAsync();
    return;
}

if (args.Length > 1 && args[0] == "tunnel")
{
    await TunnelProbe.RunAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "sina")
{
    await DmoeProbeExt.RunSinaAsync();
    return;
}

if (args.Length > 1 && args[0] == "socks")
{
    await SocksProbe.RunAsync(args[1]);
    return;
}


using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));

Console.WriteLine("=== AnimeDownloader 图源冒烟测试 ===");
var detected = ProxyDetector.Detect();
Console.WriteLine("系统代理检测结果: " +
    (detected.Count == 0 ? "无" : string.Join(", ", detected.Select(kv => $"{kv.Key}={kv.Value}"))));

var settings = new AppSettings { RequestTimeoutSeconds = 30 };

HttpClient MakeClient(bool useProxy, string? proxyUrl = null)
{
    var handler = new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(12),
    };
    if (useProxy && !string.IsNullOrWhiteSpace(proxyUrl))
    {
        handler.Proxy = new System.Net.WebProxy(proxyUrl);
        handler.UseProxy = true;
    }
    else
    {
        handler.UseProxy = false;
    }

    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeDownloader-Smoke/0.3");
    return client;
}

// 1) 直连
var directHttp = MakeClient(useProxy: false);

// 2) 应用同款客户端（读取同样的设置 + 系统代理自动检测）
var appHttp = new HttpClientFactory(settings).CreateClient();

// 3) 手动代理（参数指定，如 http://127.0.0.1:1089 或 socks5://127.0.0.1:1088）
HttpClient? manualHttp = args.Length > 0 ? MakeClient(useProxy: true, args[0]) : null;

var directSources = SourceRegistry.CreateAll(directHttp);
var appSources = SourceRegistry.CreateAll(appHttp);
var manualSources = manualHttp is null ? null : SourceRegistry.CreateAll(manualHttp);
SourceRegistry.ApplySettings(directSources, settings);
SourceRegistry.ApplySettings(appSources, settings);
if (manualSources is not null)
{
    SourceRegistry.ApplySettings(manualSources, settings);
}

Console.WriteLine();
var results = new List<string>();
foreach (var template in directSources)
{
    var appSource = appSources.First(s => s.Id == template.Id);
    var manualSource = manualSources?.FirstOrDefault(s => s.Id == template.Id);

    var probeDirect = await ProbeAsync(directHttp, template, cts.Token);
    var probeApp = await ProbeAsync(appHttp, appSource, cts.Token);

    // 真实拉取：直连可用走直连，否则走应用客户端（自动代理）
    var (pick, fetchHttp, fetchSource) = probeDirect.ok
        ? ("direct", directHttp, template)
        : ("app", appHttp, appSource);
    var fetch = await FetchAsync(fetchHttp, fetchSource, cts.Token);

    var manualInfo = string.Empty;
    if (manualSource is not null && manualHttp is not null)
    {
        var probeManual = await ProbeAsync(manualHttp, manualSource, cts.Token);
        var manualFetch = await FetchAsync(manualHttp, manualSource, cts.Token);
        manualInfo = $"  手动代理: 探测={Fmt(probeManual)} 拉取={Fmt(manualFetch)}";
    }

    var line =
        $"{template.Id,-10} 直连探测={Fmt(probeDirect)} 应用客户端探测={Fmt(probeApp)} " +
        $"拉取[{pick}]={Fmt(fetch)}{manualInfo}";
    results.Add(line);
    Console.WriteLine(line);
}

Console.WriteLine();
Console.WriteLine("=== 汇总 ===");
foreach (var line in results)
{
    Console.WriteLine(line);
}
return;

static string Fmt((bool ok, string detail) r) => r.ok ? $"✔({r.detail})" : $"✘({r.detail})";

static async Task<(bool ok, string detail)> ProbeAsync(HttpClient http, IImageSource source, CancellationToken ct)
{
    if (source.ProbeUrl is null)
    {
        return (true, "无探测端点");
    }

    try
    {
        using var resp = await http.GetAsync(source.ProbeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        return (true, $"HTTP {(int)resp.StatusCode}");
    }
    catch (Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return (false, msg.Length > 60 ? msg[..60] : msg);
    }
}

static async Task<(bool ok, string detail)> FetchAsync(HttpClient http, IImageSource source, CancellationToken ct)
{
    try
    {
        var items = await source.GetImagesAsync(NsfwMode.BlockNsfw, 3, ct);
        if (items.Count == 0)
        {
            return (false, $"0 张 ({source.LastError ?? "无错误信息"})");
        }

        // 验证第一张图真的能下载（读前几个字节）
        var item = items[0];
        var thumb = ThumbnailResolver.ResolveThumbnailUrl(item.ThumbnailUrl, item.Url, null) ?? item.Url;
        try
        {
            using var resp = await http.GetAsync(thumb, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var buf = new byte[16];
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var read = await stream.ReadAsync(buf, ct);
            return (read > 0, $"{items.Count} 张, 图片字节 {read}B OK");
        }
        catch (Exception ex)
        {
            return (false, $"元数据 {items.Count} 张, 但图片下载失败: {Trunc(ex.InnerException?.Message ?? ex.Message)}");
        }
    }
    catch (Exception ex)
    {
        return (false, $"异常: {Trunc(ex.InnerException?.Message ?? ex.Message)} ({source.LastError})");
    }

    static string Trunc(string s) => s.Length > 70 ? s[..70] : s;
}

/// <summary>dmoe 图床（百度代理链）专项探测。</summary>
internal static class DmoeProbe
{
    public static async Task RunAsync()
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = System.Net.DecompressionMethods.All };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeDownloader/0.3");

        // 先取 imgurl，再下载
        using var meta = await http.GetAsync("https://www.dmoe.cc/random.php?return=json");
        var json = await meta.Content.ReadAsStringAsync();
        Console.WriteLine($"meta status={(int)meta.StatusCode} body={Trunc(json, 200)}");
        var imgurl = System.Text.Json.Nodes.JsonNode.Parse(json)?["imgurl"]?.GetValue<string>();
        Console.WriteLine($"imgurl={imgurl}");
        if (imgurl is null)
        {
            return;
        }

        using var resp = await http.GetAsync(imgurl, HttpCompletionOption.ResponseHeadersRead);
        Console.WriteLine($"img status={(int)resp.StatusCode} type={resp.Content.Headers.ContentType} len={resp.Content.Headers.ContentLength} final={resp.RequestMessage?.RequestUri}");
        var buf = new byte[64];
        await using var s = await resp.Content.ReadAsStreamAsync();
        var read = await s.ReadAsync(buf);
        Console.WriteLine($"firstRead={read}");
    }

    internal static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;
}

/// <summary>验证 .NET 对指定代理 URL 的支持（如 socks5://… 是否原生可用）。</summary>
internal static class SocksProbe
{
    public static async Task RunAsync(string proxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new System.Net.WebProxy(proxyUrl),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var resp = await http.GetAsync("https://www.google.com/generate_204");
            Console.WriteLine($"via {proxyUrl}: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"via {proxyUrl}: 失败 {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}

internal static partial class DmoeProbeExt
{
    public static async Task RunSinaAsync()
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = System.Net.DecompressionMethods.All };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var handler2 = new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = System.Net.DecompressionMethods.All };
        using var httpWithRef = new HttpClient(handler2) { Timeout = TimeSpan.FromSeconds(30) };
        httpWithRef.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/126.0.0.0");
        httpWithRef.DefaultRequestHeaders.Referrer = new Uri("https://www.dmoe.cc/");

        using var meta = await http.GetAsync("https://www.dmoe.cc/random.php?return=json");
        var json = await meta.Content.ReadAsStringAsync();
        var imgurl = System.Text.Json.Nodes.JsonNode.Parse(json)?["imgurl"]?.GetValue<string>();
        Console.WriteLine($"imgurl={imgurl}");

        // 解码 url= 参数
        var idx = imgurl!.IndexOf("url=", StringComparison.Ordinal);
        var decoded = Uri.UnescapeDataString(imgurl[(idx + 4)..]);
        Console.WriteLine($"decoded={decoded}");

        foreach (var (label, client) in new[] { ("无Referer", http), ("带Referer", httpWithRef) })
        {
            try
            {
                using var resp = await client.GetAsync(decoded, HttpCompletionOption.ResponseHeadersRead);
                var buf = new byte[32];
                await using var s = await resp.Content.ReadAsStreamAsync();
                var read = await s.ReadAsync(buf);
                Console.WriteLine($"{label}: status={(int)resp.StatusCode} type={resp.Content.Headers.ContentType} len={resp.Content.Headers.ContentLength} firstRead={read}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: 失败 {ex.InnerException?.Message ?? ex.Message}");
            }
        }
    }
}

/// <summary>分步调探代理隧道。</summary>
internal static class TunnelProbe
{
    public static async Task RunAsync(string target)
    {
        var proxy = new Uri("socks5://127.0.0.1:1088");
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = (ctx, ct) => AnimeDownloader.Core.Services.ProxyTunnelConnector.ConnectAsync(
                proxy, ctx.DnsEndPoint, null, false, ct),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync(target);
            Console.WriteLine($"OK {sw.ElapsedMilliseconds}ms HTTP {(int)resp.StatusCode} via {resp.RequestMessage?.RequestUri}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {sw.ElapsedMilliseconds}ms: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}

/// <summary>原始 SOCKS5 握手分步计时。</summary>
internal static class RawSocksProbe
{
    public static async Task RunAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.NoDelay = true;
        Console.WriteLine($"[0ms] socket created, family={socket.AddressFamily}");
        await socket.ConnectAsync("127.0.0.1", 1088);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] tcp connected");
        await using var stream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        byte[] greeting = { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(greeting);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] greeting sent");
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] greeting reply: {reply[0]:X2} {reply[1]:X2}");
        byte[] req = { 0x05, 0x01, 0x00, 0x03, 0x0E }; // domain
        var host = "www.gstatic.com"u8.ToArray();
        await stream.WriteAsync(req);
        await stream.WriteAsync(host);
        await stream.WriteAsync(new byte[] { 0x01, 0xBB });
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] connect request sent");
        var head = new byte[4];
        await stream.ReadExactlyAsync(head);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] reply head: {head[0]:X2} {head[1]:X2} {head[2]:X2} {head[3]:X2}");
        var trailing = new byte[head[3] == 0x01 ? 6 : head[3] == 0x04 ? 18 : 0];
        if (head[3] == 0x03) { var lb = new byte[1]; await stream.ReadExactlyAsync(lb); trailing = new byte[lb[0] + 2]; }
        await stream.ReadExactlyAsync(trailing);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] tunnel established (trailing {trailing.Length}B)");
        sw.Restart();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = false,
            ConnectCallback = (ctx, ct) => AnimeDownloader.Core.Services.Socks5Connector.ConnectAsync(
                new Uri("socks5://127.0.0.1:1088"), ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var resp = await http.GetAsync("https://www.gstatic.com/generate_204");
            Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Socks5Connector via HttpClient: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Socks5Connector via HttpClient FAIL: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}

/// <summary>原始隧道 + 手动 TLS：隔离 SocketsHttpHandler 的影响。</summary>
internal static class RawTlsProbe
{
    public static async Task RunAsync(string host, int port)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.NoDelay = true;
        await socket.ConnectAsync("127.0.0.1", 1088);
        await using var stream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply);
        var hostBytes = System.Text.Encoding.UTF8.GetBytes(host);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03; req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[^2] = (byte)(port >> 8); req[^1] = (byte)(port & 0xFF);
        await stream.WriteAsync(req);
        var head = new byte[4];
        await stream.ReadExactlyAsync(head);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] socks reply: {head[0]:X2} {head[1]:X2} type={head[3]:X2}");
        var trailing = head[3] switch { 0x01 => 6, 0x04 => 18, _ => 0 };
        if (trailing > 0) { var t = new byte[trailing]; await stream.ReadExactlyAsync(t); }
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] tunnel to {host}:{port} established");
        var ssl = new System.Net.Security.SslStream(stream);
        await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = host });
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] TLS OK: {ssl.SslProtocol}");
        var buf = new byte[1024];
        await ssl.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET /generate_204 HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"));
        var read = await ssl.ReadAsync(buf);
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] response: {System.Text.Encoding.ASCII.GetString(buf, 0, Math.Min(read, 80)).Replace("\r\n", " | ")}");
    }
}

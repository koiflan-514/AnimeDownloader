using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// 以 <c>ConnectCallback</c> 形式为 HttpClient 建立 TCP 通道：
/// 直连（no_proxy 旁路）、HTTP(S) CONNECT 隧道或 SOCKS5 隧道。
/// 对"自动检测的系统代理"先尝试 SOCKS5（握手可以明确判定协议，且部分代理客户端如
/// v2rayN/xray 的混合入站对 HTTP CONNECT 建洞后不转发数据），协议不符时回退
/// HTTP CONNECT 并记住结果；用户手动指定的协议严格遵从。
/// </summary>
public static class ProxyTunnelConnector
{
    private const int MaxConnectResponseBytes = 8 * 1024;

    private enum EndpointMode
    {
        Unknown,
        Socks5,
        HttpConnect,
    }

    /// <summary>已确认协议的代理端点（键为 host:port），避免每次连接都做协议探测。</summary>
    private static readonly ConcurrentDictionary<string, EndpointMode> EndpointModes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="proxy">代理地址（http/https/socks5 方案）。</param>
    /// <param name="target">要访问的目标主机与端口。</param>
    /// <param name="bypass">no_proxy 旁路规则；命中时直连。</param>
    /// <param name="allowProtocolFallback">
    /// 允许协议自动探测与回退（自动检测的系统代理为 true；手动配置的固定协议为 false）。
    /// </param>
    public static async ValueTask<Stream> ConnectAsync(
        Uri proxy,
        DnsEndPoint target,
        ProxyBypassList? bypass,
        bool allowProtocolFallback,
        CancellationToken cancellationToken)
    {
        if (bypass is not null && bypass.IsBypassed(target.Host))
        {
            return await DirectConnectAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (HttpClientFactory.IsSocksScheme(proxy.Scheme))
        {
            return await Socks5Connector.ConnectAsync(proxy, target.Host, target.Port, cancellationToken)
                .ConfigureAwait(false);
        }

        var endpointKey = $"{proxy.Host}:{proxy.Port}";
        var mode = allowProtocolFallback ? EndpointModes.GetOrAdd(endpointKey, EndpointMode.Unknown) : EndpointMode.Unknown;
        if (!allowProtocolFallback || mode == EndpointMode.HttpConnect)
        {
            return await HttpConnectAsync(proxy, target, cancellationToken).ConfigureAwait(false);
        }

        if (mode == EndpointMode.Socks5)
        {
            return await Socks5Connector.ConnectAsync(proxy, target.Host, target.Port, cancellationToken)
                .ConfigureAwait(false);
        }

        // 自动探测：先 SOCKS5（握手结果明确），协议不符再回退 HTTP CONNECT。
        try
        {
            var stream = await Socks5Connector.ConnectAsync(proxy, target.Host, target.Port, cancellationToken)
                .ConfigureAwait(false);
            EndpointModes[endpointKey] = EndpointMode.Socks5;
            return stream;
        }
        catch (SocksProtocolMismatchException)
        {
            // 对端不是 SOCKS 服务器（如纯 HTTP 代理以 HTTP 错误应答二进制问候）。
        }

        var tunnel = await HttpConnectAsync(proxy, target, cancellationToken).ConfigureAwait(false);
        EndpointModes[endpointKey] = EndpointMode.HttpConnect;
        return tunnel;
    }

    private static async ValueTask<Stream> DirectConnectAsync(DnsEndPoint target, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(target.Host, target.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>HTTP CONNECT 隧道（https 代理时先与代理建立 TLS）。</summary>
    private static async ValueTask<Stream> HttpConnectAsync(
        Uri proxy,
        DnsEndPoint target,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        Stream? stream = null;
        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, cancellationToken).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
            if (proxy.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = proxy.Host,
                }, cancellationToken).ConfigureAwait(false);
                stream = ssl;
            }

            var builder = new StringBuilder();
            builder.Append("CONNECT ").Append(target.Host).Append(':').Append(target.Port).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(target.Host).Append(':').Append(target.Port).Append("\r\n");
            var credentials = ParseUserInfo(proxy);
            if (credentials is not null)
            {
                var token = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{credentials.Value.User}:{credentials.Value.Password}"));
                builder.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
            }

            builder.Append("Proxy-Connection: keep-alive\r\n\r\n");
            var requestBytes = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);

            var head = await ReadConnectResponseHeadAsync(stream, cancellationToken).ConfigureAwait(false);
            var statusParts = head
                .Split("\r\n\r\n", 2)[0]
                .Split('\n', 2)[0]
                .TrimEnd()
                .Split(' ', 3);
            if (statusParts.Length < 2 || !statusParts[1].StartsWith('2'))
            {
                throw new InvalidOperationException($"HTTP CONNECT 失败：{head.Split("\r\n\r\n", 2)[0].TrimEnd()}");
            }

            return stream;
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 读取 CONNECT 响应的完整头部（直到空行），保证返回的流里不残留响应字节
    /// （残留字节会破坏其上的 TLS 握手）。上限 8KB。
    /// </summary>
    private static async Task<string> ReadConnectResponseHeadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxConnectResponseBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("HTTP CONNECT 响应被截断");
            }

            length += read;
            if (length >= 4 &&
                buffer[length - 4] == (byte)'\r' && buffer[length - 3] == (byte)'\n' &&
                buffer[length - 2] == (byte)'\r' && buffer[length - 1] == (byte)'\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    private static (string User, string Password)? ParseUserInfo(Uri proxy)
    {
        if (string.IsNullOrEmpty(proxy.UserInfo))
        {
            return null;
        }

        var separator = proxy.UserInfo.IndexOf(':');
        return separator < 0
            ? (Uri.UnescapeDataString(proxy.UserInfo), string.Empty)
            : (Uri.UnescapeDataString(proxy.UserInfo[..separator]),
                Uri.UnescapeDataString(proxy.UserInfo[(separator + 1)..]));
    }
}

/// <summary>no_proxy 旁路列表：与检测到的代理一起提供，命中条目的主机绕过代理直连。</summary>
public sealed class ProxyBypassList
{
    private readonly string[] _entries;

    private ProxyBypassList(string[] entries) => _entries = entries;

    /// <summary>解析 "a.com,.b.com,c:8080" 形式的旁路列表；空串返回 null（无旁路）。</summary>
    public static ProxyBypassList? Parse(string? noProxy)
    {
        if (string.IsNullOrWhiteSpace(noProxy))
        {
            return null;
        }

        var entries = noProxy
            .Split(',')
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Select(item => item.StartsWith("*.", StringComparison.Ordinal) ? item[1..] : item)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return entries.Length == 0 ? null : new ProxyBypassList(entries);
    }

    /// <summary>
    /// 主机是否命中旁路：".domain" 条目匹配裸域与其子域，无点条目精确匹配。
    /// </summary>
    public bool IsBypassed(string host)
    {
        foreach (var entry in _entries)
        {
            if (entry.StartsWith('.'))
            {
                if (host.Equals(entry[1..], StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith(entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (host.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

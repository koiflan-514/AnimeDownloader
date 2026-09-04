using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// Minimal SOCKS5 tunnel used as an HttpClient <c>ConnectCallback</c> (.NET 的
/// WebProxy 不支持 socks5:// 方案，因此自行实现握手）。支持无认证与用户名/密码认证；
/// 目标地址按域名（或 IP 字面量）交给代理端解析，等效于 socks5h。
/// </summary>
public static class Socks5Connector
{
    private const byte Version = 0x05;
    private const byte MethodNoAuth = 0x00;
    private const byte MethodUserPass = 0x02;
    private const byte CommandConnect = 0x01;

    public static async ValueTask<Stream> ConnectAsync(
        Uri proxy,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(proxy.Host, proxy.Port, cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            try
            {
                await HandshakeAsync(stream, targetHost, targetPort, proxy, cancellationToken)
                    .ConfigureAwait(false);
                return stream;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task HandshakeAsync(
        Stream stream,
        string targetHost,
        int targetPort,
        Uri proxy,
        CancellationToken cancellationToken)
    {
        var credentials = ParseUserInfo(proxy);
        var offers = credentials is null ? new byte[] { MethodNoAuth } : new byte[] { MethodNoAuth, MethodUserPass };

        var greeting = new byte[2 + offers.Length];
        greeting[0] = Version;
        greeting[1] = (byte)offers.Length;
        Array.Copy(offers, 0, greeting, 2, offers.Length);
        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);

        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
        if (reply[0] != Version)
        {
            // 对端不是 SOCKS 服务器（如纯 HTTP 代理把二进制问候当作坏请求应答）。
            throw new SocksProtocolMismatchException(
                $"对端不是 SOCKS5 服务器（应答版本 0x{reply[0]:X2}）");
        }

        switch (reply[1])
        {
            case MethodNoAuth:
                break;
            case MethodUserPass when credentials is not null:
                await AuthenticateAsync(stream, credentials.Value, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw reply[1] == 0xFF
                    ? new SocksProtocolMismatchException("SOCKS5 服务器不接受任何本地支持的认证方式")
                    : new InvalidOperationException(
                        credentials is null
                            ? "SOCKS5 代理要求用户名/密码认证（在代理地址中提供 user:password@host:port）"
                            : "SOCKS5 代理拒绝了提供的认证方式");
        }

        await SendConnectRequestAsync(stream, targetHost, targetPort, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AuthenticateAsync(
        Stream stream,
        (string User, string Password) credentials,
        CancellationToken cancellationToken)
    {
        var user = Encoding.UTF8.GetBytes(credentials.User);
        var password = Encoding.UTF8.GetBytes(credentials.Password);
        if (user.Length > 255 || password.Length > 255)
        {
            throw new InvalidOperationException("SOCKS5 用户名或密码过长");
        }

        var auth = new byte[3 + user.Length + password.Length];
        auth[0] = 0x01;
        auth[1] = (byte)user.Length;
        Array.Copy(user, 0, auth, 2, user.Length);
        auth[2 + user.Length] = (byte)password.Length;
        Array.Copy(password, 0, auth, 3 + user.Length, password.Length);
        await stream.WriteAsync(auth, cancellationToken).ConfigureAwait(false);

        var status = new byte[2];
        await stream.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
        if (status[1] != 0x00)
        {
            throw new InvalidOperationException("SOCKS5 用户名/密码认证失败");
        }
    }

    private static async Task SendConnectRequestAsync(
        Stream stream,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        using var request = new MemoryStream();
        request.WriteByte(Version);
        request.WriteByte(CommandConnect);
        request.WriteByte(0x00); // reserved

        if (IPAddress.TryParse(targetHost, out var address))
        {
            var bytes = address.GetAddressBytes();
            request.WriteByte(address.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x04 : (byte)0x01);
            request.Write(bytes);
        }
        else
        {
            var hostBytes = Encoding.UTF8.GetBytes(targetHost);
            if (hostBytes.Length > 255)
            {
                throw new InvalidOperationException("目标主机名过长");
            }

            request.WriteByte(0x03);
            request.WriteByte((byte)hostBytes.Length);
            request.Write(hostBytes);
        }

        request.WriteByte((byte)(targetPort >> 8));
        request.WriteByte((byte)(targetPort & 0xFF));
        await stream.WriteAsync(request.ToArray(), cancellationToken).ConfigureAwait(false);

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != Version)
        {
            throw new SocksProtocolMismatchException($"SOCKS5 应答版本不支持：0x{header[0]:X2}");
        }

        if (header[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 连接被拒绝（应答码 0x{header[1]:X2}）");
        }

        var boundAddressLength = header[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => 1, // 后面先读 1 字节长度，再读该长度的域名
            _ => throw new InvalidOperationException($"SOCKS5 应答地址类型不支持：0x{header[3]:X2}"),
        };

        if (header[3] == 0x03)
        {
            var lengthByte = new byte[1];
            await stream.ReadExactlyAsync(lengthByte, cancellationToken).ConfigureAwait(false);
            boundAddressLength = lengthByte[0];
        }

        var trailing = new byte[boundAddressLength + 2];
        await stream.ReadExactlyAsync(trailing, cancellationToken).ConfigureAwait(false);
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

/// <summary>对端不是 SOCKS5 服务器（协议不匹配）；调用方可据此回退其他代理协议。</summary>
public sealed class SocksProtocolMismatchException : InvalidOperationException
{
    /// <summary>创建协议不匹配异常。</summary>
    public SocksProtocolMismatchException(string message)
        : base(message)
    {
    }
}

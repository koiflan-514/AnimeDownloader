using System.Net;
using System.Runtime.Versioning;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// 系统代理检测：环境变量优先，Windows 下回退到注册表 Internet Settings，
/// 与参考项目 proxy.py 的语义一致。NO_PROXY 旁路条目与系统代理合并。
/// </summary>
public static class ProxyDetector
{
    private static readonly string[] LocalBypassHosts = { "localhost", "127.0.0.1", "::1" };

    /// <summary>返回检测到的代理地址（http/https 相同或分别配置），未配置时为空字典。</summary>
    public static IReadOnlyDictionary<string, string> Detect()
    {
        var env = FromEnvironment();
        if (env.TryGetValue("http", out var http) || env.TryGetValue("https", out _))
        {
            return env;
        }

        var system = OperatingSystem.IsWindows() ? FromWindowsRegistry() : new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase);
        var envNoProxy = env.GetValueOrDefault("no_proxy", string.Empty);
        var systemNoProxy = system.GetValueOrDefault("no_proxy", string.Empty);
        if (!string.IsNullOrEmpty(envNoProxy) && !string.IsNullOrEmpty(systemNoProxy))
        {
            merged["no_proxy"] = string.Join(",", envNoProxy, systemNoProxy);
        }
        else
        {
            foreach (var (key, value) in system)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>从 HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/NO_PROXY 环境变量读取。</summary>
    private static Dictionary<string, string> FromEnvironment()
    {
        var proxies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in new[] { "http", "https" })
        {
            var value = Environment.GetEnvironmentVariable(scheme.ToUpperInvariant() + "_PROXY")
                ?? Environment.GetEnvironmentVariable(scheme + "_proxy");
            if (!string.IsNullOrWhiteSpace(value))
            {
                proxies[scheme] = value;
            }
        }

        var all = Environment.GetEnvironmentVariable("ALL_PROXY")
            ?? Environment.GetEnvironmentVariable("all_proxy");
        if (!string.IsNullOrWhiteSpace(all))
        {
            proxies.TryAdd("http", all);
            proxies.TryAdd("https", all);
        }

        var noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
            ?? Environment.GetEnvironmentVariable("no_proxy");
        if (!string.IsNullOrWhiteSpace(noProxy))
        {
            proxies["no_proxy"] = noProxy;
        }

        return proxies;
    }

    /// <summary>从 Windows 注册表 Internet Settings 读取系统代理。</summary>
    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> FromWindowsRegistry()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
            if (key is null)
            {
                return result;
            }

            var enabled = ConvertToBool(key.GetValue("ProxyEnable"));
            var server = key.GetValue("ProxyServer") as string;
            if (!enabled || string.IsNullOrWhiteSpace(server))
            {
                return result;
            }

            var overrideValue = key.GetValue("ProxyOverride") as string ?? string.Empty;
            var noProxyParts = new List<string>();
            foreach (var item in overrideValue.Split(';'))
            {
                var trimmed = item.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.Equals("<local>", StringComparison.OrdinalIgnoreCase))
                {
                    noProxyParts.AddRange(LocalBypassHosts);
                }
                else if (trimmed.StartsWith("*.", StringComparison.Ordinal))
                {
                    noProxyParts.Add(trimmed[2..]);
                }
                else
                {
                    noProxyParts.Add(trimmed);
                }
            }

            if (noProxyParts.Count > 0)
            {
                result["no_proxy"] = string.Join(",", noProxyParts);
            }

            // ProxyServer 可能为 "host:port" 或 "http=host:port;https=host:port;socks=host:port"
            if (server.Contains('='))
            {
                foreach (var part in server.Split(';'))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    var scheme = part[..eq].Trim().ToLowerInvariant();
                    var hostPort = part[(eq + 1)..].Trim();
                    if (hostPort.Length == 0)
                    {
                        continue;
                    }

                    if (scheme is "socks" or "socks4" or "socks5")
                    {
                        result.TryAdd("socks", "socks5://" + hostPort);
                    }
                    else if (scheme is "http" or "https")
                    {
                        result[scheme] = "http://" + hostPort;
                    }
                }
            }
            else
            {
                var url = "http://" + server.Trim();
                result["http"] = url;
                result["https"] = url;
            }
        }
        catch (System.Security.SecurityException)
        {
            // 注册表读取被拒绝：视为无系统代理
        }

        return result;
    }

    private static bool ConvertToBool(object? value) => value switch
    {
        null => false,
        string s => s.Trim().Equals("1", StringComparison.Ordinal) ||
                    s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    s.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase),
        int i => i != 0,
        uint u => u != 0,
        long l => l != 0,
        bool b => b,
        _ => false,
    };
}

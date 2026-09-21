using Android.Content;
using Android.Net;
using AnimeDownloader.Download.Contracts;

namespace AnimeDownloader.Download;

/// <summary>
/// 网络状态观察者：把 Android 的网络能力映射成 <see cref="NetworkKind"/>，
/// 并提供「等到可以传输为止」的等待原语。
/// </summary>
/// <remarks>
/// <para>手机上这一类等待是必需品而不是可选项：地铁里切基站、进电梯、从 Wi‑Fi 走到室外，
/// 网络是<b>会中途消失又回来</b>的。桌面端「下载失败 → 报错」的处理方式在手机上体验很差，
/// 正确做法是停在「等待网络」这个状态上，网络回来就继续。</para>
/// <para>用 <c>RegisterDefaultNetworkCallback</c>（API 24+）而不是轮询：系统会在默认网络
/// 变化时推给我们，省电且及时。</para>
/// </remarks>
internal sealed class AndroidNetworkMonitor : IDisposable
{
    private readonly ConnectivityManager? _manager;
    private readonly NetworkCallbackBridge _bridge;
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = new();
    private bool _registered;
    private bool _disposed;

    internal AndroidNetworkMonitor(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        _bridge = new NetworkCallbackBridge(OnNetworkChanged);
    }

    /// <summary>当前生效的网络类型。取不到管理器时按「无网络」处理（保守）。</summary>
    internal NetworkKind Current
    {
        get
        {
            if (_manager is null)
            {
                return NetworkKind.None;
            }

            var network = _manager.ActiveNetwork;
            if (network is null)
            {
                return NetworkKind.None;
            }

            var capabilities = _manager.GetNetworkCapabilities(network);
            if (capabilities is null)
            {
                return NetworkKind.None;
            }

            // 先看 NotMetered：它同时覆盖了 Wi-Fi、以太网以及用户手动标为不计费的网络，
            // 比逐个 HasTransport 判断更准。
            if (capabilities.HasCapability(NetCapability.NotMetered))
            {
                return NetworkKind.Unmetered;
            }

            if (capabilities.HasTransport(TransportType.Cellular))
            {
                return NetworkKind.Cellular;
            }

            return capabilities.HasTransport(TransportType.Wifi)
                ? NetworkKind.Unmetered
                : NetworkKind.OtherMetered;
        }
    }

    /// <summary>开始监听默认网络变化。</summary>
    internal void Start()
    {
        if (_manager is null || _registered || _disposed)
        {
            return;
        }

        try
        {
            _manager.RegisterDefaultNetworkCallback(_bridge);
            _registered = true;
        }
        catch (Exception)
        {
            // 某些定制系统上注册会抛 SecurityException（缺 ACCESS_NETWORK_STATE）。
            // 此时退化为「只在开始时判断一次当前网络」，功能受损但不崩。
            _registered = false;
        }
    }

    /// <summary>
    /// 等到网络允许传输为止。
    /// </summary>
    /// <param name="wifiOnly">是否只允许非计费网络。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <param name="onWaiting">进入等待 / 仍在等待时回调当前网络类型，用于刷新状态文案。</param>
    /// <param name="cancellationToken">取消等待。</param>
    /// <returns>true 表示可以传输；false 表示超时或被取消。</returns>
    internal async Task<bool> WaitUntilAllowedAsync(
        bool wifiOnly,
        TimeSpan timeout,
        Action<NetworkKind>? onWaiting,
        CancellationToken cancellationToken)
    {
        var current = Current;
        if (DownloadPolicy.AllowsTransfer(current, wifiOnly))
        {
            return true;
        }

        onWaiting?.Invoke(current);

        // 超时与外部取消合成一个令牌：任何一个先到都结束等待。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        var waiter = new Waiter(wifiOnly, onWaiting);
        lock (_sync)
        {
            _waiters.Add(waiter);
        }

        try
        {
            // 注册前再查一次，避免「刚判断完就恢复网络」时的漏唤醒。
            var recheck = Current;
            if (DownloadPolicy.AllowsTransfer(recheck, wifiOnly))
            {
                return true;
            }

            await waiter.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _waiters.Remove(waiter);
            }

            waiter.Dispose();
        }
    }

    private void OnNetworkChanged()
    {
        if (_disposed)
        {
            return;
        }

        Waiter[] pending;
        lock (_sync)
        {
            pending = _waiters.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        var kind = Current;
        foreach (var waiter in pending)
        {
            if (DownloadPolicy.AllowsTransfer(kind, waiter.WifiOnly))
            {
                waiter.Completion.TrySetResult(true);
            }
            else
            {
                waiter.OnWaiting?.Invoke(kind);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registered && _manager is not null)
        {
            try
            {
                _manager.UnregisterNetworkCallback(_bridge);
            }
            catch (Exception)
            {
                // 解绑失败无需处理：进程即将结束或管理器已失效。
            }
        }

        Waiter[] pending;
        lock (_sync)
        {
            pending = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var waiter in pending)
        {
            waiter.Completion.TrySetCanceled();
            waiter.Dispose();
        }

        _bridge.Dispose();
    }

    /// <summary>一次等待的句柄。</summary>
    private sealed class Waiter(bool wifiOnly, Action<NetworkKind>? onWaiting) : IDisposable
    {
        public bool WifiOnly { get; } = wifiOnly;

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>仍在等待、且网络类型发生变化时的回调 —— 让「等待 Wi‑Fi」的文案跟着实际网络刷新。</summary>
        public Action<NetworkKind>? OnWaiting { get; } = onWaiting;

        public void Dispose() => Completion.TrySetCanceled();
    }

    /// <summary>
    /// <see cref="ConnectivityManager.NetworkCallback"/> 的托管桥接。
    /// 只实现关心的三个回调，其余走基类的空实现。
    /// </summary>
    private sealed class NetworkCallbackBridge : ConnectivityManager.NetworkCallback
    {
        private readonly Action _onChange;

        public NetworkCallbackBridge(Action onChange)
        {
            ArgumentNullException.ThrowIfNull(onChange);
            _onChange = onChange;
        }

        public override void OnAvailable(Network network) => _onChange();

        public override void OnLost(Network network) => _onChange();

        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities) =>
            _onChange();
    }
}

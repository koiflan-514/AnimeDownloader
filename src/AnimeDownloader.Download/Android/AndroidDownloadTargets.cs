using System.Runtime.Versioning;
using Android.Content;
using Android.OS;
using Android.Provider;
using AnimeDownloader.Download.Contracts;
// 隐式 using 已经把 System.Environment 带进来了，而 Android.OS.Environment 是另一个类型 ——
// 不用别名会直接 CS0104 歧义。这里显式别名，读起来也比全限定名干净。
using AEnv = Android.OS.Environment;

namespace AnimeDownloader.Download;

/// <summary>
/// 按系统版本与用户选择挑选落点。
/// </summary>
/// <remarks>
/// 这段分支是 Android 存储模型分裂的正面结果：Android 10（API 29）引入分区存储后，
/// 「往公共目录写文件」从「要权限的文件路径」变成了「不要权限的数据库条目」。
/// 两套模型必须并存 —— 因为最低支持到 API 24。
/// </remarks>
internal static class AndroidDownloadTargets
{
    /// <summary>本机是否支持分区存储（API 29+）。低于此版本只能走公共目录 + 写外部存储权限。</summary>
    internal static bool SupportsScopedStorage => OperatingSystem.IsAndroidVersionAtLeast(29);

    /// <summary>创建落点。</summary>
    internal static IDownloadTarget Create(Context context, DownloadBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Location == DownloadLocation.AppPrivate)
        {
            return new AppPrivateDownloadTarget(context, options.AlbumName);
        }

        // 这里写成内联的版本判断而不是复用 SupportsScopedStorage 属性：
        // 平台分析器（CA1416）只识别 OperatingSystem.IsAndroidVersionAtLeast 这种**就地**判断，
        // 包一层属性它就看不见了，于是 MediaStore 那批 API 29+ 的成员会被判为越级调用。
        return OperatingSystem.IsAndroidVersionAtLeast(29)
            ? new MediaStoreDownloadTarget(context, options.AlbumName)
            : new LegacyPublicDownloadTarget(context, options.AlbumName);
    }

    /// <summary>把 JPG/PNG… 这类 MIME 推断结果收敛成 MediaStore 能接受的值；无法判断时返回 null。</summary>
    internal static string? NormalizeMime(string? mime) =>
        string.IsNullOrWhiteSpace(mime) ? null : mime.Trim();
}

/// <summary>
/// Android 10+ 的落点：写进 MediaStore 的 Images 集合，子相册由 RELATIVE_PATH 决定。
/// </summary>
/// <remarks>
/// <para><b>为什么不直接往 <c>/storage/emulated/0/Pictures/…</c> 写文件</b>：分区存储之后那条路径
/// 对应用既不可见也不可写，且即便在某些机型上能写，文件也不会进媒体库 —— 用户在相册里看不到，
/// 别的应用选图也选不到，等于白下。</para>
/// <para><b>原子性靠 IS_PENDING</b>：先以 pending=1 插入（此时其它应用看不到），写完再置 0。
/// 中途失败则删除该条目 —— 用户的相册里绝不会出现半张图。</para>
/// </remarks>
[SupportedOSPlatform("android29.0")]
internal sealed class MediaStoreDownloadTarget : IDownloadTarget
{
    private readonly Context _context;
    private readonly string _albumName;

    internal MediaStoreDownloadTarget(Context context, string albumName)
    {
        _context = context;
        _albumName = string.IsNullOrWhiteSpace(albumName)
            ? DownloadPlanner.DefaultAlbumName
            : albumName.Trim();
    }

    /// <inheritdoc />
    public string DisplayLocation => $"相册 / {_albumName}";

    /// <summary>MediaStore 的 RELATIVE_PATH 约定带<b>尾斜杠</b>，漏了会被归到相机胶卷根目录。</summary>
    private string RelativePath => $"{AEnv.DirectoryPictures}/{_albumName}/";

    /// <inheritdoc />
    public Task<bool> ExistsAsync(DownloadFileSpec spec, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selection = $"{MediaStore.IMediaColumns.DisplayName}=? AND {MediaStore.IMediaColumns.RelativePath}=?";
                var args = new[] { spec.DisplayName, RelativePath };
                using var cursor = _context.ContentResolver?.Query(
                    MediaStore.Images.Media.ExternalContentUri!,
                    [MediaStore.IMediaColumns.Size],
                    selection,
                    args,
                    null);
                return cursor is { Count: > 0 } && cursor.MoveToFirst();
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<string> WriteAsync(
        DownloadFileSpec spec,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var resolver = _context.ContentResolver
            ?? throw new IOException("无法访问 ContentResolver");
        var collection = MediaStore.Images.Media.ExternalContentUri
            ?? throw new IOException("无法访问媒体库");

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, spec.DisplayName);
        var mime = AndroidDownloadTargets.NormalizeMime(spec.MimeType);
        if (mime is not null)
        {
            values.Put(MediaStore.IMediaColumns.MimeType, mime);
        }

        values.Put(MediaStore.IMediaColumns.RelativePath, RelativePath);
        // 先标记为「写入中」：其它应用（相册、选图器）在置回 0 之前看不到这一条。
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(collection, values)
            ?? throw new IOException("媒体库拒绝了本次写入（DISPLAY_NAME 或目录非法）");

        try
        {
            await using (var destination = resolver.OpenOutputStream(uri)
                ?? throw new IOException("无法打开媒体库输出流"))
            {
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var commit = new ContentValues();
            commit.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, commit, null, null);
            return uri.ToString()!;
        }
        catch
        {
            // 失败即删除条目：宁可什么都不留，也不要往用户相册里塞半张图。
            TryDelete(resolver, uri);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<long> GetAvailableBytesAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 探 CacheDir 而不是 Pictures 目录：分区存储下后者是 ContentProvider 视图，
                // 拿不到 StatFs；两者最终都落在同一块 /data 卷上，读数等价。
                return StorageProbe.AvailableBytes(_context.CacheDir?.AbsolutePath);
            },
            cancellationToken);

    private static void TryDelete(ContentResolver resolver, global::Android.Net.Uri uri)
    {
        try
        {
            resolver.Delete(uri, null, null);
        }
        catch (Exception)
        {
            // 清理是尽力而为：真正的失败原因要原样抛给上层的失败归因。
        }
    }
}

/// <summary>
/// Android 9 及以下的落点：直接写公共 Pictures 目录，写完通知媒体扫描。
/// </summary>
/// <remarks>
/// 这一档需要 <c>WRITE_EXTERNAL_STORAGE</c>。清单里该权限带 <c>maxSdkVersion="28"</c> ——
/// 在 Android 10+ 上不会再申请，系统也不会给。
/// </remarks>
internal sealed class LegacyPublicDownloadTarget : IDownloadTarget
{
    private readonly Context _context;
    private readonly string _albumName;
    private readonly string _directory;

    internal LegacyPublicDownloadTarget(Context context, string albumName)
    {
        _context = context;
        _albumName = string.IsNullOrWhiteSpace(albumName)
            ? DownloadPlanner.DefaultAlbumName
            : albumName.Trim();

        var pictures = AEnv.GetExternalStoragePublicDirectory(AEnv.DirectoryPictures)
            ?? throw new IOException("此设备没有公共图片目录");
        _directory = Path.Combine(pictures.AbsolutePath!, _albumName);
    }

    /// <inheritdoc />
    public string DisplayLocation => $"公共目录 / Pictures/{_albumName}";

    /// <inheritdoc />
    public Task<bool> ExistsAsync(DownloadFileSpec spec, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(_directory, spec.DisplayName);
                return File.Exists(target) && new FileInfo(target).Length > 0;
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<string> WriteAsync(
        DownloadFileSpec spec,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        StorageProbe.EnsureLegacyWritePermission(_context);

        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, spec.DisplayName);
        var staging = target + ".part";
        try
        {
            await using (var file = new FileStream(
                staging,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(file, 81920, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(staging, target, overwrite: true);
        }
        finally
        {
            TryDeleteFile(staging);
        }

        // 不通知媒体扫描的话，文件在相册里要等到下次开机才出现。
        NotifyMediaScanner(target);
        return target;
    }

    /// <inheritdoc />
    public Task<long> GetAvailableBytesAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_directory);
                return StorageProbe.AvailableBytes(_directory);
            },
            cancellationToken);

    private void NotifyMediaScanner(string path)
    {
        try
        {
            // 全限定名而不是 using Android.Media —— 那个命名空间里有个 Android.Media.Stream，
            // 会把本文件里所有 System.IO.Stream 变成 CS0104 歧义。
            global::Android.Media.MediaScannerConnection.ScanFile(
                _context,
                [path],
                null,
                null);
        }
        catch (Exception)
        {
            // 扫描只是为了让文件更快出现在相册里；失败不影响下载结果。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 尽力而为。
        }
    }
}

/// <summary>
/// 落进应用私有外部目录：任何 Android 版本都<b>不需要权限</b>，也不进系统相册。
/// </summary>
/// <remarks>
/// 代价是卸载应用时一并删除，且其它应用（在没有 SAF 授权的情况下）看不到。
/// 存在的价值有两条：其一是给「只想自己留档、不想污染相册」的用户一个干净选择；
/// 其二是把「零权限路径」这条路留在代码里 —— 它是判断权限类问题的对照组。
/// </remarks>
internal sealed class AppPrivateDownloadTarget : IDownloadTarget
{
    private readonly Context _context;
    private readonly string _albumName;
    private readonly string _directory;

    internal AppPrivateDownloadTarget(Context context, string albumName)
    {
        _context = context;
        _albumName = string.IsNullOrWhiteSpace(albumName)
            ? DownloadPlanner.DefaultAlbumName
            : albumName.Trim();

        var root = context.GetExternalFilesDir(AEnv.DirectoryPictures)?.AbsolutePath
            ?? context.FilesDir?.AbsolutePath
            ?? throw new IOException("此设备没有可用的应用私有目录");
        _directory = Path.Combine(root, _albumName);
    }

    /// <inheritdoc />
    public string DisplayLocation => $"应用目录 / {_albumName}";

    /// <inheritdoc />
    public Task<bool> ExistsAsync(DownloadFileSpec spec, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(_directory, spec.DisplayName);
                return File.Exists(target) && new FileInfo(target).Length > 0;
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<string> WriteAsync(
        DownloadFileSpec spec,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, spec.DisplayName);
        var staging = target + ".part";
        try
        {
            await using (var file = new FileStream(
                staging,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(file, 81920, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(staging, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(staging))
                {
                    File.Delete(staging);
                }
            }
            catch (IOException)
            {
                // 尽力而为。
            }
        }

        return target;
    }

    /// <inheritdoc />
    public Task<long> GetAvailableBytesAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_directory);
                return StorageProbe.AvailableBytes(_directory);
            },
            cancellationToken);
}

/// <summary>可用空间探测与旧版权限检查。</summary>
internal static class StorageProbe
{
    /// <summary>取指定目录所在卷的可用字节数；目录不存在或读数失败时返回 <see cref="long.MaxValue"/>（视为「足够」，让真正的写入失败去报错）。</summary>
    internal static long AvailableBytes(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return long.MaxValue;
        }

        try
        {
            var stat = new StatFs(directory);
            return stat.AvailableBlocksLong * stat.BlockSizeLong;
        }
        catch (Exception)
        {
            // 探不到就不拦：预检是「提前给用户一句人话」，不是安全边界。
            return long.MaxValue;
        }
    }

    /// <summary>旧版（API 28-）写公共目录前必须持有 <c>WRITE_EXTERNAL_STORAGE</c>。</summary>
    internal static void EnsureLegacyWritePermission(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return;
        }

        var granted = context.CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage)
            == global::Android.Content.PM.Permission.Granted;
        if (!granted)
        {
            throw new UnauthorizedAccessException("缺少「存储」权限，无法写入公共图片目录");
        }
    }
}

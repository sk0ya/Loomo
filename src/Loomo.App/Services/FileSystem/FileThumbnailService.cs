using System.Windows.Media.Imaging;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧で使う Windows Shell サムネイルの取得窓口。</summary>
public interface IFileThumbnailService
{
    Task<ImageSource?> GetThumbnailAsync(string path, int edge, CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows Shell の登録済みサムネイルプロバイダーを使う。画像だけでなく、インストール済みの
/// PDF／動画コーデックが提供するサムネイルも同じ経路で扱える。プロバイダーが無い形式は null
/// を返し、呼び出し側の通常アイコンへ戻す。
/// </summary>
public sealed class FileThumbnailService : IFileThumbnailService
{
    private const int MaxCacheEntries = 256;
    private const uint ThumbnailOnly = 0x00000008;
    private const uint BiggerSizeOk = 0x00000001;
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly object _cacheGate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheItem>> _cache = new();
    private readonly LinkedList<CacheItem> _lru = new();
    private readonly Func<string, int, ImageSource?> _loader;

    public FileThumbnailService() : this(LoadShellThumbnail)
    {
    }

    // Shell はテスト環境に依存するため、キャッシュ／並列制御の検証だけは注入したローダーで行えるようにする。
    internal FileThumbnailService(Func<string, int, ImageSource?> loader)
        => _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    public Task<ImageSource?> GetThumbnailAsync(string path, int edge, CancellationToken cancellationToken = default)
    {
        if (!ThumbnailSupport.IsSupported(path) || edge <= 0)
            return Task.FromResult<ImageSource?>(null);

        if (!TryCreateCacheKey(path, edge, out var key))
            return Task.FromResult<ImageSource?>(null);
        if (TryGet(key, out var cached))
            return Task.FromResult<ImageSource?>(cached);

        return LoadAsync(key, cancellationToken);
    }

    private async Task<ImageSource?> LoadAsync(CacheKey key, CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            if (TryGet(key, out var cached))
                return cached;

            // Shell の COM 呼び出し自体はキャンセルできない。待機中の呼び出しはキャンセルしつつ、
            // 既に始まった呼び出しは最後まで実行してネイティブ資源を確実に解放する。
            var image = await Task.Run(() => _loader(key.Path, key.Edge), CancellationToken.None)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (image is not null)
                Put(key, image);
            return image;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException
            or ExternalException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // 壊れたファイル、アクセス不能なファイル、Shell 拡張の不在は通常アイコンで表示する。
            return null;
        }
        finally
        {
            if (acquired)
                _slots.Release();
        }
    }

    private static bool TryCreateCacheKey(string path, int edge, out CacheKey key)
    {
        var normalized = Normalize(path);
        try
        {
            var info = new FileInfo(normalized);
            if (!info.Exists)
            {
                key = default;
                return false;
            }

            // パスだけだと、同じファイルを上書きしたとき古い画像を返す。
            // サイズと更新時刻を含め、監視更新後は新しい内容を別エントリとして扱う。
            key = new CacheKey(normalized, edge, info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException)
        {
            key = default;
            return false;
        }
    }

    private bool TryGet(CacheKey key, out ImageSource? image)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out var node))
            {
                image = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    private void Put(CacheKey key, ImageSource image)
    {
        lock (_cacheGate)
        {
            if (_cache.Remove(key, out var old))
                _lru.Remove(old);

            var node = _lru.AddFirst(new CacheItem(key, image));
            _cache[key] = node;
            while (_cache.Count > MaxCacheEntries && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
    }

    private static ImageSource? LoadShellThumbnail(string path, int edge)
    {
        if (!File.Exists(path))
            return null;

        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (hr < 0 || factory is null)
                return null;

            var size = new ShellSize(edge, edge);
            hr = factory.GetImage(size, ThumbnailOnly | BiggerSizeOk, out hBitmap);
            if (hr < 0 || hBitmap == IntPtr.Zero)
                return null;

            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (factory is not null)
                Marshal.FinalReleaseComObject(factory);
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return path; }
    }

    private readonly record struct CacheKey(string Path, int Edge, long Length, long LastWriteUtcTicks);
    private readonly record struct CacheItem(CacheKey Key, ImageSource Image);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ShellSize(int cx, int cy)
    {
        public readonly int cx = cx;
        public readonly int cy = cy;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(ShellSize size, uint flags, out IntPtr phbm);
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);
    }
}

/// <summary>Shell に問い合わせる対象を、軽い拡張子判定で絞る。非対応形式の大量アクセスを避ける。</summary>
public static class ThumbnailSupport
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".dib", ".gif", ".heic", ".heif", ".ico", ".jfif", ".jpeg", ".jpg", ".jxl",
        ".png", ".tif", ".tiff", ".webp",
        ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".webm", ".wmv",
        ".pdf"
    };

    public static bool IsSupported(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && Extensions.Contains(Path.GetExtension(path));

    public static int EdgeFor(FilesDisplayMode mode) => mode switch
    {
        FilesDisplayMode.LargeIcons => 128,
        FilesDisplayMode.MediumIcons => 96,
        FilesDisplayMode.SmallIcons => 64,
        FilesDisplayMode.Tiles => 96,
        _ => 0,
    };
}

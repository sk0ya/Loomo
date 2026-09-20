using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace sk0ya.Loomo.App.Services;

/// <summary>「種類」列に出す、Windows エクスプローラーと同じ種類名（「Markdown ソース ファイル」など）を引く。
///
/// <para>拡張子を大文字にしただけの文字列（<c>MD</c>／<c>GITATTRIBUTES</c>）はエクスプローラー相当とは
/// 言えないうえ、拡張子が長いものは列に収まらず途中で切れて読めなくなる。シェルが持っている
/// 関連付けの名前をそのまま使う。</para>
///
/// <para>問い合わせは <c>SHGFI_USEFILEATTRIBUTES</c> 付きで行う——実ファイルを開かないので、
/// ネットワークドライブ・取り外し済みメディア・アクセス不能なファイルでも待たされない。
/// 結果は<b>拡張子ごと</b>に憶える（同じ拡張子なら答えは同じ）。フォルダーは 1 回だけ引く。</para></summary>
public static class ShellTypeNames
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _directoryTypeName;

    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;
    private const uint ShgfiTypeName = 0x000000400;
    private const uint ShgfiUseFileAttributes = 0x000000010;

    /// <summary>拡張子（<c>.md</c> のようにドット付き。無ければ空文字）に対応する種類名。
    /// シェルが答えない環境では <c>null</c> を返し、呼び出し側の従来表記へ落とす。</summary>
    public static string? ForExtension(string extension)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var key = extension ?? "";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached.Length == 0 ? null : cached;
        }

        var name = QueryWindows("file" + key, FileAttributeNormal);
        lock (Cache)
        {
            Cache[key] = name ?? "";
        }
        return name;
    }

    /// <summary>フォルダーの種類名（既定のロケールでは「ファイル フォルダー」）。</summary>
    public static string? ForDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return _directoryTypeName ??= QueryWindows("folder", FileAttributeDirectory) ?? "";
    }

    [SupportedOSPlatform("windows")]
    private static string? QueryWindows(string sampleName, uint attributes)
    {
        try
        {
            var info = default(ShFileInfo);
            var result = SHGetFileInfo(
                sampleName, attributes, ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiTypeName | ShgfiUseFileAttributes);
            if (result == IntPtr.Zero)
                return null;
            var typeName = info.szTypeName;
            return string.IsNullOrWhiteSpace(typeName) ? null : typeName;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                   or MarshalDirectiveException)
        {
            // シェルが無い／呼べない環境では種類名を諦める（列は従来表記のまま出る）。
            return null;
        }
    }

    /// <summary>テスト用。憶えた種類名を捨てる。</summary>
    internal static void ResetCache()
    {
        lock (Cache)
            Cache.Clear();
        _directoryTypeName = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    /// <summary>拡張子と「種類」表示のあいだの唯一の対応。シェルが答えなければ従来どおり
    /// 拡張子を大文字にしたものを使う（空欄は読み手には欠測に見えるので必ず何か書く）。</summary>
    public static string Describe(string name, bool isDirectory)
    {
        if (isDirectory)
            return ForDirectory() is { Length: > 0 } folder ? folder : "フォルダー";

        var ext = Path.GetExtension(name);
        if (ext.Length <= 1)
            return "ファイル";
        return ForExtension(ext) is { Length: > 0 } typeName ? typeName : ext[1..].ToUpperInvariant();
    }
}

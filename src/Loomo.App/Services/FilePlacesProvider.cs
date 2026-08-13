using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>ファイル一覧ペインの「場所」候補のうち、<b>ワークスペースの外</b>にあるもの
/// （Windows のクイックアクセス・ドライブ）を供給する。ワークスペースフォルダーとピン留めは
/// アプリ側が持っているので、ここでは扱わない。</summary>
public interface IFilePlacesProvider
{
    /// <summary>エクスプローラーの「クイックアクセス」（ピン留め＋よく使うフォルダー）。
    /// 取得できない環境では空を返す（機能が消えるだけで、他の候補は出る）。</summary>
    IReadOnlyList<FilesPlace> QuickAccess();

    /// <summary>使用可能なドライブのルート。</summary>
    IReadOnlyList<FilesPlace> Drives();
}

/// <summary>Windows シェル（`Shell.Application` COM）からクイックアクセスを読む実装。
///
/// <para>クイックアクセスの実体は <c>%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations</c> の
/// バイナリ（Jump List）で、直接読むのは現実的ではない。シェル名前空間 GUID を
/// <c>Shell.Application</c> で開くのが唯一まともな入口なので、遅延バインド（dynamic）で呼ぶ。
/// COM は STA が要るので UI スレッドから呼ぶ前提。ポップアップを開いた時だけ引き、
/// 結果は短い間キャッシュする（毎回シェルを叩くと開くたびに一拍止まる）。</para></summary>
public sealed class WindowsFilePlacesProvider : IFilePlacesProvider
{
    // シェル名前空間の「クイックアクセス」。
    private const string QuickAccessNamespace = "shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private IReadOnlyList<FilesPlace>? _quickAccess;
    private DateTime _quickAccessAt;

    public IReadOnlyList<FilesPlace> QuickAccess()
    {
        if (_quickAccess is { } cached && DateTime.UtcNow - _quickAccessAt < CacheLifetime)
            return cached;

        var places = new List<FilesPlace>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is not null && Activator.CreateInstance(shellType) is { } instance)
            {
                dynamic shell = instance;
                dynamic? folder = shell.NameSpace(QuickAccessNamespace);
                if (folder is not null)
                {
                    foreach (dynamic item in folder.Items())
                    {
                        // 「PC」のような非ファイルシステム項目も混ざるので、実在フォルダーだけ拾う。
                        string path = item.Path as string ?? "";
                        if (path.Length == 0 || !Directory.Exists(path))
                            continue;
                        var name = item.Name as string;
                        places.Add(new FilesPlace(
                            string.IsNullOrEmpty(name) ? Path.GetFileName(path.TrimEnd('\\', '/')) : name,
                            Path.GetFullPath(path), FilesPlaceKind.QuickAccess));
                    }
                }
            }
        }
        catch
        {
            // シェルが応えない／COM が使えない環境ではクイックアクセスだけ諦める。
        }

        _quickAccess = places;
        _quickAccessAt = DateTime.UtcNow;
        return places;
    }

    public IReadOnlyList<FilesPlace> Drives()
    {
        var places = new List<FilesPlace>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name.TrimEnd('\\')
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                places.Add(new FilesPlace(label, drive.RootDirectory.FullName, FilesPlaceKind.Drive));
            }
        }
        catch (IOException)
        {
            // ドライブ一覧の取得に失敗しても他の候補は出す。
        }
        return places;
    }
}

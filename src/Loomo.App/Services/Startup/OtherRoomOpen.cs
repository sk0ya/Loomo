using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>TABS の右クリック「他の Loomo で開く」：このタブの中身を、起動中の別の部屋（プロセス）へ
/// 中継パイプ（<see cref="InstanceRelay"/>）で渡す。受け手は Windows から開かれたときと同じ入口で開く。</summary>
internal static class OtherRoomOpen
{
    /// <summary>渡すもの。エディタはファイル（ディスク上の内容で開かれる）、ブラウザは URL。
    /// 渡せないもの（Untitled・about:blank 等）は null。行の表示可否にも使うのでディスクには触らない
    /// （消えたファイルはクリック時に確かめる）。ライブラリの定義（MetadataAsSource）は読み取り専用で開く
    /// 一時ファイルなので渡さない——受け手では普通の書けるファイルとして開いてしまう。</summary>
    public static ExternalOpenRequest? RequestFor(string? filePath, string? browserUrl)
    {
        if (filePath is { Length: > 0 })
            return MetadataSourceFiles.IsMetadataAsSource(filePath)
                ? null
                : new ExternalOpenRequest(null, [filePath], []);
        if (Uri.TryCreate(browserUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.IsFile))
            return new ExternalOpenRequest(null, [], [uri.AbsoluteUri]);
        return null;
    }

    /// <summary>メニューに出す部屋の名前：開いているフォルダー名（複数なら「、」でつなぐ）。
    /// フォルダーを開いていない部屋はプロセス番号で区別する。</summary>
    public static string Label(RelayInstance instance)
        => instance.Folders.Count == 0
            ? $"（フォルダーなし）· PID {instance.ProcessId}"
            : string.Join("、", instance.Folders.Select(FolderName));

    /// <summary>並べる部屋それぞれの見出し。名前がかぶる部屋（同じフォルダー・同名の別フォルダー）だけ
    /// プロセス番号を添えて見分けられるようにする。</summary>
    public static IReadOnlyList<string> Labels(IReadOnlyList<RelayInstance> instances)
    {
        var names = instances.Select(Label).ToList();
        return names
            .Select((name, i) => instances[i].Folders.Count > 0 && names.Count(n => n == name) > 1
                ? $"{name} · PID {instances[i].ProcessId}"
                : name)
            .ToList();
    }

    /// <summary>並び順：直近に前面へ来た部屋から。</summary>
    public static IReadOnlyList<RelayInstance> Order(IEnumerable<RelayInstance> instances)
        => instances.OrderByDescending(i => i.LastActiveUtc).ToList();

    private static string FolderName(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? folder : name;   // C:\ のようなルートはそのまま
    }
}

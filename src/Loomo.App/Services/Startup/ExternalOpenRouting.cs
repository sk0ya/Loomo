using System.IO;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>起動中の Loomo 1つぶんの様子（中継の問い合わせに答えたもの）。</summary>
/// <param name="ProcessId">そのプロセス。</param>
/// <param name="Folders">開いている部屋のフォルダー集合。</param>
/// <param name="LastActiveUtc">最後に前面へ来た時刻。「直近の部屋」を決めるのに使う。</param>
internal sealed record RelayInstance(int ProcessId, IReadOnlyList<string> Folders, DateTime LastActiveUtc);

/// <summary>Windows から来た「開いてほしいもの」を、どの部屋（プロセス）へ渡すか決める。
///
/// 部屋＝プロセスで、同時にいくつも立っていてよい（<see cref="WorkspaceWindowLauncher"/>）。だから
/// 既定のアプリとして振る舞うには「誰が受けるか」を決める必要がある:
/// <list type="bullet">
/// <item><b>フォルダー</b>：それを開いている部屋があればそこを前面へ。無ければ新しい部屋（自分）。</item>
/// <item><b>ファイル</b>：そのファイルを含む部屋。含む部屋が無ければ<b>直近の部屋</b>でワークスペース外の
///   ファイルとして開く——ファイル1つ開くたびに部屋が増えたり切り替わったりはしない。</item>
/// <item><b>URL</b>：直近の部屋のブラウザペイン。</item>
/// </list>
/// 受け手が無いもの（起動中の部屋がゼロ・新しい部屋で開くフォルダー）は <c>null</c>＝自分が開く。</summary>
internal static class ExternalOpenRouting
{
    public static IReadOnlyList<(int? ProcessId, ExternalOpenRequest Request)> Route(
        ExternalOpenRequest request, IReadOnlyList<RelayInstance> instances)
    {
        var mostRecent = instances.Count == 0 ? null : instances.MaxBy(i => i.LastActiveUtc);
        var folders = new Dictionary<int, string>();
        var files = new Dictionary<int, List<string>>();
        var urls = new Dictionary<int, List<string>>();
        const int self = -1;

        if (request.WorkspaceFolder is { } folder)
        {
            var owner = instances
                .Where(i => i.Folders.Any(f => SamePath(f, folder)))
                .MaxBy(i => i.LastActiveUtc);
            folders[owner?.ProcessId ?? self] = folder;
        }

        foreach (var file in request.Files)
        {
            var target = OwnerOf(file, instances) ?? mostRecent;
            Add(files, target?.ProcessId ?? self, file);
        }

        foreach (var url in request.Urls)
            Add(urls, mostRecent?.ProcessId ?? self, url);

        // 渡す順は「自分」を最後に。中継で済むものを先に片付けてから自分の起動へ進む。
        var targets = folders.Keys.Concat(files.Keys).Concat(urls.Keys)
            .Distinct()
            .OrderBy(pid => pid == self ? 1 : 0)
            .ToList();
        return targets
            .Select(pid => (
                pid == self ? (int?)null : pid,
                new ExternalOpenRequest(
                    folders.GetValueOrDefault(pid),
                    files.GetValueOrDefault(pid) ?? [],
                    urls.GetValueOrDefault(pid) ?? [])))
            .ToList();
    }

    /// <summary>そのファイルを含む部屋。入れ子なら<b>より深いフォルダー</b>を持つ部屋、同じ深さなら直近の部屋。</summary>
    private static RelayInstance? OwnerOf(string file, IReadOnlyList<RelayInstance> instances)
        => instances
            .Select(i => (Instance: i, Folder: WorkspacePaths.FolderFor(i.Folders, file)))
            .Where(x => x.Folder is not null)
            .OrderByDescending(x => x.Folder!.Length)
            .ThenByDescending(x => x.Instance.LastActiveUtc)
            .Select(x => x.Instance)
            .FirstOrDefault();

    private static void Add(Dictionary<int, List<string>> map, int key, string value)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(value);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

namespace sk0ya.Loomo.CSharp.Debug;

/// <summary>C#／.NETワークスペースから、ビルド・デバッグ対象を探す純粋な探索ロジック。
/// セッション状態や画面表示はApp側のアダプターに残し、プロジェクト形式の知識はCSharp DLLへ置く。</summary>
public static class CSharpDebugTargetResolver
{
    /// <summary>ワークスペースのいずれかに、起動対象になり得るC#プロジェクトがあるか。</summary>
    public static bool HasCSharpProject(IReadOnlyList<string> folders)
        => folders.Any(HasCSharpProjectIn);

    /// <summary>各フォルダー直下の.sln／.slnxを優先し、無ければ最初の.csprojを返す。</summary>
    public static string? FindBuildTarget(IReadOnlyList<string> folders)
    {
        foreach (var root in folders)
        {
            try
            {
                var solution = Directory.GetFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (solution is not null) return solution;
            }
            catch { /* 列挙失敗時は次のフォルダーへ */ }
        }

        foreach (var root in folders)
            if (FindProject(root) is { } project) return project;
        return null;
    }

    /// <summary>ワークスペース直下、無ければ<b>浅い順（幅優先）で最初に見つかった</b>.csprojを探す。
    /// 深さは制限しない——<c>src/apps/web/api/Api.csproj</c> のように深い階層に起動対象が置かれた
    /// ワークスペースで「.sln/.csprojが見つかりません」になり、デバッグ実行が始められなくなるため。
    /// 深さ優先だと入口として不自然な奥のプロジェクトを掴むので、浅い方を先に返す
    /// （「有るか無いか」だけを見る<see cref="HasCSharpProjectIn"/>の安価な打ち切りとは目的が違う）。</summary>
    public static string? FindProject(string root)
    {
        try
        {
            var top = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
            if (top.Length > 0) return top[0];
            return FindShallowestProject(root);
        }
        catch { return null; }
    }

    /// <summary>実行対象の近くを親方向へ遡り、ビルドに使う.csprojを推定する。</summary>
    public static string? FindProjectNear(string programPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(programPath);
            for (var i = 0; i < 6 && dir is not null; i++)
            {
                var project = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (project is not null) return project;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* アクセス不能なら推定を諦める */ }
        return null;
    }

    /// <summary>プロジェクトのbin/&lt;configuration&gt;以下から最新の出力DLLを探す。
    /// TFMが指定された場合は、そのTFMの出力だけを対象にする。</summary>
    public static string? FindOutputDll(
        string projectPath, string configuration = "Debug", string? targetFramework = null)
    {
        try
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var name = Path.GetFileNameWithoutExtension(projectPath);
            var binDirectory = Path.Combine(projectDirectory, "bin", configuration);
            if (!string.IsNullOrWhiteSpace(targetFramework))
            {
                if (Path.IsPathRooted(targetFramework) ||
                    targetFramework.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                    return null;
                binDirectory = Path.Combine(binDirectory, targetFramework);
            }
            if (!Directory.Exists(binDirectory)) return null;
            return Directory.EnumerateFiles(binDirectory, name + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool HasCSharpProjectIn(string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            if (Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly).Any())
                return true;
            return FindProjectWithinDepth(root, maxDepth: 3) is not null;
        }
        catch { return false; }
    }

    /// <summary>幅優先で最も浅い.csprojを返す。1ディレクトリの列挙失敗でも探索全体は止めない
    /// （アクセス不能なフォルダーが1つ混ざっただけで候補が消えるのを避ける）。</summary>
    private static string? FindShallowestProject(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var directory = queue.Dequeue();
            string[] subdirectories;
            try
            {
                var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (project is not null) return project;
                subdirectories = Directory.GetDirectories(directory);
            }
            catch { continue; /* アクセス不能ディレクトリは飛ばして探索を続ける */ }

            foreach (var subdirectory in subdirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                if (!IsSkippedDirectory(subdirectory))
                    queue.Enqueue(subdirectory);
        }
        return null;
    }

    /// <summary>探索から外すフォルダー（ビルド出力・依存物・隠しフォルダー）。</summary>
    private static bool IsSkippedDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return name is "bin" or "obj" or "node_modules" or ".git" or ".vs"
            || name.StartsWith(".", StringComparison.Ordinal);
    }

    private static string? FindProjectWithinDepth(string directory, int maxDepth)
    {
        try
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (project is not null) return project;
            if (maxDepth <= 0) return null;

            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                if (IsSkippedDirectory(subdirectory)) continue;
                if (FindProjectWithinDepth(subdirectory, maxDepth - 1) is { } nested)
                    return nested;
            }
        }
        catch { /* アクセス不能ディレクトリは候補から除外 */ }
        return null;
    }
}

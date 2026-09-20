using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree の「プロパティ」が読む対象。ノードの種類は、監視更新で消えた項目でも
/// 何を選んでいたかを表示できるよう、呼び出し側から渡す。</summary>
public sealed record FilePropertiesTarget(string FullPath, bool IsDirectory);

/// <summary>ACL の 1 エントリ。実効権限の推測ではなく、Windows が保持しているアクセス規則を表示する。</summary>
public sealed record FilePermissionEntry(string Identity, string Rights, string Type)
{
    public string Display => $"{Identity} — {Type}: {Rights}";
}

/// <summary>ファイル／フォルダー 1 件の読み取り結果。読み取り不能な項目も結果として返し、
/// 複数選択時に他の項目まで巻き込んでダイアログを失敗させない。</summary>
public sealed record FilePropertyItem(
    string FullPath,
    string Name,
    bool IsDirectory,
    long? SizeBytes,
    bool IsSizeIncomplete,
    DateTime? CreationTime,
    DateTime? LastWriteTime,
    FileAttributes? Attributes,
    string Location,
    IReadOnlyList<FilePermissionEntry> Permissions,
    string? Error,
    string? PermissionError = null)
{
    /// <summary>「種類」。一覧の同名列と同じく、エクスプローラーの種類名をシェルから引く
    /// （同じファイルの種類が、一覧では「Markdown ソース ファイル」・プロパティでは「ファイル」、
    /// と食い違わないようにする）。</summary>
    public string KindDisplay => ShellTypeNames.Describe(Name, IsDirectory);

    public string SizeDisplay => Error is not null
        ? "取得できません"
        : IsDirectory && IsSizeIncomplete
            ? $"{(SizeBytes is { } value ? FilePropertiesService.FormatSize(value) : "取得できません")}（一部未取得）"
            : SizeBytes is null
                ? "取得できません"
                : FilePropertiesService.FormatSize(SizeBytes.Value);

    public string CreationTimeDisplay => CreationTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "取得できません";
    public string LastWriteTimeDisplay => LastWriteTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "取得できません";
    public string AttributesDisplay => Attributes is { } value
        ? value == FileAttributes.Normal ? "なし" : value.ToString()
        : "取得できません";
    public string ErrorDisplay => Error ?? "";
}

/// <summary>複数選択ぶんのプロパティ読み取り結果。</summary>
public sealed record FilePropertiesResult(IReadOnlyList<FilePropertyItem> Items)
{
    public int Count => Items.Count;
    public string SelectionDisplay => Count == 1 ? Items[0].Name : $"{Count} 個の項目を選択中";
}

/// <summary>
/// Windows の FileInfo／DirectoryInfo と ACL からプロパティを読み取る。
/// FileTree は監視中に項目が消えたりアクセス権が変わったりするため、例外は項目単位で結果へ変換する。
/// </summary>
public sealed class FilePropertiesService
{
    private readonly Func<FilePropertiesTarget, CancellationToken, FilePropertyItem> _readOne;

    public FilePropertiesService()
        : this(ReadOneCore)
    {
    }

    // 読み取り失敗の境界をテストで固定するための注入点。実運用では既定の FileInfo／ACL 読み取りを使う。
    internal FilePropertiesService(Func<FilePropertiesTarget, FilePropertyItem> readOne)
        : this((target, _) => readOne(target))
    {
    }

    internal FilePropertiesService(Func<FilePropertiesTarget, CancellationToken, FilePropertyItem> readOne)
        => _readOne = readOne ?? throw new ArgumentNullException(nameof(readOne));

    public FilePropertiesResult ReadMany(
        IEnumerable<FilePropertiesTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var items = new List<FilePropertyItem>();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target is null || string.IsNullOrWhiteSpace(target.FullPath))
            {
                items.Add(new FilePropertyItem(
                    target?.FullPath ?? "", "（パスなし）", target?.IsDirectory ?? false,
                    null, false, null, null, null, "", Array.Empty<FilePermissionEntry>(),
                    "パスが指定されていません。"));
                continue;
            }

            try
            {
                items.Add(_readOne(target, cancellationToken));
            }
            catch (UnauthorizedAccessException)
            {
                items.Add(ErrorItem(target, "アクセスが拒否されました。"));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                items.Add(ErrorItem(target, ErrorMessage(ex)));
            }
        }

        return new FilePropertiesResult(items);
    }

    /// <summary>通常のパス、UNC パス、長いパス接頭辞を壊さずに FileInfo へ渡す。
    /// <c>\\?\</c> は Path.GetFullPath が別表現へ変換し得るため、そのまま保持する。</summary>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        return trimmed.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? trimmed
            : Path.GetFullPath(trimmed);
    }

    public static string FormatSize(long bytes)
    {
        const double unit = 1024;
        if (bytes < unit) return $"{bytes:N0} バイト";
        var value = bytes;
        string[] suffixes = ["KB", "MB", "GB", "TB", "PB"];
        var index = -1;
        var scaled = (double)bytes;
        while (scaled >= unit && index < suffixes.Length - 1)
        {
            scaled /= unit;
            index++;
        }
        return $"{scaled:N1} {suffixes[index]}（{value:N0} バイト）";
    }

    private static FilePropertyItem ReadOneCore(FilePropertiesTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = NormalizePath(target.FullPath);
        var info = target.IsDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);

        // ノード生成後に削除されたケース。FileInfo の Exists は例外を投げずに false を返す。
        if (!info.Exists)
            return ErrorItem(target with { FullPath = path }, "項目が見つかりません（削除または移動された可能性があります）。");

        var isDirectory = info is DirectoryInfo;
        long? size = null;
        var incomplete = false;
        if (info is FileInfo file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            size = file.Length;
        }
        else if (info is DirectoryInfo directory)
            (size, incomplete) = DirectorySize(directory, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var (permissions, permissionError) = ReadPermissions(info);
        var location = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(location))
            location = Path.GetPathRoot(path) ?? path;

        return new FilePropertyItem(
            path,
            info.Name,
            isDirectory,
            size,
            incomplete,
            info.CreationTime,
            info.LastWriteTime,
            info.Attributes,
            location,
            permissions,
            null,
            permissionError);
    }

    private static (long? Size, bool Incomplete) DirectorySize(
        DirectoryInfo directory,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var incomplete = false;
        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { total = checked(total + file.Length); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { incomplete = true; }
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var nested = DirectorySize(child, cancellationToken);
                if (nested.Size is { } value)
                {
                    try { total = checked(total + value); }
                    catch (OverflowException) { incomplete = true; }
                }
                incomplete |= nested.Incomplete;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            incomplete = true;
        }
        return (total, incomplete);
    }

    private static (IReadOnlyList<FilePermissionEntry> Permissions, string? Error) ReadPermissions(FileSystemInfo info)
    {
        try
        {
            AuthorizationRuleCollection rules = info switch
            {
                FileInfo file => file.GetAccessControl(AccessControlSections.Access)
                    .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.NTAccount)),
                DirectoryInfo directory => directory.GetAccessControl(AccessControlSections.Access)
                    .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.NTAccount)),
                _ => throw new NotSupportedException(),
            };

            var permissions = rules.OfType<FileSystemAccessRule>()
                .Select(rule => new FilePermissionEntry(
                    rule.IdentityReference.Value,
                    rule.FileSystemRights.ToString(),
                    rule.AccessControlType == AccessControlType.Allow ? "許可" : "拒否"))
                .ToList();
            return (permissions, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or IdentityNotMappedException)
        {
            // ACL を読めなくてもサイズ・日時・属性は有用なので、項目全体は表示する。
            return (Array.Empty<FilePermissionEntry>(), PermissionErrorMessage(ex));
        }
    }

    private static string PermissionErrorMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "権限を読み取るアクセスが拒否されました。",
        IdentityNotMappedException => "ACL のアカウント名を解決できませんでした。",
        _ => "権限情報を読み取れませんでした。",
    };

    private static FilePropertyItem ErrorItem(FilePropertiesTarget target, string error)
        => new(
            target.FullPath,
            Path.GetFileName(target.FullPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : target.FullPath,
            target.IsDirectory,
            null, false, null, null, null,
            Path.GetDirectoryName(target.FullPath) ?? "",
            Array.Empty<FilePermissionEntry>(), error);

    private static string ErrorMessage(Exception ex) => ex switch
    {
        PathTooLongException => "パスが長すぎるため読み取れません。",
        DirectoryNotFoundException or FileNotFoundException => "項目が見つかりません（削除または移動された可能性があります）。",
        _ => $"プロパティを読み取れませんでした: {ex.Message}",
    };
}

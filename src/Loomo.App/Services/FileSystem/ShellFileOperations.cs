using System.Diagnostics;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree から Windows Shell へ渡す操作。パスをコマンドラインへ連結せず、
/// <see cref="ProcessStartInfo.FileName"/> と Shell の動詞だけで起動する。</summary>
public enum ShellFileAction
{
    Open,
    OpenWith,
    Share,
    SendTo,
}

public sealed record ShellFileOperationResult(
    ShellFileAction Action,
    IReadOnlyList<string> SucceededPaths,
    IReadOnlyList<string> FailedPaths,
    bool IsCancelled,
    string? ErrorMessage)
{
    public bool Succeeded => SucceededPaths.Count > 0 && FailedPaths.Count == 0 && !IsCancelled;
}

public interface IShellFileOperations
{
    ShellFileOperationResult Execute(
        ShellFileAction action,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default);
}

/// <summary>Windows Shell の関連付け・共有・送るを起動する薄いアダプター。
/// 動詞は対象の Shell が提供するものだけを使うため、OS／Shell 拡張の差は失敗結果として
/// UI へ返す。ZIP のようなファイル変更はここでは扱わず、ファイル操作履歴を通る専用経路にする。</summary>
public sealed class ShellFileOperations : IShellFileOperations
{
    private static readonly IReadOnlyDictionary<ShellFileAction, string?> Verbs =
        new Dictionary<ShellFileAction, string?>
        {
            [ShellFileAction.Open] = null,
            [ShellFileAction.OpenWith] = "openas",
            [ShellFileAction.Share] = "share",
            [ShellFileAction.SendTo] = "sendto",
        };

    private readonly Func<ProcessStartInfo, bool> _start;

    public ShellFileOperations()
        : this(static info =>
        {
            using var process = Process.Start(info);
            return process is not null;
        })
    {
    }

    internal ShellFileOperations(Func<ProcessStartInfo, bool> start) => _start = start;

    public ShellFileOperationResult Execute(
        ShellFileAction action,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (!Verbs.ContainsKey(action))
            throw new ArgumentOutOfRangeException(nameof(action));

        var normalizedPaths = NormalizePaths(paths);
        var succeeded = new List<string>();
        var failed = new List<string>();
        if (!OperatingSystem.IsWindows())
            return new(action, succeeded, normalizedPaths, false, "Windows Shell はこの環境では利用できません。");

        for (var index = 0; index < normalizedPaths.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(action, succeeded, failed.Concat(normalizedPaths.Skip(index)).ToArray(), true, null);

            var path = normalizedPaths[index];
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                failed.Add(path);
                continue;
            }

            try
            {
                // FileName はパスそのもの、Arguments は常に空。名前に空白・引用符・& があっても
                // シェルのコマンドラインとして再解釈されない。
                var info = new ProcessStartInfo
                {
                    FileName = path,
                    Verb = Verbs[action],
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal,
                };
                if (_start(info)) succeeded.Add(path);
                else failed.Add(path);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or NotSupportedException)
            {
                failed.Add(path);
            }
        }

        var message = failed.Count == 0 ? null :
            $"{failed.Count} 件を Shell で処理できませんでした。関連付けまたは Shell 拡張が対応していない可能性があります。";
        return new(action, succeeded, failed, false, message);
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string> paths)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths));

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 起動対象には絶対に使わないが、呼び出し側がどの選択に失敗したか分かるよう残す。
                full = raw;
            }

            if (seen.Add(full))
                result.Add(full);
        }
        return result;
    }
}

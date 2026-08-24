using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

public enum QuickAccessOperationStatus
{
    Succeeded,
    AlreadyInRequestedState,
    Unsupported,
    Failed,
}

public sealed record QuickAccessOperationResult(
    QuickAccessOperationStatus Status,
    string? ErrorMessage = null)
{
    public bool Succeeded => Status is QuickAccessOperationStatus.Succeeded
        or QuickAccessOperationStatus.AlreadyInRequestedState;
}

public sealed record QuickAccessBatchResult(
    int SucceededCount,
    IReadOnlyList<string> FailedPaths,
    string? ErrorMessage)
{
    public bool HasFailures => FailedPaths.Count != 0;
}

/// <summary>Windows Explorer の「クイックアクセス／ホーム」のピンを操作する窓口。
/// Explorer の Jump List や設定ファイルを直接編集せず、Shell.Application の canonical verb
/// （<c>pintohome</c>／<c>unpinfromhome</c>）だけを呼ぶ。実状態は常に Explorer 側を再照会する。</summary>
public interface IQuickAccessService
{
    bool IsAvailable { get; }
    bool IsPinned(string path);
    bool CanPin(string path);
    QuickAccessOperationResult Pin(string path);
    QuickAccessOperationResult Unpin(string path);
    QuickAccessBatchResult PinMany(IEnumerable<string> paths);
    QuickAccessBatchResult UnpinMany(IEnumerable<string> paths);
    void Invalidate();
}

/// <summary>Shell が無い環境では全操作を安全に no-op とする実装。テストでは
/// <see cref="IQuickAccessService"/> を差し替えられるため、実機Explorerを必要としない。
///
/// <para><b>照会は一覧をまるごと 1 回引いて短時間キャッシュする。</b>クイックアクセスには
/// 「この 1 件はピンされているか」を個別に聞ける口が無く、名前空間の全項目を列挙して照合するしかない。
/// 右クリックメニューは選択項目ぶん <see cref="CanPin"/>／<see cref="IsPinned"/> を呼ぶので、
/// 1 件ずつ列挙すると複数選択で N 回（実行時にも確認するので最大 2N 回）シェルを叩き、UI スレッドが
/// 目に見えて止まる。列挙結果（パス→ピン判定）をまとめて持ち、自分で変更したときは
/// <see cref="Invalidate"/> で捨てる。</para></summary>
public sealed class WindowsQuickAccessService : IQuickAccessService
{
    private const string QuickAccessNamespace = "shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}";
    private const string PinVerb = "pintohome";
    private const string UnpinVerb = "unpinfromhome";

    /// <summary>照会キャッシュの寿命。1 回の操作（メニューを開く→実行する）をまとめる程度に短くし、
    /// Explorer 側で直接ピンを変えられても程なく追いつくようにする。</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>変更後の反映待ち。1 件ずつではなく<b>バッチ全体で 1 回だけ</b>待つ。</summary>
    private const int ConfirmAttempts = 4;
    private const int ConfirmDelayMs = 75;

    private readonly IFilePlacesProvider? _places;
    private readonly object _gate = new();
    private bool? _available;

    // クイックアクセス一覧の項目 → ピン判定（true=ピン留め／false=「よく使う場所」／null=verbs を読めず判定不能）。
    // 一覧に無いパスは未ピン。辞書そのものが null ならキャッシュ無し。
    private Dictionary<string, bool?>? _pinned;
    private DateTime _pinnedAt;

    public WindowsQuickAccessService(IFilePlacesProvider? places = null) => _places = places;

    public bool IsAvailable
    {
        get
        {
            if (_available is { } cached)
                return cached;

            try
            {
                _available = OperatingSystem.IsWindows()
                    && Type.GetTypeFromProgID("Shell.Application") is not null;
            }
            catch (Exception ex) when (ex is COMException or SecurityException or InvalidOperationException)
            {
                _available = false;
            }

            return _available.Value;
        }
    }

    public bool IsPinned(string path)
    {
        if (!TryGetPinned(path, out var pinned))
            return false;

        return pinned;
    }

    /// <summary>ピン状態を答える。<paramref name="refresh"/> で一覧を読み直す（変更の直後・反映待ち）。
    /// 戻り値は「判定できたか」で、判定できないときに未ピンと答えて解除を握りつぶさないための区別。</summary>
    private bool TryGetPinned(string path, out bool pinned, bool refresh = false)
    {
        pinned = false;
        if (!IsUsableDirectory(path) || !IsAvailable)
            return false;

        var snapshot = Snapshot(refresh);
        if (snapshot is null || Normalize(path) is not { } key)
            return false;

        // 一覧に居ない＝未ピン。
        if (!snapshot.TryGetValue(key, out var value))
            return true;

        // Quick access には「よく使う場所」も混ざる。項目の unpin verb がある場合だけ pinned と
        // 判定し、単に一覧へ出ているだけの場所を誤って解除対象にしない。verbs を読めなければ判定不能。
        if (value is not bool known)
            return false;

        pinned = known;
        return true;
    }

    /// <summary>クイックアクセス一覧を 1 回だけ列挙して、パス→ピン判定の対応を作る（短時間キャッシュ）。</summary>
    private Dictionary<string, bool?>? Snapshot(bool refresh)
    {
        lock (_gate)
        {
            if (!refresh && _pinned is { } cached && DateTime.UtcNow - _pinnedAt < CacheLifetime)
                return cached;

            var snapshot = ReadSnapshot();
            // 読めなかったときは古い値で答えない（判定不能として扱う）。
            _pinned = snapshot;
            _pinnedAt = snapshot is null ? default : DateTime.UtcNow;
            return snapshot;
        }
    }

    private Dictionary<string, bool?>? ReadSnapshot()
    {
        if (!IsAvailable)
            return null;

        return WithShell<Dictionary<string, bool?>?>(shell =>
        {
            dynamic? quickAccess = shell.NameSpace(QuickAccessNamespace);
            if (quickAccess is null)
                return null;

            var map = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic item in quickAccess.Items())
            {
                // dynamic のまま渡すと呼び出しが動的解決になるので、静的な型へ落としてから使う。
                string? itemPath = item.Path as string;
                if (Normalize(itemPath) is not { } key)
                    continue;

                bool? pinned = HasVerb(item, UnpinVerb);
                // 同じパスが重複して出る場合は「ピン留め」を優先する。
                if (!map.TryGetValue(key, out var existing) || existing is not true)
                    map[key] = pinned;
            }

            return map;
        }, null);
    }

    public bool CanPin(string path)
        => IsUsableDirectory(path) && IsAvailable && !IsPinned(path);

    public QuickAccessOperationResult Pin(string path)
        => Single(path, PinVerb, expectedPinned: true);

    public QuickAccessOperationResult Unpin(string path)
        => Single(path, UnpinVerb, expectedPinned: false);

    public QuickAccessBatchResult PinMany(IEnumerable<string> paths)
        => ApplyMany(paths, PinVerb, expectedPinned: true);

    public QuickAccessBatchResult UnpinMany(IEnumerable<string> paths)
        => ApplyMany(paths, UnpinVerb, expectedPinned: false);

    public void Invalidate()
    {
        lock (_gate)
        {
            _pinned = null;
            _pinnedAt = default;
        }
        (_places as IQuickAccessCacheInvalidator)?.InvalidateQuickAccessCache();
    }

    private QuickAccessOperationResult Single(string path, string verb, bool expectedPinned)
        => Apply([path], verb, expectedPinned)[0];

    private QuickAccessBatchResult ApplyMany(IEnumerable<string> paths, string verb, bool expectedPinned)
    {
        var targets = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var results = Apply(targets, verb, expectedPinned);

        var failed = new List<string>();
        var errors = new List<string>();
        var succeeded = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            if (results[i].Succeeded)
            {
                succeeded++;
                continue;
            }
            failed.Add(targets[i]);
            if (!string.IsNullOrWhiteSpace(results[i].ErrorMessage))
                errors.Add(results[i].ErrorMessage!);
        }

        return new(succeeded, failed, errors.Count == 0 ? null : string.Join("\n", errors.Distinct()));
    }

    /// <summary>まとめてピン留め／解除し、1 件ずつの結果を返す。<b>一覧の列挙は最初に 1 回、
    /// 反映待ちは最後に 1 回だけ</b>——1 件ごとに「照会→実行→待つ」を繰り返すと、複数選択で
    /// シェル列挙と <see cref="Thread.Sleep(int)"/> が件数ぶん積み上がって UI が止まる。</summary>
    private List<QuickAccessOperationResult> Apply(
        IReadOnlyList<string> targets, string verb, bool expectedPinned)
    {
        var results = new List<QuickAccessOperationResult>(targets.Count);
        var changed = new List<int>();

        // 現状の照会は最初の 1 件で読み直し、残りは同じ列挙結果を使う（この直後に自分で変えるので、
        // 途中で読み直しても意味がない）。
        var refresh = true;
        foreach (var path in targets)
        {
            if (!IsUsableDirectory(path) || !IsAvailable)
            {
                results.Add(new(QuickAccessOperationStatus.Unsupported,
                    "Windows Explorer のクイックアクセスを利用できません。"));
                continue;
            }

            var known = TryGetPinned(path, out var currentPinned, refresh);
            refresh = false;
            if (!known)
            {
                results.Add(new(QuickAccessOperationStatus.Failed,
                    "Explorer のクイックアクセス状態を確認できません。"));
                continue;
            }

            if (currentPinned == expectedPinned)
            {
                results.Add(new(QuickAccessOperationStatus.AlreadyInRequestedState));
                continue;
            }

            var result = InvokeVerb(path, verb);
            if (result.Status == QuickAccessOperationStatus.Succeeded)
                changed.Add(results.Count);
            results.Add(result);
        }

        // InvokeVerb が例外を出さずに無視された場合も含め、場所ポップアップは次回必ず
        // Explorer の最新状態を読む。失敗時にも副作用が起きている可能性があるため無効化する。
        Invalidate();

        if (changed.Count > 0)
        {
            var unconfirmed = WaitForState(changed.Select(i => targets[i]).ToList(), expectedPinned);
            foreach (var index in changed)
            {
                if (unconfirmed.Contains(targets[index], StringComparer.OrdinalIgnoreCase))
                    results[index] = new(QuickAccessOperationStatus.Failed,
                        "Explorer にクイックアクセスの変更が反映されたことを確認できませんでした。");
            }
        }

        return results;
    }

    private QuickAccessOperationResult InvokeVerb(string path, string verb)
    {
        try
        {
            return WithShell(shell =>
            {
                dynamic? targetFolder = shell.NameSpace(path);
                dynamic? target = targetFolder?.Self;
                if (target is null)
                    return new QuickAccessOperationResult(QuickAccessOperationStatus.Failed, "対象フォルダーをShellで開けません。");

                // 実行は表示言語に依存しない canonical verb 名だけを使う。表示名の文字列判定は
                // 既存ピンの照会に限り、ここで別のShell操作を誤って呼ぶことはない。
                target.InvokeVerb(verb);
                return new QuickAccessOperationResult(QuickAccessOperationStatus.Succeeded);
            }, new QuickAccessOperationResult(QuickAccessOperationStatus.Failed, "Shellを利用できません。"));
        }
        catch (Exception ex)
        {
            return new(QuickAccessOperationStatus.Failed, ex.Message);
        }
    }

    /// <summary>変更した全件が反映されるのを待ち、確認できなかったパスだけを返す。</summary>
    private IReadOnlyList<string> WaitForState(IReadOnlyList<string> paths, bool expectedPinned)
    {
        var pending = paths.ToList();
        for (var attempt = 0; attempt < ConfirmAttempts && pending.Count > 0; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(ConfirmDelayMs);

            var refresh = true;
            var next = new List<string>();
            foreach (var path in pending)
            {
                var known = TryGetPinned(path, out var pinned, refresh);
                refresh = false;
                if (!known || pinned != expectedPinned)
                    next.Add(path);
            }
            pending = next;
        }

        return pending;
    }

    private static bool IsUsableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || FolderTreeShellNamespaces.IsShellPath(path))
            return false;
        try { return Directory.Exists(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException or SecurityException) { return false; }
    }

    /// <summary>照合用に正規化したパス（正規化できない値は null＝照合対象外）。
    /// フルパス化できないシェル項目（「PC」等）は末尾の区切りを落とした素の文字列で照合する。</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException or SecurityException)
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private static bool? HasVerb(dynamic item, string canonicalName)
    {
        try
        {
            foreach (dynamic verb in item.Verbs())
            {
                var name = (verb.Name as string)?.Trim() ?? "";
                if (string.Equals(name, canonicalName, StringComparison.OrdinalIgnoreCase)
                    || IsUnpinDisplayName(name))
                    return true;
            }
        }
        catch
        {
            // Shell項目のverbs列挙に失敗した場合は、未ピンとは断定しない。
            // 解除側で「既に解除済み」と誤認すると、Explorer の状態を壊すため。
            return null;
        }
        return false;
    }

    private static bool IsUnpinDisplayName(string name)
        => name.Contains("unpin", StringComparison.OrdinalIgnoreCase)
            || name.Contains("unpin from", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ピン留めを外す", StringComparison.Ordinal)
            || name.Contains("ピン留めを解除", StringComparison.Ordinal)
            || name.Contains("クイック アクセスから削除", StringComparison.Ordinal)
            || name.Contains("ホームから削除", StringComparison.Ordinal);

    private static TResult WithShell<TResult>(Func<dynamic, TResult> action, TResult fallback)
    {
        object? shellObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            shellObject = shellType is null ? null : Activator.CreateInstance(shellType);
            return (shellObject is null ? fallback : action((dynamic)shellObject))!;
        }
        catch (COMException ex)
        {
            return fallback is QuickAccessOperationResult
                ? (TResult)(object)new QuickAccessOperationResult(QuickAccessOperationStatus.Failed, ex.Message)
                : fallback!;
        }
        catch (Exception ex)
        {
            return fallback is QuickAccessOperationResult
                ? (TResult)(object)new QuickAccessOperationResult(QuickAccessOperationStatus.Failed, ex.Message)
                : fallback!;
        }
        finally
        {
            if (shellObject is not null && Marshal.IsComObject(shellObject))
                Marshal.FinalReleaseComObject(shellObject);
        }
    }
}

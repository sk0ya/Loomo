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
/// <see cref="IQuickAccessService"/> を差し替えられるため、実機Explorerを必要としない。</summary>
public sealed class WindowsQuickAccessService : IQuickAccessService
{
    private const string QuickAccessNamespace = "shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}";
    private const string PinVerb = "pintohome";
    private const string UnpinVerb = "unpinfromhome";

    private readonly IFilePlacesProvider? _places;
    private bool? _available;

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

    private bool TryGetPinned(string path, out bool pinned)
    {
        pinned = false;
        if (!IsUsableDirectory(path) || !IsAvailable)
            return false;

        var result = WithShell<bool?>(shell =>
        {
            dynamic? quickAccess = shell.NameSpace(QuickAccessNamespace);
            if (quickAccess is null)
                return null;

            foreach (dynamic item in quickAccess.Items())
            {
                if (!PathsEqual(item.Path as string, path))
                    continue;

                // Quick access には「よく使う場所」も混ざる。項目の unpin verb がある場合だけ
                // pinned と判定し、単に一覧へ出ているだけの場所を誤って解除対象にしない。
                return HasVerb(item, UnpinVerb);
            }

            return false;
        }, (bool?)null);

        if (result is not bool value)
            return false;

        pinned = value;
        return true;
    }

    public bool CanPin(string path)
        => IsUsableDirectory(path) && IsAvailable && !IsPinned(path);

    public QuickAccessOperationResult Pin(string path)
        => Invoke(path, PinVerb, expectedPinned: true);

    public QuickAccessOperationResult Unpin(string path)
        => Invoke(path, UnpinVerb, expectedPinned: false);

    public QuickAccessBatchResult PinMany(IEnumerable<string> paths)
        => ApplyMany(paths, Pin);

    public QuickAccessBatchResult UnpinMany(IEnumerable<string> paths)
        => ApplyMany(paths, Unpin);

    public void Invalidate()
    {
        (_places as IQuickAccessCacheInvalidator)?.InvalidateQuickAccessCache();
    }

    private QuickAccessOperationResult Invoke(string path, string verb, bool expectedPinned)
    {
        if (!IsUsableDirectory(path) || !IsAvailable)
            return new(QuickAccessOperationStatus.Unsupported, "Windows Explorer のクイックアクセスを利用できません。");

        if (!TryGetPinned(path, out var currentPinned))
            return new(QuickAccessOperationStatus.Failed, "Explorer のクイックアクセス状態を確認できません。");

        if (currentPinned == expectedPinned)
            return new(QuickAccessOperationStatus.AlreadyInRequestedState);

        var result = WithShell(shell =>
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

        if (result.Status == QuickAccessOperationStatus.Succeeded
            && !WaitForState(path, expectedPinned))
        {
            result = new(QuickAccessOperationStatus.Failed,
                "Explorer にクイックアクセスの変更が反映されたことを確認できませんでした。");
        }

        // InvokeVerb が例外を出さずに無視された場合も含め、場所ポップアップは次回必ず
        // Explorer の最新状態を読む。失敗時にも副作用が起きている可能性があるため無効化する。
        Invalidate();
        return result;
    }

    private bool WaitForState(string path, bool expectedPinned)
    {
        const int attempts = 4;
        for (var i = 0; i < attempts; i++)
        {
            if (TryGetPinned(path, out var pinned) && pinned == expectedPinned)
                return true;

            if (i < attempts - 1)
                Thread.Sleep(75);
        }

        return false;
    }

    private QuickAccessBatchResult ApplyMany(
        IEnumerable<string> paths,
        Func<string, QuickAccessOperationResult> operation)
    {
        var failed = new List<string>();
        var errors = new List<string>();
        var succeeded = 0;

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            QuickAccessOperationResult result;
            try
            {
                result = operation(path);
            }
            catch (Exception ex)
            {
                result = new(QuickAccessOperationStatus.Failed, ex.Message);
            }

            if (result.Succeeded)
                succeeded++;
            else
            {
                failed.Add(path);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    errors.Add(result.ErrorMessage!);
            }
        }

        return new(succeeded, failed, errors.Count == 0 ? null : string.Join("\n", errors.Distinct()));
    }

    private static bool IsUsableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || FolderTreeShellNamespaces.IsShellPath(path))
            return false;
        try { return Directory.Exists(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException or SecurityException) { return false; }
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException or SecurityException)
        {
            return string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
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

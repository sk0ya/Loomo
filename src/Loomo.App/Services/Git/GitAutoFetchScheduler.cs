using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 裏で fetch するかどうかの判定（§24.92）。純関数。
/// 「最後に取得した時刻」は <c>FETCH_HEAD</c> の更新時刻なので、ターミナルで手で fetch した分も数える。
/// </summary>
internal static class GitAutoFetchPolicy
{
    /// <summary>部屋を開いた・切り替えた直後は、これより新しく取得していれば取り直さない。</summary>
    public static readonly TimeSpan FreshOnOpen = TimeSpan.FromMinutes(1);

    /// <summary>続けて失敗したら、この回数で今の対象についてはやめる（対象が変わるか、手で取得できたら戻る）。</summary>
    public const int MaxConsecutiveFailures = 3;

    public static TimeSpan Interval(int minutes) => TimeSpan.FromMinutes(Math.Max(1, minutes));

    /// <summary>
    /// 取りに行くか。<paramref name="justOpened"/> は対象（部屋・フォルダー）が変わってから最初の判定。
    /// 失敗が続いている間は間隔を広げ（失敗 1 回なら 2 倍、2 回なら 3 倍）、上限に達したらやめる。
    /// </summary>
    public static bool ShouldFetch(
        DateTime nowUtc, DateTime? lastFetchUtc, DateTime? lastAttemptUtc, int consecutiveFailures,
        TimeSpan interval, bool justOpened)
    {
        if (consecutiveFailures >= MaxConsecutiveFailures)
            return false;
        if (consecutiveFailures > 0 && lastAttemptUtc is { } attempt
            && nowUtc - attempt < interval * (consecutiveFailures + 1))
            return false;
        if (lastFetchUtc is not { } last)
            return true;
        return nowUtc - last >= (justOpened ? FreshOnOpen : interval);
    }

    /// <summary>フェッチのボタンに添える一言（最後に取得した時刻）。</summary>
    public static string Describe(DateTime? lastFetchUtc, DateTime nowUtc, bool enabled, int intervalMinutes)
    {
        var auto = enabled ? $"（自動で {Math.Max(1, intervalMinutes)} 分ごと）" : "（自動の取得は切っています）";
        if (lastFetchUtc is not { } last)
            return $"まだ取得していません{auto}";
        var ago = nowUtc - last;
        var when = ago < TimeSpan.FromMinutes(1) ? "たった今"
            : ago < TimeSpan.FromHours(1) ? $"{(int)ago.TotalMinutes} 分前"
            : ago < TimeSpan.FromDays(1) ? $"{(int)ago.TotalHours} 時間前"
            : $"{(int)ago.TotalDays} 日前";
        return $"最後に取得：{last.ToLocalTime():M/d HH:mm}（{when}）{auto}";
    }
}

/// <summary>
/// 裏で fetch する（§24.92）。Git パネルが見ている対象（部屋のフォルダー）について、開いた・切り替えたときと一定の間隔で。
/// 人が Git を操作している最中（パネルが実行中）は譲る。資格情報の窓は出さず、失敗が続けばその対象ではやめる。
/// </summary>
public sealed class GitAutoFetchScheduler : IDisposable
{
    /// <summary>判定の刻み。間隔そのものではない（間隔は設定・既定 10 分）。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    /// <summary>対象が変わってから最初に見るまでの間。切替の最中（タブの復元・LSP の起動）とぶつけない。</summary>
    private static readonly TimeSpan SettleAfterOpen = TimeSpan.FromSeconds(5);

    private readonly GitService _git;
    private readonly LoomoSettings _settings;
    private readonly GitPanelViewModel _panel;
    private readonly DispatcherTimer _timer;
    private string? _root;
    private string? _fetchHeadPath;
    private bool _justOpened = true;
    private DateTime? _lastAttemptUtc;
    private int _failures;
    private bool _running;

    public GitAutoFetchScheduler(GitService git, LoomoSettings settings, GitPanelViewModel panel)
    {
        _git = git;
        _settings = settings;
        _panel = panel;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SettleAfterOpen };
        _timer.Tick += async (_, _) => await OnTickAsync();
    }

    public void Start()
    {
        _git.ActiveRootChanged += OnActiveRootChanged;
        _git.OperationExecuted += OnOperationExecuted;
        _timer.Start();
    }

    private void OnActiveRootChanged(object? sender, EventArgs e)
    {
        // 部屋の切替・フォルダーの切替。次の判定は落ち着いてから、「開いた直後」の基準で。
        _timer.Dispatcher.BeginInvoke(() =>
        {
            _timer.Stop();
            _timer.Interval = SettleAfterOpen;
            _timer.Start();
        });
    }

    private void OnOperationExecuted(object? sender, GitOperationEventArgs e)
    {
        // 手で取得（フェッチ・プル）できたなら、認証は通っている——やめていた自動の取得を戻す。
        if (e.Success && (e.Command.StartsWith("fetch", StringComparison.Ordinal) || e.Command.StartsWith("pull", StringComparison.Ordinal)))
        {
            _failures = 0;
            _timer.Dispatcher.BeginInvoke(UpdateSummary);
        }
    }

    private async Task OnTickAsync()
    {
        _timer.Interval = Tick;
        if (_running)
            return;
        var root = _git.RootPath;
        if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
        {
            _root = root;
            _fetchHeadPath = null;
            _justOpened = true;
            _failures = 0;
            _lastAttemptUtc = null;
            UpdateSummary();   // 前の対象の時刻を出したままにしない
        }
        // 人が Git を操作している最中は譲る。「開いた直後」の取得は、空いた次の刻みへ持ち越す。
        if (_panel.IsBusy || string.IsNullOrEmpty(root))
            return;
        if (!_panel.IsRepository || !_panel.HasRemote)
        {
            _justOpened = false;
            return;
        }

        _running = true;
        try
        {
            _fetchHeadPath ??= await _git.GetFetchHeadPathAsync();
            UpdateSummary();
            if (!_settings.GitAutoFetch)
                return;
            var now = DateTime.UtcNow;
            var lastFetch = LastFetchUtc();
            var interval = GitAutoFetchPolicy.Interval(_settings.GitAutoFetchIntervalMinutes);
            var fetch = GitAutoFetchPolicy.ShouldFetch(now, lastFetch, _lastAttemptUtc, _failures, interval, _justOpened);
            _justOpened = false;
            if (!fetch)
                return;
            _lastAttemptUtc = now;
            var result = await _git.FetchInBackgroundAsync();
            // 取得している間に対象が変わったなら、結果は前の対象のもの。数えない。
            if (!string.Equals(_git.RootPath, root, StringComparison.OrdinalIgnoreCase))
                return;
            _failures = result.Success ? 0 : _failures + 1;
            UpdateSummary();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            _failures++;   // git が起動できない等。黙って次の機会へ
        }
        finally
        {
            _running = false;
        }
    }

    private DateTime? LastFetchUtc()
    {
        try
        {
            return _fetchHeadPath is { } path && System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>フェッチのボタンに添える「最後に取得した時刻」。ファイルの時刻を見るだけなので刻みごとに更新してよい。</summary>
    private void UpdateSummary()
        => _panel.FetchSummary = _fetchHeadPath is null ? null
            : GitAutoFetchPolicy.Describe(LastFetchUtc(), DateTime.UtcNow, _settings.GitAutoFetch, _settings.GitAutoFetchIntervalMinutes);

    public void Dispose()
    {
        _timer.Stop();
        _git.ActiveRootChanged -= OnActiveRootChanged;
        _git.OperationExecuted -= OnOperationExecuted;
    }
}

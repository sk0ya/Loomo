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

    /// <summary>続けて失敗したら、この回数でそのリポジトリについてはやめる（パネルの対象に選び直すか、手で取得できたら戻る）。</summary>
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
/// 裏で fetch する（§24.92）。ワークスペースの<b>全リポジトリ</b>（§40.6）について、開いた・増えたときと一定の間隔で。
/// 判定はリポジトリごとに数え（最後の取得・失敗の回数）、1 回の刻みで取得するのは 1 つだけ——リポジトリが
/// 多い部屋で、開いた瞬間に fetch が一斉に走らないように。Git パネルが見ているリポジトリを先に見る。
/// 人が Git を操作している最中（パネルが実行中）は譲る。資格情報の窓は出さず（§24.101）、失敗が続けばその
/// リポジトリではやめる。
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
    private readonly Dictionary<string, RepositoryFetchState> _states = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Git パネルが見ているリポジトリ（作業ツリーの根）。パネルの「最後に取得」はこれのもの。</summary>
    private string? _panelRepository;
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
        _git.ActiveRootChanged += OnTargetsChanged;
        _git.Repositories.Changed += OnTargetsChanged;
        _git.OperationExecuted += OnOperationExecuted;
        _timer.Start();
    }

    private void OnTargetsChanged(object? sender, EventArgs e)
    {
        // 部屋の切替・フォルダーの追加と削除・パネルの対象の切替。次の判定は落ち着いてから。
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
            _timer.Dispatcher.BeginInvoke(() =>
            {
                if (_panelRepository is { } root && _states.TryGetValue(root, out var state))
                    state.Failures = 0;
                UpdateSummary();
            });
        }
    }

    private async Task OnTickAsync()
    {
        _timer.Interval = Tick;
        if (_running)
            return;
        _running = true;
        try
        {
            // 一覧の作り直し（ディレクトリ列挙）は UI スレッドの外で。
            var panelRoot = _git.RootPath;
            var (repositories, panelRepository) = await Task.Run(() =>
            {
                var all = _git.Repositories.Repositories;
                return (all, panelRoot is null ? null : _git.Repositories.RepositoryFor(panelRoot));
            });

            SyncStates(repositories, panelRepository?.Root);
            UpdateSummary();
            // 人が Git を操作している最中は譲る。「開いた直後」の取得は、空いた次の刻みへ持ち越す。
            if (_panel.IsBusy || !_settings.GitAutoFetch)
                return;

            var interval = GitAutoFetchPolicy.Interval(_settings.GitAutoFetchIntervalMinutes);
            foreach (var repository in OrderForFetch(repositories, panelRepository?.Root))
            {
                var state = _states[repository.Root];
                var git = GitFor(repository.Root);
                state.HasRemote ??= (await git.GetRemotesAsync()).Count > 0;
                if (state.HasRemote != true)
                {
                    state.JustOpened = false;
                    continue;
                }
                state.FetchHeadPath ??= await git.GetFetchHeadPathAsync();
                if (string.Equals(repository.Root, _panelRepository, StringComparison.OrdinalIgnoreCase))
                    UpdateSummary();
                var now = DateTime.UtcNow;
                var fetch = GitAutoFetchPolicy.ShouldFetch(
                    now, state.LastFetchUtc(), state.LastAttemptUtc, state.Failures, interval, state.JustOpened);
                state.JustOpened = false;
                if (!fetch)
                    continue;

                state.LastAttemptUtc = now;
                var result = await git.FetchInBackgroundAsync();
                // 取得している間に一覧から外れた（部屋を切り替えた）なら、結果は数えない。
                if (!_states.TryGetValue(repository.Root, out var still) || !ReferenceEquals(still, state))
                    return;
                state.Failures = result.Success ? 0 : state.Failures + 1;
                UpdateSummary();
                return; // 1 回の刻みで 1 つだけ
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            // git が起動できない等。黙って次の機会へ
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>一覧に合わせて、リポジトリごとの状態を足し・捨てる。新しく見えたリポジトリとパネルの対象が
    /// 替わった先は「開いた直後」として扱う。</summary>
    private void SyncStates(IReadOnlyList<GitRepositoryInfo> repositories, string? panelRepository)
    {
        var alive = new HashSet<string>(repositories.Select(r => r.Root), StringComparer.OrdinalIgnoreCase);
        foreach (var root in _states.Keys.Where(k => !alive.Contains(k)).ToList())
            _states.Remove(root);
        foreach (var repository in repositories)
        {
            if (!_states.ContainsKey(repository.Root))
                _states[repository.Root] = new RepositoryFetchState();
        }
        if (!string.Equals(panelRepository, _panelRepository, StringComparison.OrdinalIgnoreCase))
        {
            _panelRepository = panelRepository;
            if (panelRepository is not null && _states.TryGetValue(panelRepository, out var state))
            {
                state.JustOpened = true;
                state.Failures = 0;
                state.LastAttemptUtc = null;
            }
        }
    }

    /// <summary>パネルが見ているリポジトリを先に、残りは一覧の順。</summary>
    internal static IEnumerable<GitRepositoryInfo> OrderForFetch(IReadOnlyList<GitRepositoryInfo> repositories, string? panelRepository)
        => repositories
            .OrderBy(r => string.Equals(r.Root, panelRepository, StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    /// <summary>パネルが見ているリポジトリはパネルと同じ窓口で取得する——取得後の読み直し（ahead/behind）が
    /// パネルへ届くように。それ以外はリポジトリに固定した窓口（§40.5）。</summary>
    private GitService GitFor(string repositoryRoot)
        => string.Equals(repositoryRoot, _panelRepository, StringComparison.OrdinalIgnoreCase) ? _git : _git.For(repositoryRoot);

    /// <summary>フェッチのボタンに添える「最後に取得した時刻」（パネルが見ているリポジトリのもの）。</summary>
    private void UpdateSummary()
    {
        var state = _panelRepository is { } root && _states.TryGetValue(root, out var s) ? s : null;
        _panel.FetchSummary = state?.FetchHeadPath is null ? null
            : GitAutoFetchPolicy.Describe(state.LastFetchUtc(), DateTime.UtcNow, _settings.GitAutoFetch, _settings.GitAutoFetchIntervalMinutes);
    }

    public void Dispose()
    {
        _timer.Stop();
        _git.ActiveRootChanged -= OnTargetsChanged;
        _git.Repositories.Changed -= OnTargetsChanged;
        _git.OperationExecuted -= OnOperationExecuted;
    }

    private sealed class RepositoryFetchState
    {
        public string? FetchHeadPath { get; set; }
        public bool? HasRemote { get; set; }
        public bool JustOpened { get; set; } = true;
        public DateTime? LastAttemptUtc { get; set; }
        public int Failures { get; set; }

        /// <summary>「最後に取得した時刻」は FETCH_HEAD の更新時刻（ターミナルで手で取得した分も数える）。</summary>
        public DateTime? LastFetchUtc()
        {
            try
            {
                return FetchHeadPath is { } path && System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : null;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}

using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// Terminal ペインのヘッダーに出す Claude Code の使用量（5時間枠）。ターミナルで Claude Code を回している人が、
/// 画面を切り替えずに「あとどれだけ使えるか」を周辺視野で見られるようにする。詳細（週枠・リセット時刻）はツールチップ。
/// Claude Code にログインしていなければ何も出さない。
/// </summary>
public sealed partial class ClaudeUsageViewModel : ObservableObject
{
    /// <summary>取得間隔。使用量は分単位でしか動かないので、これより細かく叩く意味は無い。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(3);

    /// <summary>この割合以上で警告色にする。</summary>
    public const double WarningPercent = 80;

    private readonly ClaudeUsageClient _client;
    private readonly DispatcherTimer _timer;
    private int _fetching;

    private ClaudeUsage? _usage;
    private DateTimeOffset? _fetchedAt;
    private ClaudeUsageProblem _problem;

    public ClaudeUsageViewModel(ClaudeUsageClient client)
    {
        _client = client;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>ヘッダーに出すか（ログインしていて、一度でも値が取れた）。</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>ヘッダーの短い表示（例: 「5h 34%」）。</summary>
    [ObservableProperty]
    private string _label = "";

    /// <summary>5時間枠の使用率（0–100）。バーの長さ。</summary>
    [ObservableProperty]
    private double _fiveHourPercent;

    /// <summary>5時間枠か週枠が <see cref="WarningPercent"/> を超えている。</summary>
    [ObservableProperty]
    private bool _isWarning;

    /// <summary>ツールチップの詳細。</summary>
    [ObservableProperty]
    private string _detail = "";

    /// <summary>定期取得を始める（初回はすぐ）。</summary>
    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>ツールチップを開く直前に「あと何分」を今の時刻で書き直す（取得は3分おきなので、そのままだとずれる）。</summary>
    public void RefreshDetail()
    {
        if (_usage is not null) Detail = FormatDetail(_usage, _fetchedAt, _problem, DateTimeOffset.Now);
    }

    /// <summary>今すぐ取り直す（表示のクリック）。</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _fetching, 1) == 1) return;
        try
        {
            var result = await Task.Run(() => _client.FetchAsync(CancellationToken.None));
            _problem = result.Problem;
            if (result.Usage is { } usage)
            {
                _usage = usage;
                _fetchedAt = DateTimeOffset.Now;
            }
            else if (result.Problem == ClaudeUsageProblem.NotSignedIn)
            {
                _usage = null;
                _fetchedAt = null;
            }
            Apply(DateTimeOffset.Now);
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    private void Apply(DateTimeOffset now)
    {
        IsVisible = _usage is not null;
        if (_usage is null) return;
        var five = _usage.FiveHour;
        FiveHourPercent = five?.Percent ?? 0;
        Label = FormatLabel(_usage);
        IsWarning = (five?.Percent ?? 0) >= WarningPercent || (_usage.SevenDay?.Percent ?? 0) >= WarningPercent;
        Detail = FormatDetail(_usage, _fetchedAt, _problem, now);
    }

    /// <summary>ヘッダーの短い表示。5時間枠が無い応答なら週枠を出す。</summary>
    public static string FormatLabel(ClaudeUsage usage) =>
        usage.FiveHour is { } five ? $"5h {Percent(five.Percent)}"
        : usage.SevenDay is { } week ? $"週 {Percent(week.Percent)}"
        : "";

    /// <summary>ツールチップの本文。</summary>
    public static string FormatDetail(ClaudeUsage usage, DateTimeOffset? fetchedAt, ClaudeUsageProblem problem, DateTimeOffset now)
    {
        var sb = new StringBuilder("Claude Code の使用量");
        AppendWindow(sb, "5時間枠", usage.FiveHour, now);
        AppendWindow(sb, "週枠", usage.SevenDay, now);
        sb.AppendLine().AppendLine();
        if (fetchedAt is { } at) sb.Append(CultureInfo.InvariantCulture, $"{at.ToLocalTime():HH:mm} 時点");
        sb.Append(problem switch
        {
            ClaudeUsageProblem.TokenExpired => "（認証の期限切れで更新を止めています。Claude Code を使うと再開します）",
            ClaudeUsageProblem.Failed => "（直近の取得に失敗しました）",
            _ => "",
        });
        sb.AppendLine().Append("クリックで今すぐ更新");
        return sb.ToString();
    }

    private static void AppendWindow(StringBuilder sb, string name, ClaudeUsageWindow? window, DateTimeOffset now)
    {
        if (window is null) return;
        sb.AppendLine().Append(CultureInfo.InvariantCulture, $"{name}: {Percent(window.Percent)} 使用");
        if (window.ResetsAt is { } reset)
            sb.Append(CultureInfo.InvariantCulture, $"　リセット {FormatReset(reset.ToLocalTime(), now.ToLocalTime())}");
    }

    /// <summary>リセット時刻。今日なら時刻だけ、別の日なら日付も。残り時間を添える。</summary>
    public static string FormatReset(DateTimeOffset reset, DateTimeOffset now)
    {
        var when = reset.Date == now.Date
            ? reset.ToString("HH:mm", CultureInfo.InvariantCulture)
            : reset.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        var left = reset - now;
        if (left <= TimeSpan.Zero) return when;
        var remain = left.TotalHours >= 24 ? $"{(int)left.TotalDays}日{left.Hours}時間"
            : left.TotalMinutes >= 60 ? $"{(int)left.TotalHours}時間{left.Minutes}分"
            : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}分";
        return $"{when}（あと{remain}）";
    }

    private static string Percent(double value) => $"{Math.Round(value).ToString(CultureInfo.InvariantCulture)}%";
}

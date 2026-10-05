using System;
using System.Collections.Generic;
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

    /// <summary>ツールチップの行（5時間枠・週枠）。</summary>
    [ObservableProperty]
    private IReadOnlyList<ClaudeUsageRow> _rows = [];

    /// <summary>ツールチップの末尾（取得時刻と状態）。</summary>
    [ObservableProperty]
    private string _footer = "";

    /// <summary>定期取得を始める（初回はすぐ）。</summary>
    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>ツールチップを開く直前に残り時間と経過線を今の時刻で引き直す（取得は3分おきなので、そのままだとずれる）。</summary>
    public void RefreshDetail()
    {
        if (_usage is not null) ApplyDetail(DateTimeOffset.Now);
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
        ApplyDetail(now);
    }

    private void ApplyDetail(DateTimeOffset now)
    {
        Rows = BuildRows(_usage!, now);
        Footer = FormatFooter(_fetchedAt, _problem);
    }

    /// <summary>ヘッダーの短い表示。5時間枠の%（無い応答なら週枠）。</summary>
    public static string FormatLabel(ClaudeUsage usage) =>
        (usage.FiveHour ?? usage.SevenDay) is { } window ? Percent(window.Percent) : "";

    /// <summary>ツールチップの行。枠の長さが分かっているので、リセット時刻から「枠のどこまで時間が経ったか」も出す。</summary>
    public static IReadOnlyList<ClaudeUsageRow> BuildRows(ClaudeUsage usage, DateTimeOffset now)
    {
        var rows = new List<ClaudeUsageRow>(2);
        if (usage.FiveHour is { } five) rows.Add(BuildRow("5時間", five, TimeSpan.FromHours(5), now));
        if (usage.SevenDay is { } week) rows.Add(BuildRow("週", week, TimeSpan.FromDays(7), now));
        return rows;
    }

    private static ClaudeUsageRow BuildRow(string name, ClaudeUsageWindow window, TimeSpan length, DateTimeOffset now)
    {
        double? elapsed = null;
        var remaining = "";
        var resetAt = "";
        if (window.ResetsAt is { } reset)
        {
            var left = reset - now;
            elapsed = Math.Clamp(100 * (1 - left / length), 0, 100);
            remaining = FormatRemaining(left);
            var local = reset.ToLocalTime();
            resetAt = local.Date == now.ToLocalTime().Date
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        }
        return new ClaudeUsageRow($"{name}  {Percent(window.Percent)}", window.Percent, elapsed,
            remaining, resetAt, window.Percent >= WarningPercent);
    }

    /// <summary>リセットまでの残り（「15分」「2時間34分」「4日15時間」）。過ぎていれば「まもなく」。</summary>
    public static string FormatRemaining(TimeSpan left) =>
        left <= TimeSpan.Zero ? "まもなく"
        : left.TotalHours >= 24 ? $"{(int)left.TotalDays}日{left.Hours}時間"
        : left.TotalMinutes >= 60 ? $"{(int)left.TotalHours}時間{left.Minutes}分"
        : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}分";

    /// <summary>ツールチップの末尾。</summary>
    public static string FormatFooter(DateTimeOffset? fetchedAt, ClaudeUsageProblem problem)
    {
        var at = fetchedAt is { } t ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " 時点" : "";
        var state = problem switch
        {
            ClaudeUsageProblem.TokenExpired => "・認証の期限切れで停止中（Claude Code を使うと再開）",
            ClaudeUsageProblem.Failed => "・直近の取得に失敗",
            _ => "",
        };
        return $"{at}{state}　クリックで更新";
    }

    private static string Percent(double value) => $"{Math.Round(value).ToString(CultureInfo.InvariantCulture)}%";
}

/// <summary>ツールチップの1行。<paramref name="ElapsedPercent"/> は枠の時間がどこまで経ったか（縦線の位置）。</summary>
public sealed record ClaudeUsageRow(
    string Name, double Percent, double? ElapsedPercent, string Remaining, string ResetAt, bool IsWarning)
{
    public bool HasElapsed => ElapsedPercent is not null;
}

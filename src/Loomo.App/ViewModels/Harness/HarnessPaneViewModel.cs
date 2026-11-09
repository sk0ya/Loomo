using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>エディタで開いてほしいファイルと行（1始まり、0 なら先頭）。</summary>
public sealed record HarnessOpenRequest(string Path, int Line);

/// <summary>ブラウザペインで開いてほしいチケット。</summary>
public sealed record HarnessTicketOpenRequest(string Url, string Title);

/// <summary>
/// チケットの題と画面 URL を引く口（§37.3.2）。ハーネスは記録だけから決まる（問い合わせない）ので、題は
/// 既に読み込んである一覧から引けたときだけ添える。実装は Work Items（Azure DevOps）。
/// </summary>
public interface IHarnessTicketDirectory
{
    string? TitleOf(HarnessTicketRef ticket);
    string? UrlOf(HarnessTicketRef ticket);

    /// <summary>引ける題が変わった（一覧を読み込んだ）。UI スレッドで上がる。</summary>
    event EventHandler? TicketsChanged;
}

/// <summary>
/// ハーネスペイン（<see cref="PaneKind.Harness"/>、§37）。記録を見て、版を比べ、直し所探しを Claude Code に渡す作業台。
/// <para><b>観測事実だけを出す</b>：数値は記録にあるものだけ（料金が無ければ「不明」）。直し所の推測はペインではせず、
/// 記録を読ませるプロンプトにして渡す（§37.5）。</para>
/// <para>記録の解析は重いので、初めて見えたとき・期間を変えたとき・更新を押したときだけ走る。記録フォルダーは
/// 監視しない——Claude Code はターンごとに追記するので、監視すると作業中ずっと再解析が走る（§37.8）。</para>
/// </summary>
public sealed partial class HarnessPaneViewModel : ObservableObject
{
    /// <summary>分析（§37.5）。タブの番号は永続化される（旧「改善候補」の 0 を引き継ぐ。旧「点検」の 4 は 0 に戻す）。</summary>
    public const int TabAnalysis = 0;
    public const int TabRuns = 1;
    public const int TabConfig = 2;
    public const int TabVersions = 3;

    private const int DetailMaxChars = 20_000;

    private readonly HarnessAnalyzer _analyzer;
    private readonly IWorkspaceService _workspace;
    private readonly IHarnessTicketDirectory? _tickets;
    private CancellationTokenSource? _loadCts;
    private readonly DetailSlot _callDetailLoad = new();
    private HarnessSnapshot? _snapshot;
    private bool _loaded;
    private bool _stale = true;

    public HarnessPaneViewModel(HarnessAnalyzer analyzer, IWorkspaceService workspace, IHarnessTicketDirectory? tickets = null)
    {
        _analyzer = analyzer;
        _workspace = workspace;
        _tickets = tickets;
        // 一覧が後から読み込まれたら題を添え直す（記録は読み直さない）。
        if (_tickets is not null)
            _tickets.TicketsChanged += (_, _) =>
            {
                if (_snapshot is { } snapshot)
                    ApplyRuns(snapshot);
            };
        // ワークスペースが変われば記録の出どころも変わる。見えていれば読み直し、見えていなければ次に見えたとき。
        _workspace.FoldersChanged += (_, _) => QueueRefresh();
        ConfigView = new System.Windows.Data.ListCollectionView(ConfigRows);
        ConfigView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(HarnessConfigRow.ScopeLabel)));
    }

    private bool _refreshQueued;

    /// <summary>
    /// 読み直しを1回にまとめて予約する。ワークスペースの切替では LoadRoot・追加フォルダーごとの FoldersChanged と
    /// <see cref="Restore"/> が同じ流れで続けて来るので、その都度読むと記録の走査と git log が重ねて走る。
    /// </summary>
    private void QueueRefresh()
    {
        _stale = true;
        if (_refreshQueued || Application.Current?.Dispatcher is not { } dispatcher)
            return;
        _refreshQueued = true;
        dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (_loaded && _stale)
                _ = RefreshAsync();
        });
    }

    public IReadOnlyList<HarnessPeriodOption> PeriodOptions { get; } =
    [
        new(7, "7日"), new(30, "30日"), new(90, "90日"), new(365, "1年"),
    ];

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private HarnessPeriodOption? _selectedPeriod;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _emptyMessage = "";

    // ===== 下の帯（周辺視野） =====
    [ObservableProperty] private string _runsLabel = "—";
    [ObservableProperty] private string _verdictsLabel = "—";
    [ObservableProperty] private string _tokensPerAcceptedLabel = "—";
    [ObservableProperty] private string _totalTokensLabel = "—";
    [ObservableProperty] private string _cacheLabel = "—";
    [ObservableProperty] private string _errorRateLabel = "—";
    [ObservableProperty] private string _costLabel = "—";
    [ObservableProperty] private string _harnessLabel = "";
    /// <summary>期間内に判定（受け入れ／差し戻し）が1件でもあるか。無ければ判定まわりの数字は出さない（0／0 と「受け入れなし」が並ぶだけになる）。</summary>
    [ObservableProperty] private bool _hasVerdicts;

    // ===== 分析 =====
    /// <summary>Claude Code に渡すプロンプト（期間の記録のパス・指標・構成要素）。コピーする前に中身を見せる。</summary>
    [ObservableProperty] private string _analysisPrompt = "";
    /// <summary>何を渡すかの一行（期間・Run 数・差し戻し・失敗のある Run）。</summary>
    [ObservableProperty] private string _analysisSummary = "";

    // ===== 実行 =====
    public ObservableCollection<HarnessRunRow> Runs { get; } = new();
    public ObservableCollection<HarnessCallRow> RunCalls { get; } = new();
    [ObservableProperty] private HarnessRunRow? _selectedRun;
    [ObservableProperty] private HarnessCallRow? _selectedCall;
    [ObservableProperty] private string _runDetail = "";
    [ObservableProperty] private string _callDetail = "";

    // ===== 構成・版 =====
    public ObservableCollection<HarnessConfigRow> ConfigRows { get; } = new();
    /// <summary>構成タブの一覧（効く範囲＝このワークスペース／ユーザー全体で見出しを分ける）。</summary>
    public System.ComponentModel.ICollectionView ConfigView { get; }
    public ObservableCollection<HarnessVersionRowViewModel> Versions { get; } = new();
    [ObservableProperty] private HarnessConfigRow? _selectedConfig;
    [ObservableProperty] private string _configNote = "";

    /// <summary>エディタで開く（ShellWindow が受ける）。</summary>
    public event EventHandler<HarnessOpenRequest>? OpenRequested;

    /// <summary>チケットをブラウザペインで開く（ホストが購読する）。</summary>
    public event EventHandler<HarnessTicketOpenRequest>? TicketOpenRequested;
    /// <summary>タブ・期間が変わった（ワークスペースへ保存する合図）。</summary>
    public event EventHandler? StateChanged;

    public bool HasSelectedRun => SelectedRun is not null;

    /// <summary>ペインが初めて見えたとき（またはワークスペースが変わってから最初に見えたとき）に読む。</summary>
    public void EnsureLoaded()
    {
        _loaded = true;
        if (_stale)
            _ = RefreshAsync();
    }

    public HarnessPaneSnapshot Capture() => new()
    {
        SelectedTab = SelectedTab,
        PeriodDays = (SelectedPeriod ?? DefaultPeriod).Days,
    };

    public void Restore(HarnessPaneSnapshot? snapshot)
    {
        _restoring = true;
        try
        {
            SelectedTab = snapshot is { SelectedTab: >= TabAnalysis and <= TabVersions } s ? s.SelectedTab : TabAnalysis;
            SelectedPeriod = PeriodOptions.FirstOrDefault(p => p.Days == snapshot?.PeriodDays) ?? DefaultPeriod;
        }
        finally
        {
            _restoring = false;
        }
        QueueRefresh();
    }

    private bool _restoring;

    private HarnessPeriodOption DefaultPeriod => PeriodOptions[1];

    partial void OnSelectedTabChanged(int value)
    {
        if (!_restoring)
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedPeriodChanged(HarnessPeriodOption? value)
    {
        if (_restoring)
            return;
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (_loaded)
            _ = RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _stale = false;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        StatusText = "記録を読んでいます…";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!cts.IsCancellationRequested && p.Total > 0)
                StatusText = $"記録を読んでいます… {p.Done}/{p.Total}";
        });
        try
        {
            var period = TimeSpan.FromDays((SelectedPeriod ?? DefaultPeriod).Days);
            var snapshot = await _analyzer.AnalyzeAsync(period, progress, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Apply(snapshot);
            StatusText = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"読めませんでした: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    private void Apply(HarnessSnapshot snapshot)
    {
        _snapshot = snapshot;
        var summary = HarnessMetrics.Summarize(snapshot.Runs, snapshot.Verdicts);
        RunsLabel = summary.RunCount.ToString();
        VerdictsLabel = $"{summary.Accepted} ／ {summary.Rejected}";
        TokensPerAcceptedLabel = summary.TokensPerAccepted is { } tpa ? HarnessFormat.Tokens(tpa) : "受け入れなし";
        TotalTokensLabel = HarnessFormat.Tokens(summary.Usage.Total);
        CacheLabel = HarnessFormat.Percent(summary.Usage.CacheReadRatio);
        ErrorRateLabel = HarnessFormat.Percent(summary.ToolErrorRate);
        CostLabel = summary.CostKnownRuns == 0
            ? "不明"
            : $"${summary.CostTotal:0.00}（記録 {summary.CostKnownRuns}/{summary.RunCount} Run）";
        HasVerdicts = summary.Accepted + summary.Rejected > 0;
        var def = snapshot.Definition;
        // 宣言された ID・版があるときだけ出す（無いのに「.harness」「.harness なし」を出しても何も伝えない）。
        HarnessLabel = def.HarnessId is null && def.DeclaredVersion is null
            ? ""
            : $"{def.HarnessId ?? ".harness"}{(def.DeclaredVersion is { } v ? " " + v : "")}";

        EmptyMessage = snapshot.TranscriptDirectories.Count == 0
            ? "このワークスペースで起動した Claude Code の記録が見つかりません（~/.claude/projects）。"
            : snapshot.Runs.Count == 0 ? "この期間の記録がありません。期間を広げてください。" : "";

        ApplyConfig(snapshot);
        ApplyRuns(snapshot);
        ApplyVersions(snapshot);
        ApplyAnalysis(snapshot);
    }

    // ===== 分析タブ =====

    private void ApplyAnalysis(HarnessSnapshot snapshot)
    {
        var runs = snapshot.Runs;
        AnalysisPrompt = runs.Count == 0
            ? ""
            : HarnessAnalysisPrompt.Build(runs, snapshot.Verdicts, snapshot.VersionOf, snapshot.Definition.Components,
                snapshot.Since, DateTimeOffset.Now, DisplayPath);
        if (runs.Count == 0)
        {
            AnalysisSummary = "";
            return;
        }
        var rejected = runs.Count(r => snapshot.Verdicts.TryGetValue(r.Id, out var v) && v.Verdict == HarnessVerdictKind.Rejected);
        var failed = runs.Count(r => r.ToolErrorCount > 0);
        var listed = Math.Min(runs.Count, HarnessAnalysisPrompt.MaxRuns);
        AnalysisSummary = $"{HarnessFormat.Date(snapshot.Since)}〜{HarnessFormat.Date(DateTimeOffset.Now)} の {runs.Count} Run"
            + (listed < runs.Count ? $"（新しい {listed} Run の記録を渡す）" : "の記録を渡す")
            + $" ・ 失敗のある Run {failed}"
            + (rejected > 0 ? $" ・ 差し戻し {rejected}" : "")
            + $" ・ 構成要素 {snapshot.Definition.Components.Count}";
    }

    /// <summary>分析プロンプトをコピーする。Claude Code に貼れば、記録を読んで直し所を表で返す（直すのは了承したものだけ）。</summary>
    [RelayCommand]
    private void CopyAnalysisPrompt()
    {
        if (AnalysisPrompt.Length == 0)
        {
            StatusText = "この期間の記録がありません。";
            return;
        }
        try
        {
            Clipboard.SetText(AnalysisPrompt);
            StatusText = "分析プロンプトをコピーしました。ターミナルの Claude Code に貼ってください。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusText = "クリップボードが使えませんでした。";
        }
    }

    // ===== 構成タブ =====

    private void ApplyConfig(HarnessSnapshot snapshot)
    {
        var previous = SelectedConfig?.Component.Id;
        ConfigRows.Clear();
        // 開きに来ることが多いもの（指示・設定）を先に、ワークスペースのものをユーザー全体のものより先に並べる。
        // Hook は設定ファイルの中身なので行を分けず、その設定ファイルの行に「hooks: …」として添える（同じファイルが3行並ばない）。
        var hooksByFile = snapshot.Definition.Components.Where(c => c.Kind == HarnessComponentKind.Hook)
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(c => HookEvent(c.Name)).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var component in snapshot.Definition.Components
                     .Where(c => c.Kind != HarnessComponentKind.Hook)
                     .OrderBy(c => c.UserScope).ThenBy(c => ConfigRank(c.Kind)))
        {
            var usage = component.Kind == HarnessComponentKind.Settings && hooksByFile.TryGetValue(component.Path, out var hooks)
                ? "hooks: " + string.Join(", ", hooks)
                : UsageOf(component, snapshot.Runs);
            ConfigRows.Add(new HarnessConfigRow(component, usage, DisplayPath(component.Path)));
        }
        ConfigNote = "Claude Code が読む指示・設定・Skill など（設定されているもの）と、期間内の使用（観測）。";
        SelectedConfig = ConfigRows.FirstOrDefault(r => r.Component.Id == previous);
    }

    /// <summary>Hook の構成要素名「Notification（settings.json）」からイベント名だけを取る。</summary>
    private static string HookEvent(string name) => name.IndexOf('（') is var i and > 0 ? name[..i] : name;

    private static int ConfigRank(HarnessComponentKind kind) => kind switch
    {
        HarnessComponentKind.Instructions => 0,
        HarnessComponentKind.Settings => 1,
        HarnessComponentKind.Hook => 2,
        _ => 3,
    };

    /// <summary>構成要素の使用（観測）。Skill／サブエージェントだけは呼ばれた回数が記録に残る。</summary>
    private static string UsageOf(HarnessComponent component, IReadOnlyList<HarnessRun> runs) => component.Kind switch
    {
        HarnessComponentKind.Skill => CountLabel(runs.Sum(r => r.SkillsUsed.Count(s => s.Equals(component.Name, StringComparison.OrdinalIgnoreCase)))),
        HarnessComponentKind.Agent when component.Path.Contains(Path.DirectorySeparatorChar + ".claude" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            => CountLabel(runs.Sum(r => r.AgentsUsed.Count(a => a.Equals(component.Name, StringComparison.OrdinalIgnoreCase)))),
        HarnessComponentKind.Instructions => "毎回読まれる",
        _ => "",
    };

    private static string CountLabel(int count) => count == 0 ? "観測なし" : $"{count}回";

    // ===== 実行タブ =====

    private void ApplyRuns(HarnessSnapshot snapshot)
    {
        var previous = SelectedRun?.Run.Id;
        Runs.Clear();
        foreach (var run in snapshot.Runs)
        {
            snapshot.Verdicts.TryGetValue(run.Id, out var verdict);
            Runs.Add(new HarnessRunRow(run, snapshot.VersionOf(run), verdict,
                WithTicketTitles(HarnessTaskLinks.Links(run, snapshot.Definition.Tasks))));
        }
        SelectedRun = Runs.FirstOrDefault(r => r.Run.Id == previous) ?? Runs.FirstOrDefault();
    }

    /// <summary>チケットだけのタスクに、読み込み済みの一覧から引けた題を添える（引けなければチケットの鍵のまま）。</summary>
    private IReadOnlyList<HarnessTaskLink> WithTicketTitles(IReadOnlyList<HarnessTaskLink> links)
        => _tickets is null
            ? links
            : links.Select(l => l.Task is { IsTicketOnly: true, Ticket: { } ticket } && _tickets.TitleOf(ticket) is { Length: > 0 } title
                ? l with { Task = l.Task with { Title = title } }
                : l).ToList();

    /// <summary>選んだ Run に書かれたチケットをブラウザペインで開く（複数あれば最初のもの）。</summary>
    [RelayCommand]
    private void OpenRunTicket()
    {
        if (SelectedRun?.Tickets.FirstOrDefault() is not { } ticket)
            return;
        if ((_tickets?.UrlOf(ticket) ?? ticket.Url) is { } url)
            TicketOpenRequested?.Invoke(this, new HarnessTicketOpenRequest(url,
                _tickets?.TitleOf(ticket) is { Length: > 0 } title ? $"{ticket.Key}: {title}" : ticket.Key));
        else
            ToastService.Warning($"{ticket.Key} の開き先が分からない（Work Items の組織 URL が未設定）。");
    }

    partial void OnSelectedRunChanged(HarnessRunRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedRun));
        // 前の Run の呼び出しを選んだままにしない（「記録を開く」が別の Run の行を開いてしまう）。
        SelectedCall = null;
        CallDetail = "";
        RunCalls.Clear();
        RunDetail = value?.Detail ?? "";
        if (value is null)
            return;
        foreach (var call in value.Run.ToolCalls)
            RunCalls.Add(new HarnessCallRow(call));
    }

    partial void OnSelectedCallChanged(HarnessCallRow? value)
        => _ = LoadRecordAsync(_callDetailLoad, value?.Call.TranscriptPath, value?.Call.LineNumber ?? 0, text => CallDetail = text);

    [RelayCommand]
    private void OpenRunRecord()
    {
        if (SelectedCall is { } call)
            OpenRequested?.Invoke(this, new HarnessOpenRequest(call.Call.TranscriptPath, call.Call.LineNumber));
        else if (SelectedRun is { } run)
            OpenRequested?.Invoke(this, new HarnessOpenRequest(run.Run.TranscriptPath, 0));
    }

    [RelayCommand]
    private void AcceptRun() => SetVerdict(HarnessVerdictKind.Accepted);

    [RelayCommand]
    private void RejectRun() => SetVerdict(HarnessVerdictKind.Rejected);

    [RelayCommand]
    private void ClearVerdict() => SetVerdict(null);

    private void SetVerdict(HarnessVerdictKind? kind)
    {
        if (SelectedRun?.Run is not { } run || _snapshot is null)
            return;
        if (_workspace.FolderForOrPrimary(run.Cwd) is not { } folder)
        {
            StatusText = "ワークスペースフォルダーが開かれていません。";
            return;
        }
        var verdict = kind is { } k ? new HarnessVerdict(k, DateTimeOffset.Now, null) : null;
        try
        {
            HarnessVerdictStore.Set(HarnessVerdictStore.PathFor(folder), run.Id, verdict);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText = $"判定を保存できませんでした: {ex.Message}";
            return;
        }
        // 判定は指標（受け入れ1件あたり・版の比較）にだけ効くので、記録を読み直さずに差し替える。
        var verdicts = new Dictionary<string, HarnessVerdict>(_snapshot.Verdicts, StringComparer.Ordinal);
        if (verdict is null) verdicts.Remove(run.Id);
        else verdicts[run.Id] = verdict;
        Apply(_snapshot with { Verdicts = verdicts });
    }

    // ===== 構成・版タブ（操作） =====

    [RelayCommand]
    private void OpenConfig(HarnessConfigRow? row)
    {
        if ((row ?? SelectedConfig)?.Component.Path is { } path && File.Exists(path))
            OpenRequested?.Invoke(this, new HarnessOpenRequest(path, 0));
    }

    private void ApplyVersions(HarnessSnapshot snapshot)
    {
        Versions.Clear();
        foreach (var row in HarnessMetrics.CompareVersions(snapshot.Runs, snapshot.Verdicts, snapshot.VersionOf))
            Versions.Add(new HarnessVersionRowViewModel(row));
    }

    // ===== 共通 =====

    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>ワークスペースの中はフォルダー基準、ホームの下（<c>~/.claude</c> 等）は <c>~</c> で縮めて出す。</summary>
    private string DisplayPath(string path)
    {
        if (_workspace.Contains(path))
            return _workspace.ToDisplayPath(path);
        // 区切りは / に揃える（日本語フォントでは \ が ¥ に見えるうえ、ワークスペース側の表示とも揃う）。
        if (UserProfile.Length > 0 && path.StartsWith(UserProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "~" + path[UserProfile.Length..].Replace('\\', '/');
        return path;
    }

    /// <summary>記録の1行を整形して出す（数 MB の記録をエディタで開かなくても中身が見えるように）。</summary>
    private async Task LoadRecordAsync(DetailSlot slot, string? path, int line, Action<string> apply)
    {
        slot.Cts?.Cancel();
        if (path is null || line <= 0)
        {
            apply("");
            return;
        }
        var cts = slot.Cts = new CancellationTokenSource();
        string text;
        try
        {
            text = await Task.Run(() => ReadRecord(path, line), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!cts.IsCancellationRequested)
            apply(text);
    }

    private static string ReadRecord(string path, int line)
    {
        try
        {
            var raw = ClaudeTranscriptLocator.ReadLines(path).Skip(line - 1).FirstOrDefault();
            if (raw is null)
                return "（記録の行が見つかりません。記録が書き換わった可能性があります）";
            string pretty;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                pretty = JsonSerializer.Serialize(doc.RootElement, PrettyJson);
            }
            catch (JsonException)
            {
                pretty = raw;
            }
            return pretty.Length <= DetailMaxChars ? pretty : pretty[..DetailMaxChars] + "\n…（以下略）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（記録を読めませんでした: {ex.Message}）";
        }
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class DetailSlot
    {
        public CancellationTokenSource? Cts;
    }
}

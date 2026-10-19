using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>このタブで実行し終えたコマンド1回分（§39.5）。</summary>
public sealed record TerminalSupportRunRow(TerminalCommandOutput Output, string Title, string Badge, bool Failed, string Age, bool CanCompare);

/// <summary>TUI の操作キー1つ（§39.3）。</summary>
public sealed record TerminalSupportKeyRow(string Keys, string Action);

/// <summary>頭の1行の調子：人間の番か、Claude が動いているか。</summary>
public enum TerminalSupportNowTone { Human, Working, Idle }

/// <summary>
/// ターミナルサポートペイン（<see cref="PaneKind.TerminalSupport"/>、§39）。いま前に出ているターミナルのタブに
/// 追従し、そこで動いているものの中身を<b>ターミナルとは別の見せ方</b>で出す横の面——EditorSupport が .md を
/// プレビューにするのと同じ型。新しい情報を足すのではなく、同じ中身を描き直す。
/// <list type="bullet">
/// <item>Claude Code：会話を<b>読み物</b>として（頼みごとを見出しに、返事を Markdown として描画した1本の文書、
///   <see cref="ClaudeConversationDocument"/>）。ここは本文の HTML を作るまでで、描くのは View 側の WebView。</item>
/// <item>そのほかのプログラム（TUI など）：コマンド行・経過時間・知っているプログラムなら操作キー</item>
/// <item>シェル：このタブで実行し終えたコマンドと、その出力（開く・前回と比べる）</item>
/// </list>
/// 状態の組み立ては ShellWindow（タブ・タイトル・OSC 133 を持っている側）が行い、ここは受け取って整えるだけ。
/// Claude の記録だけはここで裏読みする（数 MB になり得るので UI スレッドでは読まない）。
/// </summary>
public sealed partial class TerminalSupportViewModel : ObservableObject
{
    private Guid? _tabId;
    private string? _transcriptPath;
    private (long Length, DateTime WriteUtc)? _digestStamp;
    private int _digestGeneration;
    private Func<string, string> _displayPath = path => path;
    private Func<string, bool> _inWorkspace = _ => true;
    private string? _agentTitle;
    private ClaudeAgentState? _agentState;
    private ClaudeSessionDigest? _digest;
    private int _bodyGeneration;

    [ObservableProperty] private bool _hasTab;
    [ObservableProperty] private bool _isClaude;
    [ObservableProperty] private bool _isProgram;
    [ObservableProperty] private bool _isShell;

    [ObservableProperty] private string _tabName = "";
    [ObservableProperty] private string _workingDirectory = "";

    // ── プログラム（TUI） ──
    [ObservableProperty] private string _programName = "";
    [ObservableProperty] private string _programDescription = "";
    [ObservableProperty] private string _commandLine = "";
    [ObservableProperty] private string _elapsed = "";
    private DateTime? _runningSinceUtc;

    // ── Claude Code ──
    [ObservableProperty] private string _claudeTitle = "";
    [ObservableProperty] private string _claudeFacts = "";
    [ObservableProperty] private string _claudeNotice = "";
    [ObservableProperty] private bool _hasClaudeSession;
    [ObservableProperty] private TerminalSupportNowTone _nowTone = TerminalSupportNowTone.Idle;
    [ObservableProperty] private string _nowHeadline = "";
    /// <summary>会話を読み物にした本文（body の中身の HTML）。記録が無ければ空。</summary>
    [ObservableProperty] private string _conversationBody = "";

    public ObservableCollection<TerminalSupportKeyRow> Keys { get; } = [];
    public ObservableCollection<TerminalSupportRunRow> Runs { get; } = [];

    [ObservableProperty] private bool _hasRuns;
    [ObservableProperty] private bool _hasKeys;

    /// <summary>ファイルをエディタで開いてほしい。</summary>
    public event EventHandler<string>? FileOpenRequested;
    /// <summary>コマンドの出力をエディタで開いてほしい。</summary>
    public event EventHandler<TerminalCommandOutput>? OutputOpenRequested;
    /// <summary>コマンドの出力を前回と比べてほしい。</summary>
    public event EventHandler<TerminalCommandOutput>? OutputCompareRequested;
    /// <summary>コマンドをプロンプトへ入れてほしい（Enter は送らない、§24.3 の「送る」）。</summary>
    public event EventHandler<string>? CommandInsertRequested;
    /// <summary>追従しているタブへ行きたい。</summary>
    public event EventHandler? GoToTerminalRequested;

    /// <summary>ファイルの見せ方（マルチルートでは先頭にフォルダー名、§32.10）。</summary>
    internal void SetDisplayPath(Func<string, string> displayPath) => _displayPath = displayPath;

    /// <summary>ワークスペースの中か（<c>workspace.Contains</c>、マルチルート）。外で書き換えたファイル——Claude の
    /// 一時ファイル・作業メモ——は数だけ出す。</summary>
    internal void SetWorkspaceFilter(Func<string, bool> inWorkspace) => _inWorkspace = inWorkspace;

    /// <summary>追従するタブの様子を受け取る。<paramref name="runs"/> は覚えているコマンドの出力（全タブぶん、新しい順）。
    /// <paramref name="comparable"/> は前回の出力も持っているか。</summary>
    internal void Update(
        TerminalSupportSubject? subject,
        IReadOnlyList<TerminalCommandOutput> runs,
        Func<TerminalCommandOutput, bool> comparable,
        DateTime nowUtc)
    {
        HasTab = subject is not null;
        var mode = subject?.Mode ?? TerminalSupportMode.NoTab;
        IsClaude = mode == TerminalSupportMode.ClaudeCode;
        IsProgram = mode == TerminalSupportMode.Program;
        IsShell = mode == TerminalSupportMode.Shell;
        if (subject is null)
        {
            _tabId = null;
            ClearClaude();
            return;
        }
        if (_tabId != subject.TabId)
        {
            _tabId = subject.TabId;
            ClearClaude();
        }
        TabName = subject.TabName;
        WorkingDirectory = subject.WorkingDirectory ?? "";

        var program = TerminalProgramCatalog.Match(subject.RunningCommand);
        CommandLine = subject.RunningCommand ?? "";
        ProgramName = program?.Name ?? FirstWord(subject.RunningCommand);
        ProgramDescription = program?.Description ?? "";
        _runningSinceUtc = subject.RunningSinceUtc;
        // 操作キーは TUI の面だけ。Claude の面は会話を読む場所で、キーの案内は Claude Code 自身が出している。
        ReplaceIfChanged(Keys, (mode == TerminalSupportMode.Program ? program?.Keys ?? [] : [])
            .Select(k => new TerminalSupportKeyRow(k.Keys, k.Action)).ToList());
        HasKeys = Keys.Count > 0;

        var previousState = _agentState;
        _agentState = subject.Agent?.State;
        _agentTitle = subject.Agent?.Title ?? _agentTitle;
        if (IsClaude)
        {
            UpdateClaudeTitle();
            UpdateNow();
            if (previousState != _agentState)
                _ = RebuildConversationAsync();   // 作業中かどうかで最後の本文の扱い（途中／返事）が変わる
        }

        var tabRuns = runs.Where(r => r.TabId == subject.TabId).ToList();
        ReplaceIfChanged(Runs, tabRuns.Select(r => new TerminalSupportRunRow(
            r, RecentTerminalCommands.Title(r.Command), RecentTerminalCommands.Badge(r.ExitCode),
            r.ExitCode is { } code && code != 0, Age(nowUtc - r.FinishedUtc), comparable(r))).ToList());
        HasRuns = Runs.Count > 0;
        Tick(nowUtc);
    }

    /// <summary>経過時間を書き直す（ShellWindow の1秒時計から。ペインが見えている間だけ呼ばれる）。</summary>
    internal void Tick(DateTime nowUtc)
        => Elapsed = _runningSinceUtc is { } since ? FormatElapsed(nowUtc - since) : "";

    /// <summary>Claude の記録を読み直す。長さと更新時刻が前回と同じなら読まない（作業中は数秒おきに呼ばれる）。
    /// <paramref name="transcriptPath"/> が null なら、このタブのセッションを確かめられなかった（理由は
    /// <paramref name="unresolvedReason"/>）——そのときは何も描かない（別の会話を出すより、出さないほうがよい）。</summary>
    internal async Task RefreshClaudeAsync(string? transcriptPath, string? unresolvedReason)
    {
        if (transcriptPath is null)
        {
            _transcriptPath = null;
            _digestStamp = null;
            await ApplyDigest(null);
            ClaudeNotice = unresolvedReason ?? "このタブの Claude Code のセッションが見つかりません。";
            return;
        }
        (long, DateTime)? stamp;
        try
        {
            var info = new FileInfo(transcriptPath);
            stamp = info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stamp = null; }
        if (stamp is null)
            return;
        if (_transcriptPath == transcriptPath && _digestStamp == stamp)
            return;
        _transcriptPath = transcriptPath;
        _digestStamp = stamp;

        var generation = ++_digestGeneration;
        ClaudeSessionDigest? digest;
        try { digest = await Task.Run(() => ClaudeSessionDigest.Read(transcriptPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { digest = null; }
        if (generation != _digestGeneration || _transcriptPath != transcriptPath)
            return;   // 読んでいる間にタブが替わった・もっと新しい読み直しが走った
        await ApplyDigest(digest);
    }

    internal Task ApplyDigest(ClaudeSessionDigest? digest)
    {
        _digest = digest;
        HasClaudeSession = digest is not null;
        ClaudeNotice = digest is null ? "セッションの記録を読めませんでした。" : "";
        UpdateClaudeTitle();
        ClaudeFacts = digest is null ? "" : string.Join(" · ", new[]
        {
            digest.ContextTokens is { } tokens ? $"⧉ {ClaudeSessionCatalog.FormatTokens(tokens)}" : null,
            digest.Turns.Count > 0 ? $"{digest.Turns.Count} ターン" : null,
        }.Where(s => s is not null));
        return RebuildConversationAsync();
    }

    /// <summary>本文を組み直す。全ターンの Markdown を描くので裏のスレッドで（作業中は数秒おきに来る）。
    /// 組んでいる間にもっと新しい組み直しが走ったら、古い結果は捨てる。</summary>
    private async Task RebuildConversationAsync()
    {
        var generation = ++_bodyGeneration;
        if (_digest is not { } digest)
        {
            ConversationBody = "";
            return;
        }
        var (inWorkspace, displayPath, working) = (_inWorkspace, _displayPath, _agentState == ClaudeAgentState.Working);
        var body = await Task.Run(() => ClaudeConversationDocument.BuildBody(digest, inWorkspace, displayPath, working));
        if (generation == _bodyGeneration)
            ConversationBody = body;
    }

    /// <summary>頭の1行。自分の番か（承認待ち・ターンの終わり）、Claude が動いているか。</summary>
    private void UpdateNow()
    {
        (NowTone, NowHeadline) = _agentState switch
        {
            ClaudeAgentState.NeedsApproval => (TerminalSupportNowTone.Human, "● 承認待ち"),
            ClaudeAgentState.TurnEnded => (TerminalSupportNowTone.Human, "✳ あなたの番"),
            ClaudeAgentState.Working => (TerminalSupportNowTone.Working, "◐ 作業中"),
            ClaudeAgentState.Waiting => (TerminalSupportNowTone.Idle, "入力待ち"),
            _ => (TerminalSupportNowTone.Idle, "起動中"),
        };
    }

    /// <summary>題の選び方：Claude がタイトルに出している題が具体的ならそれ（いまの作業に追従する）、
    /// 汎用の「Claude Code」なら記録の ai-title、どちらも無ければタブ名。</summary>
    private void UpdateClaudeTitle()
    {
        var live = _agentTitle is { Length: > 0 } t && !t.Equals(GenericClaudeTitle, StringComparison.OrdinalIgnoreCase) ? t : null;
        ClaudeTitle = live ?? _digest?.Title ?? _agentTitle ?? TabName;
    }

    /// <summary>題の無いときに Claude Code がタイトルへ出す名前。</summary>
    internal const string GenericClaudeTitle = "Claude Code";

    private void ClearClaude()
    {
        _transcriptPath = null;
        _digestStamp = null;
        _digest = null;
        _digestGeneration++;
        _bodyGeneration++;
        _agentTitle = null;
        _agentState = null;
        ClaudeTitle = ClaudeFacts = ClaudeNotice = NowHeadline = ConversationBody = "";
        NowTone = TerminalSupportNowTone.Idle;
        HasClaudeSession = false;
    }

    [RelayCommand]
    private void OpenTranscript()
    {
        if (_transcriptPath is { } path) FileOpenRequested?.Invoke(this, path);
    }

    [RelayCommand]
    private void OpenOutput(TerminalSupportRunRow? row)
    {
        if (row is not null) OutputOpenRequested?.Invoke(this, row.Output);
    }

    [RelayCommand]
    private void CompareOutput(TerminalSupportRunRow? row)
    {
        if (row is { CanCompare: true }) OutputCompareRequested?.Invoke(this, row.Output);
    }

    [RelayCommand]
    private void InsertCommand(TerminalSupportRunRow? row)
    {
        if (row is not null) CommandInsertRequested?.Invoke(this, row.Output.Command);
    }

    [RelayCommand]
    private void GoToTerminal() => GoToTerminalRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>中身が同じなら入れ替えない（更新でビュー状態を壊さない）。</summary>
    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.Count == items.Count && target.SequenceEqual(items))
            return;
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static string FirstWord(string? commandLine)
        => TerminalProgramCatalog.Tokenize(commandLine).FirstOrDefault() ?? "";

    internal static string Age(TimeSpan age)
        => age.TotalSeconds < 60 ? "たった今"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} 分前"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} 時間前"
            : $"{(int)age.TotalDays} 日前";

    internal static string FormatElapsed(TimeSpan elapsed)
        => elapsed < TimeSpan.Zero ? ""
            : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
}

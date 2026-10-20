using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Tasks;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>このタブで実行し終えたコマンド1回分（§39.5）。
/// <paramref name="IsSelected"/> は下に出力を見せている回か（§39.5.1）。</summary>
public sealed record TerminalSupportRunRow(TerminalCommandOutput Output, string Title, string Badge, bool Failed, string Age, bool CanCompare,
    bool IsSelected = false);

/// <summary>TUI の操作キー1つ（§39.3）。</summary>
public sealed record TerminalSupportKeyRow(string Keys, string Action);

/// <summary>
/// Claude の会話のツリーの1行（§39.9）。<see cref="Id"/> は記録の位置から作った節の id で、記録が書き足されても・トグルを
/// 替えても同じ行は同じインスタンスのまま——開閉と選択を持ち越す（更新でビュー状態を壊さない）。
/// </summary>
public sealed partial class ClaudeTreeItem(string id) : ObservableObject
{
    public string Id { get; } = id;
    /// <summary>節の種類の名前（<see cref="ClaudeTreeKind"/>。印の色・等幅などの見た目を XAML で振り分ける）。</summary>
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _hint = "";
    /// <summary>ターン（頼みごと）の行。太字にする。</summary>
    [ObservableProperty] private bool _isTurn;
    /// <summary>失敗した操作の行（題を赤く）。</summary>
    [ObservableProperty] private bool _failed;
    /// <summary>中に含む失敗（ターン・エージェント、「✗N」）。題ではなくこれだけを赤く——束ねる行まで赤いと騒がしい。</summary>
    [ObservableProperty] private string _failBadge = "";
    [ObservableProperty] private bool _working;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    public ObservableCollection<ClaudeTreeItem> Children { get; } = [];
    internal ClaudeTreeNode? Node { get; set; }
}

/// <summary>頭の1行の調子：人間の番か、Claude が動いているか。</summary>
public enum TerminalSupportNowTone { Human, Working, Idle }

/// <summary>
/// ターミナルサポートペイン（<see cref="PaneKind.TerminalSupport"/>、§39）。いま前に出ているターミナルのタブに
/// 追従し、そこで動いているものの中身を<b>ターミナルとは別の見せ方</b>で出す横の面——EditorSupport が .md を
/// プレビューにするのと同じ型。新しい情報を足すのではなく、同じ中身を描き直す。
/// <list type="bullet">
/// <item>Claude Code：会話を<b>ツリー</b>に（ターン → 返事の節・書き換えたファイル・サブエージェントとその操作・コマンド・失敗、
///   種類はトグルで選び、文字で絞り込む、<see cref="ClaudeSessionTree"/>、§39.9）、選んだ節<b>だけ</b>を読み物として
///   （<see cref="ClaudeConversationDocument.BuildNodeBody"/>）。ここは本文の HTML を作るまでで、描くのは View 側の WebView。</item>
/// <item>そのほかのプログラム（TUI など）：コマンド行・経過時間・知っているプログラムなら操作キー</item>
/// <item>シェル：このタブで実行し終えたコマンドと、選んだ回の出力を<b>形に合わせて見せ直したもの</b>（表・JSON のツリー・
///   枝の図・テストとビルドの成否と file:line、<see cref="TerminalOutputPresenter"/>、§39.5.1）</item>
/// </list>
/// 状態の組み立ては ShellWindow（タブ・タイトル・OSC 133 を持っている側）が行い、ここは受け取って整えるだけ。
/// Claude の記録だけはここで裏読みする（数 MB になり得るので UI スレッドでは読まない）。
/// </summary>
public sealed partial class TerminalSupportViewModel : ObservableObject
{
    private Guid? _tabId;
    private string? _transcriptPath;
    private ClaudeSessionFollower? _follower;
    private int _appliedVersion = -1;
    private int _digestGeneration;
    private Func<string, string> _displayPath = path => path;
    private Func<string, bool> _inWorkspace = _ => true;
    private string? _agentTitle;
    private ClaudeAgentState? _agentState;
    private ClaudeSessionDigest? _digest;
    private int _bodyGeneration;
    private ClaudeConversationDocument.RenderCache _renderCache = new();

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
    /// <summary>ツリーで選んだ節を読み物にした本文（body の中身の HTML）。記録が無ければ空。</summary>
    [ObservableProperty] private string _conversationBody = "";
    /// <summary><see cref="ConversationBody"/> がどの節のものか。替わったらページは先頭から見せる。</summary>
    [ObservableProperty] private string _conversationNodeId = "";
    /// <summary>「会話をすべて」を見せている（ページはターンに付いていく、§39.4）。</summary>
    [ObservableProperty] private bool _showsWholeConversation;

    // ── Claude：ツリーとトグル（§39.9） ──
    public ObservableCollection<ClaudeTreeItem> Tree { get; } = [];
    [ObservableProperty] private bool _showReplies = true;
    [ObservableProperty] private bool _showChanges = true;
    [ObservableProperty] private bool _showAgents = true;
    [ObservableProperty] private bool _showCommands;
    [ObservableProperty] private bool _showFailures;
    [ObservableProperty] private bool _showNotes;
    /// <summary>ツリーの絞り込み（頼みごと・返事の本文・ファイル名・コマンド・エージェントへの頼みごとに当てる）。</summary>
    [ObservableProperty] private string _treeFilter = "";
    /// <summary>種類ごとの件数（チップに添える。トグルに関わらずセッション全体の数）。</summary>
    [ObservableProperty] private int _replyCount;
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private int _agentCount;
    [ObservableProperty] private int _commandCount;
    [ObservableProperty] private int _failureCount;
    [ObservableProperty] private int _noteCount;
    private readonly Dictionary<string, ClaudeTreeItem> _treeItems = new(StringComparer.Ordinal);
    private string? _selectedNodeId;
    private ClaudeTreeNode? _selectedNode;
    /// <summary>最新のターン（の中）を見ている——新しいターンが始まったらその頭へ付いていく。古いターンを読み返しているときは動かさない。</summary>
    private bool _followLatestTurn = true;
    private string? _renderedKey;

    partial void OnShowRepliesChanged(bool value) => _ = RebuildConversationAsync();
    partial void OnShowChangesChanged(bool value) => _ = RebuildConversationAsync();
    partial void OnShowAgentsChanged(bool value) => _ = RebuildConversationAsync();
    partial void OnShowCommandsChanged(bool value) => _ = RebuildConversationAsync();
    partial void OnShowFailuresChanged(bool value) => _ = RebuildConversationAsync();
    partial void OnShowNotesChanged(bool value) => _ = RebuildConversationAsync();
    partial void OnTreeFilterChanged(string value) => _ = RebuildConversationAsync();

    private ClaudeTreeFilter CurrentFilter
        => new(ShowReplies, ShowChanges, ShowAgents, ShowCommands, ShowFailures, ShowNotes, TreeFilter ?? "");

    public ObservableCollection<TerminalSupportKeyRow> Keys { get; } = [];
    public ObservableCollection<TerminalSupportRunRow> Runs { get; } = [];

    [ObservableProperty] private bool _hasRuns;
    [ObservableProperty] private bool _hasKeys;

    // ── シェル：選んだ回の出力の見せ直し（§39.5.1） ──
    /// <summary>見せている回。新しい回が来たら付いていく——古い回を選んで読んでいる間は動かさない。</summary>
    [ObservableProperty] private TerminalSupportRunRow? _selectedRun;
    [ObservableProperty] private string _outputLabel = "";
    /// <summary>見せ直した出力（body の中身の HTML）。描くのは View 側の WebView。</summary>
    [ObservableProperty] private string _outputBody = "";
    private TerminalCommandOutput? _presentedOutput;
    private bool _followNewestRun = true;
    private int _outputGeneration;
    private Func<TerminalCommandOutput, TerminalOutputMatching> _matching =
        output => new TerminalOutputMatching(new ProblemMatchContext(output.WorkingDirectory ?? "", output.WorkingDirectory ?? ""));

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

    /// <summary>出力の診断の読み方（相対パスの基準・tasks.json のタスクならその matcher）。</summary>
    internal void SetOutputMatching(Func<TerminalCommandOutput, TerminalOutputMatching> matching) => _matching = matching;

    /// <summary>見せている回を読み直す（読み方が後から分かった——タスクの終わりは出力の切り出しより遅れて届く）。</summary>
    internal void RepresentOutput()
    {
        if (SelectedRun?.Output is { } output)
            _ = PresentOutputAsync(output);
    }

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

        _tabRuns = runs.Where(r => r.TabId == subject.TabId).ToList();
        _comparable = comparable;
        _runsNowUtc = nowUtc;
        // 選び直す：最新を見ていたら新しい最新へ、古い回を選んでいたらその回のまま、消えていたら最新へ。
        var keep = _followNewestRun ? null : _tabRuns.FirstOrDefault(r => ReferenceEquals(r, SelectedRun?.Output));
        if (keep is null)
            _followNewestRun = true;
        RebuildRuns(keep ?? _tabRuns.FirstOrDefault());
        Tick(nowUtc);
    }

    private IReadOnlyList<TerminalCommandOutput> _tabRuns = [];
    private Func<TerminalCommandOutput, bool> _comparable = _ => false;
    private DateTime _runsNowUtc;

    /// <summary>一覧を組み直し、<paramref name="selected"/> を選んだ回にする（行は値なので、選んだ印も行に入れる）。</summary>
    private void RebuildRuns(TerminalCommandOutput? selected)
    {
        ReplaceIfChanged(Runs, _tabRuns.Select(r => new TerminalSupportRunRow(
            r, RecentTerminalCommands.Title(r.Command), RecentTerminalCommands.Badge(r.ExitCode),
            r.ExitCode is { } code && code != 0, Age(_runsNowUtc - r.FinishedUtc), _comparable(r),
            ReferenceEquals(r, selected))).ToList());
        HasRuns = Runs.Count > 0;
        SelectedRun = Runs.FirstOrDefault(r => r.IsSelected);
    }

    partial void OnSelectedRunChanged(TerminalSupportRunRow? value)
    {
        if (ReferenceEquals(value?.Output, _presentedOutput))
            return;
        _presentedOutput = value?.Output;
        _ = PresentOutputAsync(value?.Output);
    }

    /// <summary>選んだ回の出力を形に合わせて見せ直す。出力は最大 1MB あるので裏のスレッドで。</summary>
    private async Task PresentOutputAsync(TerminalCommandOutput? output)
    {
        var generation = ++_outputGeneration;
        if (output is null)
        {
            OutputLabel = OutputBody = "";
            return;
        }
        var matching = _matching(output);
        var presentation = await Task.Run(() => TerminalOutputPresenter.Present(output, matching));
        if (generation != _outputGeneration)
            return;
        OutputLabel = presentation.Label;
        OutputBody = presentation.Body;
    }

    /// <summary>経過時間を書き直す（ShellWindow の1秒時計から。ペインが見えている間だけ呼ばれる）。</summary>
    internal void Tick(DateTime nowUtc)
        => Elapsed = _runningSinceUtc is { } since ? FormatElapsed(nowUtc - since) : "";

    /// <summary>Claude の記録を読み直す。読むのは前回からの追記分だけ（<see cref="ClaudeSessionFollower"/>、§39.8）で、
    /// 何も書き足されていなければ描き直さない（作業中は数秒おきに呼ばれる）。
    /// <paramref name="transcriptPath"/> が null なら、このタブのセッションを確かめられなかった（理由は
    /// <paramref name="unresolvedReason"/>）——そのときは何も描かない（別の会話を出すより、出さないほうがよい）。</summary>
    internal async Task RefreshClaudeAsync(string? transcriptPath, string? unresolvedReason)
    {
        if (transcriptPath is null)
        {
            _transcriptPath = null;
            _follower = null;
            _appliedVersion = -1;
            await ApplyDigest(null);
            ClaudeNotice = unresolvedReason ?? "このタブの Claude Code のセッションが見つかりません。";
            return;
        }
        if (_follower is null || !string.Equals(_follower.TranscriptPath, transcriptPath, StringComparison.OrdinalIgnoreCase))
        {
            _follower = new ClaudeSessionFollower(transcriptPath);
            _appliedVersion = -1;
        }
        _transcriptPath = transcriptPath;
        var follower = _follower;

        var generation = ++_digestGeneration;
        (int Version, ClaudeSessionDigest? Digest) result;
        try { result = await Task.Run(follower.Refresh); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        // 読んでいる間にタブが替わった・もっと新しい読み直しが走った。捨てても取りこぼさない——次の読み直しは
        // 版がこちらの描いたものと違えば（追記が無くても）描く。
        if (generation != _digestGeneration || !ReferenceEquals(_follower, follower) || result.Version == _appliedVersion)
            return;
        _appliedVersion = result.Version;
        await ApplyDigest(result.Digest);
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

    /// <summary>ツリーを組み直し、選んでいる節の本文を描き直す（中身が変わったときだけ）。</summary>
    private Task RebuildConversationAsync()
    {
        if (_digest is not { } digest)
        {
            ++_bodyGeneration;
            Tree.Clear();
            _treeItems.Clear();
            _selectedNodeId = null;
            _selectedNode = null;
            _renderedKey = null;
            ConversationBody = "";
            return Task.CompletedTask;
        }
        var counts = ClaudeSessionTree.Count(digest, _inWorkspace);
        (ReplyCount, ChangeCount, AgentCount, CommandCount, FailureCount, NoteCount)
            = (counts.Replies, counts.Changes, counts.Agents, counts.Commands, counts.Failures, counts.Notes);
        var filter = CurrentFilter;
        var nodes = ClaudeSessionTree.Build(digest, filter, _inWorkspace, _displayPath, _agentState == ClaudeAgentState.Working);
        var searching = filter.Text.Trim().Length > 0;
        var latestTurnId = digest.Turns.Count > 0 ? "t" + digest.Turns[^1].PromptLine : null;
        var byId = new Dictionary<string, ClaudeTreeNode>(StringComparer.Ordinal);
        SyncTree(Tree, nodes, byId, latestTurnId, searching);

        // 選び直す：何も選んでいない・最新のターンに付いていく途中なら、最新のターンへ。
        if (latestTurnId is not null && (_selectedNodeId is null
                || (_followLatestTurn && !InTurn(_selectedNodeId, latestTurnId))))
            _selectedNodeId = latestTurnId;
        // 選んだ節がトグル・絞り込みで隠れても、見せている中身はそのまま（隠れる前の節を描き続ける）。
        if (_selectedNodeId is { } selectedId && byId.TryGetValue(selectedId, out var node))
            _selectedNode = node;
        foreach (var item in _treeItems.Values)
            item.IsSelected = item.Id == _selectedNodeId && byId.ContainsKey(item.Id);
        return RenderSelectedAsync();
    }

    /// <summary>ツリーの行を節に合わせる。同じ id の行は使い回す（開閉・選択がそのまま残る）。</summary>
    private void SyncTree(
        ObservableCollection<ClaudeTreeItem> target, IReadOnlyList<ClaudeTreeNode> nodes, Dictionary<string, ClaudeTreeNode> byId,
        string? latestTurnId, bool searching)
    {
        var items = new List<ClaudeTreeItem>(nodes.Count);
        foreach (var node in nodes)
        {
            byId[node.Id] = node;
            if (!_treeItems.TryGetValue(node.Id, out var item))
            {
                // 最新のターンだけ開いて生やす。ほかは畳んでおき、開閉は人に任せる。
                item = new ClaudeTreeItem(node.Id) { IsExpanded = node.Id == latestTurnId };
                _treeItems[node.Id] = item;
            }
            item.Node = node;
            item.Kind = node.Kind.ToString();
            item.Icon = Icon(node.Kind);
            item.Label = node.Label;
            item.Hint = node.Hint;
            item.IsTurn = node.Kind == ClaudeTreeKind.Turn;
            item.Failed = node.Failed && node.Kind is ClaudeTreeKind.Command or ClaudeTreeKind.Failure;
            item.FailBadge = node.FailureCount > 0 ? $"✗{node.FailureCount}" : "";
            item.Working = node.Working && node.Kind is ClaudeTreeKind.Turn or ClaudeTreeKind.Agent;   // 点は束ねる行にだけ
            if (searching && node.Children.Count > 0)
                item.IsExpanded = true;   // 絞り込み中は当たったところまで開く
            SyncTree(item.Children, node.Children, byId, latestTurnId, searching);
            items.Add(item);
        }
        ReplaceIfChanged(target, items);
    }

    private static string Icon(ClaudeTreeKind kind) => kind switch
    {
        ClaudeTreeKind.Conversation => "≡",
        ClaudeTreeKind.Turn => "",   // 開閉の矢印がターンの印を兼ねる
        ClaudeTreeKind.Section => "§",
        ClaudeTreeKind.Note => "…",
        ClaudeTreeKind.File => "✎",
        ClaudeTreeKind.Command => "$",
        ClaudeTreeKind.Agent => "◆",
        ClaudeTreeKind.Failure => "✗",
        _ => "",
    };

    private static bool InTurn(string nodeId, string turnId)
        => nodeId == turnId || nodeId.StartsWith(turnId + "/", StringComparison.Ordinal);

    /// <summary>ツリーの行を選んだ（View の SelectedItemChanged から。こちらが選んだときも来る）。</summary>
    internal Task SelectTreeItemAsync(ClaudeTreeItem? item)
    {
        if (item?.Node is not { } node || item.Id == _selectedNodeId)
            return Task.CompletedTask;
        _selectedNodeId = item.Id;
        _selectedNode = node;
        var latestTurnId = _digest is { Turns.Count: > 0 } d ? "t" + d.Turns[^1].PromptLine : null;
        // 最新のターン（の中）を選んだなら、以後も新しいターンへ付いていく。「会話をすべて」はページ自身が付いていく。
        _followLatestTurn = latestTurnId is not null && InTurn(item.Id, latestTurnId);
        return RenderSelectedAsync();
    }

    /// <summary>選んでいる節の本文を描く。記録を読むことがあるので裏のスレッドで。中身の指紋が同じなら描き直さない。
    /// 組んでいる間にもっと新しい組み直しが走ったら、古い結果は捨てる。</summary>
    private async Task RenderSelectedAsync()
    {
        if (_digest is not { } digest || _selectedNode is not { } node)
        {
            ++_bodyGeneration;
            _renderedKey = null;
            // 頼みごとがまだ無いセッションは、その旨だけ（会話をすべての空の形）。
            ConversationBody = _digest is { } empty ? ClaudeConversationDocument.BuildBody(empty, _inWorkspace, _displayPath, false) : "";
            return;
        }
        var working = _agentState == ClaudeAgentState.Working;
        var key = node.Id + "#" + node.Key + "#" + working;
        if (key == _renderedKey)
            return;
        var generation = ++_bodyGeneration;
        var matching = _matching;
        var context = new ClaudeConversationDocument.NodeContext(
            _inWorkspace, _displayPath, working, WorkingDirectory.Length > 0 ? WorkingDirectory : null,
            ClaudeTranscriptLookup.ReadResult, ClaudeTranscriptLookup.ReadInput,
            output => TerminalOutputPresenter.Present(output, matching(output)), _renderCache);
        var body = await Task.Run(() => ClaudeConversationDocument.BuildNodeBody(node, digest, context));
        if (generation != _bodyGeneration)
            return;
        _renderedKey = key;
        ShowsWholeConversation = node.Kind == ClaudeTreeKind.Conversation;
        ConversationNodeId = node.Id;
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
        _follower = null;
        _appliedVersion = -1;
        _digest = null;
        _renderCache = new();
        _digestGeneration++;
        _bodyGeneration++;
        Tree.Clear();
        _treeItems.Clear();
        _selectedNodeId = null;
        _selectedNode = null;
        _renderedKey = null;
        _followLatestTurn = true;
        ConversationNodeId = "";
        ShowsWholeConversation = false;
        _agentTitle = null;
        _agentState = null;
        ClaudeTitle = ClaudeFacts = ClaudeNotice = NowHeadline = ConversationBody = "";
        NowTone = TerminalSupportNowTone.Idle;
        HasClaudeSession = false;
    }

    [RelayCommand]
    private void ClearTreeFilter() => TreeFilter = "";

    [RelayCommand]
    private void OpenTranscript()
    {
        if (_transcriptPath is { } path) FileOpenRequested?.Invoke(this, path);
    }

    /// <summary>一覧の行を押した：その回を見せる。最新の行なら、以後も最新に付いていく。</summary>
    [RelayCommand]
    private void SelectRun(TerminalSupportRunRow? row)
    {
        if (row is null)
            return;
        _followNewestRun = _tabRuns.Count > 0 && ReferenceEquals(row.Output, _tabRuns[0]);
        RebuildRuns(row.Output);
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

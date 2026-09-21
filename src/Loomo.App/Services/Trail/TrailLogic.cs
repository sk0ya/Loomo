namespace sk0ya.Loomo.App.Services;

/// <summary>ペインから記録する軌跡の基本情報。</summary>
internal readonly record struct TrailPaneRecordTarget(
    TrailEntryKind Kind, string Target, string Label, int Line = -1, int Column = -1);
internal readonly record struct TrailLayoutState(
    string Key, DisplayMode Mode, PaneKind? StagePane, string? PaneLayout);
internal readonly record struct TrailLayoutKeyTransition(string Key, bool ShouldRecord);

/// <summary>軌跡の分類・表示・変更検出に使うUI非依存ロジック。</summary>
public static class TrailLogic
{
    private static readonly JsonSerializerOptions LayoutJson = new();

    internal static string? SerializeLayout(PaneNodeSnapshot? snapshot)
        => snapshot is null ? null : JsonSerializer.Serialize(snapshot, LayoutJson);

    internal static TrailLayoutState CreateLayoutState(
        DisplayMode mode, PaneKind? stagePane, PaneNodeSnapshot? snapshot, string? dock)
    {
        var paneLayout = SerializeLayout(snapshot);
        return new(LayoutKey(mode, stagePane, snapshot, dock), mode, stagePane, paneLayout);
    }

    internal static TrailLayoutKeyTransition AdvanceLayoutKey(
        string? previousKey, string currentKey, bool suppressed)
        => new(currentKey,
            previousKey is not null
            && !string.Equals(previousKey, currentKey, StringComparison.Ordinal)
            && !suppressed);

    internal static bool TryDeserializeLayout(string? json, out PaneNodeSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            snapshot = JsonSerializer.Deserialize<PaneNodeSnapshot>(json, LayoutJson);
            return snapshot is not null;
        }
        catch
        {
            return false;
        }
    }

    internal static string LayoutChangeLabel(DisplayMode mode, PaneKind? stagePane)
        => mode == DisplayMode.Solo
            ? $"集中 · {PaneDisplayName(stagePane ?? PaneKind.Editor)}"
            : mode == DisplayMode.Dock ? "ドック変更" : "レイアウト変更";

    /// <summary>ペインの現在対象から、記録する軌跡の種類と表示情報を決める。</summary>
    internal static TrailPaneRecordTarget CreatePaneRecordTarget(
        PaneKind pane, string? editorPath, bool editorIsVirtual, int editorLine, int editorColumn,
        Guid? terminalId, string? terminalLabel, string? previewPath, bool previewIsVirtual,
        string? browserUrl, string? browserTitle, string defaultBrowserUrl)
    {
        if (pane == PaneKind.Editor && IsRecordableFile(editorPath, editorIsVirtual))
            return new(TrailEntryKind.File, editorPath!, Path.GetFileName(editorPath!)!, editorLine, editorColumn);
        if (pane == PaneKind.Terminal && terminalId is { } id)
            return new(TrailEntryKind.Terminal, id.ToString("D"),
                TerminalLabel(terminalLabel, null));
        if (pane == PaneKind.EditorSupport && IsRecordableFile(previewPath, previewIsVirtual))
            return new(TrailEntryKind.Preview, previewPath!, Path.GetFileName(previewPath!)!);
        if (pane == PaneKind.Browser && IsRecordableBrowserUrl(browserUrl, defaultBrowserUrl))
            return new(TrailEntryKind.Browser, browserUrl!, BrowserLabel(browserUrl!, browserTitle));
        return new(TrailEntryKind.Pane, pane.ToString(), PaneDisplayName(pane));
    }

    internal static string TerminalLabel(string? title, string? headerTitle)
        => !string.IsNullOrWhiteSpace(title) ? title
            : !string.IsNullOrWhiteSpace(headerTitle) ? headerTitle : "ターミナル";

    private static string BrowserLabel(string url, string? title)
        => !string.IsNullOrWhiteSpace(title) ? title.Trim()
            : Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host : url;

    internal static bool IsRecordableFile(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? path, bool isVirtual)
        => !isVirtual && !string.IsNullOrWhiteSpace(path);

    /// <summary>軌跡項目の保存データと、呼び出し元から渡された実行時存在条件を検証する。</summary>
    internal static bool CanJumpToEntry(
        TrailEntryKind kind,
        string? target,
        string? paneLayout,
        Func<PaneKind, bool> hasPane,
        Func<Guid, bool> hasTerminal,
        Func<string, bool> hasSession)
    {
        if (!string.IsNullOrWhiteSpace(paneLayout) && !TryDeserializeLayout(paneLayout, out _))
            return false;

        return kind switch
        {
            TrailEntryKind.File or TrailEntryKind.Preview or TrailEntryKind.Edit => File.Exists(target),
            TrailEntryKind.Browser => !string.IsNullOrWhiteSpace(target),
            TrailEntryKind.Pane => Enum.TryParse(target, out PaneKind pane) && hasPane(pane),
            TrailEntryKind.Panel => Enum.TryParse<SidebarPanel>(target, out _),
            TrailEntryKind.Terminal => Guid.TryParse(target, out var id) && hasTerminal(id),
            TrailEntryKind.Session => target is not null && hasSession(target),
            TrailEntryKind.Layout => !string.IsNullOrWhiteSpace(paneLayout),
            TrailEntryKind.Git => false,
            _ => false,
        };
    }

    /// <summary><paramref name="dock"/> はドックモードで開いている領域（例 "Git+EditorSupport"）。
    /// 中央のタイルが同じでも道具の開閉で見え方は変わるので、これも配置の一部として鍵に入れる
    /// ——入れないと、ドックモード中の操作が軌跡に1点も残らない。</summary>
    public static string LayoutKey(DisplayMode mode, PaneKind? stagePane, PaneNodeSnapshot? snapshot, string? dock = null)
    {
        var structure = snapshot is null ? "-" : PaneLayoutTree.StructureSignature(snapshot);
        return $"{(int)mode}|{stagePane?.ToString() ?? "-"}|{structure}|{dock ?? "-"}";
    }

    public static bool IsRecordableBrowserUrl(string? url, string defaultUrl)
        => !string.IsNullOrWhiteSpace(url)
           && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(url, defaultUrl, StringComparison.OrdinalIgnoreCase);

    public static (string Key, string Label) DescribeGitOperation(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return ("", "");
        var sub = parts[0];
        bool Has(string flag) => Array.IndexOf(parts, flag) >= 0;
        var lastRef = parts.Length > 1 ? parts[^1] : "";
        return sub switch
        {
            "commit" => ("commit", Has("--amend") ? "コミット（amend）" : "コミット"),
            "add" => ("stage", "ステージ"),
            "restore" when Has("--staged") => ("unstage", "アンステージ"),
            "restore" or "clean" or "apply" => ("discard", "変更を破棄"),
            "push" => ("push", "プッシュ"),
            "pull" => ("pull", "プル"),
            "fetch" => ("fetch", "フェッチ"),
            "switch" when Has("-c") => ("branch-create", $"ブランチ作成: {lastRef}"),
            "switch" => ("checkout", $"ブランチ切替: {lastRef}"),
            "checkout" when Has("--detach") => ("checkout-detach", "コミットをチェックアウト"),
            "checkout" => ("checkout", $"ブランチ切替: {lastRef}"),
            "branch" when Has("-d") || Has("-D") => ("branch-delete", $"ブランチ削除: {lastRef}"),
            "branch" => ("branch", "ブランチ操作"),
            "merge" when Has("--continue") => ("merge", "マージ続行"),
            "merge" when Has("--abort") => ("merge", "マージ中止"),
            "merge" => ("merge", $"マージ: {lastRef}"),
            "rebase" when Has("--continue") => ("rebase", "リベース続行"),
            "rebase" when Has("--abort") => ("rebase", "リベース中止"),
            "rebase" when Has("--skip") => ("rebase", "リベーススキップ"),
            "rebase" => ("rebase", "リベース"),
            "cherry-pick" => ("cherry-pick", "チェリーピック"),
            "revert" => ("revert", "リバート"),
            "reset" => ("reset", "リセット"),
            "stash" => ("stash", "スタッシュ"),
            "tag" => ("tag", "タグ"),
            "submodule" => ("submodule", "サブモジュール"),
            "init" => ("init", "リポジトリ初期化"),
            _ => (sub, $"git {sub}")
        };
    }

    public static string PaneDisplayName(PaneKind kind) => kind switch
    {
        PaneKind.Editor => "エディタ", PaneKind.Terminal => "ターミナル",
        PaneKind.Browser => "ブラウザ", PaneKind.EditorSupport => "プレビュー",
        PaneKind.Ai => "AI", PaneKind.Git => "Git", PaneKind.Diff => "Diff",
        PaneKind.Trace => "トレース", PaneKind.Debug => "IDE", PaneKind.Search => "検索",
        PaneKind.TsIde => "TS IDE", PaneKind.Files => "ファイル一覧", _ => kind.ToString()
    };

    public static string PanelDisplayName(SidebarPanel panel) => panel switch
    {
        SidebarPanel.Explorer => "エクスプローラ",
        SidebarPanel.Git => "Gitパネル",
        SidebarPanel.Pegboard => "ペグボード",
        SidebarPanel.Solution => "ソリューション",
        SidebarPanel.Tabs => "タブ一覧",
        _ => panel.ToString()
    };
}

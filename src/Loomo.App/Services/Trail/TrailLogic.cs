namespace sk0ya.Loomo.App.Services;

/// <summary>軌跡の分類・表示・変更検出に使うUI非依存ロジック。</summary>
public static class TrailLogic
{
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
        _ => panel.ToString()
    };
}

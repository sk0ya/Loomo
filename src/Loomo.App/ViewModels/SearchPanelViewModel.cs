using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>サイドバー Search パネルの検索範囲。テキスト＝ファイル内を grep、ファイル＝名前で曖昧検索、
/// ターミナル＝アクティブなターミナル内テキスト。</summary>
public enum SearchScope { Text, FileName, Terminal }

/// <summary>ターミナル内一致1件（行インデックス0始まり・行内位置・一致長・行テキスト）。
/// ジャンプ時に <c>TerminalTabView.SelectMatch</c> へ渡すための情報を持つ。</summary>
public readonly record struct TerminalSearchHit(int LineIndex, int Column, int Length, string LineText);

/// <summary>grep の1ヒット行（サイドバー Search パネル）。ターミナル一致も同じリスト/テンプレートで表示するため
/// <see cref="IsTerminal"/> で両対応する。</summary>
public sealed class SearchMatchItem
{
    public SearchMatchItem(ContentSearchHit hit)
    {
        FullPath = hit.FullPath;
        RelativePath = hit.RelativePath;
        Line = hit.Line;
        Column = hit.Column;
        LineText = hit.LineText.TrimEnd();
    }

    private SearchMatchItem(TerminalSearchHit hit)
    {
        IsTerminal = true;
        TerminalHit = hit;
        FullPath = "";
        RelativePath = "";
        Line = hit.LineIndex + 1; // 表示は1始まり
        Column = hit.Column;
        LineText = hit.LineText.TrimEnd();
    }

    /// <summary>ターミナル一致から候補を作る。</summary>
    public static SearchMatchItem ForTerminal(TerminalSearchHit hit) => new(hit);

    public bool IsTerminal { get; }
    public TerminalSearchHit TerminalHit { get; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public int Line { get; }
    public int Column { get; }
    public string LineText { get; }
    public string Preview => LineText.TrimStart();
}

/// <summary>1ファイル分のヒットをまとめたグループ（ファイル名見出し＋一致行）。</summary>
public sealed class SearchFileGroup
{
    public SearchFileGroup(string fullPath, string relativePath, IEnumerable<SearchMatchItem> matches)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Matches = new ObservableCollection<SearchMatchItem>(matches);
    }

    public string FullPath { get; }
    public string RelativePath { get; }
    public ObservableCollection<SearchMatchItem> Matches { get; }
    public int Count => Matches.Count;
    // ファイル名検索のヒットは一致行を持たない（Count==0）ので、件数の括弧は付けない。
    public string Header => Count > 0 ? $"{RelativePath}  ({Count})" : RelativePath;
}

/// <summary>
/// 選択／確定で開きたい場所（ファイル＋1始まりの行・列）。grep 一致行とファイル名ヒットの共通ペイロード。
/// <paramref name="Highlight"/> は grep ヒットのとき検索ワード（エディタで全マッチをハイライトする）、
/// ファイル名検索のヒットでは空。
/// </summary>
public readonly record struct SearchHit(string FullPath, int Line, int Column, string Highlight = "");

/// <summary>
/// サイドバー Search パネルの ViewModel。クエリ・オプション（大小区別／正規表現／include・exclude glob）で
/// <see cref="IWorkspaceSearchService.GrepAsync"/>（内容検索）または <see cref="IWorkspaceSearchService.FindFilesAsync"/>
/// （ファイル名検索）またはアクティブなターミナル内検索を走らせ、結果をファイル別にグルーピングして保持する。
/// <see cref="Scope"/> でモードを切り替える。
/// 入力はデバウンスし、直前の検索はキャンセルする。選択／確定はイベントで ShellWindow へ委ねる
/// （プレビュータブ表示・行ジャンプは View 側の責務）。
/// </summary>
public sealed partial class SearchPanelViewModel : ObservableObject
{
    private readonly IWorkspaceSearchService _search;
    private readonly IWorkspaceService _workspace;
    private CancellationTokenSource? _cts;

    /// <summary>1ヒットを「エディタでプレビュー」したい（単クリック・キーボード選択）。</summary>
    public event EventHandler<SearchHit>? PreviewRequested;

    /// <summary>1ヒットを通常タブで開きたい（Enter・ダブルクリック）。</summary>
    public event EventHandler<SearchHit>? ActivateRequested;

    /// <summary>エディタの検索ハイライトを消したい（クエリを空にした・Esc・ファイル名/ターミナル検索へ切替）。</summary>
    public event EventHandler? ClearHighlightRequested;

    /// <summary>ターミナル一致を選んだ（プレビュー＝単クリック・確定＝Enter/ダブルクリックとも、その箇所へジャンプ）。</summary>
    public event EventHandler<TerminalSearchHit>? TerminalRevealRequested;

    /// <summary>アクティブなターミナル内テキストを検索する供給口（ShellWindow が実ターミナルを束ねる）。
    /// 引数＝(クエリ, 大小区別)、戻り＝一致一覧。未設定／ターミナル無しのときは null。</summary>
    public Func<string, bool, IReadOnlyList<TerminalSearchHit>>? TerminalSearchProvider { get; set; }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private string _includeGlob = "";
    [ObservableProperty] private string _excludeGlob = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>検索範囲（テキスト grep / ファイル名 / ターミナル）。</summary>
    [ObservableProperty] private SearchScope _scope = SearchScope.Text;

    /// <summary>検索の開始フォルダー。ワークスペースルートからの相対パス（'/' 区切り）で持つ。
    /// 空＝ワークスペースルート全体。既定は FolderTree の表示ルート（<see cref="SetDefaultRoot"/>）。
    /// ルート配下のフォルダのみ有効（サービス側でルート外は無視される）。
    /// テキスト／ファイル名検索でのみ使う（ターミナル検索は対象外）。</summary>
    [ObservableProperty] private string _searchRoot = "";

    // 既定の開始フォルダー（FolderTree の表示ルート・相対）。リセットの戻り先・追従の基準。
    private string _defaultRoot = "";

    /// <summary>既定（FolderTree ルート）と違うフォルダーに絞っているか（リセットボタンの表示可否）。</summary>
    public bool CanResetSearchRoot
        => !string.Equals(Normalize(SearchRoot), Normalize(_defaultRoot), StringComparison.OrdinalIgnoreCase);

    /// <summary>フォルダー欄を表示するか（ターミナル検索では対象外）。</summary>
    public bool ShowSearchRoot => Scope != SearchScope.Terminal;

    /// <summary>フォルダパス補完（インテリセンス）の基準となるワークスペースルート。</summary>
    public string? WorkspaceRoot => _workspace.RootPath;

    /// <summary>検索結果の各行で強調する検索ワード（テキスト／ファイル名／ターミナルとも Query を渡す）。
    /// 空ならハイライトなし。</summary>
    public string HighlightQuery => Query;

    /// <summary>結果ハイライトを正規表現として扱うか。テキスト grep で正規表現モードのときだけ。
    /// （ファイル名・ターミナルは常にリテラル一致でハイライトする。）</summary>
    public bool HighlightUseRegex => Scope == SearchScope.Text && UseRegex;

    /// <summary>結果ハイライトで大文字小文字を区別するか。テキスト grep の大小区別オンのときだけ
    /// （ファイル名は曖昧検索・ターミナルは無区別なので区別しない）。</summary>
    public bool HighlightCaseSensitive => Scope == SearchScope.Text && CaseSensitive;

    /// <summary>クエリ欄のプレースホルダ（モードで文言を変える）。</summary>
    public string QueryPlaceholder => Scope switch
    {
        SearchScope.FileName => "ファイル名で検索",
        SearchScope.Terminal => "ターミナル内を検索",
        _ => "検索ワード（ファイル内を grep）",
    };

    public ObservableCollection<SearchFileGroup> Results { get; } = new();

    public SearchPanelViewModel(IWorkspaceSearchService search, IWorkspaceService workspace)
    {
        _search = search;
        _workspace = workspace;
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HighlightQuery));
        ScheduleSearch();
    }

    partial void OnCaseSensitiveChanged(bool value)
    {
        OnPropertyChanged(nameof(HighlightCaseSensitive));
        ScheduleSearch();
    }

    partial void OnUseRegexChanged(bool value)
    {
        OnPropertyChanged(nameof(HighlightUseRegex));
        ScheduleSearch();
    }

    partial void OnIncludeGlobChanged(string value) => ScheduleSearch();
    partial void OnExcludeGlobChanged(string value) => ScheduleSearch();

    partial void OnSearchRootChanged(string value)
    {
        OnPropertyChanged(nameof(CanResetSearchRoot));
        ScheduleSearch();
    }

    partial void OnScopeChanged(SearchScope value)
    {
        OnPropertyChanged(nameof(QueryPlaceholder));
        OnPropertyChanged(nameof(ShowSearchRoot));
        OnPropertyChanged(nameof(HighlightUseRegex));
        OnPropertyChanged(nameof(HighlightCaseSensitive));
        // grep 以外（ファイル名・ターミナル）はエディタのハイライト対象がないので、切替時に残りを消す。
        if (value != SearchScope.Text)
            ClearHighlightRequested?.Invoke(this, EventArgs.Empty);
        ScheduleSearch();
    }

    /// <summary>検索ワードをクリアする（結果もエディタのハイライトも消える）。Esc 用。</summary>
    public void ClearQuery() => Query = "";

    /// <summary>検索の開始フォルダーを指定する（フォルダーツリーの「このフォルダーで検索」用）。
    /// フルパスをワークスペースルート相対に直して持つ。</summary>
    public void SetSearchRoot(string fullPath) => SearchRoot = ToRelative(fullPath);

    /// <summary>検索の既定の開始フォルダー（FolderTree の表示ルート）を設定する。
    /// ルートが変わったら検索フォルダーもそこへ合わせる（明示的なルート変更なので追従させる）。</summary>
    public void SetDefaultRoot(string? fullPath)
    {
        _defaultRoot = ToRelative(fullPath);
        SearchRoot = _defaultRoot;             // OnSearchRootChanged 経由で再検索＆ボタン更新
        OnPropertyChanged(nameof(CanResetSearchRoot)); // 値が同じでも基準が変わったので通知する
    }

    /// <summary>検索フォルダーを既定（FolderTree のルート）へ戻す。</summary>
    [RelayCommand]
    private void ResetSearchRoot() => SearchRoot = _defaultRoot;

    /// <summary>フルパスをワークスペースルートからの相対パス（'/' 区切り）へ。ルート＝空、
    /// ルート外・ルート未設定はフルパスのまま（サービス側でルート外は無視される）。</summary>
    private string ToRelative(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return "";
        var root = _workspace.RootPath;
        if (string.IsNullOrEmpty(root))
            return fullPath;
        try
        {
            var rel = System.IO.Path.GetRelativePath(root, fullPath);
            if (rel == ".")
                return "";
            if (rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel))
                return fullPath;
            return rel.Replace('\\', '/');
        }
        catch
        {
            return fullPath;
        }
    }

    private static string Normalize(string? path)
        => (path ?? "").Replace('\\', '/').TrimEnd('/');

    /// <summary>入力が変わるたびに直前の検索をキャンセルし、少し待ってから再検索する。</summary>
    private void ScheduleSearch()
    {
        _cts?.Cancel();

        if (string.IsNullOrEmpty(Query))
        {
            Results.Clear();
            StatusMessage = "";
            IsBusy = false;
            // クエリが空になったらエディタのハイライトも消す。
            ClearHighlightRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunSearchAsync(cts.Token);
    }

    private async Task RunSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(160, ct); // 連続入力をまとめる
            IsBusy = true;

            switch (Scope)
            {
                case SearchScope.FileName:
                    await RunFindFilesAsync(ct);
                    break;
                case SearchScope.Terminal:
                    RunTerminalSearch();
                    break;
                default:
                    await RunGrepAsync(ct);
                    break;
            }
        }
        catch (OperationCanceledException) { /* 新しい入力に置き換わった */ }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsBusy = false;
        }
    }

    /// <summary>ファイル内容を grep し、ファイル別にグルーピングして結果へ反映する。</summary>
    private async Task RunGrepAsync(CancellationToken ct)
    {
        var options = new GrepOptions(
            CaseSensitive: CaseSensitive,
            UseRegex: UseRegex,
            IncludeGlob: NullIfBlank(IncludeGlob),
            ExcludeGlob: NullIfBlank(ExcludeGlob),
            MaxResults: 1000);

        var hits = await _search.GrepAsync(Query, options, ct, NullIfBlank(SearchRoot));
        if (ct.IsCancellationRequested)
            return;

        Results.Clear();
        var groups = hits
            .GroupBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SearchFileGroup(g.First().FullPath, g.Key,
                g.Select(h => new SearchMatchItem(h))));
        foreach (var group in groups)
            Results.Add(group);

        var fileCount = Results.Count;
        var matchCount = Results.Sum(r => r.Count);
        StatusMessage = matchCount == 0
            ? "一致なし"
            : $"{matchCount} 件 / {fileCount} ファイル";
    }

    /// <summary>ファイル名を曖昧検索し、1ファイル1行（一致行なし）の結果として反映する。</summary>
    private async Task RunFindFilesAsync(CancellationToken ct)
    {
        var hits = await _search.FindFilesAsync(Query, 500, ct, NullIfBlank(SearchRoot));
        if (ct.IsCancellationRequested)
            return;

        Results.Clear();
        foreach (var hit in hits)
            Results.Add(new SearchFileGroup(hit.FullPath, hit.RelativePath, Array.Empty<SearchMatchItem>()));

        StatusMessage = Results.Count == 0 ? "一致なし" : $"{Results.Count} ファイル";
    }

    /// <summary>アクティブなターミナル内テキストを検索し、1グループ「ターミナル」配下に一致を並べる。
    /// 供給口（<see cref="TerminalSearchProvider"/>）は ShellWindow が実ターミナルへ橋渡しする。</summary>
    private void RunTerminalSearch()
    {
        Results.Clear();

        if (TerminalSearchProvider is not { } provider)
        {
            StatusMessage = "ターミナルがありません";
            return;
        }

        // 大小区別はターミナル検索では区別しない（コマンドパレットの $ 検索と揃える）。
        var hits = provider(Query, false);
        if (hits.Count == 0)
        {
            StatusMessage = "一致なし";
            return;
        }

        const int max = 1000;
        var items = hits.Take(max).Select(SearchMatchItem.ForTerminal);
        Results.Add(new SearchFileGroup("", "ターミナル", items));
        StatusMessage = hits.Count > max ? $"{max}+ 件" : $"{hits.Count} 件";
    }

    /// <summary>
    /// エディタで全マッチをハイライトする検索ワード。Editor の <c>HighlightSearch</c> は
    /// literal substring マッチなので、リテラル grep のときだけ渡す（正規表現／ファイル名／ターミナルでは空）。
    /// </summary>
    private string HighlightTerm => Scope == SearchScope.Text && !UseRegex ? Query : "";

    public void Preview(SearchMatchItem match)
    {
        if (match.IsTerminal)
        {
            TerminalRevealRequested?.Invoke(this, match.TerminalHit);
            return;
        }
        PreviewRequested?.Invoke(this, new SearchHit(match.FullPath, match.Line, match.Column, HighlightTerm));
    }

    public void Activate(SearchMatchItem match)
    {
        if (match.IsTerminal)
        {
            TerminalRevealRequested?.Invoke(this, match.TerminalHit);
            return;
        }
        ActivateRequested?.Invoke(this, new SearchHit(match.FullPath, match.Line, match.Column, HighlightTerm));
    }

    /// <summary>ファイル名ヒット（グループ見出し自体）を開く。先頭（1,1）へジャンプする。</summary>
    public void Preview(SearchFileGroup group)
        => PreviewRequested?.Invoke(this, new SearchHit(group.FullPath, 1, 1));

    public void Activate(SearchFileGroup group)
        => ActivateRequested?.Invoke(this, new SearchHit(group.FullPath, 1, 1));

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

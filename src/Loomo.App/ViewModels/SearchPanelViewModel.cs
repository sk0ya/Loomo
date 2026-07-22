using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 検索ペイン（Editor/Terminal 等と同格の舞台ペイン、<see cref="Services.PaneKind.Search"/>）の
/// ViewModel。クエリ・オプション（大小区別／正規表現／include・exclude glob）で
/// <see cref="IWorkspaceSearchService.GrepAsync"/>（内容検索）または <see cref="IWorkspaceSearchService.FindFilesAsync"/>
/// （ファイル名検索）またはアクティブなターミナル内検索を走らせ、結果をファイル別にグルーピングして保持する。
/// <see cref="Scope"/> でモードを切り替える。
/// 入力はデバウンスし、直前の検索はキャンセルする。選択／確定はイベントで ShellWindow へ委ねる
/// （プレビュータブ表示・行ジャンプは View 側の責務）。
/// </summary>
public sealed partial class SearchPanelViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly SearchPanelQuery _searchQuery;
    private readonly SearchResultTreeMapper _treeMapper;
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

    /// <summary>クラス／シンボル検索（LSP ワークスペースシンボル）の供給口
    /// （ShellWindow が接続中の言語サーバーへ橋渡しする）。引数＝(クエリ, isClass, キャンセル)。
    /// 未設定のときは「言語サーバーが未接続」として扱う。</summary>
    public Func<string, bool, CancellationToken, Task<SymbolSearchResult>>? SymbolSearchProvider { get; set; }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private string _includeGlob = "";
    [ObservableProperty] private string _excludeGlob = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>検索範囲（テキスト grep / ファイル名 / ターミナル）。</summary>
    [ObservableProperty] private SearchScope _scope = SearchScope.Text;

    /// <summary>置換欄の入力（テキスト検索のみ）。1件（ファイル単位）／全置換のどちらも、その時点でのこの値を使う。</summary>
    [ObservableProperty] private string _replaceText = "";

    /// <summary>置換欄（入力＋「すべて置換」・ファイルごとの「置換」ボタン）を表示しているか。</summary>
    [ObservableProperty] private bool _isReplaceVisible;

    /// <summary>置換機能を出せるか（テキスト grep のときだけ。ファイル名／ターミナル検索には無い）。</summary>
    public bool CanReplace => Scope == SearchScope.Text;

    /// <summary>検索の開始フォルダー。単一ルートはワークスペースルートからの相対パス（'/' 区切り）。
    /// マルチルートは先頭にワークスペースフォルダーの表示名を付けた「フォルダー名/相対パス」
    /// （結果ツリーのフォルダー見出しと同じ表記・<see cref="EffectiveSearchRoot"/> で解決する）。
    /// 空＝ワークスペース全体。既定は FolderTree の表示ルート（<see cref="SetDefaultRoot"/>）。
    /// テキスト／ファイル名検索でのみ使う（ターミナル検索は対象外）。</summary>
    [ObservableProperty] private string _searchRoot = "";

    // 既定の開始フォルダー（FolderTree の表示ルート・SearchRoot と同じ表記）。リセットの戻り先・追従の基準。
    private string _defaultRoot = "";

    /// <summary>既定（FolderTree ルート）と違うフォルダーに絞っているか（リセットボタンの表示可否）。</summary>
    public bool CanResetSearchRoot
        => !string.Equals(Normalize(SearchRoot), Normalize(_defaultRoot), StringComparison.OrdinalIgnoreCase);

    /// <summary>フォルダー欄を表示するか（ターミナル・クラス・シンボル検索では対象外——
    /// LSP のワークスペースシンボル検索は接続中の言語サーバー基準で、フォルダー絞り込みの概念が無い）。</summary>
    public bool ShowSearchRoot => Scope is not (SearchScope.Terminal or SearchScope.Class or SearchScope.Symbol);

    /// <summary>フォルダパス補完（インテリセンス）が辿るワークスペースフォルダー一覧
    /// （マルチルート時は各フォルダー配下を「フォルダー名/…」として提示する）。</summary>
    public IReadOnlyList<string> WorkspaceFolders => _workspace.Folders;

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
        SearchScope.Class => "クラス名で検索（ワークスペース横断）",
        SearchScope.Symbol => "シンボル名で検索（ワークスペース横断）",
        _ => "検索ワード（ファイル内を grep）",
    };

    /// <summary>結果ツリー。トップレベルは <see cref="SearchFolderNode"/>（フォルダー）か、
    /// ルート直下のファイル（<see cref="SearchFileGroup"/>）。</summary>
    public ObservableCollection<object> Results { get; } = new();

    public SearchPanelViewModel(IWorkspaceService workspace, SearchPanelQuery searchQuery,
        SearchResultTreeMapper treeMapper)
    {
        _workspace = workspace;
        _searchQuery = searchQuery;
        _treeMapper = treeMapper;
        _workspace.FoldersChanged += (_, _) => OnFoldersChanged();
    }

    // マルチルートになった瞬間（フォルダー追加）は既定の開始フォルダーをワークスペース全体へ戻す。
    // FolderTree の CurrentRootChanged は単一フォルダー時のピン留め切替でしか発火しない
    // （複数フォルダー時は「今表示中のルート」という概念が無い＝全フォルダー見出しを同時に表示するため）
    // ので、ここで直接 FoldersChanged を見て追従する。単一フォルダーへ戻ったときは
    // CurrentRootChanged 側の SetDisplayRoot が改めて既定フォルダーを設定し直す。
    private void OnFoldersChanged()
    {
        OnPropertyChanged(nameof(WorkspaceFolders));
        if (_workspace.Folders.Count > 1)
            SetDefaultRoot(null);
    }

    private static string LabelFor(string fullPath)
    {
        var name = System.IO.Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }

    /// <summary>フルパスを含むワークスペースフォルダー（マルチルートの各ルート）を返す。
    /// どれにも属さなければプライマリフォルダーへ退避する。</summary>
    private string? FindOwningFolder(string fullPath)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(fullPath).TrimEnd('\\', '/'); }
        catch { return _workspace.RootPath; }

        foreach (var folder in _workspace.Folders)
        {
            string root;
            try { root = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'); }
            catch { continue; }
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return folder;
        }
        return _workspace.RootPath;
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
        OnPropertyChanged(nameof(CanReplace));
        // grep 以外（ファイル名・ターミナル）はエディタのハイライト対象がないので、切替時に残りを消す。
        if (value != SearchScope.Text)
        {
            ClearHighlightRequested?.Invoke(this, EventArgs.Empty);
            IsReplaceVisible = false;
        }
        ScheduleSearch();
    }

    [RelayCommand]
    private void ToggleReplace() => IsReplaceVisible = !IsReplaceVisible;

    /// <summary>現在の結果ツリーに含まれる全ファイルグループ（フォルダー節点をたどって集める）。
    /// 全置換の対象集合・確認ダイアログの件数計算に使う（表示専用の <see cref="Results"/> と違い平坦なリスト）。</summary>
    public IReadOnlyList<SearchFileGroup> AllFileGroups()
    {
        var list = new List<SearchFileGroup>();
        void Walk(IEnumerable<object> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is SearchFileGroup g) list.Add(g);
                else if (node is SearchFolderNode f) Walk(f.Children);
            }
        }
        Walk(Results);
        return list;
    }

    /// <summary>置換でディスク上の内容を書き換えたファイル（複数可）。開いているエディタタブがあれば
    /// 読み直させ、検索ハイライトを新しい内容に合わせて更新させるため（置換した箇所の下線が消えたことを
    /// はっきり見せる＝見た目が古いまま「まだ一致している」ように誤解させない）。ShellWindow が購読する。</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesReplacedOnDisk;

    /// <summary>1ファイル内の一致をすべて置換する。置換後は最新状態を反映するため再検索する。
    /// 実際に置換できた件数を返す（クエリが空なら何もせず0）。</summary>
    public int ReplaceInFile(SearchFileGroup group)
    {
        if (string.IsNullOrEmpty(Query)) return 0;
        var count = _searchQuery.ReplaceInFile(group.FullPath, Query, ReplaceText, CaseSensitive, UseRegex);
        if (count > 0)
        {
            ScheduleSearch();
            FilesReplacedOnDisk?.Invoke(this, new[] { group.FullPath });
        }
        return count;
    }

    /// <summary>1件（右クリックメニュー・「置換」ボタン）だけを置換する。一覧からは消さず、その項目を
    /// 「置換済み」にする（<see cref="SearchMatchItem.IsReplaced"/>、表示は取り消し線）。次々処理していく
    /// 操作の途中で一覧が消えたり並び直ったりしないよう、あえて再検索しない。
    /// 成功したら true（クエリが空・ターミナル一致・対象が見つからない＝内容がずれた場合は false）。</summary>
    public bool ReplaceOne(SearchMatchItem match)
    {
        if (string.IsNullOrEmpty(Query) || match.IsTerminal) return false;
        var ok = _searchQuery.ReplaceOneInFile(match.FullPath, Query, ReplaceText, CaseSensitive, UseRegex,
            match.Line, match.Column);
        if (ok)
        {
            match.IsReplaced = true;
            FilesReplacedOnDisk?.Invoke(this, new[] { match.FullPath });
        }
        return ok;
    }

    /// <summary>現在の結果に含まれる全ファイルへ置換を適用する。
    /// (置換したファイル数, 置換した件数の合計) を返す（クエリが空なら (0, 0)）。</summary>
    public (int Files, int Matches) ReplaceAll()
    {
        if (string.IsNullOrEmpty(Query)) return (0, 0);
        var files = 0;
        var matches = 0;
        var changedPaths = new List<string>();
        foreach (var g in AllFileGroups())
        {
            var n = _searchQuery.ReplaceInFile(g.FullPath, Query, ReplaceText, CaseSensitive, UseRegex);
            if (n > 0) { files++; matches += n; changedPaths.Add(g.FullPath); }
        }
        if (matches > 0)
        {
            ScheduleSearch();
            FilesReplacedOnDisk?.Invoke(this, changedPaths);
        }
        return (files, matches);
    }

    /// <summary>検索ワードをクリアする（結果もエディタのハイライトも消える）。Esc・ワークスペース切替時に使う。</summary>
    public void ClearQuery() => Query = "";

    /// <summary>検索の開始フォルダーを指定する（フォルダーツリーの「このフォルダーで検索」用）。
    /// フルパスが属するワークスペースフォルダーからの相対パスとして持つ（マルチルートは
    /// 「フォルダー名/相対パス」表記）。</summary>
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

    /// <summary>フルパスを検索フォルダー欄用の表記へ変換する。単一ルートはワークスペースルートからの
    /// 相対パス（'/' 区切り）。マルチルートは先頭にそのフォルダーの表示名を付けた「フォルダー名/相対パス」
    /// （<see cref="EffectiveSearchRoot"/> が同じ表記を解決する）。基準そのもの＝空、
    /// ルート外・変換不可はフルパスのまま（サービス側でルート外は無視される）。</summary>
    private string ToRelative(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return "";

        var owner = FindOwningFolder(fullPath);
        if (string.IsNullOrEmpty(owner))
            return fullPath;

        string rel;
        try
        {
            rel = System.IO.Path.GetRelativePath(owner, fullPath);
            if (rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel))
                return fullPath;
            rel = rel == "." ? "" : rel.Replace('\\', '/');
        }
        catch
        {
            return fullPath;
        }

        if (_workspace.Folders.Count <= 1)
            return rel;

        var name = LabelFor(owner);
        return string.IsNullOrEmpty(rel) ? name : name + "/" + rel;
    }

    /// <summary>サービスへ渡す実際の検索開始フォルダー。空＝全ルート。単一ルートは <see cref="SearchRoot"/> を
    /// そのまま渡す（従来通りサービス側でワークスペースルート相対として解決）。マルチルートは先頭セグメントを
    /// ワークスペースフォルダーの表示名として解決し、絶対パスに組み立てて渡す（同名フォルダー・同名
    /// サブフォルダーの取り違えを防ぐ）。名前解決できなければそのまま渡す（サービス側の「各フォルダーへ
    /// 順に試す」フォールバックに任せる）。</summary>
    private string? EffectiveSearchRoot()
    {
        var raw = NullIfBlank(SearchRoot);
        if (raw is null || _workspace.Folders.Count <= 1)
            return raw;

        var normalized = raw.Replace('\\', '/');
        var slash = normalized.IndexOf('/');
        var rootName = slash >= 0 ? normalized[..slash] : normalized;
        var sub = slash >= 0 ? normalized[(slash + 1)..] : "";

        var folder = _workspace.Folders.FirstOrDefault(f =>
            string.Equals(LabelFor(f), rootName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return raw;
        if (string.IsNullOrEmpty(sub))
            return folder;
        try { return System.IO.Path.GetFullPath(sub, folder); }
        catch { return folder; }
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
                case SearchScope.Class:
                    await RunSymbolSearchAsync(isClass: true, ct);
                    break;
                case SearchScope.Symbol:
                    await RunSymbolSearchAsync(isClass: false, ct);
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
        var result = await _searchQuery.GrepAsync(Query, CaseSensitive, UseRegex,
            NullIfBlank(IncludeGlob), NullIfBlank(ExcludeGlob), EffectiveSearchRoot(), ct);
        if (ct.IsCancellationRequested) return;
        ReplaceResults(result.Roots);
        StatusMessage = result.StatusMessage;
    }

    /// <summary>ファイル名を曖昧検索し、1ファイル1行（一致行なし）の結果として反映する。</summary>
    private async Task RunFindFilesAsync(CancellationToken ct)
    {
        var result = await _searchQuery.FindFilesAsync(Query, EffectiveSearchRoot(), ct);
        if (ct.IsCancellationRequested) return;
        ReplaceResults(result.Roots);
        StatusMessage = result.StatusMessage;
    }

    /// <summary>アクティブなターミナル内テキストを検索し、1グループ「ターミナル」配下に一致を並べる。
    /// 供給口（<see cref="TerminalSearchProvider"/>）は ShellWindow が実ターミナルへ橋渡しする。</summary>
    private void RunTerminalSearch()
    {
        if (TerminalSearchProvider is not { } provider)
        {
            ReplaceResults(Array.Empty<object>());
            StatusMessage = "ターミナルがありません";
            return;
        }

        // 大小区別はターミナル検索では区別しない（コマンドパレットの $ 検索と揃える）。
        var hits = provider(Query, false);
        if (hits.Count == 0)
        {
            ReplaceResults(Array.Empty<object>());
            StatusMessage = "一致なし";
            return;
        }

        const int max = 1000;
        var items = hits.Take(max).Select(SearchMatchItem.ForTerminal);
        // ターミナルはフォルダー階層を持たないので、ルート直下の単一グループとして並べる。
        ReplaceResults(_treeMapper.Map(new[] { new SearchFileGroup("", "ターミナル", items) }));
        StatusMessage = hits.Count > max ? $"{max}+ 件" : $"{hits.Count} 件";
    }

    /// <summary>接続中の言語サーバーへワークスペースシンボル検索を投げ、ファイル別にグルーピングして
    /// 結果へ反映する。供給口（<see cref="SymbolSearchProvider"/>）は ShellWindow が接続中のLSPマネージャーへ
    /// 橋渡しする（コード編集タブが1つも開いていない／未接続なら「言語サーバーが未接続」と表示する）。</summary>
    private async Task RunSymbolSearchAsync(bool isClass, CancellationToken ct)
    {
        var label = isClass ? "クラス" : "シンボル";
        if (SymbolSearchProvider is not { } provider)
        {
            ReplaceResults(Array.Empty<object>());
            StatusMessage = "言語サーバーが未接続です（対象コードのファイルを開いてください）";
            return;
        }

        var result = await provider(Query, isClass, ct);
        if (ct.IsCancellationRequested) return;

        if (!result.HasConnection)
        {
            ReplaceResults(Array.Empty<object>());
            StatusMessage = "言語サーバーが未接続です（対象コードのファイルを開いてください）";
            return;
        }
        if (result.Symbols.Count == 0)
        {
            ReplaceResults(Array.Empty<object>());
            StatusMessage = $"一致なし（{label}）";
            return;
        }

        const int max = 500;
        var groups = result.Symbols.Take(max)
            .Select(ToSymbolMatch)
            .Where(m => m is not null)
            .GroupBy(m => m!.Value.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SearchFileGroup(g.Key, ToRelative(g.Key),
                g.Select(m => SearchMatchItem.ForSymbol(m!.Value.FullPath, ToRelative(m!.Value.FullPath), m!.Value.Line, m!.Value.Column, m!.Value.Preview))))
            .ToList();
        ReplaceResults(_treeMapper.Map(groups));
        StatusMessage = result.Symbols.Count > max ? $"{max}+ 件" : $"{result.Symbols.Count} 件";
    }

    /// <summary>1シンボルをファイルパス・ジャンプ位置・表示テキストへ変換する。ローカルパスへ解決できない
    /// （<see cref="CodeEditorSupport.TryUriToLocalPath"/> が null を返す）シンボルは除外する。</summary>
    private static (string FullPath, int Line, int Column, string Preview)? ToSymbolMatch(LspSymbolInformation symbol)
    {
        var path = CodeEditorSupport.TryUriToLocalPath(symbol.Location?.Uri);
        if (path is null) return null;
        var line = (symbol.Location?.Range?.Start?.Line ?? 0) + 1;
        var column = (symbol.Location?.Range?.Start?.Character ?? 0) + 1;
        var preview = string.IsNullOrEmpty(symbol.ContainerName) ? symbol.Name : $"{symbol.Name}  ·  {symbol.ContainerName}";
        return (path, line, column, preview);
    }

    /// <summary>組み上がった結果ツリーを UI の <see cref="Results"/> へ一括反映する（UI スレッド）。
    /// 重いツリー構築は <see cref="BuildResultTree"/> で済ませてあるので、ここはトップレベル節点を
    /// 並べ替え済みで足すだけ（件数が少なく入力をほぼ妨げない）。</summary>
    private void ReplaceResults(IReadOnlyList<object> roots)
    {
        Results.Clear();
        foreach (var child in roots)
            Results.Add(child);
    }

    /// <summary>
    /// エディタで全マッチをハイライトする検索ワード。Editor の <c>HighlightSearch</c> は
    /// literal substring マッチなので、リテラル grep のときだけ渡す（正規表現／ファイル名／ターミナルでは空）。
    /// </summary>
    public string HighlightTerm => Scope == SearchScope.Text && !UseRegex ? Query : "";

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

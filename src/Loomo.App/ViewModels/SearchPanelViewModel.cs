using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>grep の1ヒット行（サイドバー Search パネル）。</summary>
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
/// （ファイル名検索）を走らせ、結果をファイル別にグルーピングして保持する。<see cref="SearchByName"/> でモードを切り替える。
/// 入力はデバウンスし、直前の検索はキャンセルする。選択／確定はイベントで ShellWindow へ委ねる
/// （プレビュータブ表示・行ジャンプは View 側の責務）。
/// </summary>
public sealed partial class SearchPanelViewModel : ObservableObject
{
    private readonly IWorkspaceSearchService _search;
    private CancellationTokenSource? _cts;

    /// <summary>1ヒットを「エディタでプレビュー」したい（単クリック・キーボード選択）。</summary>
    public event EventHandler<SearchHit>? PreviewRequested;

    /// <summary>1ヒットを通常タブで開きたい（Enter・ダブルクリック）。</summary>
    public event EventHandler<SearchHit>? ActivateRequested;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private string _includeGlob = "";
    [ObservableProperty] private string _excludeGlob = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>true ＝ ファイル名で検索、false ＝ ファイル内容を grep。</summary>
    [ObservableProperty] private bool _searchByName;

    /// <summary>クエリ欄のプレースホルダ（モードで文言を変える）。</summary>
    public string QueryPlaceholder => SearchByName ? "ファイル名で検索" : "検索ワード（ファイル内を grep）";

    public ObservableCollection<SearchFileGroup> Results { get; } = new();

    public SearchPanelViewModel(IWorkspaceSearchService search) => _search = search;

    partial void OnQueryChanged(string value) => ScheduleSearch();
    partial void OnCaseSensitiveChanged(bool value) => ScheduleSearch();
    partial void OnUseRegexChanged(bool value) => ScheduleSearch();
    partial void OnIncludeGlobChanged(string value) => ScheduleSearch();
    partial void OnExcludeGlobChanged(string value) => ScheduleSearch();

    partial void OnSearchByNameChanged(bool value)
    {
        OnPropertyChanged(nameof(QueryPlaceholder));
        ScheduleSearch();
    }

    /// <summary>入力が変わるたびに直前の検索をキャンセルし、少し待ってから再検索する。</summary>
    private void ScheduleSearch()
    {
        _cts?.Cancel();

        if (string.IsNullOrEmpty(Query))
        {
            Results.Clear();
            StatusMessage = "";
            IsBusy = false;
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

            if (SearchByName)
                await RunFindFilesAsync(ct);
            else
                await RunGrepAsync(ct);
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

        var hits = await _search.GrepAsync(Query, options, ct);
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
        var hits = await _search.FindFilesAsync(Query, 500, ct);
        if (ct.IsCancellationRequested)
            return;

        Results.Clear();
        foreach (var hit in hits)
            Results.Add(new SearchFileGroup(hit.FullPath, hit.RelativePath, Array.Empty<SearchMatchItem>()));

        StatusMessage = Results.Count == 0 ? "一致なし" : $"{Results.Count} ファイル";
    }

    /// <summary>
    /// エディタで全マッチをハイライトする検索ワード。Editor の <c>HighlightSearch</c> は
    /// literal substring マッチなので、リテラル grep のときだけ渡す（正規表現／ファイル名検索では空）。
    /// </summary>
    private string HighlightTerm => !SearchByName && !UseRegex ? Query : "";

    public void Preview(SearchMatchItem match)
        => PreviewRequested?.Invoke(this, new SearchHit(match.FullPath, match.Line, match.Column, HighlightTerm));

    public void Activate(SearchMatchItem match)
        => ActivateRequested?.Invoke(this, new SearchHit(match.FullPath, match.Line, match.Column, HighlightTerm));

    /// <summary>ファイル名ヒット（グループ見出し自体）を開く。先頭（1,1）へジャンプする。</summary>
    public void Preview(SearchFileGroup group)
        => PreviewRequested?.Invoke(this, new SearchHit(group.FullPath, 1, 1));

    public void Activate(SearchFileGroup group)
        => ActivateRequested?.Invoke(this, new SearchHit(group.FullPath, 1, 1));

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

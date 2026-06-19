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
    public string Header => $"{RelativePath}  ({Count})";
}

/// <summary>
/// サイドバー Search パネルの ViewModel。クエリ・オプション（大小区別／正規表現／include・exclude glob）で
/// <see cref="IWorkspaceSearchService.GrepAsync"/> を走らせ、結果をファイル別にグルーピングして保持する。
/// 入力はデバウンスし、直前の検索はキャンセルする。選択／確定はイベントで ShellWindow へ委ねる
/// （プレビュータブ表示・行ジャンプは View 側の責務）。
/// </summary>
public sealed partial class SearchPanelViewModel : ObservableObject
{
    private readonly IWorkspaceSearchService _search;
    private CancellationTokenSource? _cts;

    /// <summary>1ヒットを「エディタでプレビュー」したい（単クリック・キーボード選択）。</summary>
    public event EventHandler<SearchMatchItem>? PreviewRequested;

    /// <summary>1ヒットを通常タブで開きたい（Enter・ダブルクリック）。</summary>
    public event EventHandler<SearchMatchItem>? ActivateRequested;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private string _includeGlob = "";
    [ObservableProperty] private string _excludeGlob = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    public ObservableCollection<SearchFileGroup> Results { get; } = new();

    public SearchPanelViewModel(IWorkspaceSearchService search) => _search = search;

    partial void OnQueryChanged(string value) => ScheduleSearch();
    partial void OnCaseSensitiveChanged(bool value) => ScheduleSearch();
    partial void OnUseRegexChanged(bool value) => ScheduleSearch();
    partial void OnIncludeGlobChanged(string value) => ScheduleSearch();
    partial void OnExcludeGlobChanged(string value) => ScheduleSearch();

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
        catch (OperationCanceledException) { /* 新しい入力に置き換わった */ }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsBusy = false;
        }
    }

    public void Preview(SearchMatchItem match) => PreviewRequested?.Invoke(this, match);
    public void Activate(SearchMatchItem match) => ActivateRequested?.Invoke(this, match);

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

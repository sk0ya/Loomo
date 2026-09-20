using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>1回の検索の結果（一覧と、入力欄の下に出す状態文言）。</summary>
public sealed record PaletteSearchOutcome(IReadOnlyList<PaletteCommand> Items, string Status);

/// <summary>
/// コマンドパレットの「探して飛ぶ」側の段取り——待ち（デバウンス）・打ち直しのキャンセル・
/// 供給元（ファイル名／全文／シンボル）の振り分けを一手に持つ。ShellWindow は現在の入力を渡して
/// 返ってきたものを描くだけ（`docs/完了済み/ShellWindow責務境界.md`）。
/// <para>追い越された問い合わせは <c>null</c> を返す＝**表示に触らない**。古い結果が新しい入力を
/// 上書きする、という一覧検索でいちばん起きやすい壊れ方をここ1か所で止める。</para>
/// </summary>
public sealed class PaletteSearchCoordinator
{
    /// <summary>全文・シンボルはこの文字数から走らせる（1文字で部屋中を総なめにしない）。</summary>
    public const int MinQueryChars = 2;

    private readonly IWorkspaceSearchService _search;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<PaletteLocation>>> _symbolSearch;
    private readonly int _searchDelayMs;
    private readonly int _previewDelayMs;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;

    /// <param name="symbolSearch">シンボルの供給口（言語サーバー）。セッションは ShellWindow が持つので外から渡す。</param>
    /// <param name="searchDelayMs">入力が続いている間まとめる時間。検索ペインと同じ 160ms。</param>
    /// <param name="previewDelayMs">↑↓で流している間、通り過ぎた行を読まないための短い待ち。</param>
    public PaletteSearchCoordinator(
        IWorkspaceSearchService search,
        Func<string, CancellationToken, Task<IReadOnlyList<PaletteLocation>>> symbolSearch,
        int searchDelayMs = 160,
        int previewDelayMs = 70)
    {
        _search = search;
        _symbolSearch = symbolSearch;
        _searchDelayMs = searchDelayMs;
        _previewDelayMs = previewDelayMs;
    }

    public void Cancel()
    {
        CancelSearch();
        CancelPreview();
    }

    public void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }

    public void CancelPreview()
    {
        _previewCts?.Cancel();
        _previewCts = null;
    }

    /// <summary>
    /// 候補を探す。<paramref name="onStatus"/> は待ちに入る前に同期で呼ぶので、呼び出し側の
    /// スレッド（＝UI）で「検索中…」を出せる。戻り値 null は「追い越された／キャンセルされた」で、
    /// このときは一覧も状態も触らないのが正しい。
    /// </summary>
    public async Task<PaletteSearchOutcome?> SearchAsync(
        PaletteQuery query, Func<PaletteTarget, Action> jump, Action<string> onStatus)
    {
        CancelSearch();
        if (!query.IsNavigation || query.Mode is PaletteMode.All or PaletteMode.Line)
            return null;

        // ファイル名だけは空クエリでも意味がある（全件の先頭が出る＝一覧代わりに使える）。
        if (query.Mode != PaletteMode.File && query.Text.Length < MinQueryChars)
            return new PaletteSearchOutcome(Array.Empty<PaletteCommand>(), $"{MinQueryChars} 文字以上で検索します");

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;
        onStatus("検索中…");
        try
        {
            await Task.Delay(_searchDelayMs, ct);
            var items = query.Mode switch
            {
                PaletteMode.File => PaletteNavigationItems.ForFiles(
                    await _search.FindFilesAsync(query.Text, PaletteNavigationItems.MaxResults, ct), jump),
                PaletteMode.Text => PaletteNavigationItems.ForText(
                    await _search.GrepAsync(query.Text,
                        new GrepOptions(MaxResults: PaletteNavigationItems.MaxResults), ct),
                    query.Text, jump),
                _ => PaletteNavigationItems.ForLocations(await _symbolSearch(query.Text, ct), jump),
            };
            return ct.IsCancellationRequested ? null : new PaletteSearchOutcome(items, StatusFor(query, items.Count));
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            return ct.IsCancellationRequested
                ? null
                : new PaletteSearchOutcome(Array.Empty<PaletteCommand>(), $"検索に失敗しました: {ex.Message}");
        }
    }

    /// <summary>コマンド・ファイル・全文・シンボルを同時に検索し、結果を種別ごとにまとめて返す。</summary>
    public async Task<PaletteSearchOutcome?> SearchAllAsync(
        PaletteQuery query,
        IReadOnlyList<PaletteCommand> commands,
        Func<PaletteTarget, Action> jump,
        Action<string> onStatus)
    {
        CancelSearch();
        if (query.Mode != PaletteMode.All)
            return null;

        var commandItems = PaletteFilter.Filter(commands, query.Text)
            .Take(PaletteNavigationItems.MaxResults)
            .ToArray();
        if (query.Text.Length < MinQueryChars)
        {
            var hint = query.Text.Length == 0
                ? "入力すると全対象から検索します"
                : $"{MinQueryChars} 文字以上で全対象を検索します";
            return new PaletteSearchOutcome(commandItems, hint);
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;
        onStatus("すべてを検索中…");
        try
        {
            await Task.Delay(_searchDelayMs, ct);
            var filesTask = CaptureSearchAsync(
                () => _search.FindFilesAsync(query.Text, PaletteNavigationItems.MaxResults, ct), ct);
            var textTask = CaptureSearchAsync(
                () => _search.GrepAsync(query.Text,
                    new GrepOptions(MaxResults: PaletteNavigationItems.MaxResults), ct), ct);
            var symbolsTask = CaptureSearchAsync(() => _symbolSearch(query.Text, ct), ct);
            await Task.WhenAll(filesTask, textTask, symbolsTask);
            if (ct.IsCancellationRequested)
                return null;

            var files = await filesTask;
            var text = await textTask;
            var symbols = await symbolsTask;
            var items = new List<PaletteCommand>(commandItems.Length + files.Items.Count + text.Items.Count + symbols.Items.Count);
            items.AddRange(commandItems);
            items.AddRange(PaletteNavigationItems.ForFiles(files.Items, jump));
            items.AddRange(PaletteNavigationItems.ForText(text.Items, query.Text, jump));
            items.AddRange(PaletteNavigationItems.ForLocations(symbols.Items, jump));

            var failures = new[] { files.Error, text.Error, symbols.Error }
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .ToArray();
            var status = failures.Length == 0
                ? StatusFor(query, items.Count)
                : items.Count == 0
                    ? $"検索に失敗しました: {string.Join(" / ", failures)}"
                    : $"{items.Count} 件（一部の検索に失敗）";
            return new PaletteSearchOutcome(items, status);
        }
        catch (OperationCanceledException) { return null; }
    }

    private sealed record SearchBatch<T>(IReadOnlyList<T> Items, string? Error);

    private static async Task<SearchBatch<T>> CaptureSearchAsync<T>(
        Func<Task<IReadOnlyList<T>>> search, CancellationToken ct)
    {
        try
        {
            return new SearchBatch<T>(await search(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SearchBatch<T>(Array.Empty<T>(), ex.Message);
        }
    }

    /// <summary>選択中の場所の中身を読む。追い越されたら null（前の表示を残す）。</summary>
    public async Task<PalettePreviewContent?> PreviewAsync(PaletteTarget target, string displayPath)
    {
        CancelPreview();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var ct = cts.Token;
        try
        {
            await Task.Delay(_previewDelayMs, ct);
            // 読むのはバックグラウンド（大きいファイルでも入力欄の打鍵を止めない）。
            var content = await Task.Run(() => PalettePreviewLoader.LoadAsync(target, displayPath, ct), ct);
            return ct.IsCancellationRequested ? null : content;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            return ct.IsCancellationRequested
                ? null
                : new PalettePreviewContent(System.IO.Path.GetFileName(target.FullPath), displayPath,
                    $"プレビューできませんでした: {ex.Message}", Array.Empty<PalettePreviewLine>(), null);
        }
    }

    private static string StatusFor(PaletteQuery query, int count) => count switch
    {
        0 when query.Mode == PaletteMode.Symbol => "一致なし（言語サーバーが未接続かもしれません）",
        0 => "一致なし",
        _ => $"{count} 件",
    };
}

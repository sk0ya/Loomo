using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Editor.Core.Syntax;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>DiffSessionViewModel の差分本体パート：選択ファイルの差分行（統合／左右）を組み立て、
/// 表示中の形式のコレクションだけを差し替える。git パッチのキャッシュもここで持つ。</summary>
public sealed partial class DiffSessionViewModel
{
    // ===== 差分本体 =====

    /// <summary>読込の世代番号。読込中に選択や一覧が変わったとき、古い結果の適用を捨てる。</summary>
    private int _diffLoadVersion;

    /// <summary>統合表示の各行と1対1の構文トークン列（色付けしない差分では空）。ビューは行の入れ替え時に
    /// これを読んで <see cref="Run"/> を割る。<b>行と必ず同時に差し替える</b>——別々に置くと、次のファイルの
    /// 読込が始まった瞬間に前提だけ先へ進み、まだ前の行を出している最中のビューが「別ファイルの言語」で
    /// 色を付けてしまう（行が入れ替われば直るが、一瞬化ける）。</summary>
    public IReadOnlyList<SyntaxToken[]?> UnifiedSyntax { get; private set; } = DiffSyntaxHighlighter.None;

    /// <summary>左右並び表示の左側（旧）の構文トークン列。<see cref="UnifiedSyntax"/> と同じ約束。</summary>
    public IReadOnlyList<SyntaxToken[]?> SideSyntaxLeft { get; private set; } = DiffSyntaxHighlighter.None;

    /// <summary>左右並び表示の右側（新）の構文トークン列。<see cref="UnifiedSyntax"/> と同じ約束。</summary>
    public IReadOnlyList<SyntaxToken[]?> SideSyntaxRight { get; private set; } = DiffSyntaxHighlighter.None;

    /// <summary>統合表示の組み立て結果（行＋その行の構文トークン）。行と色付けを一組で運ぶための器。</summary>
    private sealed record UnifiedContent(List<DiffRowVm> Rows, IReadOnlyList<SyntaxToken[]?> Syntax);

    /// <summary>左右並び表示の組み立て結果（行＋左右それぞれの構文トークン）。</summary>
    private sealed record SideContent(
        List<DiffSideRowVm> Rows,
        IReadOnlyList<SyntaxToken[]?> LeftSyntax,
        IReadOnlyList<SyntaxToken[]?> RightSyntax);

    /// <summary>
    /// 差分本体を読み込む。全行を組み立ててから、現在の表示と異なるときだけ差し替える
    /// （Clear→await→再追加だと自動更新のたびに空白が見えてチラつくため）。
    /// 表示中の形式（統合／左右）のコレクションだけを組み立てる。
    /// </summary>
    private async Task LoadDiffAsync(DiffFileItem? item)
    {
        var version = ++_diffLoadVersion;
        await LoadHunksAsync(item, version);
        if (IsSideBySide)
        {
            var content = await BuildSideContentAsync(item);
            if (version != _diffLoadVersion)
                return; // より新しい読込が始まっている
            SideSyntaxLeft = content.LeftSyntax;
            SideSyntaxRight = content.RightSyntax;
            ReplaceIfChanged(SideRows, content.Rows);
        }
        else
        {
            var content = await BuildUnifiedContentAsync(item);
            if (version != _diffLoadVersion)
                return;
            UnifiedSyntax = content.Syntax;
            ReplaceIfChanged(DiffRows, content.Rows);
        }
    }

    /// <summary>同一内容なら再描画しない差し替え（行 VM は record なので値比較）。</summary>
    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, List<T> rows)
    {
        if (rows.Count == target.Count && rows.SequenceEqual(target))
            return;
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    /// <summary>左右並びの全文表示で使うコンテキスト行数（ファイル全体を含めるための大きな値）。</summary>
    private const int FullFileContext = 1_000_000;

    /// <summary>
    /// 取得済み Git パッチのキャッシュ（同一ファイル参照×コンテキスト行数で引く）。表示形式の切替
    /// （統合↔左右）では git を再実行せずここから返す。一覧やリポジトリ／ジャーナルが変わるたびに
    /// <see cref="RefreshAsync"/> 冒頭で破棄するので、作業ツリーの変化には追従する。
    /// </summary>
    private readonly Dictionary<(DiffFileItem Item, int Context), string> _patchCache = new();

    /// <summary>
    /// 作業ツリー git 差分のパッチキャッシュを、その1ファイル分だけ捨てる。ファイルを選択し直すたびに呼び、
    /// 別ファイルの差分を見てから戻ってきたときに編集後の最新差分を読み直せるようにする（表示形式の
    /// 切替時は選択が変わらないので走らず、その用途のキャッシュは保たれる）。AI変更は内容が item に
    /// 閉じ、コミット範囲は不変なので対象外（どちらも <see cref="DiffFileItem.Entry"/> が null）。
    /// </summary>
    private void InvalidateWorkingTreePatch(DiffFileItem? item)
    {
        if (item?.Entry is null) return;
        foreach (var key in _patchCache.Keys.Where(k => k.Item == item).ToList())
            _patchCache.Remove(key);
    }

    /// <summary>Git 差分のパッチテキストを取得する（作業ツリー／コミット範囲）。同じファイルの再取得はキャッシュで省く。</summary>
    private async Task<string> GetPatchTextAsync(DiffFileItem item, int contextLines)
    {
        var key = (item, contextLines);
        if (_patchCache.TryGetValue(key, out var cached))
            return cached;
        var text = await (item.CommitFile is { } commitFile && _commitRange is { } range
            ? _git.GetRangeFileDiffAsync(range.From, range.To, commitFile, contextLines)
            : _git.GetDiffTextAsync(item.Entry!, item.IsStaged, contextLines));
        _patchCache[key] = text;
        return text;
    }

    private const string TooLargeMessage = "（ファイルが大きいため全文を保持していません。差分を表示できません）";
    private const string NoDiffMessage = "（差分はありません）";

    // 差分の組み立て（LCS・パッチ解析・字句解析）は WPF に一切触れない純粋な計算だが、行数に比例して
    // 重い（数千行で数百ms〜）。UI スレッドで走らせるとその間ペインごと固まるので Task.Run へ逃がす。
    // 逃がせるのはここまで——この後の FlowDocument 構築とレイアウトは UI スレッドでしかできない。

    private async Task<UnifiedContent> BuildUnifiedContentAsync(DiffFileItem? item)
    {
        if (item is null) return new UnifiedContent(new List<DiffRowVm>(), DiffSyntaxHighlighter.None);
        var path = item.FullPath;

        if (item.UsesInlineContent)
        {
            if (item.OldContent is null || item.NewContent is null)
                return UnifiedMessage(TooLargeMessage);
            var (oldText, newText) = (item.OldContent, item.NewContent);
            return await Task.Run(() =>
            {
                var rows = new List<DiffRowVm>();
                foreach (var line in DiffUtil.Compute(oldText, newText))
                    rows.Add(new DiffRowVm(line.Kind.ToString(), line.Text));
                // AI変更・比較は全文2つから組み立てる経路で、行は本文そのもの（パッチの1文字プレフィックス無し）。
                return new UnifiedContent(
                    rows, DiffSyntaxHighlighter.ForUnified(path, hasPatchPrefix: false, rows));
            });
        }

        var text = await GetPatchTextAsync(item, 3);
        if (text.Length == 0) return UnifiedMessage(NoDiffMessage);
        return await Task.Run(() =>
        {
            var rows = new List<DiffRowVm>();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                rows.Add(new DiffRowVm(SideBySideDiff.ClassifyPatchLine(raw).ToString(), raw));
            return new UnifiedContent(
                rows, DiffSyntaxHighlighter.ForUnified(path, hasPatchPrefix: true, rows));
        });
    }

    private async Task<SideContent> BuildSideContentAsync(DiffFileItem? item)
    {
        if (item is null)
            return new SideContent(
                new List<DiffSideRowVm>(), DiffSyntaxHighlighter.None, DiffSyntaxHighlighter.None);
        var path = item.FullPath;

        if (item.UsesInlineContent)
        {
            if (item.OldContent is null || item.NewContent is null)
                return SideMessage(TooLargeMessage);
            var (oldText, newText) = (item.OldContent, item.NewContent);
            // 左右は実際のファイルのように全文を行番号付きで対比する（ハンク折りたたみなし）
            return await Task.Run(() =>
                WithSideSyntax(path, ToSideRows(SideBySideDiff.Build(DiffUtil.ComputeFull(oldText, newText)))));
        }

        // 全文コンテキストの diff を取り、git ヘッダ・ハンク見出しを隠してファイルそのものに見せる
        var text = await GetPatchTextAsync(item, FullFileContext);
        if (text.Length == 0) return SideMessage(NoDiffMessage);
        return await Task.Run(() =>
            WithSideSyntax(path, ToSideRows(SideBySideDiff.FromUnifiedPatch(text, hideChrome: true))));
    }

    private static UnifiedContent UnifiedMessage(string message)
        => new([new DiffRowVm("Header", message)], DiffSyntaxHighlighter.None);

    private static SideContent SideMessage(string message)
        => new([SharedRow("Header", message)], DiffSyntaxHighlighter.None, DiffSyntaxHighlighter.None);

    private static SideContent WithSideSyntax(string path, List<DiffSideRowVm> rows)
        => new(rows,
            DiffSyntaxHighlighter.ForSide(path, rows, left: true),
            DiffSyntaxHighlighter.ForSide(path, rows, left: false));

    // ===== ハンク単位ステージ =====

    /// <summary>
    /// ハンク単位ステージ／アンステージの対象となるファイルか。作業ツリーの追跡済みファイル
    /// （AI変更・コミット範囲・未追跡・コンフリクトは対象外。これらは部分ステージできない／意味がない）。
    /// </summary>
    private static bool SupportsHunkStaging(DiffFileItem? item)
        => item is { IsAi: false, CommitFile: null, Entry: { IsUntracked: false, IsConflicted: false } };

    /// <summary>選択ファイルのハンク一覧を組み立てる（対象外なら空にする）。コンテキスト3のパッチを使う。
    /// <paramref name="version"/> は <see cref="LoadDiffAsync"/> の読込世代。await の間に新しい読込が
    /// 始まっていたら（version 不一致）Hunks には触れず、別ファイルのハンクで上書きしないようにする。</summary>
    private async Task LoadHunksAsync(DiffFileItem? item, int version)
    {
        if (item is null || _commitRange is not null || !SupportsHunkStaging(item))
        {
            if (version != _diffLoadVersion) return; // より新しい読込が Hunks を所有している
            Hunks.Clear();
            OnPropertyChanged(nameof(CanStageHunks));
            return;
        }

        var text = await GetPatchTextAsync(item, 3);
        if (version != _diffLoadVersion)
            return; // より新しい読込が始まっている（Hunks は触らない）

        var split = GitPatchSplitter.Split(text);
        Hunks.Clear();
        for (var i = 0; i < split.Hunks.Count; i++)
            Hunks.Add(new DiffHunkVm(i, split.Hunks[i].HeaderLine,
                SummarizeHunk(split.Hunks[i]), item.IsStaged));
        OnPropertyChanged(nameof(CanStageHunks));
    }

    /// <summary>ハンクの簡易サマリ（@@ 行＋増減行数）。</summary>
    private static string SummarizeHunk(GitPatchSplitter.Hunk hunk)
    {
        int added = 0, removed = 0;
        foreach (var line in hunk.Text.Split('\n'))
        {
            if (line.StartsWith("+") && !line.StartsWith("+++")) added++;
            else if (line.StartsWith("-") && !line.StartsWith("---")) removed++;
        }
        return $"{hunk.HeaderLine}   +{added} −{removed}";
    }

    /// <summary>ハンク単位でステージ／アンステージする。ステージ済みハンクは逆適用（アンステージ）になる。</summary>
    [RelayCommand]
    private async Task ToggleHunkAsync(DiffHunkVm? hunk)
    {
        if (hunk is null || SelectedFile is not { } item || !SupportsHunkStaging(item))
            return;

        // 最新のパッチを取り直してから対象ハンクを切り出す（表示後に作業ツリーが変わっていても整合させる）。
        var text = await GetPatchTextAsync(item, 3);
        var split = GitPatchSplitter.Split(text);
        if (hunk.Index < 0 || hunk.Index >= split.Hunks.Count)
        {
            SetStatus("ハンクが変化したため適用できませんでした。差分を開き直してください。", isError: true);
            return;
        }

        var patch = GitPatchSplitter.BuildSingleHunkPatch(split.Header, split.Hunks[hunk.Index]);
        // ステージ済みファイルのハンク＝逆適用でアンステージ、未ステージ＝順適用でステージ。
        var result = await _git.ApplyCachedPatchAsync(patch, reverse: item.IsStaged);
        if (result.Success)
            SetStatus(item.IsStaged ? "ハンクをアンステージしました。" : "ハンクをステージしました。", isError: false);
        else
            SetStatus($"ハンクの適用に失敗しました: {result.Message}", isError: true);
        // RepositoryChanged が RefreshAsync を呼び、一覧・差分・ハンクが更新される。
    }

    private static DiffSideRowVm SharedRow(string kind, string text) => new(kind, text, kind, text, "", "");

    private static List<DiffSideRowVm> ToSideRows(IReadOnlyList<SideBySideRow> source)
    {
        var rows = new List<DiffSideRowVm>(source.Count);
        foreach (var row in source)
            rows.Add(new DiffSideRowVm(
                row.LeftKind.ToString(), row.LeftText, row.RightKind.ToString(), row.RightText,
                row.LeftLine?.ToString() ?? "", row.RightLine?.ToString() ?? ""));
        return rows;
    }
}


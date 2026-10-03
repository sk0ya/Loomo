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

    /// <summary>
    /// <see cref="SideRows"/> がどの項目の差分か。左右並びの本文はエディタ2つで出すので、どのファイルを
    /// 右で編集させるか・見出しを何にするかは<b>行と同じ項目</b>から決める——<see cref="SelectedFile"/> で
    /// 決めると、選択が移ってから新しい行が届くまでの間に「前のファイルの行を、次のファイルとして編集させる」
    /// 取り違えが起きる。行と必ず同時に差し替える。
    /// </summary>
    public DiffFileItem? SideRowsItem { get; private set; }

    /// <summary>
    /// 左右並びの右側を、そのファイルとして編集・保存できるならそのパス（できなければ null）。
    /// 右側が作業ツリーのファイルそのものである差分だけ——未ステージの変更と、比較基準（ブランチ／
    /// 分岐点）に対する作業ツリー。ステージ済み（右はインデックス）・コミット範囲・アドホック比較・
    /// コンフリクト・消えたファイルは読み取り専用で出す。
    /// </summary>
    public string? EditableSidePath => SideRowsItem is { } item && IsWorkingTreeRight(item) ? item.FullPath : null;

    /// <summary>左右並びの左側の見出し（エディタの文書名。ステータスバーに出る）。</summary>
    public string SideLeftTitle => SideRowsItem switch
    {
        { Comparison: { } comparison } => comparison.LeftTitle,
        { } item => $"{item.FileName}（旧）",
        null => "",
    };

    /// <summary>左右並びの右側の見出し（読み取り専用で出すとき）。</summary>
    public string SideRightTitle => SideRowsItem switch
    {
        { Comparison: { } comparison } => comparison.RightTitle,
        { } item => $"{item.FileName}（新）",
        null => "",
    };

    private bool IsWorkingTreeRight(DiffFileItem item)
        => _commitRange is null
           && item is { Comparison: null, CommitFile: null, FullPath.Length: > 0 }
           && (item.Entry is { IsConflicted: false } || item.CompareBaseFile is not null)
           && File.Exists(item.FullPath);

    /// <summary>
    /// 右のエディタで保存前の編集が入ったので、その本文で取り直した行へ差し替える（ビューが呼ぶ）。
    /// 次/前の変更・中央の帯は、いま見えている行を数えるようになる。保存すると git の読み直しが同じ本文の
    /// 行を返すので、表示は動かない。
    /// </summary>
    public void ApplyLiveSideRows(IReadOnlyList<DiffSideRowVm> rows)
    {
        if (!IsSideBySide || SideRowsItem is null) return;
        ReplaceIfChanged(SideRows, rows.ToList());
    }

    /// <summary>統合表示の組み立て結果（行＋その行の構文トークン）。行と色付けを一組で運ぶための器。</summary>
    private sealed record UnifiedContent(List<DiffRowVm> Rows, IReadOnlyList<SyntaxToken[]?> Syntax);

    /// <summary>左右並び表示の組み立て結果。構文の色はエディタ自身が付ける。</summary>
    private sealed record SideContent(List<DiffSideRowVm> Rows);

    /// <summary>
    /// 差分本体を読み込む。全行を組み立ててから、現在の表示と異なるときだけ差し替える
    /// （Clear→await→再追加だと自動更新のたびに空白が見えてチラつくため）。
    /// 表示中の形式（統合／左右）のコレクションだけを組み立てる。
    /// </summary>
    private async Task LoadDiffAsync(DiffFileItem? item)
    {
        var version = ++_diffLoadVersion;
        if (UseMarkdownRender(item))
        {
            // レンダリング表示中はテキスト行を組み立てない（画面から退けてあるものを作っても捨てるだけ）。
            var render = await BuildMarkdownRenderAsync(item!);
            if (version != _diffLoadVersion)
                return; // より新しい読込が始まっている
            ApplyMarkdownRender(render);
            return;
        }
        if (version != _diffLoadVersion)
            return;                  // より新しい読込が始まっている
        // モードを戻したら前の HTML を残さない。**世代チェックの内側で**消すのが要点——外でやると、
        // 遅れて再開した古い読込が、既に出ている新しいレンダリング結果を消して真っ白なペインにする。
        MarkdownRenderHtml = null;
        MarkdownRenderChangeCount = 0;
        MarkdownRenderNotice = "";
        // 行ごとの「ステージ済み」の印。差分本体と同じ読込の中で引く（印だけ古い／新しいのずれを作らない）。
        var stageMap = await LoadStageMapAsync(item);
        if (IsSideBySide)
        {
            var content = await BuildSideContentAsync(item);
            if (version != _diffLoadVersion)
                return; // より新しい読込が始まっている
            SetSideRowsItem(item);
            ReplaceIfChanged(SideRows, stageMap is null ? content.Rows : MarkStaged(content.Rows, stageMap));
        }
        else
        {
            var content = await BuildUnifiedContentAsync(item);
            var rows = stageMap is null ? content.Rows : MarkStaged(content.Rows, await GetPatchTextAsync(item!, 3), stageMap);
            if (version != _diffLoadVersion)
                return;
            UnifiedSyntax = content.Syntax;
            ReplaceIfChanged(DiffRows, rows);
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
    /// （統合↔左右）では git を再実行せずここから返す。一覧やリポジトリが変わるたびに
    /// <see cref="RefreshAsync"/> 冒頭で破棄するので、作業ツリーの変化には追従する。
    /// </summary>
    private readonly Dictionary<(DiffFileItem Item, int Context), string> _patchCache = new();

    /// <summary>
    /// 作業ツリー git 差分のパッチキャッシュを、その1ファイル分だけ捨てる。ファイルを選択し直すたびに呼び、
    /// 別ファイルの差分を見てから戻ってきたときに編集後の最新差分を読み直せるようにする（表示形式の
    /// 切替時は選択が変わらないので走らず、その用途のキャッシュは保たれる）。アドホック比較は内容が item に
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
        // 比較基準の項目は、その項目が作られたときの ref で引く（項目と ref を一緒に持たせてある）。
        var text = await (item.CommitFile is { } commitFile && _commitRange is { } range
            ? _git.GetRangeFileDiffAsync(range.From, range.To, commitFile, contextLines)
            : item.CompareBaseFile is { } compareFile
                ? _git.GetCompareFileDiffAsync(compareFile.BaseRef, compareFile.Change, contextLines)
                : _git.GetHeadDiffTextAsync(item.Entry!, contextLines));
        _patchCache[key] = text;
        return text;
    }

    private const string NoDiffMessage = "（差分はありません）";

    // 差分の組み立て（LCS・パッチ解析・字句解析）は WPF に一切触れない純粋な計算だが、行数に比例して
    // 重い（数千行で数百ms〜）。UI スレッドで走らせるとその間ペインごと固まるので Task.Run へ逃がす。
    // 逃がせるのはここまで——この後の FlowDocument 構築とレイアウトは UI スレッドでしかできない。

    private async Task<UnifiedContent> BuildUnifiedContentAsync(DiffFileItem? item)
    {
        if (item is null) return new UnifiedContent(new List<DiffRowVm>(), DiffSyntaxHighlighter.None);
        var path = item.FullPath;

        // アドホック比較は git を引かず、素材の全文2つから組み立てる。
        if (item.Comparison is { } comparison)
        {
            var (oldText, newText) = (comparison.LeftText, comparison.RightText);
            return await Task.Run(() =>
            {
                var rows = new List<DiffRowVm>();
                foreach (var line in DiffUtil.Compute(oldText, newText))
                    rows.Add(new DiffRowVm(line.Kind.ToString(), line.Text));
                // アドホック比較は全文2つから組み立てる経路で、行は本文そのもの（パッチの1文字プレフィックス無し）。
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
            return new SideContent(new List<DiffSideRowVm>());

        if (item.Comparison is { } comparison)
        {
            var (oldText, newText) = (comparison.LeftText, comparison.RightText);
            // 左右は実際のファイルのように全文を行番号付きで対比する（ハンク折りたたみなし）
            return await Task.Run(() =>
                new SideContent(ToSideRows(SideBySideDiff.Build(DiffUtil.ComputeFull(oldText, newText)))));
        }

        // 全文コンテキストの diff を取り、git ヘッダ・ハンク見出しを隠してファイルそのものに見せる
        var text = await GetPatchTextAsync(item, FullFileContext);
        if (text.Length == 0) return SideMessage(NoDiffMessage);
        return await Task.Run(() =>
            new SideContent(ToSideRows(SideBySideDiff.FromUnifiedPatch(text, hideChrome: true))));
    }

    private static UnifiedContent UnifiedMessage(string message)
        => new([new DiffRowVm("Header", message)], DiffSyntaxHighlighter.None);

    private static SideContent SideMessage(string message)
        => new([SharedRow("Header", message)]);

    /// <summary>行の出どころを差し替える。見出し・編集できるかが変わるので、行が同じでもビューに知らせる。</summary>
    private void SetSideRowsItem(DiffFileItem? item)
    {
        if (ReferenceEquals(SideRowsItem, item)) return;
        SideRowsItem = item;
        OnPropertyChanged(nameof(SideRowsItem));
    }

    /// <summary>
    /// 行・変更単位でステージ／アンステージできるファイルか。作業ツリーの追跡済みファイルだけ
    /// （コミット範囲・未追跡・コンフリクト・アドホック比較は対象外。これらは部分ステージできない／意味がない
    /// ——比較は <see cref="DiffFileItem.Entry"/> が null なのでこの条件で落ちる）。
    /// </summary>
    private static bool SupportsLineStaging(DiffFileItem? item)
        => item is { CommitFile: null, CompareBaseFile: null,
                     Entry: { IsUntracked: false, IsConflicted: false } };

    // ===== 行・変更単位のステージ／アンステージ／破棄 =====
    //
    // 差分本体は HEAD↔作業ツリーの1枚。行番号は「削除行＝HEAD の行」「追加行＝作業ツリーの行」で受け取り、
    // StagedChangeMap でステージ済み／未ステージに振り分けてから、それぞれの git 差分の行番号でパッチを作る。

    /// <summary>このファイルの HEAD↔インデックス（ステージ済み）とインデックス↔作業ツリー（未ステージ）の差分。
    /// 操作のたびに取り直す（キャッシュすると、別の場所でステージした後に古い行番号でパッチを作る）。</summary>
    private async Task<(string Staged, string Unstaged)> GetStagePatchesAsync(DiffFileItem item)
    {
        var staged = await _git.GetDiffTextAsync(item.Entry!, staged: true, 3);
        var unstaged = await _git.GetDiffTextAsync(item.Entry!, staged: false, 3);
        return (staged, unstaged);
    }

    /// <summary>行の印付けに使う対応表。ステージの概念が無い項目・何もステージしていない項目は null（印は全部「未」）。</summary>
    private async Task<StagedChangeMap?> LoadStageMapAsync(DiffFileItem? item)
    {
        if (item is null || _commitRange is not null || !SupportsLineStaging(item) || item.Stage == DiffStageState.None)
            return null;
        var (staged, unstaged) = await GetStagePatchesAsync(item);
        return await Task.Run(() => StagedChangeMap.Build(staged, unstaged));
    }

    private static List<DiffSideRowVm> MarkStaged(List<DiffSideRowVm> rows, StagedChangeMap map)
        => rows.Select(row => row with
        {
            LeftStaged = row.LeftKind == "Removed" && int.TryParse(row.LeftLine, out var head) && map.IsStagedRemoval(head),
            RightStaged = row.RightKind == "Added" && int.TryParse(row.RightLine, out var work) && map.IsStagedAddition(work),
        }).ToList();

    /// <summary>統合表示の行（パッチを改行で分割したものと1対1）へ印を付ける。</summary>
    private static List<DiffRowVm> MarkStaged(List<DiffRowVm> rows, string patch, StagedChangeMap map)
    {
        var lines = UnifiedPatchEditor.DescribeLines(patch);
        return rows.Select((row, i) => i < lines.Count && lines[i] is { Marker: not '\0' } line
            ? row with
            {
                Staged = line.Marker == '-' ? map.IsStagedRemoval(line.OldLine) : map.IsStagedAddition(line.NewLine),
            }
            : row).ToList();
    }

    /// <summary>統合表示で選んだ行（<see cref="DiffRows"/> の添字）を、HEAD／作業ツリーの行番号へ直す。</summary>
    public async Task<(IReadOnlySet<int> HeadLines, IReadOnlySet<int> WorktreeLines)> UnifiedRowsToLinesAsync(
        IReadOnlySet<int> rowIndices)
    {
        var head = new HashSet<int>();
        var worktree = new HashSet<int>();
        if (SelectedFile is not { } item) return (head, worktree);
        var lines = UnifiedPatchEditor.DescribeLines(await GetPatchTextAsync(item, 3));
        foreach (var index in rowIndices)
        {
            if (index < 0 || index >= lines.Count) continue;
            if (lines[index].Marker == '-') head.Add(lines[index].OldLine);
            else if (lines[index].Marker == '+') worktree.Add(lines[index].NewLine);
        }
        return (head, worktree);
    }

    /// <summary>
    /// 選んだ変更（削除行＝HEAD の行番号、追加行＝作業ツリーの行番号）をステージ（<paramref name="stage"/>）／
    /// アンステージする。選択にステージ済みと未ステージが混ざっていても、その向きに当てはまる行だけが動く。
    /// 作業ツリーには触れない。
    /// </summary>
    public async Task StageLinesAsync(IReadOnlySet<int> headLines, IReadOnlySet<int> worktreeLines, bool stage)
    {
        if (!CanStageLines || SelectedFile is not { } item) return;
        if (headLines.Count == 0 && worktreeLines.Count == 0) return;

        var (stagedPatch, unstagedPatch) = await GetStagePatchesAsync(item);
        var split = StagedChangeMap.Build(stagedPatch, unstagedPatch).Split(headLines, worktreeLines);
        // ステージ＝未ステージの差分（インデックス↔作業ツリー）から選んだ行だけをインデックスへ順適用。
        // アンステージ＝ステージ済みの差分（HEAD↔インデックス）から選んだ行だけをインデックスへ逆適用。
        var reduced = stage
            ? UnifiedPatchEditor.BuildStagePatchForLines(unstagedPatch, split.Unstaged.OldLines, split.Unstaged.NewLines)
            : UnifiedPatchEditor.BuildReverseDiscardPatchForLines(stagedPatch, split.Staged.OldLines, split.Staged.NewLines);
        var verb = stage ? "ステージ" : "アンステージ";
        if (reduced.IsEmpty)
        {
            SetStatus(stage ? "選んだ変更はすべてステージ済みです。" : "選んだ変更にステージ済みのものはありません。", isError: false);
            return;
        }

        // 成功すると RepositoryChanged が RefreshAsync を呼び、行の印・一覧の印が付け直される（行は消えない）。
        var result = await _git.ApplyCachedPatchAsync(reduced.Patch, reverse: !stage);
        if (result.Success)
            SetStatus($"{item.DisplayPath} の {reduced.LineCount} 行を{verb}しました。", isError: false);
        else
            SetStatus($"{verb}に失敗しました: {result.Message.Trim()}", isError: true);
    }

    /// <summary>
    /// 選んだ変更のうち<b>未ステージのもの</b>を作業ツリーから取り消す（インデックス↔作業ツリーの差分を逆適用）。
    /// ステージ済みの変更はインデックスに入っているので、作業ツリーを戻しても消えない——対象外として知らせる。
    /// </summary>
    public async Task DiscardLinesAsync(IReadOnlySet<int> headLines, IReadOnlySet<int> worktreeLines)
    {
        if (!CanDiscardLines || SelectedFile is not { } item) return;
        if (headLines.Count == 0 && worktreeLines.Count == 0) return;

        var (stagedPatch, unstagedPatch) = await GetStagePatchesAsync(item);
        var split = StagedChangeMap.Build(stagedPatch, unstagedPatch).Split(headLines, worktreeLines);
        var reduced = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(
            unstagedPatch, split.Unstaged.OldLines, split.Unstaged.NewLines);
        if (reduced.IsEmpty)
        {
            SetStatus("破棄できる未ステージの変更がありません（ステージ済みの変更は、アンステージしてから破棄してください）。",
                isError: false);
            return;
        }

        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} の選んだ {reduced.LineCount} 行ぶんの変更を破棄しますか？\n作業ツリーのその変更が失われます。",
            "変更の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // 適用が成功すると GitService が RepositoryChanged を発火し、一覧・差分は自動で読み直される。
        var result = await _commands.ApplyReverseAsync(reduced.Patch,
            $"{item.DisplayPath} の {reduced.LineCount} 行を破棄しました。");
        SetStatus(result.Message, !result.Success);
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


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Editor.Core.Syntax;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>DiffSessionViewModel の「通しで読む」パート（§24.50）：一覧の全ファイルの差分を、ファイルの見出しを挟んで
/// 1 本に並べる。コミットの前や、Claude Code がまとめて書き換えたあとに、どこを触ったかを切り替えずに一度に読む。
/// 行は統合表示の行そのもの（各ファイルのパッチ）で、どのファイルの行かは <see cref="DiffRowVm.Owner"/> が持つ。</summary>
public sealed partial class DiffSessionViewModel
{
    /// <summary>1 本に並べるファイルの上限。超えた分は一覧から 1 つずつ開く（組み立てと描画を際限なく重くしない）。</summary>
    internal const int ContinuousMaxFiles = 200;

    /// <summary>1 本に並べる行の上限（超えたところで打ち切る）。</summary>
    internal const int ContinuousMaxRows = 40_000;

    /// <summary>通しで読む（統合表示の一種。左右並びとは同時に効かない）。</summary>
    [ObservableProperty] private bool _isContinuous;

    // 表示形式の 3 択（左右／統合／通し）のラジオボタン用。true を書いたものへ切り替え、false は捨てる
    // （RadioButton は選択が移るとき旧選択に false を書くので、ここで捨てないと二重に切り替わる）。
    public bool IsLayoutSide
    {
        get => IsSideBySide && !IsContinuous;
        set { if (value) { IsContinuous = false; IsSideBySide = true; } }
    }

    public bool IsLayoutUnified
    {
        get => !IsSideBySide && !IsContinuous;
        set { if (value) { IsContinuous = false; IsSideBySide = false; } }
    }

    public bool IsLayoutContinuous
    {
        get => IsContinuous;
        set { if (value) { IsSideBySide = false; IsContinuous = true; } }
    }

    partial void OnIsContinuousChanged(bool value)
    {
        NotifyLayoutChoice();
        UpdateCanDiscard();
        ResetChangeCursor();
        NotifyMarkdownRenderState();
        _ = LoadAndAutoJumpAsync(SelectedFile);
    }

    private void NotifyLayoutChoice()
    {
        OnPropertyChanged(nameof(IsLayoutSide));
        OnPropertyChanged(nameof(IsLayoutUnified));
        OnPropertyChanged(nameof(IsLayoutContinuous));
    }

    /// <summary>ファイルの見出しの行。どのファイルか・増減の行数。</summary>
    internal static DiffRowVm ContinuousFileHeader(DiffFileItem item, int added, int removed)
        => new("Header", $"━━ {item.DisplayPath}　+{added} −{removed}", Owner: item);

    /// <summary>一覧の全ファイルを 1 本にする。各ファイルの行は 1 ファイルの統合表示と同じ組み立て（パッチのキャッシュも共有）。</summary>
    private async Task<UnifiedContent> BuildContinuousContentAsync()
    {
        var files = Files.ToList();
        var parts = new List<ContinuousPart>();
        var rowCount = 0;
        foreach (var item in files)
        {
            if (parts.Count >= ContinuousMaxFiles || rowCount >= ContinuousMaxRows)
                break;
            var content = await BuildUnifiedContentAsync(item);
            parts.Add(new ContinuousPart(item, content.Rows, content.Syntax, content.Inline));
            rowCount += content.Rows.Count + 1;
        }
        // 移動はファイルをまたいで探す（クラスへの抽出は別のファイルへ移る）ので、つないだ後の行で調べ直す。
        var joined = Join(parts, files.Count - parts.Count);
        return await Task.Run(() => joined.WithMoves());
    }

    /// <summary>1 ファイル分の行と、その行の構文・行内差分。</summary>
    internal sealed record ContinuousPart(
        DiffFileItem Item, IReadOnlyList<DiffRowVm> Rows,
        IReadOnlyList<SyntaxToken[]?> Syntax, IReadOnlyList<IReadOnlyList<TextSpan>?> Inline);

    /// <summary>ファイルごとの行を、見出しを挟んで 1 本につなぐ。構文・行内差分は行と 1 対 1 のまま（見出しは色なし）。</summary>
    private static UnifiedContent Join(IReadOnlyList<ContinuousPart> parts, int omittedFiles)
    {
        var rows = new List<DiffRowVm>();
        var syntax = new List<SyntaxToken[]?>();
        var inline = new List<IReadOnlyList<TextSpan>?>();
        foreach (var part in parts)
        {
            var added = part.Rows.Count(r => r.Kind == nameof(DiffLineKind.Added));
            var removed = part.Rows.Count(r => r.Kind == nameof(DiffLineKind.Removed));
            rows.Add(ContinuousFileHeader(part.Item, added, removed));
            syntax.Add(null);
            inline.Add(null);
            for (var i = 0; i < part.Rows.Count; i++)
            {
                rows.Add(part.Rows[i] with { Owner = part.Item });
                syntax.Add(i < part.Syntax.Count ? part.Syntax[i] : null);
                inline.Add(i < part.Inline.Count ? part.Inline[i] : null);
            }
        }
        if (omittedFiles > 0)
        {
            rows.Add(new DiffRowVm("Header", $"…ここまで（残り {omittedFiles} ファイルは一覧から 1 つずつ開いてください）"));
            syntax.Add(null);
            inline.Add(null);
        }
        return rows.Count == 0 ? UnifiedMessage(NoDiffMessage) : new UnifiedContent(rows, syntax, inline);
    }

    /// <summary>テスト用：つないだ行だけ。</summary>
    internal static IReadOnlyList<DiffRowVm> JoinForTests(IReadOnlyList<ContinuousPart> parts, int omittedFiles)
        => Join(parts, omittedFiles).Rows;

    /// <summary>そのファイルの見出しの行（無ければ -1）。</summary>
    internal static int ContinuousHeaderRow(IReadOnlyList<DiffRowVm> rows, DiffFileItem? item)
    {
        if (item is null) return -1;
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i].Owner, item) && rows[i].Kind == "Header")
                return i;
        return -1;
    }

    /// <summary>組み上がった 1 本の中で、そのファイルの見出しへ移る。まだそのファイルの行が無い（一覧が作り直された直後など）なら false。</summary>
    private bool TryScrollToContinuousFile(DiffFileItem? item)
    {
        var header = ContinuousHeaderRow(DiffRows, item);
        if (header < 0) return false;
        ResetChangeCursor();
        ScrollToRowRequested?.Invoke(header);
        return true;
    }

    /// <summary>その行のファイルと、そのファイルの行だけの範囲（見出しの次から、次のファイルの見出しの前まで）。</summary>
    internal static (DiffFileItem? Owner, int Start, int Count) ContinuousSlice(IReadOnlyList<DiffRowVm> rows, int index)
    {
        if (index < 0 || index >= rows.Count || rows[index].Owner is not { } owner) return (null, 0, 0);
        var start = index;
        while (start > 0 && ReferenceEquals(rows[start - 1].Owner, owner)) start--;
        var end = index;
        while (end + 1 < rows.Count && ReferenceEquals(rows[end + 1].Owner, owner)) end++;
        if (rows[start].Kind == "Header") start++;   // ファイルの見出しは本文ではない
        return (owner, start, Math.Max(0, end - start + 1));
    }

    /// <summary>通しで読んでいる行を、その行のファイルのその行で開く（見出しならファイルを開くだけ）。</summary>
    private void RequestOpenContinuousRowInEditor(int rowIndex)
    {
        var (owner, start, count) = ContinuousSlice(DiffRows, rowIndex);
        if (owner is not { FullPath.Length: > 0 })
        {
            SetStatus("この行には開けるファイルがありません。", isError: true);
            return;
        }
        var line = rowIndex < start
            ? 0
            : DiffRowLineMapper.LineForUnifiedRow(DiffRows.Skip(start).Take(count).ToList(), rowIndex - start, owner.FileIsLeft);
        EditorLineOpenRequested?.Invoke(this, (owner.FullPath, line));
    }
}

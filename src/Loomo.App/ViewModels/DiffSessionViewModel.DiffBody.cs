using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>
    /// 差分本体を読み込む。全行を組み立ててから、現在の表示と異なるときだけ差し替える
    /// （Clear→await→再追加だと自動更新のたびに空白が見えてチラつくため）。
    /// 表示中の形式（統合／左右）のコレクションだけを組み立てる。
    /// </summary>
    private async Task LoadDiffAsync(DiffFileItem? item)
    {
        var version = ++_diffLoadVersion;
        if (IsSideBySide)
        {
            var rows = await BuildSideRowsAsync(item);
            if (version != _diffLoadVersion)
                return; // より新しい読込が始まっている
            ReplaceIfChanged(SideRows, rows);
        }
        else
        {
            var rows = await BuildDiffRowsAsync(item);
            if (version != _diffLoadVersion)
                return;
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

    private async Task<List<DiffRowVm>> BuildDiffRowsAsync(DiffFileItem? item)
    {
        var rows = new List<DiffRowVm>();
        if (item is null) return rows;

        if (item.IsAi)
        {
            if (item.OldContent is null || item.NewContent is null)
            {
                rows.Add(new DiffRowVm("Header", TooLargeMessage));
                return rows;
            }
            foreach (var line in DiffUtil.Compute(item.OldContent, item.NewContent))
                rows.Add(new DiffRowVm(line.Kind.ToString(), line.Text));
            return rows;
        }

        var text = await GetPatchTextAsync(item, 3);
        if (text.Length == 0)
        {
            rows.Add(new DiffRowVm("Header", NoDiffMessage));
            return rows;
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            rows.Add(new DiffRowVm(SideBySideDiff.ClassifyPatchLine(raw).ToString(), raw));
        return rows;
    }

    private async Task<List<DiffSideRowVm>> BuildSideRowsAsync(DiffFileItem? item)
    {
        var rows = new List<DiffSideRowVm>();
        if (item is null) return rows;

        if (item.IsAi)
        {
            if (item.OldContent is null || item.NewContent is null)
            {
                rows.Add(SharedRow("Header", TooLargeMessage));
                return rows;
            }
            // 左右は実際のファイルのように全文を行番号付きで対比する（ハンク折りたたみなし）
            AddSideRows(rows, SideBySideDiff.Build(DiffUtil.ComputeFull(item.OldContent, item.NewContent)));
            return rows;
        }

        // 全文コンテキストの diff を取り、git ヘッダ・ハンク見出しを隠してファイルそのものに見せる
        var text = await GetPatchTextAsync(item, FullFileContext);
        if (text.Length == 0)
        {
            rows.Add(SharedRow("Header", NoDiffMessage));
            return rows;
        }
        AddSideRows(rows, SideBySideDiff.FromUnifiedPatch(text, hideChrome: true));
        return rows;
    }

    private static DiffSideRowVm SharedRow(string kind, string text) => new(kind, text, kind, text, "", "");

    private static void AddSideRows(List<DiffSideRowVm> rows, IReadOnlyList<SideBySideRow> source)
    {
        foreach (var row in source)
            rows.Add(new DiffSideRowVm(
                row.LeftKind.ToString(), row.LeftText, row.RightKind.ToString(), row.RightText,
                row.LeftLine?.ToString() ?? "", row.RightLine?.ToString() ?? ""));
    }
}


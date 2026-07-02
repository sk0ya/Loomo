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

/// <summary>DiffSessionViewModel の差分ナビゲーションパート：次/前の変更へのジャンプ、
/// ファイル跨ぎ、自動ジャンプ、変更ブロックのアンカー算出。</summary>
public sealed partial class DiffSessionViewModel
{
    // ===== 差分ナビゲーション（次/前の変更へジャンプ） =====

    /// <summary>差分本体の指定行（変更ブロックの先頭）までスクロールしてほしいことを View へ伝える。</summary>
    public event Action<int>? ScrollToRowRequested;

    /// <summary>ファイルを開いた／表示形式を切り替えたことを View へ伝える。View は差分の組み立てが
    /// 整ってから <see cref="JumpToFirstChange"/> を呼んで最初の変更へ自動ジャンプする。</summary>
    public event Action? AutoJumpRequested;

    /// <summary>「次/前の変更」の現在位置（<see cref="ChangeAnchors"/> の並びでのインデックス）。</summary>
    private int _changeCursor = -1;

    /// <summary>ファイル跨ぎで前のファイルへ移ったとき、自動ジャンプ先を「最後の変更」にするフラグ。</summary>
    private bool _pendingJumpToLast;

    /// <summary>blame クリック等からの「このファイルの新側この行へ」の保留ジャンプ（1始まり・-1 は無し）。
    /// 対象ファイル（<see cref="_pendingJumpFile"/>）の差分が組み上がった自動ジャンプ時に消費する。</summary>
    private int _pendingJumpNewLine = -1;
    private DiffFileItem? _pendingJumpFile;

    [RelayCommand]
    private void JumpToNextChange() => JumpChange(forward: true);

    [RelayCommand]
    private void JumpToPrevChange() => JumpChange(forward: false);

    /// <summary>
    /// ファイルを開いた／表示形式を切り替えた直後の自動ジャンプ先へ飛ぶ。通常は最初の変更、
    /// ファイル跨ぎで前のファイルへ移った直後（<see cref="_pendingJumpToLast"/>）だけ最後の変更。
    /// </summary>
    public void JumpToAutoTarget()
    {
        // blame からのピンポイントジャンプ：対象ファイルが選択されたままなら該当行へ。
        // 行が見つからないとき（コンテキスト外など）は消費せず通常の自動ジャンプへ落とす
        // （別ファイル読込の古い通知で走った場合も、対象ファイルの読込完了時にやり直せる）。
        if (_pendingJumpNewLine > 0 && ReferenceEquals(SelectedFile, _pendingJumpFile)
            && JumpToNewSideLine(_pendingJumpNewLine))
        {
            _pendingJumpNewLine = -1;
            _pendingJumpFile = null;
            return;
        }

        if (_pendingJumpToLast)
        {
            _pendingJumpToLast = false;
            JumpToLastChange();
        }
        else
        {
            JumpToFirstChange();
        }
    }

    /// <summary>最初の変更ブロックへジャンプする（ファイル跨ぎはしない）。</summary>
    public void JumpToFirstChange()
    {
        var anchors = ChangeAnchors();
        if (anchors.Count == 0) { _changeCursor = -1; return; }
        _changeCursor = 0;
        ScrollToRowRequested?.Invoke(anchors[0]);
    }

    /// <summary>最後の変更ブロックへジャンプする（前のファイルへ跨いだ直後用。ファイル跨ぎはしない）。</summary>
    private void JumpToLastChange()
    {
        var anchors = ChangeAnchors();
        if (anchors.Count == 0) { _changeCursor = -1; return; }
        _changeCursor = anchors.Count - 1;
        ScrollToRowRequested?.Invoke(anchors[_changeCursor]);
    }

    /// <summary>
    /// 次/前の変更へジャンプする。現在ファイルの端を越えるときは隣のファイルへ移り、
    /// 次方向なら次ファイルの最初の変更、前方向なら前ファイルの最後の変更へ飛ぶ。
    /// </summary>
    private void JumpChange(bool forward)
    {
        var anchors = ChangeAnchors();
        if (forward)
        {
            if (_changeCursor + 1 < anchors.Count)
            {
                _changeCursor++;
                ScrollToRowRequested?.Invoke(anchors[_changeCursor]);
            }
            else
            {
                MoveToAdjacentFile(forward: true); // 末尾を越える → 次ファイルの最初へ
            }
        }
        else
        {
            if (_changeCursor > 0)
            {
                _changeCursor--;
                ScrollToRowRequested?.Invoke(anchors[_changeCursor]);
            }
            else
            {
                MoveToAdjacentFile(forward: false); // 先頭より前 → 前ファイルの最後へ
            }
        }
    }

    /// <summary>隣のファイルを選択し、その差分の最初／最後の変更へ自動ジャンプさせる（端なら何もしない）。</summary>
    private void MoveToAdjacentFile(bool forward)
    {
        if (SelectedFile is null) return;
        var idx = Files.IndexOf(SelectedFile);
        var nextIdx = forward ? idx + 1 : idx - 1;
        if (idx < 0 || nextIdx < 0 || nextIdx >= Files.Count) return;
        _pendingJumpToLast = !forward; // 前方向は移動先の「最後の変更」から見せる
        SelectedFile = Files[nextIdx]; // 選択変更→差分読込→自動ジャンプ（JumpToAutoTarget）
    }

    /// <summary>
    /// 差分本体の「新側の行番号」が <paramref name="newLine"/> の行までスクロールする。
    /// 完全一致が無ければ手前で最も近い行へ。新側に一度も現れない（統合表示のコンテキスト外など）
    /// ときは false を返す。
    /// </summary>
    private bool JumpToNewSideLine(int newLine)
    {
        int best = -1;
        int bestLine = int.MinValue;
        if (IsSideBySide)
        {
            // 左右表示は各行が新側行番号（RightLine）を直接持っている
            for (var i = 0; i < SideRows.Count; i++)
            {
                if (!int.TryParse(SideRows[i].RightLine, out var rl) || rl > newLine) continue;
                if (rl == newLine) { best = i; break; }
                if (rl > bestLine) { bestLine = rl; best = i; }
            }
        }
        else
        {
            // 統合表示は @@ ハンク見出しから新側の行番号を数える（Added/Context が新側の1行を消費）
            int counter = -1;
            for (var i = 0; i < DiffRows.Count; i++)
            {
                var row = DiffRows[i];
                if (row.Kind == "Gap")
                {
                    if (SideBySideDiff.TryParseHunkStarts(row.Text, out _, out var newStart))
                        counter = newStart;
                    continue;
                }
                if (counter < 0 || row.Kind is not ("Added" or "Context")) continue;
                if (counter == newLine) { best = i; break; }
                if (counter < newLine && counter > bestLine) { bestLine = counter; best = i; }
                counter++;
            }
        }
        if (best < 0) return false;
        _changeCursor = -1; // 次/前の変更ジャンプは先頭からやり直す
        ScrollToRowRequested?.Invoke(best);
        return true;
    }

    /// <summary>変更ブロック（連続する追加/削除/空セルのかたまり）の先頭行インデックス一覧。</summary>
    private List<int> ChangeAnchors()
    {
        var anchors = new List<int>();
        var inBlock = false;
        if (IsSideBySide)
        {
            for (var i = 0; i < SideRows.Count; i++)
            {
                var changed = SideRows[i].LeftKind is "Removed" or "Empty"
                    || SideRows[i].RightKind is "Added" or "Empty";
                if (changed && !inBlock) anchors.Add(i);
                inBlock = changed;
            }
        }
        else
        {
            for (var i = 0; i < DiffRows.Count; i++)
            {
                var changed = DiffRows[i].Kind is "Added" or "Removed";
                if (changed && !inBlock) anchors.Add(i);
                inBlock = changed;
            }
        }
        return anchors;
    }
}


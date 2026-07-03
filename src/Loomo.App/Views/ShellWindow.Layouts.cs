using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: レイアウトモードの保存レイアウト（自由タイル配置に名前を付けたもの）の保存・呼び出し・巡回。
/// ワークスペース毎に保持し、タイトルバーの「📐 レイアウト」ドロップダウンから切り替える。Ctrl+T で
/// 巡回する（未保存の変更は単一スクラッチ枠へ退避）。巡回の純ロジックは <see cref="LayoutCycleLogic"/>。
/// </summary>
public partial class ShellWindow
{
    /// <summary>このワークスペースに保存したレイアウト（タイトルバーのドロップダウンに並ぶ）。</summary>
    private readonly List<SavedLayout> _layouts = new();
    /// <summary>未保存作業を退避する単一スクラッチ枠（次の未保存編集で上書きされる）。</summary>
    private PaneNodeSnapshot? _scratchLayout;
    /// <summary>巡回の現在位置（-1＝スクラッチ、0..n＝<see cref="_layouts"/>[i]）。</summary>
    private int _activeLayoutIndex = -1;
    /// <summary>現在のタイル配置が保存レイアウトから変化しているか（編集で立つ）。</summary>
    private bool _layoutDirty;

    /// <summary>ワークスペース復元時に保存レイアウトを読み込む（無ければ既定3種を投入）。</summary>
    private void LoadLayouts(IEnumerable<SavedLayout> layouts, PaneNodeSnapshot? scratch, int activeIndex, bool dirty)
    {
        _layouts.Clear();
        _layouts.AddRange(layouts);
        if (_layouts.Count == 0)
            _layouts.AddRange(SavedLayout.Defaults());
        _scratchLayout = scratch;
        _activeLayoutIndex = activeIndex;
        _layoutDirty = dirty;
        UpdateModeButtons();
    }

    /// <summary>タイトルバーのモードトグルとレイアウトボタンの表示／ラベルを現状へ同期する。</summary>
    private void UpdateModeButtons()
    {
        if (StageModeToggle is not null)
            StageModeToggle.IsChecked = _stageActive;

        if (LayoutButton is null)   // InitializeComponent 前のガード
            return;
        // レイアウトモード時のみレイアウト切替ボタンを出す。
        LayoutButton.Visibility = _stageActive ? Visibility.Collapsed : Visibility.Visible;
        LayoutButtonLabel.Text = CurrentLayoutLabel();
    }

    /// <summary>現在のレイアウト表示名（保存レイアウト名、または未保存）。</summary>
    private string CurrentLayoutLabel()
    {
        if (!_layoutDirty && _activeLayoutIndex >= 0 && _activeLayoutIndex < _layouts.Count)
            return _layouts[_activeLayoutIndex].Name;
        return "（未保存）";
    }

    /// <summary>Ctrl+T（レイアウトモード）：保存レイアウトを巡回する。未保存の変更はスクラッチへ退避。</summary>
    private void CycleLayout(int direction)
    {
        if (_stageActive)
            return;

        // 現在の比率を取り込んでから巡回（リサイズ結果を保つ）。
        CaptureLayoutSizes();

        // 未保存の変更（または現在がスクラッチ）なら、現配置を単一スクラッチへ退避（上書き）。
        // ただし保存レイアウトと同じ配置なら、退避すると Ctrl+T で同じものが2度出てしまうので
        // 退避せず、その保存レイアウトを現在位置として巡回を続ける。
        if ((_layoutDirty || _activeLayoutIndex < 0) && _root is not null)
        {
            var current = ToSnapshot(_root);
            var sameAsSaved = _layouts.FindIndex(l => PaneLayoutTree.SnapshotsEquivalent(l.Tree, current));
            if (sameAsSaved >= 0)
            {
                _activeLayoutIndex = sameAsSaved;
                _layoutDirty = false;
            }
            else
            {
                _scratchLayout = current;
            }
        }

        var next = LayoutCycleLogic.NextIndex(
            _activeLayoutIndex, _layouts.Count, _scratchLayout is not null, direction);
        if (next == _activeLayoutIndex && !_layoutDirty)
            return;   // 1枚しかない等、行き先が無い

        LoadLayoutAt(next);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>巡回位置のレイアウトを <c>_root</c> へ立てる（-1＝スクラッチ）。</summary>
    private void LoadLayoutAt(int index)
    {
        BeginTrailLayoutChange();
        var snapshot = index < 0
            ? _scratchLayout
            : index < _layouts.Count ? _layouts[index].Tree : null;
        _activeLayoutIndex = index;
        _layoutDirty = false;
        ApplyPaneLayout(snapshot);
        UpdateModeButtons();
        if (AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind is { } first)
            FocusPane(first);
    }

    /// <summary>保存レイアウトをポップアップから読み込む。</summary>
    private void LoadLayout(int index)
    {
        if (index < 0 || index >= _layouts.Count || _stageActive)
            return;
        LoadLayoutAt(index);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>現在のタイル配置を保存レイアウトとして保存（同名は上書き）。</summary>
    private void SaveCurrentLayout(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _stageActive || _root is null)
            return;

        CaptureLayoutSizes();
        var layout = new SavedLayout { Name = name, Tree = ToSnapshot(_root) };
        var existing = _layouts.FindIndex(p => p.Name == name);
        if (existing >= 0)
        {
            _layouts[existing] = layout;
            _activeLayoutIndex = existing;
        }
        else
        {
            _layouts.Add(layout);
            _activeLayoutIndex = _layouts.Count - 1;
        }

        _layoutDirty = false;
        UpdateModeButtons();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>保存レイアウトを削除する。</summary>
    private void DeleteLayout(int index)
    {
        if (index < 0 || index >= _layouts.Count)
            return;
        _layouts.RemoveAt(index);
        if (_activeLayoutIndex == index)
            _activeLayoutIndex = -1;
        else if (_activeLayoutIndex > index)
            _activeLayoutIndex--;
        UpdateModeButtons();
        SaveActiveWorkspaceSnapshot();
    }

    private void OnLayoutMenuClick(object sender, RoutedEventArgs e)
    {
        if (_stageActive)
            return;
        BuildLayoutPopup();
        LayoutPopup.IsOpen = true;
    }

    /// <summary>ポップアップの中身（保存レイアウトの一覧）を組み直す。</summary>
    private void BuildLayoutPopup()
    {
        LayoutPopupList.Children.Clear();

        if (_layouts.Count == 0)
        {
            LayoutPopupList.Children.Add(new TextBlock
            {
                Text = "保存したレイアウトはまだありません",
                FontSize = 12,
                Margin = new Thickness(10, 6, 10, 6),
                Foreground = (Brush)FindResource("FgDim"),
            });
            return;
        }

        for (var i = 0; i < _layouts.Count; i++)
        {
            var index = i;
            var layout = _layouts[i];
            var row = new DockPanel { LastChildFill = true };

            var del = new Button
            {
                Content = "✕",
                FontSize = 11,
                ToolTip = "このレイアウトを削除",
                Width = 28,
                Style = (Style)FindResource("BranchMenuItem"),
            };
            del.Click += (_, _) =>
            {
                DeleteLayout(index);
                BuildLayoutPopup();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            var active = !_layoutDirty && index == _activeLayoutIndex;
            var load = new Button
            {
                Style = (Style)FindResource("BranchMenuItem"),
                FontSize = 12,
                Content = new TextBlock
                {
                    Text = LayoutSummary(layout),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = active ? (Brush)FindResource("Accent") : (Brush)FindResource("Fg"),
                },
            };
            load.Click += (_, _) =>
            {
                LayoutPopup.IsOpen = false;
                LoadLayout(index);
            };
            row.Children.Add(load);

            LayoutPopupList.Children.Add(row);
        }
    }

    private static string LayoutSummary(SavedLayout layout)
    {
        var panes = LeafKinds(layout.Tree).Select(PaneLabel);
        return $"{layout.Name}  ({string.Join(" · ", panes)})";
    }

    /// <summary>ツリー内の全リーフ（ペイン種別）を表示順に列挙する。</summary>
    private static IEnumerable<PaneKind> LeafKinds(PaneNodeSnapshot node)
    {
        if (node.Kind is { } kind)
        {
            yield return kind;
            yield break;
        }
        foreach (var child in node.Children)
            foreach (var k in LeafKinds(child))
                yield return k;
    }

    private void OnLayoutSaveClick(object sender, RoutedEventArgs e)
    {
        var name = LayoutNameInput.Text;
        if (string.IsNullOrWhiteSpace(name))
            name = $"レイアウト {_layouts.Count + 1}";
        SaveCurrentLayout(name);
        LayoutNameInput.Clear();
        BuildLayoutPopup();
    }
}

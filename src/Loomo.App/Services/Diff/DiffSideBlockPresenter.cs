using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>左右差分の中央ガターに変更範囲と、その操作（↶ 元に戻す／✓◐＋ ステージの切り替え）を表示する。行の位置は左のエディタから取る
/// （行 i は左右どちらのエディタでも表示行 i ＝ <see cref="DiffEditorAlignment"/>）。</summary>
internal sealed class DiffSideBlockPresenter
{
    private static readonly Brush BlockModified = DiffFlowDocumentRenderer.FrozenBrush("#33FFB74D");
    private static readonly Brush BlockAdded = DiffFlowDocumentRenderer.FrozenBrush("#3366BB6A");
    private static readonly Brush BlockRemoved = DiffFlowDocumentRenderer.FrozenBrush("#33E57373");
    private static readonly Brush BlockHover = DiffFlowDocumentRenderer.FrozenBrush("#80FFC107");
    /// <summary>ステージ済みの帯。追加／削除の色の上に重ねず、別の色で塗る——「もうインデックスに入った」は
    /// 「何が変わったか」とは別の軸なので、同じ色相の濃淡にすると見分けがつかない。</summary>
    private static readonly Brush BlockStaged = DiffFlowDocumentRenderer.FrozenBrush("#8042A5F5");
    private static readonly Brush BlockPartial = DiffFlowDocumentRenderer.FrozenBrush("#4042A5F5");

    private readonly Canvas _canvas;
    private readonly Func<DiffSessionViewModel?> _viewModel;
    private readonly Func<(double TextTop, double VerticalOffset, double LineHeight)?> _geometry;
    private readonly Func<double> _viewportHeight;
    private readonly Func<double> _width;
    private readonly Func<bool> _hasUnsavedEdits;
    private IReadOnlyList<DiffSideBlock> _blocks = Array.Empty<DiffSideBlock>();
    private IReadOnlyList<DiffSideRowVm> _rows = Array.Empty<DiffSideRowVm>();

    internal DiffSideBlockPresenter(
        Canvas canvas,
        Func<DiffSessionViewModel?> viewModel,
        Func<(double TextTop, double VerticalOffset, double LineHeight)?> geometry,
        Func<double> viewportHeight,
        Func<double> width,
        Func<bool> hasUnsavedEdits)
    {
        _canvas = canvas;
        _viewModel = viewModel;
        _geometry = geometry;
        _viewportHeight = viewportHeight;
        _width = width;
        _hasUnsavedEdits = hasUnsavedEdits;
    }

    internal void SetRows(IReadOnlyList<DiffSideRowVm> rows)
    {
        _rows = rows;
        _blocks = DiffSideBlockMapper.Map(rows);
    }

    internal void Render()
    {
        _canvas.Children.Clear();
        if (_blocks.Count == 0 || _geometry() is not { } geometry) return;

        // 本文の上端（パンくず等の帯）のぶんは、スクロール位置を戻す形で足す。
        var offset = geometry.VerticalOffset - geometry.TextTop;
        var viewport = _viewportHeight();
        var width = _width();
        var viewModel = _viewModel();
        var canStage = viewModel?.CanStageLines == true;
        var canRevert = viewModel?.CanDiscardLines == true;

        foreach (var block in _blocks)
        {
            if (!DiffSideBlockMapper.TryGetVisiblePlacement(
                    block, geometry.LineHeight, offset, viewport, out var placement))
                continue;

            // 帯は左右2つの押し場所：左＝↶ 元に戻す（旧側へ寄せる向き）、右＝ステージの印（✓／◐／＋）。
            // 操作できない表示（コミット範囲・比較など）では色だけの帯。
            var band = new Grid { Width = width, Height = placement.Height, Background = BrushFor(block) };
            if (canStage || canRevert)
            {
                band.ColumnDefinitions.Add(new ColumnDefinition());
                band.ColumnDefinitions.Add(new ColumnDefinition());
                // ステージ済みだけのブロックは作業ツリーを戻しても消えない（インデックスに入っている）ので ↶ を出さない。
                if (canRevert && block.Stage != DiffStageState.All)
                    band.Children.Add(Button(block, column: 0, "↶", "この変更を元に戻す（作業ツリーの未ステージの変更を破棄）",
                        () => Revert(block)));
                if (canStage)
                    band.Children.Add(RowToggles(block, geometry.LineHeight));
            }
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, placement.Top);
            _canvas.Children.Add(band);
        }
    }

    /// <summary>帯の半分の押し場所。触れている間だけそこを明るくして、どちらを押すのかが見えるようにする。</summary>
    private static Border Button(DiffSideBlock block, int column, string glyph, string toolTip, Action click)
    {
        var button = new Border
        {
            Background = Brushes.Transparent,
            Tag = block,
            ToolTip = toolTip,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = UiFontManager.Scaled(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = Brushes.White,
                IsHitTestVisible = false,
            },
        };
        Grid.SetColumn(button, column);
        button.MouseEnter += (_, _) => button.Background = BlockHover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, _) => click();
        return button;
    }

    /// <summary>
    /// 右半分：<b>行ごと</b>のステージの押し場所を縦に並べる。クリックでその行だけ、Shift+クリックで連続した変更丸ごと
    /// ステージ／アンステージ。行は左右で揃っているので、1行＝左の削除と右の追加の組（片側だけの行はその1行）。
    /// </summary>
    private StackPanel RowToggles(DiffSideBlock block, double lineHeight)
    {
        var panel = new StackPanel();
        Grid.SetColumn(panel, 1);
        for (var index = block.Start; index <= block.End && index < _rows.Count; index++)
        {
            var row = new DiffSideBlock(index, index, false, false, DiffSideBlockMapper.StageOf(_rows[index]));
            var toggle = Button(row, column: 0, StageGlyph(row.Stage), StageToolTip(row.Stage), () => { });
            toggle.Height = lineHeight;
            toggle.MouseLeftButtonUp += (_, _) =>
            {
                // Shift は「この変更」全体——帯は状態ごとに分けて描いているので、描いたブロックではなく
                // 連続した変更のかたまり全体に効かせる。
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    Toggle(DiffSideBlockMapper.RegionOf(_rows, row.Start) ?? block);
                else Toggle(row);
            };
            panel.Children.Add(toggle);
        }
        return panel;
    }

    private static string StageGlyph(DiffStageState stage) => stage switch
    {
        DiffStageState.All => "✓",
        DiffStageState.Partial => "◐",
        _ => "+",
    };

    private static string StageToolTip(DiffStageState stage) => stage switch
    {
        DiffStageState.All => "ステージ済み — クリックでこの行をアンステージ（Shift+クリックでブロック全体）",
        DiffStageState.Partial => "一部ステージ済み — クリックでこの行の残りをステージ（Shift+クリックでブロック全体）",
        _ => "未ステージ — クリックでこの行をステージ（Shift+クリックでブロック全体）",
    };

    /// <summary>未ステージ（一部を含む）ならステージ、ステージ済みならアンステージ。<paramref name="block"/> は1行でも可。</summary>
    private async void Toggle(DiffSideBlock block)
    {
        if (Collect(block, "ステージ") is not { } target) return;
        await target.ViewModel.StageLinesAsync(
            target.Lines.OldLines, target.Lines.NewLines, stage: block.Stage != DiffStageState.All);
    }

    /// <summary>↶：このブロックの未ステージの変更を作業ツリーから取り消す（確認ダイアログは VM が出す）。</summary>
    private async void Revert(DiffSideBlock block)
    {
        if (Collect(block, "元に戻") is not { } target) return;
        await target.ViewModel.DiscardLinesAsync(target.Lines.OldLines, target.Lines.NewLines);
    }

    private (DiffSessionViewModel ViewModel, DiffSideSelection Lines)? Collect(DiffSideBlock block, string verb)
    {
        if (_viewModel() is not { } viewModel) return null;
        // 見えている行番号は保存前の本文のもの、パッチはディスクの行番号で作る——混ぜると別の行に効く。
        if (_hasUnsavedEdits())
        {
            viewModel.SetStatusMessage($"右側に保存していない編集があります。保存してから{verb}してください。", isError: true);
            return null;
        }
        return (viewModel, DiffSideBlockMapper.CollectChangedLines(viewModel.SideRows, block));
    }

    private Brush BrushFor(DiffSideBlock block) => block.Stage switch
    {
        DiffStageState.All => BlockStaged,
        DiffStageState.Partial => BlockPartial,
        _ => DiffSideBlockMapper.BrushFor(block, BlockModified, BlockAdded, BlockRemoved),
    };
}

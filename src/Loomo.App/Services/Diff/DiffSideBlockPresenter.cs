using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>左右差分の中央ガターに変更範囲と破棄操作を表示する。</summary>
internal sealed class DiffSideBlockPresenter
{
    private static readonly Brush BlockModified = DiffFlowDocumentRenderer.FrozenBrush("#33FFB74D");
    private static readonly Brush BlockAdded = DiffFlowDocumentRenderer.FrozenBrush("#3366BB6A");
    private static readonly Brush BlockRemoved = DiffFlowDocumentRenderer.FrozenBrush("#33E57373");
    private static readonly Brush BlockHover = DiffFlowDocumentRenderer.FrozenBrush("#80FFC107");

    private readonly Canvas _canvas;
    private readonly Func<DiffSessionViewModel?> _viewModel;
    private readonly Func<double> _verticalOffset;
    private readonly Func<double> _viewportHeight;
    private readonly Func<double> _width;
    private IReadOnlyList<DiffSideBlock> _blocks = Array.Empty<DiffSideBlock>();

    internal DiffSideBlockPresenter(
        Canvas canvas,
        Func<DiffSessionViewModel?> viewModel,
        Func<double> verticalOffset,
        Func<double> viewportHeight,
        Func<double> width)
    {
        _canvas = canvas;
        _viewModel = viewModel;
        _verticalOffset = verticalOffset;
        _viewportHeight = viewportHeight;
        _width = width;
    }

    internal void SetRows(IReadOnlyList<DiffSideRowVm> rows)
        => _blocks = DiffSideBlockMapper.Map(rows);

    internal void Render()
    {
        _canvas.Children.Clear();
        if (_blocks.Count == 0) return;

        var offset = _verticalOffset();
        var viewport = _viewportHeight();
        var width = _width();
        var canDiscard = _viewModel()?.CanDiscardLines == true;

        foreach (var block in _blocks)
        {
            if (!DiffSideBlockMapper.TryGetVisiblePlacement(
                    block, DiffFlowDocumentRenderer.LineHeight, offset, viewport, out var placement))
                continue;

            var band = new Border
            {
                Width = width,
                Height = placement.Height,
                Background = BrushFor(block),
                Tag = block,
                ToolTip = canDiscard ? "この範囲の変更を破棄（作業ツリーを元に戻す）" : null,
                Cursor = canDiscard ? Cursors.Hand : null,
            };
            if (canDiscard)
            {
                band.Child = new TextBlock
                {
                    Text = "»",
                    FontSize = UiFontManager.Scaled(13),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    IsHitTestVisible = false,
                };
                band.MouseEnter += (_, _) => band.Background = BlockHover;
                band.MouseLeave += (_, _) => band.Background = BrushFor(block);
                band.MouseLeftButtonUp += (_, _) => Discard(block);
            }
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, placement.Top);
            _canvas.Children.Add(band);
        }
    }

    private async void Discard(DiffSideBlock block)
    {
        if (_viewModel() is not { } viewModel) return;
        var lines = DiffSideBlockMapper.CollectChangedLines(viewModel.SideRows, block);
        await viewModel.DiscardSideLinesAsync(lines.OldLines, lines.NewLines);
    }

    private Brush BrushFor(DiffSideBlock block)
        => DiffSideBlockMapper.BrushFor(block, BlockModified, BlockAdded, BlockRemoved);
}

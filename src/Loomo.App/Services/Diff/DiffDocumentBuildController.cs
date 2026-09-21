using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>差分 FlowDocument の再構築を分割し、UI入力へ処理時間を返す。</summary>
internal sealed class DiffDocumentBuildController : IDisposable
{
    private const int BuildChunkRows = 100;

    private readonly Dispatcher _dispatcher;
    private readonly Func<DiffSessionViewModel?> _viewModel;
    private readonly DiffFlowDocumentRenderer _renderer;
    private readonly RichTextBox _unifiedBox;
    private readonly RichTextBox _leftTextBox;
    private readonly RichTextBox _rightTextBox;
    private readonly RichTextBox _leftGutter;
    private readonly RichTextBox _rightGutter;
    private readonly Action _beforeRebuild;
    private readonly Action<IReadOnlyList<DiffSideRowVm>> _sideRebuilt;
    private bool _unifiedDirty;
    private bool _sideDirty;
    private ChunkedAppendState? _unifiedBuild;
    private ChunkedAppendState? _sideBuild;

    internal DiffDocumentBuildController(
        Dispatcher dispatcher,
        Func<DiffSessionViewModel?> viewModel,
        DiffFlowDocumentRenderer renderer,
        RichTextBox unifiedBox,
        RichTextBox leftTextBox,
        RichTextBox rightTextBox,
        RichTextBox leftGutter,
        RichTextBox rightGutter,
        Action beforeRebuild,
        Action<IReadOnlyList<DiffSideRowVm>> sideRebuilt)
    {
        _dispatcher = dispatcher;
        _viewModel = viewModel;
        _renderer = renderer;
        _unifiedBox = unifiedBox;
        _leftTextBox = leftTextBox;
        _rightTextBox = rightTextBox;
        _leftGutter = leftGutter;
        _rightGutter = rightGutter;
        _beforeRebuild = beforeRebuild;
        _sideRebuilt = sideRebuilt;
    }

    internal bool HasPendingBuild(bool sideBySide)
        => sideBySide
            ? _sideDirty || _sideBuild is { IsRunning: true }
            : _unifiedDirty || _unifiedBuild is { IsRunning: true };

    internal void ScheduleUnified()
    {
        if (_unifiedDirty) return;
        _unifiedDirty = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _unifiedDirty = false;
            RebuildUnified();
        }), DispatcherPriority.Background);
    }

    internal void ScheduleSide()
    {
        if (_sideDirty) return;
        _sideDirty = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _sideDirty = false;
            RebuildSide();
        }), DispatcherPriority.Background);
    }

    /// <summary>行番号を添字で指定する操作の直前に、分割中の文書を組み切る。</summary>
    internal void Flush()
    {
        _unifiedBuild?.Finish();
        _sideBuild?.Finish();
    }

    public void Dispose()
    {
        _unifiedBuild?.Cancel();
        _sideBuild?.Cancel();
    }

    private void RebuildUnified()
    {
        _unifiedBuild?.Cancel();
        _beforeRebuild();
        var viewModel = _viewModel();
        var rows = viewModel?.DiffRows.ToList() ?? [];
        var syntax = viewModel?.UnifiedSyntax ?? DiffSyntaxHighlighter.None;
        var document = _renderer.BuildUnified(rows, syntax);
        _unifiedBox.Document = document.Document;
        _unifiedBuild = document.Build;
        Pump(_unifiedBuild);
    }

    private void RebuildSide()
    {
        _sideBuild?.Cancel();
        _beforeRebuild();
        var viewModel = _viewModel();
        var rows = viewModel?.SideRows.ToList() ?? [];
        var leftSyntax = viewModel?.SideSyntaxLeft ?? DiffSyntaxHighlighter.None;
        var rightSyntax = viewModel?.SideSyntaxRight ?? DiffSyntaxHighlighter.None;
        var documents = _renderer.BuildSide(rows, leftSyntax, rightSyntax);
        _leftTextBox.Document = documents.Left;
        _rightTextBox.Document = documents.Right;
        _leftGutter.Document = documents.LeftNumbers;
        _rightGutter.Document = documents.RightNumbers;
        _sideBuild = documents.Build;
        Pump(_sideBuild);
        _sideRebuilt(rows);
    }

    private void Pump(ChunkedAppendState build)
    {
        build.Step(BuildChunkRows);
        if (!build.IsRunning) return;
        _dispatcher.BeginInvoke(new Action(() => Pump(build)), DispatcherPriority.Background);
    }
}

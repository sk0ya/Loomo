using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>統合表示の差分 FlowDocument の再構築を分割し、UI入力へ処理時間を返す
/// （左右並びはエディタ2つで出すので、ここでは組み立てない＝<see cref="DiffSideEditorPresenter"/>）。</summary>
internal sealed class DiffDocumentBuildController : IDisposable
{
    private const int BuildChunkRows = 100;

    private readonly Dispatcher _dispatcher;
    private readonly Func<DiffSessionViewModel?> _viewModel;
    private readonly DiffFlowDocumentRenderer _renderer;
    private readonly RichTextBox _unifiedBox;
    private readonly Action _beforeRebuild;
    private bool _unifiedDirty;
    private ChunkedAppendState? _unifiedBuild;

    internal DiffDocumentBuildController(
        Dispatcher dispatcher,
        Func<DiffSessionViewModel?> viewModel,
        DiffFlowDocumentRenderer renderer,
        RichTextBox unifiedBox,
        Action beforeRebuild)
    {
        _dispatcher = dispatcher;
        _viewModel = viewModel;
        _renderer = renderer;
        _unifiedBox = unifiedBox;
        _beforeRebuild = beforeRebuild;
    }

    internal bool HasPendingBuild => _unifiedDirty || _unifiedBuild is { IsRunning: true };

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

    /// <summary>行番号を添字で指定する操作の直前に、分割中の文書を組み切る。</summary>
    internal void Flush() => _unifiedBuild?.Finish();

    public void Dispose() => _unifiedBuild?.Cancel();

    private void RebuildUnified()
    {
        _unifiedBuild?.Cancel();
        _beforeRebuild();
        var viewModel = _viewModel();
        var rows = viewModel?.DiffRows.ToList() ?? [];
        var syntax = viewModel?.UnifiedSyntax ?? DiffSyntaxHighlighter.None;
        var inline = viewModel?.UnifiedInline ?? DiffInlineHighlighter.None;
        var document = _renderer.BuildUnified(rows, syntax, inline);
        _unifiedBox.Document = document.Document;
        _unifiedBuild = document.Build;
        Pump(_unifiedBuild);
    }

    private void Pump(ChunkedAppendState build)
    {
        build.Step(BuildChunkRows);
        if (!build.IsRunning) return;
        _dispatcher.BeginInvoke(new Action(() => Pump(build)), DispatcherPriority.Background);
    }
}

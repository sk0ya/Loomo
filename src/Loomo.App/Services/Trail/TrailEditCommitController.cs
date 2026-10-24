using System.Windows.Threading;
using Editor.Controls;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタの編集記録を一定時間まとめ、最後のカーソル位置を軌跡へ残す。
/// <para>「残す編集か」は<b>本文が変わった時点</b>で決める：最後の変更で未保存なら保留し、打って全部取り消して
/// 元に戻ったら保留を捨てる。確定時に未保存の印を見直してはいけない——待っている間に保存されると
/// （Vim の <c>Esc :w</c>、切り離し窓や Diff の右側での保存が本体を保存済みに揃えるのも含む）印は消え、
/// すぐ保存する人の編集が一つも残らなかった。</para>
/// <para>保存前処理（保存時の整形・C# の整理）が入れる変更は人の編集ではないので、
/// <see cref="BeginSavePreparation"/>〜<see cref="EndSavePreparation"/> の間は数えない。</para>
/// <para><paramref name="positionOf"/> はタブの「どのファイルのどこか」。既定はタブのファイルとキャレットで、
/// 抜粋タブ（§35.3）なら元ファイルの位置に直したものを返す（仮想文書のままでは記録できない）。</para>
/// <para><paramref name="record"/> には編集の位置も渡す：軌跡の側はその位置を「これから立つ場所」として扱い、
/// 同じ場所を File の点として二重に積まない（§27.3.3）。</para></summary>
internal sealed class TrailEditCommitController(
    TrailViewModel trail,
    Func<bool> isSuppressed,
    Action<TrailFilePosition, Action<DisplayMode, PaneKind?, string?>> record,
    Func<EditorTab, TrailFilePosition?>? positionOf = null)
{
    private TrailFilePosition? PositionOf(EditorTab tab)
    {
        if (positionOf is not null)
            return positionOf(tab);
        var path = tab.PeekFilePath;
        if (!TrailLogic.IsRecordableFile(path, tab.PeekIsVirtual))
            return null;
        return tab.IsRealized ? new(path!, tab.Control.Caret.Line, tab.Control.Caret.Column) : new(path!, -1, -1);
    }

    private readonly HashSet<VimEditorControl> _preparingSave = new();
    private DispatcherTimer? _timer;
    private EditorTab? _pendingTab;

    public void Request(EditorTab tab)
    {
        if (isSuppressed() || !tab.IsRealized || _preparingSave.Contains(tab.Control))
            return;
        if (!tab.Control.IsModified)
        {
            // 取り消して保存済みの本文へ戻った：待っていた編集は無かったことになる。
            if (ReferenceEquals(_pendingTab, tab))
                Cancel();
            return;
        }
        if (PositionOf(tab) is null)
            return;

        if (_pendingTab is { } pending && !ReferenceEquals(pending, tab))
            CommitPending();

        _pendingTab = tab;
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
    }

    public void BeginSavePreparation(VimEditorControl control) => _preparingSave.Add(control);

    public void EndSavePreparation(VimEditorControl control) => _preparingSave.Remove(control);

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) => CommitPending();
        return timer;
    }

    private void Cancel()
    {
        _timer?.Stop();
        _pendingTab = null;
    }

    /// <summary>待っている編集があれば今すぐ確定する。別の地点を積む直前に呼ぶ——後から確定させると、
    /// 編集の点が移った先の点より後ろに並ぶ。</summary>
    public void CommitPending()
    {
        _timer?.Stop();
        if (_pendingTab is not { } tab)
            return;
        _pendingTab = null;
        if (isSuppressed() || !tab.IsRealized)
            return;

        if (PositionOf(tab) is not { } position)
            return;
        record(position, (mode, stagePane, layout) =>
            trail.RecordEdit(position.Path, position.Line, position.Column, mode, stagePane, layout));
    }
}

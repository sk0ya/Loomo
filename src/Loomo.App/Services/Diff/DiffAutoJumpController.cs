using System.Windows.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>差分本文の構築完了後に、最初の変更行へ一度だけ移動する状態を管理する。</summary>
internal sealed class DiffAutoJumpController
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _hasViewModel;
    private readonly Func<bool> _buildPending;
    private readonly Action _jump;
    private bool _pending;

    internal DiffAutoJumpController(
        Dispatcher dispatcher, Func<bool> hasViewModel, Func<bool> buildPending, Action jump)
    {
        _dispatcher = dispatcher;
        _hasViewModel = hasViewModel;
        _buildPending = buildPending;
        _jump = jump;
    }

    internal void Request()
    {
        _pending = true;
        Schedule();
    }

    internal void Cancel() => _pending = false;

    private void Schedule()
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_pending || !_hasViewModel())
                return;
            if (_buildPending())
            {
                Schedule();
                return;
            }
            _pending = false;
            _dispatcher.BeginInvoke(_jump, DispatcherPriority.Loaded);
        }), DispatcherPriority.Background);
    }
}

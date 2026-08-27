namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <see cref="IEditorSupportRenderabilityWatch"/> の実装（UI ディスパッチャのタイマー）。
/// <b>要求が残っている間だけ動く</b>——仕掛けるのは <see cref="EditorSupportUpdateLoop"/> が
/// 「描けないので要求を持ち越した」ときだけで、描けた時点で止まる。止め忘れても
/// 一発きり（Tick で自分を止める）なので、走りっぱなしのタイマーにはならない。
/// </summary>
public sealed class DispatcherRenderabilityWatch : IEditorSupportRenderabilityWatch
{
    private readonly DispatcherTimer _timer = new();
    private Action? _tick;

    public DispatcherRenderabilityWatch()
        => _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _tick?.Invoke();
        };

    public void Schedule(TimeSpan delay, Action tick)
    {
        _tick = tick;
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    public void Cancel() => _timer.Stop();
}

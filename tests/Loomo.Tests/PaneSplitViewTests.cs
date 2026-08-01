using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

public sealed class PaneSplitViewTests
{
    [Fact]
    public void Activate_can_rebuild_without_moving_keyboard_focus()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var id = Guid.NewGuid();
                var control = new Border();
                var focusCount = 0;
                var sut = new PaneSplitView(
                    new Grid(),
                    requested => requested == id ? control : null,
                    () => [control],
                    () => Brushes.Gray,
                    () => Brushes.Blue,
                    _ => focusCount++,
                    () => { });

                sut.Activate(id, focusControl: false);
                Assert.Equal(0, focusCount);

                sut.Activate(id);
                Assert.Equal(1, focusCount);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw failure;
    }

    [Fact]
    public void Capture_and_restore_preserve_nested_split_tabs_weights_and_focus()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
                var controls = ids.ToDictionary(id => id, _ => (FrameworkElement)new Border());
                PaneSplitView Create() => new(
                    new Grid(), id => controls.GetValueOrDefault(id), () => controls.Values,
                    () => Brushes.Gray, () => Brushes.Blue, _ => { }, () => { });

                var source = Create();
                source.Activate(ids[0], focusControl: false);
                source.SplitFocused(sk0ya.Loomo.App.Layout.SplitKind.Columns, ids[1]);
                source.SplitFocused(sk0ya.Loomo.App.Layout.SplitKind.Rows, ids[2]);
                var snapshot = source.Capture();

                var restored = Create();
                Assert.True(restored.Restore(snapshot, ids));
                Assert.Equal(3, restored.LeafCount);
                Assert.Equal(ids[2], restored.FocusedTabId);
                Assert.Equal(snapshot, restored.Capture(), ViewportSnapshotComparer.Instance);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private sealed class ViewportSnapshotComparer : IEqualityComparer<sk0ya.Loomo.App.Services.ViewportNodeSnapshot?>
    {
        public static readonly ViewportSnapshotComparer Instance = new();
        public bool Equals(sk0ya.Loomo.App.Services.ViewportNodeSnapshot? x, sk0ya.Loomo.App.Services.ViewportNodeSnapshot? y)
            => System.Text.Json.JsonSerializer.Serialize(x) == System.Text.Json.JsonSerializer.Serialize(y);
        public int GetHashCode(sk0ya.Loomo.App.Services.ViewportNodeSnapshot? obj) => 0;
    }
}

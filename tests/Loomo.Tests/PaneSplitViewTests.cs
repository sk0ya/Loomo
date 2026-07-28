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
}

using System;
using System.Threading;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed partial class FlowEditorSupportTests
{
    [Fact]
    public void Visual_PrepareAsync_DoesNotThrowForMissingFlowFile()
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var visual = new FlowVisual();
                var apply = visual.PrepareAsync(
                    "C:\\does-not-exist\\missing.flow",
                    text: "",
                    CancellationToken.None).GetAwaiter().GetResult();
                exception = Record.Exception(apply);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(exception);
    }
}

using System;
using sk0ya.Loomo.NetFxDebug.Dap;
using sk0ya.Loomo.NetFxDebug.Debugger;

namespace sk0ya.Loomo.NetFxDebug;

internal static class Program
{
    /// <summary>標準入出力で DAP を話す。ICorDebug は STA から使えないので MTA で動かす。</summary>
    [MTAThread]
    private static int Main()
    {
        Engine.MakeStandardHandlesNonInheritable();
        var channel = new DapChannel(Console.OpenStandardInput(), Console.OpenStandardOutput());
        return new DapServer(channel).Run();
    }
}

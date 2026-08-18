using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>WebView2 は UserDataFolder を共有する全コントロールが同一のブラウザ引数である必要があり、
/// それはプロセスをまたいでも同じ——Loomo を2つ起動したとき、2つ目が別のポートを選ぶと
/// 環境生成が ERROR_INVALID_STATE (0x8007139F) で失敗し、ブラウザペインと EditorSupport が丸ごと無反応になる。
/// なので「先行インスタンスが居るなら、空きでなくてもその番号を引き継ぐ」が満たすべき性質。</summary>
public class WebViewDebugPortTests
{
    private static string NewRecordPath()
        => Path.Combine(Path.GetTempPath(), "Loomo.Tests", Guid.NewGuid().ToString("N"), "debug-port");

    private static void Cleanup(string recordPath)
    {
        var folder = Path.GetDirectoryName(recordPath)!;
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void 控えが無ければ空きポートを選んで書き残す()
    {
        var record = NewRecordPath();
        try
        {
            var port = WebViewDebugPort.Resolve(record, _ => false, _ => false, () => 9333);

            Assert.Equal(9333, port);
            Assert.True(WebViewDebugPort.TryReadRecord(record, out var written));
            Assert.Equal(9333, written.Port);
            Assert.Equal(Environment.ProcessId, written.OwnerProcessId);
        }
        finally { Cleanup(record); }
    }

    [Fact]
    public void 先行インスタンスのブラウザが握っているポートは空いていなくても引き継ぐ()
    {
        var record = NewRecordPath();
        try
        {
            WebViewDebugPort.TryWriteRecord(record, new WebViewDebugPort.Record(4321, 9333));

            // 9333 は listen 中（＝共有ブラウザプロセスが居る）。素朴な空き探索なら 9334 を返す場面。
            var port = WebViewDebugPort.Resolve(record, p => p == 9333, _ => false, () => 9334);

            Assert.Equal(9333, port);
        }
        finally { Cleanup(record); }
    }

    [Fact]
    public void まだlistenしていなくても控えの持ち主が生きていれば引き継ぐ()
    {
        var record = NewRecordPath();
        try
        {
            WebViewDebugPort.TryWriteRecord(record, new WebViewDebugPort.Record(4321, 9333));

            // 先行インスタンスの起動直後（WebView2 をまだ作っていない）＝ポートは未 listen。
            var port = WebViewDebugPort.Resolve(record, _ => false, pid => pid == 4321, () => 9334);

            Assert.Equal(9333, port);
        }
        finally { Cleanup(record); }
    }

    [Fact]
    public void 誰も居ない古い控えは捨てて選び直す()
    {
        var record = NewRecordPath();
        try
        {
            WebViewDebugPort.TryWriteRecord(record, new WebViewDebugPort.Record(4321, 9999));

            var port = WebViewDebugPort.Resolve(record, _ => false, _ => false, () => 9333);

            Assert.Equal(9333, port);
            Assert.True(WebViewDebugPort.TryReadRecord(record, out var written));
            Assert.Equal(9333, written.Port);
            Assert.Equal(Environment.ProcessId, written.OwnerProcessId);
        }
        finally { Cleanup(record); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("9333")]
    [InlineData("abc 9333")]
    [InlineData("4321 0")]
    [InlineData("4321 70000")]
    public void 壊れた控えは無かったことにする(string content)
    {
        var record = NewRecordPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            File.WriteAllText(record, content);

            Assert.False(WebViewDebugPort.TryReadRecord(record, out _));
            Assert.Equal(9333, WebViewDebugPort.Resolve(record, _ => true, _ => true, () => 9333));
        }
        finally { Cleanup(record); }
    }
}

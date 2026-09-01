using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpHoverServiceTests
{
    [Fact]
    public void Returns_symbol_signature_and_xml_summary()
    {
        const string path = "C:\\work\\Service.cs";
        const string source = """
            class Service
            {
                /// <summary>値を返すサービス。</summary>
                public int Read() => 1;
                int Run() => Read();
            }
            """;
        // Service.Read()の呼び出し位置を、CSharpHoverServiceが作るRoslyn symbolで調べる。
        var result = CSharpHoverService.Get(
            null, path, source, 4,
            source.IndexOf("Read();", StringComparison.Ordinal) -
            source[..source.IndexOf("Read();", StringComparison.Ordinal)].LastIndexOf('\n') - 1);

        Assert.NotNull(result);
        Assert.Contains("Service.Read()", result, StringComparison.Ordinal);
        Assert.Contains("値を返すサービス。", result, StringComparison.Ordinal);
    }
}

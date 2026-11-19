using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>Markdown のタブへファイルを落としたときに入れるリンクの文字（§24.110 の 3）。</summary>
public class MarkdownDropLinksTests
{
    private const string Doc = @"C:\repo\docs\設計\README.md";

    [Fact]
    public void File_IsRelativeLinkFromTheDocumentFolder()
        => Assert.Equal("[Program.cs](../../src/Program.cs)", MarkdownDropLinks.Build(Doc, [@"C:\repo\src\Program.cs"]));

    [Fact]
    public void Image_IsImageReference_WithNameAsAlt()
        => Assert.Equal("![画面](img/画面.png)", MarkdownDropLinks.Build(Doc, [@"C:\repo\docs\設計\img\画面.png"]));

    [Fact]
    public void ManyFiles_OneLinkPerLine()
        => Assert.Equal("[a.md](a.md)\n[b.md](b.md)",
            MarkdownDropLinks.Build(Doc, [@"C:\repo\docs\設計\a.md", @"C:\repo\docs\設計\b.md"]));

    [Fact]
    public void SpacesAreEncoded_JapaneseIsKept()
        => Assert.Equal("[my notes.md](../my%20notes.md)", MarkdownDropLinks.Build(Doc, [@"C:\repo\docs\my notes.md"]));

    [Fact]
    public void OtherDrive_IsAbsolute()
        => Assert.Equal("[x.txt](D:/data/x.txt)", MarkdownDropLinks.Build(Doc, [@"D:\data\x.txt"]));

    [Fact]
    public void UnbalancedParenthesis_IsEncoded()
        => Assert.Equal("[a(1.md](a%281.md)", MarkdownDropLinks.Build(Doc, [@"C:\repo\docs\設計\a(1.md"]));

    [Fact]
    public void BracketsInName_AreEscaped()
        => Assert.Equal(@"[\[draft\].md](%5Bdraft%5D.md)".Replace("%5Bdraft%5D", "[draft]"),
            MarkdownDropLinks.Build(Doc, [@"C:\repo\docs\設計\[draft].md"]));

    [Fact]
    public void SourceLine_FromTerminalLink_AddsLineAnchor()
        => Assert.Equal("[Program.cs:12](../../src/Program.cs#L12)",
            MarkdownDropLinks.Build(Doc, [@"C:\repo\src\Program.cs"], sourceLine: 12));

    [Theory]
    [InlineData(@"C:\a\b.md", true)]
    [InlineData(@"C:\a\b.MARKDOWN", true)]
    [InlineData(@"C:\a\b.txt", false)]
    [InlineData(null, false)]
    public void OnlyMarkdownDocumentsTakeLinks(string? path, bool expected)
        => Assert.Equal(expected, MarkdownDropLinks.IsMarkdownDocument(path));
}

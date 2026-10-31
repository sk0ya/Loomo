using System.IO;
using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.LanguageServer;
using sk0ya.Loomo.CSharp.Packages;

namespace sk0ya.Loomo.Tests;

/// <summary>§36（Rider 相当の使い勝手）の純ロジック：Roslyn 設定・Live／Postfix Template・
/// 言語共通キーの関門・逆コンパイルタブ・ローカル履歴・NuGet の解析。</summary>
public class RiderUsabilityTests
{
    // ── Roslyn 設定 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters", "true")]
    [InlineData("csharp|inlay_hints.csharp_enable_inlay_hints_for_implicit_variable_types", "true")]
    [InlineData("csharp|completion.dotnet_show_completion_items_from_unimported_namespaces", "true")]
    [InlineData("navigation.dotnet_navigate_to_decompiled_sources", "true")]
    [InlineData("csharp|inlay_hints.csharp_enable_inlay_hints_for_implicit_object_creation", "false")]
    public void RoslynConfiguration_AnswersKnownOptions(string section, string expected)
        => Assert.Equal(expected, RoslynClientConfiguration.Resolve(section));

    [Theory]
    [InlineData("csharp|completion.dotnet_completion_items_from_unimported_namespaces_commit_behavior")]
    [InlineData("python.analysis.typeCheckingMode")]
    [InlineData(null)]
    [InlineData("")]
    public void RoslynConfiguration_LeavesUnknownOptionsToServerDefault(string? section)
        => Assert.Null(RoslynClientConfiguration.Resolve(section));

    // ── Live Template ───────────────────────────────────────────────────────

    private static (int Line, int Character) CaretOf(ref string source)
    {
        var offset = source.IndexOf('|');
        source = source.Remove(offset, 1);
        var before = source[..offset];
        var line = before.Count(c => c == '\n');
        return (line, offset - (before.LastIndexOf('\n') + 1));
    }

    private static IReadOnlyList<LspCompletionItem> Templates(string sourceWithCaret, string file = "C:/w/Widget.cs")
    {
        var source = sourceWithCaret;
        var (line, character) = CaretOf(ref source);
        return CSharpTemplateCompletion.Get(file, source, line, character);
    }

    [Fact]
    public void Live_StatementTemplatesAppearAtStatementStart()
    {
        var items = Templates("class C\n{\n    void M()\n    {\n        fore|\n    }\n}\n");
        var foreachItem = Assert.Single(items, i => i.Label == "foreach");
        Assert.Equal(CompletionItemKind.Snippet, foreachItem.Kind);
        Assert.Equal(InsertTextFormat.Snippet, foreachItem.TextFormat);
        // 2 行目以降に現在行の字下げが付く。
        Assert.Contains("\n        {\n            $0\n        }", foreachItem.TextEdit!.NewText);
        Assert.DoesNotContain(items, i => i.Label == "prop");
    }

    [Fact]
    public void Live_MemberTemplatesAppearInTypeBody_AndCtorUsesTypeName()
    {
        var items = Templates("public class Widget\n{\n    ct|\n}\n");
        var ctor = Assert.Single(items, i => i.Label == "ctor");
        Assert.StartsWith("public Widget(", ctor.TextEdit!.NewText);
        Assert.Contains(items, i => i.Label == "prop");
        Assert.DoesNotContain(items, i => i.Label == "foreach");
    }

    [Fact]
    public void Live_TypeTemplatesUseFileName()
    {
        var items = Templates("namespace N;\n\ncla|\n");
        var cls = Assert.Single(items, i => i.Label == "class");
        Assert.Contains("${1:Widget}", cls.TextEdit!.NewText);
    }

    [Fact]
    public void Live_NothingInsideExpressionsCommentsOrStrings()
    {
        Assert.Empty(Templates("class C { void M() { var x = fore|; } }"));
        Assert.Empty(Templates("class C { void M() { // fore|\n } }"));
        Assert.Empty(Templates("class C { void M() { var s = \"fore|\"; } }"));
    }

    [Fact]
    public void Live_NonCSharpFilesGetNothing()
        => Assert.Empty(Templates("fore|", "C:/w/a.ts"));

    // ── Postfix Template ────────────────────────────────────────────────────

    [Fact]
    public void Postfix_ForeachWrapsTheWholeExpression()
    {
        var items = Templates("class C\n{\n    void M(int[] items)\n    {\n        this.Get(items).Values.|\n    }\n}\n");
        var item = Assert.Single(items, i => i.Label == "foreach");
        Assert.StartsWith("foreach (var ${1:item} in this.Get(items).Values)", item.TextEdit!.NewText);
        // 置換範囲は式の先頭からキャレット（ドットの直後）まで。
        Assert.Equal(new LspPosition(4, 8), item.TextEdit.Range.Start);
        Assert.Equal(new LspPosition(4, 31), item.TextEdit.Range.End);
    }

    [Fact]
    public void Postfix_StatementTemplatesOnlyWhenExpressionIsTheStatement()
    {
        var inArgument = Templates("class C { void M(bool ok) { Use(ok.|); } void Use(bool b) { } }");
        Assert.DoesNotContain(inArgument, i => i.Label == "if");
        var not = Assert.Single(inArgument, i => i.Label == "not");
        Assert.StartsWith("!ok", not.TextEdit!.NewText);
    }

    [Fact]
    public void Postfix_NotParenthesizesBinaryExpressions()
    {
        var items = Templates("class C { void M(int a) { var ok = a > 1.|; } }");
        Assert.DoesNotContain(items, i => i.Label == "not" && i.TextEdit!.NewText.StartsWith("!a"));
    }

    [Fact]
    public void Postfix_StatementTemplatesWhenTheNextLineIsAnotherStatement()
    {
        // `.foreach` を外しただけだと `items` が次行と繋がって「items 型の変数宣言」と読まれていた。
        var items = Templates("class C\n{\n    void M(int[] items)\n    {\n        items.|\n        Console.WriteLine();\n    }\n}\n");
        Assert.Contains(items, i => i.Label == "foreach");
        Assert.Contains(items, i => i.Label == "if");
    }

    [Fact]
    public void Live_CtrlSpaceOnBlankLineAboveAStatementOffersStatementTemplates()
    {
        var items = Templates("class C\n{\n    void M()\n    {\n        |\n        Console.WriteLine();\n    }\n}\n");
        Assert.Contains(items, i => i.Label == "foreach");
    }

    [Fact]
    public void Live_AfterAMethodBodyOffersMemberTemplates()
    {
        var items = Templates("class C\n{\n    void M()\n    {\n    }\n|\n}\n");
        Assert.Contains(items, i => i.Label == "prop");
        Assert.DoesNotContain(items, i => i.Label == "foreach");
    }

    [Fact]
    public void Postfix_DoesNotSwallowTheAssignmentTarget()
    {
        var items = Templates("class C { void M(int[] xs) { int n; n = xs.Length.|; } }");
        Assert.StartsWith("(xs.Length)", Assert.Single(items, i => i.Label == "par").TextEdit!.NewText);
    }

    [Fact]
    public void Postfix_VarAndReturn()
    {
        var items = Templates("class C\n{\n    int M()\n    {\n        Compute().|\n    }\n    int Compute() => 1;\n}\n");
        Assert.StartsWith("var ${1:name} = Compute();", Assert.Single(items, i => i.Label == "var").TextEdit!.NewText);
        Assert.StartsWith("return Compute();", Assert.Single(items, i => i.Label == "return").TextEdit!.NewText);
    }

    [Fact]
    public void TemplatePreview_StripsSnippetSyntax()
        => Assert.Equal("foreach (var item in xs)", CSharpTemplateCompletion.PlainPreview("foreach (var ${1:item} in xs)$0"));

    // ── 言語共通キー ────────────────────────────────────────────────────────

    [Fact]
    public void LanguageNeutralCommands_AreTheLspGenericOnes()
    {
        Assert.True(CSharpEditorCommandDispatchPolicy.IsLanguageNeutral(CSharpEditorCommandCatalog.GoToDefinition));
        Assert.True(CSharpEditorCommandDispatchPolicy.IsLanguageNeutral(CSharpEditorCommandCatalog.FindReferences));
        Assert.True(CSharpEditorCommandDispatchPolicy.IsLanguageNeutral(CSharpEditorCommandCatalog.Rename));
        Assert.False(CSharpEditorCommandDispatchPolicy.IsLanguageNeutral(CSharpEditorCommandCatalog.ExtractMethod));
        Assert.False(CSharpEditorCommandDispatchPolicy.IsLanguageNeutral(CSharpEditorCommandCatalog.OrganizeUsings));
    }

    [Fact]
    public void GoToDeclaration_YieldsCtrlBToVimInNonCSharpFiles()
    {
        Assert.False(CSharpEditorCommandDispatchPolicy.AcceptsInNonCSharpEditor(CSharpEditorCommandCatalog.GoToDeclaration, vimEnabled: true));
        Assert.True(CSharpEditorCommandDispatchPolicy.AcceptsInNonCSharpEditor(CSharpEditorCommandCatalog.GoToDeclaration, vimEnabled: false));
        Assert.True(CSharpEditorCommandDispatchPolicy.AcceptsInNonCSharpEditor(CSharpEditorCommandCatalog.GoToDefinition, vimEnabled: true));
    }

    // ── 逆コンパイルタブ ────────────────────────────────────────────────────

    [Fact]
    public void MetadataAsSource_IsRecognizedOnlyUnderTempFolder()
    {
        var temp = Path.Combine(Path.GetTempPath(), "LoomoTestTemp");
        Assert.True(MetadataSourceFiles.IsMetadataAsSource(
            Path.Combine(temp, "MetadataAsSource", "abc", "Console.cs"), temp));
        Assert.False(MetadataSourceFiles.IsMetadataAsSource(Path.Combine(temp, "Other", "Console.cs"), temp));
        Assert.False(MetadataSourceFiles.IsMetadataAsSource(@"C:\work\MetadataAsSource\x.cs", temp));
        Assert.False(MetadataSourceFiles.IsMetadataAsSource(null, temp));
    }

    // ── ローカル履歴 ────────────────────────────────────────────────────────

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loomo-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void LocalHistory_RecordsOpenedAndSaved_NewestFirst_AndSkipsDuplicates()
    {
        var root = TempDir();
        var file = Path.Combine(TempDir(), "a.cs");
        File.WriteAllText(file, "v1");
        var now = new DateTime(2026, 10, 11, 1, 0, 0, DateTimeKind.Utc);
        var store = new LocalHistoryStore(root, () => now, synchronous: true);

        store.RecordOpened(file);
        now = now.AddMinutes(1);
        store.RecordContent(file, "v1", LocalHistoryKind.Saved);   // 同じ中身は積まない
        now = now.AddMinutes(1);
        store.RecordContent(file, "v2", LocalHistoryKind.Saved);

        var entries = store.GetEntries(file);
        Assert.Equal([LocalHistoryKind.Saved, LocalHistoryKind.Opened], entries.Select(e => e.Kind));
        Assert.Equal("v2", store.ReadContent(entries[0]));
        Assert.Equal("v1", store.ReadContent(entries[1]));
    }

    [Fact]
    public void LocalHistory_PrunesOldVersionsButKeepsTheNewest()
    {
        var root = TempDir();
        var file = Path.Combine(TempDir(), "b.cs");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new LocalHistoryStore(root, () => now, synchronous: true);

        store.RecordContent(file, "old", LocalHistoryKind.Saved);
        now = now.AddDays(30);
        store.RecordContent(file, "new", LocalHistoryKind.Saved);

        var entries = store.GetEntries(file);
        Assert.Equal("new", store.ReadContent(Assert.Single(entries)));
    }

    [Fact]
    public void LocalHistory_LabeledVersionsSurvivePruning_AndKeepTheirName()
    {
        var root = TempDir();
        var file = Path.Combine(TempDir(), "c.cs");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new LocalHistoryStore(root, () => now, synchronous: true);

        store.RecordContent(file, "v1", LocalHistoryKind.Saved);
        now = now.AddMinutes(1);
        store.RecordLabeled(file, "v1", "リファクタリング前\n2 行目");   // 直前と同じ中身でも積む
        now = now.AddDays(30);
        store.RecordContent(file, "v2", LocalHistoryKind.Saved);

        var entries = store.GetEntries(file);
        Assert.Equal([LocalHistoryKind.Saved, LocalHistoryKind.Labeled], entries.Select(e => e.Kind));
        Assert.Equal("リファクタリング前 2 行目", entries[1].Label);
        Assert.Equal("「リファクタリング前 2 行目」", entries[1].KindLabel);
        Assert.Equal("v1", store.ReadContent(entries[1]));
    }

    [Fact]
    public void LocalHistory_ListsDeletedFiles_AndIgnoresMetadataSources()
    {
        var root = TempDir();
        var folder = TempDir();
        var gone = Path.Combine(folder, "gone.cs");
        var store = new LocalHistoryStore(root, synchronous: true);
        store.RecordContent(gone, "content", LocalHistoryKind.Saved);
        store.RecordContent(Path.Combine(Path.GetTempPath(), "MetadataAsSource", "x", "Console.cs"), "decompiled", LocalHistoryKind.Saved);

        var deleted = Assert.Single(store.GetDeletedFiles());
        Assert.Equal(Path.GetFullPath(gone), deleted.FilePath);
        Assert.DoesNotContain(store.GetRecent(), e => e.FilePath.Contains("MetadataAsSource"));
    }

    [Fact]
    public void LocalHistory_TreatsCrlfAndLfAsTheSameContent()
    {
        var root = TempDir();
        var file = Path.Combine(TempDir(), "crlf.cs");
        File.WriteAllText(file, "a\r\nb\r\n");
        var store = new LocalHistoryStore(root, synchronous: true);
        store.RecordOpened(file);
        store.RecordContent(file, "a\nb\n", LocalHistoryKind.Saved);   // エディタの本文は LF
        Assert.Single(store.GetEntries(file));
    }

    [Fact]
    public void LocalHistory_KeepsRecordingAfterACorruptNewestVersion()
    {
        var root = TempDir();
        var file = Path.Combine(TempDir(), "corrupt.cs");
        var now = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
        var store = new LocalHistoryStore(root, () => now, synchronous: true);
        store.RecordContent(file, "v1", LocalHistoryKind.Saved);
        File.WriteAllBytes(store.GetEntries(file)[0].StoragePath, [1, 2, 3]);   // 書き込み途中で落ちた gzip

        now = now.AddMinutes(1);
        store.RecordContent(file, "v2", LocalHistoryKind.Saved);

        Assert.Equal("v2", store.ReadContent(Assert.Single(store.GetEntries(file))));
    }

    [Fact]
    public void LocalHistory_NameRoundTrips()
    {
        Assert.True(LocalHistoryStore.TryParseName("20261011T010203004Z_Saved.gz", out var at, out var kind));
        Assert.Equal(new DateTime(2026, 10, 11, 1, 2, 3, 4, DateTimeKind.Utc), at);
        Assert.Equal(LocalHistoryKind.Saved, kind);
        Assert.False(LocalHistoryStore.TryParseName("path.txt", out _, out _));
    }

    [Fact]
    public void LocalHistoryPresentation_WholeDocumentRangeCoversLastLine()
    {
        var range = LocalHistoryPresentation.WholeDocumentRange("a\nbc\n");
        Assert.Equal(new LspPosition(0, 0), range.Start);
        Assert.Equal(new LspPosition(2, 0), range.End);
        Assert.Equal(new LspPosition(1, 2), LocalHistoryPresentation.WholeDocumentRange("a\nbc").End);
    }

    [Fact]
    public void LocalHistoryPresentation_RelativeTime()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("たった今", LocalHistoryPresentation.RelativeTime(now.AddSeconds(-10), now));
        Assert.Equal("5分前", LocalHistoryPresentation.RelativeTime(now.AddMinutes(-5), now));
    }

    // ── NuGet ───────────────────────────────────────────────────────────────

    [Fact]
    public void NuGet_ParsesListOutput_MergingFrameworks()
    {
        const string json = """
            {
              "version": 1,
              "projects": [
                {
                  "path": "C:/w/A/A.csproj",
                  "frameworks": [
                    { "framework": "net8.0", "topLevelPackages": [ { "id": "Newtonsoft.Json", "requestedVersion": "13.0.1", "resolvedVersion": "13.0.1" } ] },
                    { "framework": "net10.0", "topLevelPackages": [ { "id": "Newtonsoft.Json", "requestedVersion": "13.0.1", "resolvedVersion": "13.0.1" } ] }
                  ]
                },
                { "path": "C:/w/Legacy/Legacy.csproj" }
              ],
              "problems": [ { "project": "C:/w/Legacy/Legacy.csproj", "level": "error", "text": "packages.config は対象外です" } ]
            }
            """;
        var result = NuGetPackageService.ParseListOutput(json);
        var package = Assert.Single(result.Packages);
        Assert.Equal("Newtonsoft.Json", package.Id);
        Assert.Equal(["net8.0", "net10.0"], package.Frameworks);
        Assert.Equal("Legacy.csproj: packages.config は対象外です", Assert.Single(result.Problems));
    }

    [Fact]
    public void NuGet_ParsesSourcesAndSearch()
    {
        var sources = NuGetPackageService.ParseSources(
            "E https://api.nuget.org/v3/index.json\nD https://disabled.example/v3/index.json\nE C:\\local\\feed\n");
        Assert.Equal(["https://api.nuget.org/v3/index.json"], sources);

        var results = NuGetPackageService.ParseSearch("""
            {"data":[{"id":"Serilog","version":"4.0.1","description":"log","authors":["Serilog Contributors"],
              "totalDownloads":10,"versions":[{"version":"3.0.0"},{"version":"4.0.1"}]}]}
            """, "src");
        var serilog = Assert.Single(results);
        Assert.Equal("4.0.1", serilog.LatestVersion);
        Assert.Equal(["3.0.0", "4.0.1"], serilog.Versions);
        Assert.Equal("Serilog Contributors", serilog.Authors);
    }

    [Theory]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("2.0.0-preview.1", "2.0.0", -1)]
    [InlineData("13.0.1", "[13.0.1, )", 0)]
    [InlineData("1.0", "1.0.0", 0)]
    public void NuGet_ComparesVersions(string left, string right, int sign)
        => Assert.Equal(sign, Math.Sign(NuGetPackageService.CompareVersions(left, right)));

    [Fact]
    public void NuGet_DetectsInconsistentVersionsForConsolidation()
    {
        var installs = new[]
        {
            new NuGetInstalledPackage("A.csproj", "X", "1.0.0", "1.0.0", ["net10.0"]),
            new NuGetInstalledPackage("B.csproj", "X", "1.1.0", "1.1.0", ["net10.0"]),
        };
        Assert.True(NuGetPackageService.NeedsConsolidation(installs));
        Assert.False(NuGetPackageService.NeedsConsolidation(installs.Take(1)));
    }
}

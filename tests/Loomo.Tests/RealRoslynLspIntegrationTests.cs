using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor.Controls.Lsp;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;
using sk0ya.Loomo.Services.Lsp;
using Xunit.Sdk;

namespace sk0ya.Loomo.Tests;

/// <summary>インストール済みRoslyn Language Serverとの実通信確認。
/// 通常のテストでは外部プロセスを起動せず、<c>LOOMO_RUN_REAL_LSP=1</c> のときだけ実行する。</summary>
[Collection(CSharpExternalProcessCollection.Name)]
public sealed class RealRoslynLspIntegrationTests
{
    [RealRoslynFact]
    public async Task Roslyn_initializes_and_answers_completion_and_diagnostics()
    {
        var root = FindFixtureRoot();
        var file = Path.Combine(root, "tests", "Feature.Tests", "FeatureTests.cs");
        var uri = LspUri.FromPath(file);
        var source = File.ReadAllText(file);
        var completionSource = source.Replace(
            "new FeatureService().GetValue()", "new FeatureService().", StringComparison.Ordinal);
        var completionLine = completionSource.Split('\n')[8].TrimEnd('\r');
        var completionPosition = new LspPosition(8, completionLine.Length);
        var brokenSource = completionSource.Replace(
            "new FeatureService().", "new FeatureService().MissingMethod()", StringComparison.Ordinal);

        var executable = ExecutableResolver.Resolve("roslyn-language-server");
        Assert.False(string.IsNullOrWhiteSpace(executable), "Roslyn Language ServerがPATHにありません。");
        using var client = new LspClient(executable!, LspServerCatalog.RoslynArgs, root);

        await client.InitializeAsync(LspUri.FromPath(root), [root]);
        Assert.True(client.SupportsCompletionResolve);
        Assert.True(client.SupportsDocumentDiagnostics);
        await client.OpenDocumentAsync(uri, "csharp", source);

        // Project auto-load is asynchronous.  A document-symbol request is a cheap, observable
        // readiness probe before asking for semantic completion from the project.
        IReadOnlyList<DocumentSymbol> symbols = [];
        for (var attempt = 0; attempt < 30 && symbols.Count == 0; attempt++)
        {
            symbols = await client.GetDocumentSymbolsAsync(uri);
            if (symbols.Count == 0) await Task.Delay(1000);
        }
        Assert.NotEmpty(symbols);

        if (client.SupportsInlayHint)
        {
            var closingBrace = source.LastIndexOf('}');
            var inlaySource = source[..closingBrace] +
                "    private static string WithLabel(string expected, string actual) => actual;\n" +
                source[closingBrace..];
            inlaySource = inlaySource.Replace(
                "new FeatureService().GetValue()",
                "WithLabel(\"generated\", new FeatureService().GetValue())",
                StringComparison.Ordinal);

            await client.ChangeDocumentAsync(uri, 1, inlaySource);
            IReadOnlyList<InlayHint> inlayHints = await client.GetInlayHintsAsync(uri, new LspRange(
                new LspPosition(0, 0),
                new LspPosition(inlaySource.Count(c => c == '\n') + 1, 0)));
            // Roslynのheadless構成はproviderを広告しても設定や解析状態により空配列を返す。
            // ここでは有効な範囲で要求し、返ったhintが不正な空labelでないことまで確認する。
            Assert.All(inlayHints, hint => Assert.False(string.IsNullOrWhiteSpace(hint.Label)));
        }

        await client.ChangeDocumentAsync(uri, 2, completionSource);
        IReadOnlyList<LspCompletionItem> completions = [];
        for (var attempt = 0; attempt < 30 && completions.Count == 0; attempt++)
        {
            completions = await client.GetCompletionAsync(uri, completionPosition);
            if (completions.Count == 0) await Task.Delay(1000);
        }
        Assert.NotEmpty(completions);

        await client.ChangeDocumentAsync(uri, 3, brokenSource);
        LspDocumentDiagnosticReport? report = null;
        for (var attempt = 0; attempt < 30 && report is null; attempt++)
        {
            report = await client.GetDocumentDiagnosticsAsync(uri);
            if (report is null) await Task.Delay(1000);
        }
        Assert.NotNull(report);
        Assert.NotEmpty(report!.Diagnostics);

        var serviceFile = Path.Combine(root, "src", "Feature", "FeatureService.cs");
        var serviceUri = LspUri.FromPath(serviceFile);
        var serviceSource = File.ReadAllText(serviceFile);
        await client.ChangeDocumentAsync(uri, 4, source);
        await client.OpenDocumentAsync(serviceUri, "csharp", serviceSource);
        IReadOnlyList<DocumentSymbol> serviceSymbols = [];
        for (var attempt = 0; attempt < 30 && serviceSymbols.Count == 0; attempt++)
        {
            serviceSymbols = await client.GetDocumentSymbolsAsync(serviceUri);
            if (serviceSymbols.Count == 0) await Task.Delay(1000);
        }
        Assert.NotEmpty(serviceSymbols);

        // semantic tokenが返る場合は、C# fallback lexerと範囲を突き合わせる。LSPが
        // identifierの一部だけを返すことは許すが、文字列／コメントを壊す範囲ずれは許さない。
        if (client.SupportsSemanticTokens)
        {
            Assert.Contains("static", client.SemanticTokensLegend?.TokenModifiers ?? [],
                StringComparer.Ordinal);
            Assert.Contains("ReassignedVariable", client.SemanticTokensLegend?.TokenModifiers ?? [],
                StringComparer.Ordinal);
            Assert.Contains("deprecated", client.SemanticTokensLegend?.TokenModifiers ?? [],
                StringComparer.Ordinal);
            IReadOnlyList<SemanticToken> semanticTokens = [];
            for (var attempt = 0; attempt < 30 && semanticTokens.Count == 0; attempt++)
            {
                semanticTokens = await client.GetSemanticTokensAsync(serviceUri) ?? [];
                if (semanticTokens.Count == 0) await Task.Delay(1000);
            }
            Assert.NotEmpty(semanticTokens);
            var comparison = CSharpSemanticTokenVerifier.Compare(
                serviceSource.Split('\n'), semanticTokens);
            Assert.True(comparison.IsCompatible,
                string.Join(" / ", comparison.Mismatches.Select(mismatch =>
                    $"{mismatch.Line}:{mismatch.StartChar} {mismatch.TokenType}: {mismatch.Message}")));
        }

        var valueOffset = serviceSource.IndexOf("_value", StringComparison.Ordinal);
        var valuePosition = PositionOf(serviceSource, valueOffset);
        var definition = await client.GetDefinitionAsync(serviceUri, valuePosition);
        Assert.NotNull(definition);
        Assert.EndsWith("FeatureService.cs", definition!.Value.Uri, StringComparison.OrdinalIgnoreCase);

        if (client.SupportsImplementation)
        {
            var contractOffset = serviceSource.IndexOf("IFixtureContract", StringComparison.Ordinal);
            var implementations = await client.GetImplementationAsync(
                serviceUri, PositionOf(serviceSource, contractOffset));
            // Roslynのheadless構成はcapabilityを広告しても、interface宣言位置からの
            // implementation応答を空配列にすることがある。返却された場合だけURIを検証する。
            Assert.All(implementations, location =>
                Assert.EndsWith(".cs", location.Uri, StringComparison.OrdinalIgnoreCase));
        }

        var featureOffset = source.IndexOf("FeatureService", StringComparison.Ordinal);
        var featurePosition = PositionOf(source, featureOffset);
        if (client.SupportsTypeDefinition)
        {
            var typeDefinitions = await client.GetTypeDefinitionAsync(uri, featurePosition);
            Assert.All(typeDefinitions, location =>
                Assert.EndsWith(".cs", location.Uri, StringComparison.OrdinalIgnoreCase));
        }

        if (client.SupportsDeclaration)
        {
            var declarations = await client.GetDeclarationAsync(uri, featurePosition);
            Assert.All(declarations, location =>
                Assert.EndsWith(".cs", location.Uri, StringComparison.OrdinalIgnoreCase));
        }

        var references = await client.GetReferencesAsync(serviceUri, valuePosition);
        Assert.NotEmpty(references);
        var highlights = await client.RequestDocumentHighlightAsync(
            serviceUri, valuePosition.Line, valuePosition.Character);
        if (client.SupportsDocumentHighlight)
        {
            Assert.NotNull(highlights);
            Assert.NotEmpty(highlights!);
        }
        else
            Assert.Null(highlights);

        var methodOffset = serviceSource.IndexOf("GetValue", StringComparison.Ordinal);
        var methodPosition = PositionOf(serviceSource, methodOffset);
        if (client.SupportsPrepareRename)
            Assert.NotNull(await client.PrepareRenameAsync(serviceUri, methodPosition));
        var rename = await client.GetRenameAsync(serviceUri, methodPosition, "ReadValue");
        Assert.NotNull(rename);
        Assert.NotEmpty(rename!.Changes);

        await client.CloseDocumentAsync(serviceUri);
        await client.CloseDocumentAsync(uri);
    }

    [RealRoslynFact]
    public async Task Workspace_service_feeds_roslyn_references_to_change_signature()
    {
        var fixtureRoot = FindFixtureRoot();
        var root = CopyFixtureToTemp(fixtureRoot);
        try
        {
            var servicePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var callerPath = Path.Combine(root, "tests", "Feature.Tests", "FeatureTests.cs");
            var serviceText = File.ReadAllText(servicePath).Replace(
                "    public string GetValue()\r\n        => _value;",
                "    public string GetValue(string prefix, int repeat)\r\n        => prefix + _value + repeat;",
                StringComparison.Ordinal);
            if (serviceText == File.ReadAllText(servicePath))
            {
                serviceText = File.ReadAllText(servicePath).Replace(
                    "    public string GetValue()\n        => _value;",
                    "    public string GetValue(string prefix, int repeat)\n        => prefix + _value + repeat;",
                    StringComparison.Ordinal);
            }
            File.WriteAllText(servicePath, serviceText);

            var callerText = File.ReadAllText(callerPath).Replace(
                "new FeatureService().GetValue()",
                "new FeatureService().GetValue(\"value:\", 1)",
                StringComparison.Ordinal);
            File.WriteAllText(callerPath, callerText);

            var workspace = new FakeWorkspaceService(root);
            using var lsp = new LspWorkspaceService(workspace, new LspServerTable(null));
            using var document = lsp.OpenDocument(servicePath, serviceText);
            using var callerDocument = lsp.OpenDocument(callerPath, callerText);
            Assert.NotNull(document);
            Assert.NotNull(callerDocument);
            for (var attempt = 0; attempt < 45 && (!document!.IsReady || !callerDocument!.IsReady); attempt++)
                await Task.Delay(1000);
            Assert.True(document!.IsReady, "LSP文書が準備完了になりませんでした。");
            Assert.True(callerDocument!.IsReady, "呼び出し元のLSP文書が準備完了になりませんでした。");

            var offset = serviceText.IndexOf("GetValue", StringComparison.Ordinal);
            var original = CSharpSignatureSyntax.Read(
                servicePath, LspUri.FromPath(servicePath), serviceText,
                PositionOf(serviceText, offset).Line, PositionOf(serviceText, offset).Character).Signature;
            Assert.NotNull(original);

            var change = new SignatureChange("string", [
                new SignatureParameterChange(1, new SignatureParameter("repeat", "int")),
                new SignatureParameterChange(0, new SignatureParameter("prefix", "string")),
            ]);
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [servicePath] = serviceText,
                [callerPath] = callerText,
            };
            var refactoring = new CSharpSignatureRefactoring(
                lsp, [root], path => texts.GetValueOrDefault(Path.GetFullPath(path)));

            var plan = await refactoring.PlanAsync(original!, change);

            Assert.Null(plan.Error);
            Assert.Equal(2, plan.SiteCount);
            Assert.Contains(plan.Changes.Keys, uri =>
                string.Equals(LspUri.TryToLocalPath(uri), servicePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Changes.Keys, uri =>
                string.Equals(LspUri.TryToLocalPath(uri), callerPath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Changes.Values.SelectMany(edits => edits), edit =>
                edit.NewText.Contains("(1, \"value:\")", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>通常のCIでは外部プロセスを起動せず、明示的な環境変数でだけ実行するFact。</summary>
    public sealed class RealRoslynFactAttribute : FactAttribute
    {
        public RealRoslynFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("LOOMO_RUN_REAL_LSP"), "1",
                    StringComparison.Ordinal))
                Skip = "LOOMO_RUN_REAL_LSP=1 のときだけ実Roslynサーバーを起動します。";
        }
    }

    private static string FindFixtureRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "Fixtures", "CSharpIde");
            if (File.Exists(Path.Combine(candidate, "CSharpIde.sln"))) return candidate;
            current = current.Parent;
        }

        // テスト出力からリポジトリを辿れない構成（単独pack等）では、開発ワークスペースを明示的に使う。
        var workspace = Path.Combine(Environment.GetEnvironmentVariable("LOOMO_WORKSPACE") ?? "",
            "tests", "Fixtures", "CSharpIde");
        if (File.Exists(Path.Combine(workspace, "CSharpIde.sln"))) return workspace;
        throw new XunitException("CSharpIde fixtureが見つかりません。");
    }

    private static LspPosition PositionOf(string source, int offset)
    {
        var line = source[..offset].Count(c => c == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1));
        return new LspPosition(line, offset - (lineStart < 0 ? 0 : lineStart + 1));
    }

    private static string CopyFixtureToTemp(string sourceRoot)
    {
        var destination = Path.Combine(Path.GetTempPath(), "Loomo-Roslyn-" + Guid.NewGuid().ToString("N"));
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination,
                Path.GetRelativePath(sourceRoot, directory)));
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }
}

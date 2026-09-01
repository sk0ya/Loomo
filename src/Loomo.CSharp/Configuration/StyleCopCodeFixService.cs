using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>StyleCop公式CodeFixProviderをWorkspaceEditへ変換するIDE用アダプター。
/// ルール判定・修正内容はStyleCopのCodeFixes DLLへ委譲する。</summary>
public sealed class StyleCopCodeFixService
{
    private readonly StyleCopConfigurationService _configuration;
    private readonly StyleCopDiagnosticService _diagnostics;

    public StyleCopCodeFixService(StyleCopConfigurationService? configuration = null)
    {
        _configuration = configuration ?? new StyleCopConfigurationService();
        _diagnostics = new StyleCopDiagnosticService(_configuration);
    }

    /// <summary>対象プロジェクトに公式StyleCop CodeFix DLLが導入済みかを返す。</summary>
    public bool IsAvailable(ProjectModel project)
        => FindCodeFixAssembly(project) is not null;

    /// <summary>公式CodeFixを文書ごとに繰り返し適用し、LSPのsource.fixAllがStyleCopを返さない
    /// 環境でもプロジェクト範囲のFix allを提供する。各文書は解析→修正→再解析を最大100回行い、
    /// 変更は全対象を検証してから一つのWorkspaceEditへまとめる。</summary>
    public async Task<StyleCopCodeFixBatchResult> ApplyAllAsync(
        ProjectModel project,
        IReadOnlyList<string> filePaths,
        IReadOnlyDictionary<string, string>? currentTexts = null,
        CancellationToken cancellationToken = default)
    {
        if (FindCodeFixAssembly(project) is null)
            return new(null, 0, 0, "StyleCop公式CodeFixes DLLが見つかりません。");

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var path = Path.GetFullPath(rawPath);
            if (!File.Exists(path)) continue;
            texts[path] = currentTexts?.TryGetValue(path, out var current) == true
                ? current : await File.ReadAllTextAsync(path, cancellationToken);
        }

        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase);
        var actionsFound = 0;
        foreach (var (path, initialText) in texts.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = initialText;
            var baselineCompilerErrors = CompilerErrorKeys(project, path, initialText);
            for (var pass = 0; pass < 100; pass++)
            {
                var analysis = await _diagnostics.AnalyzeAsync(project, path, text, cancellationToken);
                if (analysis.Error is { Length: > 0 } analysisError)
                    return new(null, texts.Count, actionsFound, analysisError);
                var diagnostics = analysis.Diagnostics
                    .Where(d => d.Code?.StartsWith("SA", StringComparison.OrdinalIgnoreCase) == true)
                    .ToArray();
                if (diagnostics.Length == 0) break;

                var changed = false;
                // 1件適用すると、後続診断の行／文字位置が変わり得る。古い範囲を
                // そのまま使わず、最初に実際に変更できた1件でこのpassを終えて再解析する。
                foreach (var diagnostic in diagnostics)
                {
                    var fix = await ApplyAsync(project, path, text, diagnostic, cancellationToken);
                    if (fix.Edit is not { } edit || !edit.Changes.TryGetValue(LspUri.FromPath(path), out var edits))
                        continue;
                    var updated = ApplyTextEdits(text, edits);
                    if (string.Equals(updated, text, StringComparison.Ordinal)) continue;
                    if (!PreservesUsingDirectives(text, updated))
                        continue;
                    // 公式CodeFixの一部は簡略Workspaceでusing分類を誤り、解決不能な
                    // namespaceを挿入することがある。元本文に無かったcompiler errorを
                    // 増やす候補はStyleCopの診断が消えても採用しない。
                    if (IntroducesCompilerError(project, path, updated, baselineCompilerErrors))
                        continue;
                    text = updated;
                    actionsFound++;
                    changed = true;
                    break;
                }
                if (!changed) break;
            }
            if (!string.Equals(text, initialText, StringComparison.Ordinal))
            {
                changes[LspUri.FromPath(path)] = [new LspTextEdit(FullDocumentRange(initialText), text)];
            }
        }

        return new(changes.Count == 0 ? null : new LspWorkspaceEdit(changes), texts.Count, actionsFound);
    }

    /// <summary>StyleCop CodeFixはusingの配置や順序を変えてよいが、参照先そのものを
    /// 別namespaceへ置換してはいけない。簡略Compilationでは未解決usingを見逃す場合が
    /// あるため、意味診断だけに頼らず構文上のdirective集合も不変にする。</summary>
    private static bool PreservesUsingDirectives(string before, string after)
    {
        static string[] Directives(string source)
            => CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(usingDirective => usingDirective.ToString().Trim())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

        return Directives(before).SequenceEqual(Directives(after), StringComparer.Ordinal);
    }

    private static HashSet<string> CompilerErrorKeys(
        ProjectModel project, string filePath, string source)
    {
        try
        {
            var compilation = CreateValidationCompilation(project, filePath, source);
            return compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error &&
                    IsDiagnosticInFile(diagnostic, filePath))
                .Select(DiagnosticKey)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            // CodeFixの既存動作を壊さず、Compilationを作れない環境ではこの追加検証を
            // 適用しない。対象本文の構文検証は公式CodeFix側と最終Apply側が行う。
            return [];
        }
    }

    private static bool IntroducesCompilerError(
        ProjectModel project, string filePath, string source, ISet<string> baselineErrors)
    {
        try
        {
            var compilation = CreateValidationCompilation(project, filePath, source);
            return compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error &&
                    IsDiagnosticInFile(diagnostic, filePath))
                .Select(DiagnosticKey)
                .Any(key => !baselineErrors.Contains(key));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            return true;
        }
    }

    private static CSharpCompilation CreateValidationCompilation(
        ProjectModel project, string filePath, string source)
    {
        var solution = new SolutionModel(null, project.Name, project.Directory, [project],
            ProjectLoadState.Ready);
        return CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            CSharpWorkspaceSourceScope.ProjectGraph,
            includeSemanticCompilation: true).SemanticCompilation
            ?? throw new InvalidOperationException("StyleCop CodeFix検証用Compilationを作れません。");
    }

    private static bool IsDiagnosticInFile(Diagnostic diagnostic, string filePath)
        => diagnostic.Location.IsInSource && string.Equals(
            Path.GetFullPath(diagnostic.Location.SourceTree?.FilePath ?? ""),
            Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);

    private static string DiagnosticKey(Diagnostic diagnostic)
        => diagnostic.Id + "\u001f" + diagnostic.GetMessage();

    public async Task<StyleCopCodeFixResult> ApplyAsync(
        ProjectModel project,
        string filePath,
        string source,
        LspDiagnostic diagnostic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = Path.GetFullPath(filePath);
        if (!string.Equals(diagnostic.Code?.Trim(), "SA1101", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(diagnostic.Code))
            return new(null, "StyleCopの診断コードがありません。");

        var codeFixPath = FindCodeFixAssembly(project);
        if (codeFixPath is null)
            return new(null, "StyleCop公式CodeFixes DLLが見つかりません。");

        try
        {
            var providers = LoadProviders(codeFixPath, diagnostic.Code!);
            if (providers.Count == 0)
                return new(null, $"{diagnostic.Code} に対応するStyleCop Code Fixがありません。");

            var hostAssemblies = MefHostServices.DefaultAssemblies
                .Concat([Assembly.Load("Microsoft.CodeAnalysis.CSharp.Workspaces")]);
            using var workspace = new AdhocWorkspace(MefHostServices.Create(hostAssemblies));
            var projectId = ProjectId.CreateNewId();
            var references = (project.SelectedTargetFrameworkModel?.References ?? [])
                .Where(item => File.Exists(item.FullPath))
                .Select(item => MetadataReference.CreateFromFile(item.FullPath))
                .ToImmutableArray<MetadataReference>();
            var target = project.SelectedTargetFrameworkModel;
            workspace.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), project.Name,
                project.Name, LanguageNames.CSharp, filePath: project.FullPath,
                metadataReferences: references)
                .WithParseOptions(CSharpProjectCompilationOptions.Parse(target))
                .WithCompilationOptions(CSharpProjectCompilationOptions.Compilation(target)));

            var documentIds = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
            var compileFiles = project.SelectedTargetFrameworkModel?.CompileFiles ?? [];
            foreach (var item in compileFiles)
            {
                var compilePath = Path.GetFullPath(item.FullPath);
                if (!File.Exists(compilePath) || !documentIds.TryAdd(compilePath, DocumentId.CreateNewId(projectId)))
                    continue;
                var compileText = string.Equals(compilePath, path, StringComparison.OrdinalIgnoreCase)
                    ? source
                    : await File.ReadAllTextAsync(compilePath, cancellationToken);
                workspace.AddDocument(DocumentInfo.Create(documentIds[compilePath], Path.GetFileName(compilePath),
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(compileText), VersionStamp.Create())),
                    filePath: compilePath));
            }
            if (!documentIds.TryGetValue(path, out var documentId))
            {
                documentId = DocumentId.CreateNewId(projectId);
                documentIds[path] = documentId;
                workspace.AddDocument(DocumentInfo.Create(documentId, Path.GetFileName(path),
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
                    filePath: path));
            }

            var document = workspace.CurrentSolution.GetDocument(documentId)
                ?? throw new InvalidOperationException("Code Fix対象の文書をWorkspaceへ追加できませんでした。");
            var roslynDiagnostic = CreateDiagnostic(document, diagnostic);
            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actions = new List<CodeAction>();
                var context = new CodeFixContext(document, roslynDiagnostic,
                    (action, _) => actions.Add(action), cancellationToken);
                try
                {
                    await provider.RegisterCodeFixesAsync(context);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 公式CodeFixの一部は、簡略Workspaceでは設定不足のため失敗することがある。
                    // 同じ診断IDの別Provider／別アクションを試し、Fix all全体を巻き込まない。
                    continue;
                }
                foreach (var action in actions)
                {
                    ApplyChangesOperation? operation;
                    try
                    {
                        operation = (await action.GetOperationsAsync(cancellationToken))
                            .OfType<ApplyChangesOperation>().FirstOrDefault();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    var updated = operation?.ChangedSolution.GetDocument(documentId);
                    var updatedText = updated is null ? null : await updated.GetTextAsync(cancellationToken);
                    if (updatedText is null || string.Equals(updatedText.ToString(), source, StringComparison.Ordinal))
                        continue;
                    var range = FullDocumentRange(source);
                    var edit = new LspWorkspaceEdit(
                        new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [LspUri.FromPath(path)] = [new LspTextEdit(range, updatedText.ToString())],
                        });
                    return new(edit, null, action.Title);
                }
            }
            return new(null, $"{diagnostic.Code}のCode Fixを適用できませんでした。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   BadImageFormatException or FileLoadException)
        {
            return new(null, $"StyleCop Code Fixに失敗しました: {ex.Message}");
        }
    }

    private string? FindCodeFixAssembly(ProjectModel project)
        => _configuration.Resolve(project).AnalyzerPaths
            .Select(path => Path.Combine(Path.GetDirectoryName(path) ?? "",
                "StyleCop.Analyzers.CodeFixes.dll"))
            .FirstOrDefault(File.Exists);

    private static IReadOnlyList<CodeFixProvider> LoadProviders(string assemblyPath, string code)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        return assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(type))
            .Select(type => Activator.CreateInstance(type) as CodeFixProvider)
            .Where(provider => provider is not null && provider.FixableDiagnosticIds.Any(id =>
                string.Equals(id, code, StringComparison.OrdinalIgnoreCase)))
            .Select(provider => provider!)
            .ToArray();
    }

    private static Diagnostic CreateDiagnostic(Document document, LspDiagnostic diagnostic)
    {
        var text = document.GetTextAsync().GetAwaiter().GetResult();
        var start = ToOffset(text, diagnostic.Range.Start);
        var end = ToOffset(text, diagnostic.Range.End);
        var descriptor = new DiagnosticDescriptor(diagnostic.Code ?? "StyleCop", diagnostic.Code ?? "StyleCop",
            diagnostic.Message, "StyleCop", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, true);
        var tree = CSharpSyntaxTree.ParseText(text, path: document.FilePath ?? "");
        return Microsoft.CodeAnalysis.Diagnostic.Create(descriptor,
            Location.Create(tree, TextSpan.FromBounds(start, end)));
    }

    private static int ToOffset(SourceText text, LspPosition position)
    {
        var line = Math.Clamp(position.Line, 0, text.Lines.Count - 1);
        var textLine = text.Lines[line];
        return Math.Clamp(textLine.Start + position.Character, textLine.Start, textLine.End);
    }

    private static string ApplyTextEdits(string source, IReadOnlyList<LspTextEdit> edits)
    {
        var text = SourceText.From(source);
        var ranges = edits.Select(edit => (edit, start: ToOffset(text, edit.Range.Start), end: ToOffset(text, edit.Range.End)))
            .OrderByDescending(item => item.start).ToArray();
        var result = source;
        var lastStart = int.MaxValue;
        foreach (var item in ranges)
        {
            if (item.start > item.end || item.end > result.Length || item.end > lastStart)
                throw new InvalidOperationException("StyleCop Code Fixの編集範囲が競合しています。");
            result = result[..item.start] + item.edit.NewText + result[item.end..];
            lastStart = item.start;
        }
        return result;
    }

    private static LspRange FullDocumentRange(string source)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lastNewline = normalized.LastIndexOf('\n');
        var line = normalized.Count(c => c == '\n');
        var character = lastNewline < 0 ? normalized.Length : normalized.Length - lastNewline - 1;
        return new(new(0, 0), new(line, character));
    }
}

public sealed record StyleCopCodeFixResult(LspWorkspaceEdit? Edit, string? Error, string? Title = null);

public sealed record StyleCopCodeFixBatchResult(
    LspWorkspaceEdit? Edit, int DocumentsScanned, int ActionsFound, string? Error = null);

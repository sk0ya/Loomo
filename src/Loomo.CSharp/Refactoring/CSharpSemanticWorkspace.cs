using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>CSharpCompilationをRoslyn Workspace APIへ一時的に写像する共有ホスト。
/// Rename／参照検索などWorkspaceが必要な機能で、Compilationと文書本文の組み立てを重複させない。</summary>
internal sealed class CSharpSemanticWorkspace : IDisposable
{
    private CSharpSemanticWorkspace(AdhocWorkspace workspace,
        IReadOnlyDictionary<string, DocumentId> documentIds)
    {
        Workspace = workspace;
        DocumentIds = documentIds;
    }

    public AdhocWorkspace Workspace { get; }
    public Solution Solution => Workspace.CurrentSolution;
    public IReadOnlyDictionary<string, DocumentId> DocumentIds { get; }

    public static CSharpSemanticWorkspace Create(
        CSharpCompilation compilation,
        IEnumerable<string>? sourceDocumentPaths = null)
    {
        var assemblies = MefHostServices.DefaultAssemblies.ToList();
        foreach (var assemblyName in new[]
                 { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
        {
            try
            {
                var assembly = Assembly.Load(assemblyName);
                if (!assemblies.Contains(assembly)) assemblies.Add(assembly);
            }
            catch (FileNotFoundException)
            {
                // 機能アセンブリが配布されない環境でも、rename／参照検索は利用できる。
            }
        }
        var workspace = new AdhocWorkspace(MefHostServices.Create(assemblies));
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            compilation.AssemblyName ?? "LoomoCSharpWorkspace",
            compilation.AssemblyName ?? "LoomoCSharpWorkspace",
            LanguageNames.CSharp,
            metadataReferences: compilation.References
                .OfType<PortableExecutableReference>()
                .ToImmutableArray(),
            parseOptions: compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions,
            compilationOptions: compilation.Options);
        workspace.AddProject(projectInfo);

        var allowedSourcePaths = sourceDocumentPaths is null
            ? null
            : sourceDocumentPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var documentIds = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrWhiteSpace(tree.FilePath)) continue;
            var path = Path.GetFullPath(tree.FilePath);
            if (allowedSourcePaths is not null && !allowedSourcePaths.Contains(path)) continue;
            if (!documentIds.TryAdd(path, DocumentId.CreateNewId(projectId))) continue;
            var text = tree.GetText();
            workspace.AddDocument(DocumentInfo.Create(
                documentIds[path], Path.GetFileName(path),
                loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create())),
                filePath: path));
        }

        return new(workspace, documentIds);
    }

    public void Dispose() => Workspace.Dispose();
}

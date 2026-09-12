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

    /// <summary>
    /// MEF の合成結果。<b>プロセスで一度だけ作る。</b>
    ///
    /// <para>以前はこのクラスを呼ぶたびに <see cref="MefHostServices.Create(IEnumerable{Assembly})"/> を
    /// 走らせていた。合成は Roslyn の Features アセンブリ全体を走査するので実測で初回 648ms・以降 140ms
    /// かかり、補完（打鍵ごと）がそれを毎回払っていた。ホストサービスは不変なので共有して問題ない。</para>
    /// </summary>
    private static readonly Lazy<MefHostServices> SharedHost =
        new(CreateHost, LazyThreadSafetyMode.ExecutionAndPublication);

    private static MefHostServices CreateHost()
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
        return MefHostServices.Create(assemblies);
    }

    public static CSharpSemanticWorkspace Create(
        CSharpCompilation compilation,
        IEnumerable<string>? sourceDocumentPaths = null)
    {
        var workspace = new AdhocWorkspace(SharedHost.Value);
        var projectId = ProjectId.CreateNewId();

        var allowedSourcePaths = sourceDocumentPaths is null
            ? null
            : sourceDocumentPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var documentIds = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        var documents = new List<DocumentInfo>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrWhiteSpace(tree.FilePath)) continue;
            var path = Path.GetFullPath(tree.FilePath);
            if (allowedSourcePaths is not null && !allowedSourcePaths.Contains(path)) continue;
            if (!documentIds.TryAdd(path, DocumentId.CreateNewId(projectId))) continue;
            var text = tree.GetText();
            documents.Add(DocumentInfo.Create(
                documentIds[path], Path.GetFileName(path),
                loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create())),
                filePath: path));
        }

        // 文書は ProjectInfo へ<b>まとめて</b>渡す。AdhocWorkspace.AddDocument は 1 件ごとに
        // Solution を作り直すので、1000 文書を 1 件ずつ足すと実測 522ms かかっていた（一括なら 5ms）。
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
            compilationOptions: compilation.Options,
            documents: documents);
        workspace.AddProject(projectInfo);

        return new(workspace, documentIds);
    }

    public void Dispose() => Workspace.Dispose();
}

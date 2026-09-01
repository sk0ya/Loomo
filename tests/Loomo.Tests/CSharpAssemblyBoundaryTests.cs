using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Testing;
using sk0ya.Loomo.CSharp.Debug;
using sk0ya.Loomo.CSharp.LanguageServer;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpAssemblyBoundaryTests
{
    [Fact]
    public void CSharp_project_models_live_in_the_dedicated_assembly()
    {
        Assert.Equal("sk0ya.Loomo.CSharp", typeof(ProjectModel).Assembly.GetName().Name);
        Assert.Equal("sk0ya.Loomo.CSharp", typeof(ITestDiscoveryService).Assembly.GetName().Name);
        Assert.Equal("sk0ya.Loomo.CSharp", typeof(CSharpTrxResultParser).Assembly.GetName().Name);

        var coreTypes = typeof(AgentOrchestrator).Assembly.GetExportedTypes();
        var legacyProjectNamespace = "sk0ya.Loomo." + "Core.Projects";
        Assert.DoesNotContain(coreTypes, type =>
            type.Namespace?.StartsWith(legacyProjectNamespace, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(coreTypes, type =>
            type.Name is "ISolutionModelService" or "IProjectEvaluator" or "ProjectEvaluation"
                or "ITestDiscoveryService" or "DiscoveredTest");
    }

    [Fact]
    public void CSharp_debug_language_rules_live_in_the_dedicated_assembly()
    {
        Assert.Equal("sk0ya.Loomo.CSharp", typeof(CSharpAutosExtractor).Assembly.GetName().Name);
        Assert.Equal("sk0ya.Loomo.Core", typeof(AutosExtractor).Assembly.GetName().Name);
        Assert.DoesNotContain(typeof(AutosExtractor).GetNestedTypes(), type => type.Name == "AutosLanguage");
    }

    [Fact]
    public void CSharp_language_server_definition_lives_in_the_dedicated_assembly()
    {
        Assert.Equal("sk0ya.Loomo.CSharp", typeof(CSharpLanguageServerCatalog).Assembly.GetName().Name);
        Assert.Equal("roslyn-language-server", CSharpLanguageServerCatalog.RoslynExecutable);
        Assert.Contains("--autoLoadProjects", CSharpLanguageServerCatalog.RoslynArgs);
    }

    [Fact]
    public void CSharp_editor_commands_live_in_the_dedicated_assembly()
    {
        Assert.Equal("sk0ya.Loomo.CSharp", typeof(CSharpEditorCommandCatalog).Assembly.GetName().Name);
        Assert.Equal(CSharpEditorCommandCatalog.All.Count,
            CSharpEditorCommandCatalog.All.Select(command => command.Id).Distinct().Count());
        Assert.Contains(CSharpEditorCommandCatalog.Format,
            CSharpEditorCommandCatalog.All.Select(command => command.Id));
        Assert.Contains(CSharpEditorCommandCatalog.Cleanup,
            CSharpEditorCommandCatalog.All.Select(command => command.Id));
        Assert.Contains(CSharpEditorCommandCatalog.PeekDefinition,
            CSharpEditorCommandCatalog.All.Select(command => command.Id));
        Assert.Equal("Alt+Enter", CSharpEditorCommandCatalog.All
            .Single(command => command.Id == CSharpEditorCommandCatalog.QuickFix).DefaultBinding);
        Assert.Equal("F12", CSharpEditorCommandCatalog.All
            .Single(command => command.Id == CSharpEditorCommandCatalog.GoToDefinition).DefaultBinding);
    }

    [Fact]
    public void CSharp_completion_implementation_stays_out_of_the_App_assembly()
    {
        Assert.Equal("sk0ya.Loomo.CSharp", typeof(CSharpCompletionService).Assembly.GetName().Name);
        var csharpReferences = typeof(CSharpCompletionService).Assembly.GetReferencedAssemblies();
        Assert.Contains(csharpReferences, assembly =>
            assembly.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true);

        var uiAndAdapterAssemblies = new[]
        {
            typeof(sk0ya.Loomo.App.Views.ShellWindow).Assembly,
            typeof(sk0ya.Loomo.Services.Lsp.LspServerCatalog).Assembly,
            typeof(AgentOrchestrator).Assembly,
        };

        foreach (var assembly in uiAndAdapterAssemblies)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), referencedAssembly =>
                referencedAssembly.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true);
        }
    }
}

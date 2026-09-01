using System.IO;
using System.Diagnostics;
using Editor.Controls;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

[Collection(CSharpExternalProcessCollection.Name)]
public sealed class CSharpIdeFixtureTests
{
    private static string FixtureRoot
    {
        get
        {
            var start = new DirectoryInfo(Environment.CurrentDirectory);
            for (var directory = start; directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "CSharpIde");
                if (File.Exists(Path.Combine(candidate, "CSharpIde.sln"))) return candidate;
            }
            throw new DirectoryNotFoundException("CSharpIde fixture がリポジトリ内に見つかりません。");
        }
    }

    [Fact]
    public void FixtureContainsTheProjectShapesNeededByPhaseZero()
    {
        var root = FixtureRoot;
        Assert.True(File.Exists(Path.Combine(root, "Directory.Build.props")));
        Assert.True(File.Exists(Path.Combine(root, "Directory.Build.targets")));
        Assert.True(File.Exists(Path.Combine(root, ".editorconfig")));
        Assert.True(File.Exists(Path.Combine(root, "stylecop.json")));
        Assert.Contains("TargetFrameworks", File.ReadAllText(Path.Combine(root, "src", "Feature", "Feature.csproj")));
        Assert.Contains("StyleCop.Analyzers", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        Assert.Contains("dotnet_diagnostic.SA1101.severity = error", File.ReadAllText(Path.Combine(root, ".editorconfig")));
        Assert.Contains("OutputItemType=\"Analyzer\"", File.ReadAllText(Path.Combine(root, "src", "Feature", "Feature.csproj")));
        Assert.Contains("Compile Include=\"..\\Shared\\LinkedFile.cs\"", File.ReadAllText(Path.Combine(root, "src", "Feature", "Feature.csproj")));
        Assert.True(File.Exists(Path.Combine(root, "src", "Client", "Client.csproj")));
        Assert.True(File.Exists(Path.Combine(root, "src", "Client", "Properties", "launchSettings.json")));
        Assert.True(File.Exists(Path.Combine(root, "tests", "Feature.Tests", "Feature.Tests.csproj")));
    }

    [Fact]
    public void SolutionDiscoveryFindsEveryFixtureProject()
    {
        var (solution, projects) = SolutionProjectDiscovery.Find(FixtureRoot);

        Assert.EndsWith("CSharpIde.sln", solution, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, projects.Count);
        Assert.Contains(projects, p => p.EndsWith("Feature.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projects, p => p.EndsWith("FixtureGenerator.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projects, p => p.EndsWith("Client.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projects, p => p.EndsWith("Feature.Tests.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Real_solution_model_loads_fixture_projects_and_preserves_selected_tfm()
    {
        var root = FixtureRoot;
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        using var service = new SolutionModelService(workspace, new MsBuildProjectEvaluator());

        var model = await service.ReloadAsync();

        Assert.Equal(ProjectLoadState.Ready, model.State);
        Assert.Equal(5, model.Projects.Count);
        Assert.Equal(["Debug", "Release"], model.ConfigurationOptions);
        var feature = Assert.Single(model.Projects, project => project.Name == "Feature");
        Assert.Equal(["net9.0", "net10.0"], feature.TargetFrameworks.Select(target => target.Name));
        Assert.Contains(feature.SelectedTargetFrameworkModel!.ProjectReferences!, reference =>
            reference.EndsWith(Path.Combine("Contracts", "Contracts.csproj"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(feature.TargetFrameworks.SelectMany(target => target.CompileFiles), item =>
            item.Link == "Shared/LinkedFile.cs" || item.FullPath.EndsWith("Shared\\LinkedFile.cs", StringComparison.OrdinalIgnoreCase));
        Assert.True(Assert.Single(model.Projects, project => project.Name == "Feature.Tests").IsTestProject);

        var featurePath = Path.Combine(root, "src", "Feature", "Feature.csproj");
        Assert.True(await service.SelectTargetFrameworkAsync(featurePath, "net9.0"));
        Assert.Equal("net9.0", service.Current.ProjectForTarget(featurePath)!.SelectedTargetFramework);
        await service.ReloadAsync();
        Assert.Equal("net9.0", service.Current.ProjectForTarget(featurePath)!.SelectedTargetFramework);
    }

    [Fact]
    public async Task Fixture_runs_the_build_gate_and_test_journey()
    {
        var root = FixtureRoot;
        var failedBuild = await RunDotnetAsync(root, "build", "CSharpIde.sln",
            "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal");
        Assert.NotEqual(0, failedBuild.ExitCode);
        Assert.Contains("SA1101", failedBuild.Output, StringComparison.OrdinalIgnoreCase);

        var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
            "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
            "-p:NoWarn=SA1101");
        Assert.Equal(0, build.ExitCode);

        var tests = await RunDotnetAsync(root, "test",
            Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
            "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
        Assert.Equal(0, tests.ExitCode);
    }

    [Fact]
    public async Task Fixture_edit_diagnose_fix_and_rediagnose_uses_the_selected_target_framework()
    {
        var root = CopyFixtureToTemp(FixtureRoot);
        try
        {
            var projectPath = Path.Combine(root, "src", "Feature", "Feature.csproj");
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var evaluation = await new MsBuildProjectEvaluator().EvaluateAsync(projectPath, "net10.0", "Debug");
            var project = CreateProjectModel(projectPath, evaluation, "net10.0");
            var solution = new SolutionModel(
                Path.Combine(root, "CSharpIde.sln"), "CSharpIde", root, [project], ProjectLoadState.Ready);

            var original = await File.ReadAllTextAsync(sourcePath);
            var edited = original.Replace("=> _value", "=> _value + missing", StringComparison.Ordinal);
            var compiler = new CSharpCompilerDiagnosticService();
            var broken = await compiler.AnalyzeAsync(solution, sourcePath, edited);
            Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Code == "CS0103");

            var styleCop = new StyleCopDiagnosticService();
            var style = await styleCop.AnalyzeAsync(project, sourcePath, edited);
            Assert.Contains(style.Diagnostics, diagnostic => diagnostic.Code == "SA1101");

            var fixedBatch = await new StyleCopCodeFixService().ApplyAllAsync(
                project, [sourcePath], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourcePath] = edited,
                });
            Assert.Null(fixedBatch.Error);
            var fixedText = Assert.Single(fixedBatch.Edit!.Changes.Values).Single().NewText;
            Assert.Contains("this.value", fixedText);

            var corrected = fixedText.Replace(" + missing", "", StringComparison.Ordinal);
            var compilerAfter = await compiler.AnalyzeAsync(solution, sourcePath, corrected);
            Assert.DoesNotContain(compilerAfter.Diagnostics,
                diagnostic => diagnostic.Code == "CS0103" && diagnostic.Message.Contains("missing", StringComparison.Ordinal));
            var styleAfter = await styleCop.AnalyzeAsync(project, sourcePath, corrected);
            Assert.DoesNotContain(styleAfter.Diagnostics, diagnostic => diagnostic.Code == "SA1101");

            await File.WriteAllTextAsync(sourcePath, corrected);
            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.Equal(0, build.ExitCode);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.Equal(0, tests.ExitCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Extract_interface_creates_file_and_fixture_copy_still_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var destinationPath = Path.Combine(root, "src", "Feature", "IFeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var classOffset = source.IndexOf("FeatureService", StringComparison.Ordinal);
            var selection = new LspRange(
                Position(source, classOffset),
                Position(source, classOffset + "FeatureService".Length));

            var result = CSharpExtractInterfaceService.Extract(
                sourcePath, source, selection, "IFeatureService", destinationPath);

            Assert.Null(result.Error);
            Assert.NotNull(result.Edit);
            var edit = result.Edit!;
            var sourceUri = LspUri.FromPath(sourcePath);
            var destinationUri = LspUri.FromPath(destinationPath);
            var updatedSource = VimEditorControl.ApplyTextEdits(
                source, edit.Changes[sourceUri]);
            var generated = VimEditorControl.ApplyTextEdits(
                "", edit.Changes[destinationUri]);
            Assert.Contains("IFeatureService", updatedSource, StringComparison.Ordinal);
            Assert.Contains("public interface IFeatureService", generated, StringComparison.Ordinal);
            Assert.Contains(edit.FileOperations!, operation =>
                operation.Kind == LspFileOperationKind.Create &&
                string.Equals(operation.Uri, destinationUri, StringComparison.OrdinalIgnoreCase));

            await File.WriteAllTextAsync(sourcePath, updatedSource);
            await File.WriteAllTextAsync(destinationPath, generated);
            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.Equal(0, build.ExitCode);

            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.Equal(0, tests.ExitCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_rename_updates_fixture_callers_and_solution_still_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var original = await File.ReadAllTextAsync(sourcePath);
            var methodOffset = original.IndexOf("GetValue", StringComparison.Ordinal);
            var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
                solution, sourcePath, original, CSharpWorkspaceSourceScope.Solution,
                includeSemanticCompilation: true));

            var result = await CSharpRenameService.RenameAsync(
                sourcePath, original, Position(original, methodOffset), "ReadValue",
                context.SemanticCompilation!, sourceDocumentPaths: context.Snapshot.Texts.Keys);

            Assert.Null(result.Error);
            Assert.Contains(result.Edit!.Changes.Keys, path =>
                path.EndsWith("FeatureService.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Edit.Changes.Keys, path =>
                path.EndsWith("FeatureTests.cs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Edit.Changes.Keys, path =>
                path.Contains("FixtureGenerated.g.cs", StringComparison.OrdinalIgnoreCase));
            foreach (var (uri, edits) in result.Edit.Changes)
            {
                var path = LspUri.TryToLocalPath(uri);
                Assert.NotNull(path);
                var text = await File.ReadAllTextAsync(path!);
                await File.WriteAllTextAsync(path!, VimEditorControl.ApplyTextEdits(text, edits));
            }

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.Equal(0, build.ExitCode);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.Equal(0, tests.ExitCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Code_generation_edit_on_fixture_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var caret = Position(source, source.IndexOf("_value", StringComparison.Ordinal));

            var result = await CSharpSemanticOperations.GenerateAsync(
                solution, sourcePath, source, caret.Line, caret.Character,
                CSharpCodeGenerationKind.Deconstruct);

            Assert.Null(result.Error);
            var edit = result.Edit!;
            var updated = VimEditorControl.ApplyTextEdits(
                source, edit.Changes[LspUri.FromPath(sourcePath)]);
            Assert.Contains("void Deconstruct(out string value)", updated,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, updated);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.Equal(0, build.ExitCode);

            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.Equal(0, tests.ExitCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Equality_code_generation_edit_on_fixture_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var caret = Position(source, source.IndexOf("_value", StringComparison.Ordinal));

            var result = await CSharpSemanticOperations.GenerateAsync(
                solution, sourcePath, source, caret.Line, caret.Character,
                CSharpCodeGenerationKind.EqualsAndGetHashCode);

            Assert.True(result.Error is null, result.Error);
            var updated = VimEditorControl.ApplyTextEdits(
                source, result.Edit!.Changes[LspUri.FromPath(sourcePath)]);
            Assert.Contains("public override bool Equals(object? obj)", updated,
                StringComparison.Ordinal);
            Assert.Contains("Object.Equals(_value, other._value);", updated,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, updated);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);

            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Async_dispose_code_generation_on_fixture_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            source = source.Replace(
                "using Loomo.CSharpFixture.Contracts;",
                "using System;\nusing System.Threading.Tasks;\nusing Loomo.CSharpFixture.Contracts;",
                StringComparison.Ordinal);
            source = source.Replace(
                "    private readonly string _value = FixtureGenerated.Value;",
                "    private readonly string _value = FixtureGenerated.Value;\n"
                + "    private AsyncResource? _resource;",
                StringComparison.Ordinal);
            source += "\ninternal sealed class AsyncResource : IAsyncDisposable\n"
                + "{\n    public ValueTask DisposeAsync() => ValueTask.CompletedTask;\n}\n";
            await File.WriteAllTextAsync(sourcePath, source);

            var caret = Position(source, source.IndexOf("_resource", StringComparison.Ordinal));
            var result = await CSharpSemanticOperations.GenerateAsync(
                solution, sourcePath, source, caret.Line, caret.Character,
                CSharpCodeGenerationKind.AsyncDisposePattern);

            Assert.True(result.Error is null, result.Error);
            var updated = VimEditorControl.ApplyTextEdits(
                source, result.Edit!.Changes[LspUri.FromPath(sourcePath)]);
            Assert.Contains("IAsyncDisposable", updated, StringComparison.Ordinal);
            Assert.Contains("ValueTask DisposeAsync()", updated, StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, updated);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);

            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_move_type_creates_file_and_fixture_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var destinationPath = Path.Combine(root, "src", "Feature", "MovedFixtureType.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            source += "\n\npublic sealed class MovedFixtureType\n{\n    public string Value => \"moved\";\n}\n";
            await File.WriteAllTextAsync(sourcePath, source);

            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var typeOffset = source.IndexOf("MovedFixtureType", StringComparison.Ordinal);
            var result = await CSharpSemanticOperations.MoveTypeToFileAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, typeOffset),
                    Position(source, typeOffset + "MovedFixtureType".Length)),
                destinationPath);

            Assert.Null(result.Error);
            Assert.NotNull(result.Edit);
            var sourceUri = LspUri.FromPath(sourcePath);
            var destinationUri = LspUri.FromPath(destinationPath);
            var edit = result.Edit!;
            Assert.Contains(edit.FileOperations!, operation =>
                operation.Kind == LspFileOperationKind.Create &&
                string.Equals(operation.Uri, destinationUri, StringComparison.OrdinalIgnoreCase));
            var updatedSource = VimEditorControl.ApplyTextEdits(source, edit.Changes[sourceUri]);
            var moved = VimEditorControl.ApplyTextEdits("", edit.Changes[destinationUri]);
            Assert.DoesNotContain("MovedFixtureType", updatedSource, StringComparison.Ordinal);
            Assert.Contains("public sealed class MovedFixtureType", moved, StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, updatedSource);
            await File.WriteAllTextAsync(destinationPath, moved);
            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);

            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_safe_delete_removes_unused_member_and_rejects_referenced_member()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            source = source.Replace(
                "    public string GetValue()\n        => _value;",
                "    public string GetValue()\n        => _value;\n\n"
                + "    private string UnusedForSafeDelete() => \"unused\";",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, source);

            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();

            var fieldOffset = source.IndexOf("_value", StringComparison.Ordinal);
            var referenced = await CSharpSemanticOperations.SafeDeleteAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, fieldOffset),
                    Position(source, fieldOffset + "_value".Length)));
            Assert.NotNull(referenced.Error);
            Assert.Contains("参照", referenced.Error, StringComparison.Ordinal);
            Assert.Null(referenced.Edit);

            var methodOffset = source.IndexOf("UnusedForSafeDelete", StringComparison.Ordinal);
            var removable = await CSharpSemanticOperations.SafeDeleteAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, methodOffset),
                    Position(source, methodOffset + "UnusedForSafeDelete".Length)));
            Assert.Null(removable.Error);
            Assert.NotNull(removable.Edit);
            await ApplyWorkspaceEditAsync(removable.Edit!);

            var updated = await File.ReadAllTextAsync(sourcePath);
            Assert.DoesNotContain("UnusedForSafeDelete", updated, StringComparison.Ordinal);
            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_inline_method_and_variable_update_fixture_and_build()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            source = source.Replace(
                "    public string GetValue()\n        => _value;",
                "    public string GetValue()\n        => _value;\n\n"
                + "    private string AddPrefix(string prefix) => prefix + _value;\n\n"
                + "    public string FormattedValue() => AddPrefix(" + "\"value: \");\n\n"
                + "    public string InlineValue()\n    {\n"
                + "        var current = _value;\n        return current;\n    }",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, source);

            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();

            var methodOffset = source.IndexOf("AddPrefix", StringComparison.Ordinal);
            var inlineMethod = await CSharpSemanticOperations.InlineMethodAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, methodOffset),
                    Position(source, methodOffset + "AddPrefix".Length)));
            Assert.Null(inlineMethod.Error);
            Assert.NotNull(inlineMethod.Edit);
            await ApplyWorkspaceEditAsync(inlineMethod.Edit!);

            var afterMethod = await File.ReadAllTextAsync(sourcePath);
            Assert.DoesNotContain("AddPrefix", afterMethod, StringComparison.Ordinal);
            Assert.Contains("FormattedValue", afterMethod, StringComparison.Ordinal);
            Assert.Contains("value: ", afterMethod, StringComparison.Ordinal);
            Assert.Contains("_value", afterMethod, StringComparison.Ordinal);

            await solutionService.ReloadAsync();
            var variableOffset = afterMethod.IndexOf("current", StringComparison.Ordinal);
            var inlineVariable = await CSharpSemanticOperations.InlineVariableAsync(
                solutionService.Current, sourcePath, afterMethod,
                new LspRange(Position(afterMethod, variableOffset),
                    Position(afterMethod, variableOffset + "current".Length)));
            Assert.Null(inlineVariable.Error);
            Assert.NotNull(inlineVariable.Edit);
            await ApplyWorkspaceEditAsync(inlineVariable.Edit!);

            var updated = await File.ReadAllTextAsync(sourcePath);
            Assert.DoesNotContain("var current", updated, StringComparison.Ordinal);
            Assert.DoesNotContain("return current", updated, StringComparison.Ordinal);
            Assert.Contains("return (_value);", updated, StringComparison.Ordinal);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_encapsulate_field_adds_property_to_fixture_and_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var fieldOffset = source.IndexOf("_value", StringComparison.Ordinal);

            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var result = await CSharpSemanticOperations.EncapsulateFieldAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, fieldOffset),
                    Position(source, fieldOffset + "_value".Length)),
                "Value");

            Assert.Null(result.Error);
            Assert.NotNull(result.Edit);
            await ApplyWorkspaceEditAsync(result.Edit!);

            var updated = await File.ReadAllTextAsync(sourcePath);
            Assert.Contains("Value => _value;", updated, StringComparison.Ordinal);
            Assert.Contains("GetValue()", updated, StringComparison.Ordinal);
            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_pull_up_and_push_down_update_fixture_files_and_build()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var featureDirectory = Path.Combine(root, "src", "Feature");
            var pullBasePath = Path.Combine(featureDirectory, "PullBase.cs");
            var pullDerivedPath = Path.Combine(featureDirectory, "PullDerived.cs");
            var pushBasePath = Path.Combine(featureDirectory, "PushBase.cs");
            var pushDerivedPath = Path.Combine(featureDirectory, "PushDerived.cs");
            await File.WriteAllTextAsync(pullBasePath,
                "namespace Loomo.CSharpFixture.Feature;\n\n"
                + "public class PullBase\n{\n}\n");
            await File.WriteAllTextAsync(pullDerivedPath,
                "namespace Loomo.CSharpFixture.Feature;\n\n"
                + "public sealed class PullDerived : PullBase\n{\n"
                + "    public string Describe() => \"pulled\";\n}\n");
            await File.WriteAllTextAsync(pushBasePath,
                "namespace Loomo.CSharpFixture.Feature;\n\n"
                + "public class PushBase\n{\n"
                + "    public string Describe() => \"pushed\";\n}\n");
            await File.WriteAllTextAsync(pushDerivedPath,
                "namespace Loomo.CSharpFixture.Feature;\n\n"
                + "public sealed class PushDerived : PushBase\n{\n}\n");

            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();

            var pullDerived = await File.ReadAllTextAsync(pullDerivedPath);
            var pullOffset = pullDerived.IndexOf("Describe", StringComparison.Ordinal);
            var pullResult = await CSharpSemanticOperations.PullUpAsync(
                solution, pullDerivedPath, pullDerived,
                new LspRange(Position(pullDerived, pullOffset),
                    Position(pullDerived, pullOffset + "Describe".Length)));

            Assert.Null(pullResult.Error);
            Assert.NotNull(pullResult.Edit);
            Assert.Equal(2, pullResult.Edit!.Changes.Count);
            await ApplyWorkspaceEditAsync(pullResult.Edit);

            var pushedBase = await File.ReadAllTextAsync(pushBasePath);
            var pushOffset = pushedBase.IndexOf("Describe", StringComparison.Ordinal);
            var pushResult = await CSharpSemanticOperations.PushDownAsync(
                solution, pushBasePath, pushedBase,
                new LspRange(Position(pushedBase, pushOffset),
                    Position(pushedBase, pushOffset + "Describe".Length)));

            Assert.Null(pushResult.Error);
            Assert.NotNull(pushResult.Edit);
            Assert.Equal(2, pushResult.Edit!.Changes.Count);
            await ApplyWorkspaceEditAsync(pushResult.Edit);

            var updatedPullBase = await File.ReadAllTextAsync(pullBasePath);
            var updatedPullDerived = await File.ReadAllTextAsync(pullDerivedPath);
            var updatedPushBase = await File.ReadAllTextAsync(pushBasePath);
            var updatedPushDerived = await File.ReadAllTextAsync(pushDerivedPath);
            Assert.Contains("Describe", updatedPullBase, StringComparison.Ordinal);
            Assert.DoesNotContain("Describe", updatedPullDerived, StringComparison.Ordinal);
            Assert.DoesNotContain("Describe", updatedPushBase, StringComparison.Ordinal);
            Assert.Contains("Describe", updatedPushDerived, StringComparison.Ordinal);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Generic_partial_extract_class_on_fixture_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var partPath = Path.Combine(root, "src", "Feature", "ConditionalFeature.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var part = await File.ReadAllTextAsync(partPath);
            source += "\npublic partial class GenericBox<T> where T : class\n"
                + "{\n    private T _value;\n\n"
                + "    public T Get()\n    {\n        return _value;\n    }\n}\n";
            part += "\npublic partial class GenericBox<T> where T : class\n"
                + "{\n    public void Other() { }\n}\n";
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(partPath, part);

            var genericStart = source.IndexOf("public partial class GenericBox", StringComparison.Ordinal);
            var memberStart = source.IndexOf("    private T _value", genericStart, StringComparison.Ordinal);
            var methodStart = source.IndexOf("    public T Get", memberStart, StringComparison.Ordinal);
            var memberEnd = source.IndexOf("\n}", methodStart, StringComparison.Ordinal) + 2;
            var destinationPath = Path.Combine(root, "src", "Feature", "GenericBoxState.cs");
            var result = await CSharpSemanticOperations.ExtractClassAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, memberStart), Position(source, memberEnd)),
                "GenericBoxState", destinationPath,
                new Dictionary<string, string>
                {
                    [sourcePath] = source,
                    [partPath] = part,
                });

            Assert.True(result.Error is null, result.Error);
            var sourceUri = LspUri.FromPath(sourcePath);
            var destinationUri = LspUri.FromPath(destinationPath);
            var updated = VimEditorControl.ApplyTextEdits(source, result.Edit!.Changes[sourceUri]);
            var generated = result.Edit.Changes[destinationUri].Single().NewText;
            Assert.Contains("GenericBoxState<T>", updated, StringComparison.Ordinal);
            Assert.Contains("internal sealed class GenericBoxState<T> where T : class", generated,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Other", generated, StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, updated);
            await File.WriteAllTextAsync(destinationPath, generated);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_extract_method_edit_on_fixture_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            source = source.Replace(
                "public string GetValue()\n        => _value;",
                "public string GetValue()\n    {\n        var current = _value;\n        return current;\n    }",
                StringComparison.Ordinal);
            var start = source.IndexOf("return current;", StringComparison.Ordinal);
            var end = start + "return current;".Length;

            var result = await CSharpSemanticOperations.ExtractMethodAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, start), Position(source, end)),
                "ReturnCurrent");

            Assert.Null(result.Error);
            var updated = VimEditorControl.ApplyTextEdits(
                source, result.Edit!.Changes[LspUri.FromPath(sourcePath)]);
            Assert.Contains("return ReturnCurrent(current);", updated,
                StringComparison.Ordinal);
            Assert.Contains("private string", updated, StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, updated);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.Equal(0, build.ExitCode);

            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.Equal(0, tests.ExitCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_introduce_parameter_updates_fixture_callers_and_builds_and_tests()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var callerPath = Path.Combine(root, "tests", "Feature.Tests", "FeatureTests.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            source = source.Replace(
                "public string GetValue()\n        => _value;",
                "public string GetValue()\n        => _value;\n\n    public string FormatValue()\n        => _value;",
                StringComparison.Ordinal);
            var caller = await File.ReadAllTextAsync(callerPath);
            caller = caller.Replace("GetValue()", "FormatValue()", StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(callerPath, caller);
            var methodOffset = source.IndexOf("FormatValue", StringComparison.Ordinal);

            var result = await CSharpSemanticOperations.IntroduceParameterAsync(
                solution, sourcePath, source,
                new LspRange(Position(source, methodOffset),
                    Position(source, methodOffset + "FormatValue".Length)),
                "marker", "int", "42");

            Assert.Null(result.Error);
            Assert.Equal(2, result.Edit!.Changes.Count);
            foreach (var (uri, edits) in result.Edit.Changes)
            {
                var path = LspUri.TryToLocalPath(uri);
                Assert.NotNull(path);
                var current = await File.ReadAllTextAsync(path!);
                await File.WriteAllTextAsync(path!, VimEditorControl.ApplyTextEdits(current, edits));
            }

            var updatedSource = await File.ReadAllTextAsync(sourcePath);
            var updatedCaller = await File.ReadAllTextAsync(callerPath);
            Assert.Contains("FormatValue(int marker)", updatedSource, StringComparison.Ordinal);
            Assert.Contains("FormatValue(42)", updatedCaller, StringComparison.Ordinal);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_change_signature_updates_fixture_callers_without_an_lsp_server()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var callerPath = Path.Combine(root, "tests", "Feature.Tests", "FeatureTests.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var caller = await File.ReadAllTextAsync(callerPath);
            var methodOffset = source.IndexOf("GetValue", StringComparison.Ordinal);
            var signaturePosition = Position(source, methodOffset);
            var original = CSharpSignatureSyntax.Read(
                sourcePath, LspUri.FromPath(sourcePath), source,
                signaturePosition.Line, signaturePosition.Character).Signature;
            Assert.NotNull(original);

            var change = new SignatureChange("string", [
                new SignatureParameterChange(
                    SignatureParameterChange.Added,
                    new SignatureParameter("prefix", "string"),
                    "\"generated: \""),
            ]);
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [sourcePath] = source,
                [callerPath] = caller,
            };
            var plan = await CSharpSignatureRefactoring.PlanWithSolutionAsync(
                null!, [root], path => texts.GetValueOrDefault(Path.GetFullPath(path)),
                solution, sourcePath, source, original!, change, texts);

            Assert.Null(plan.Error);
            Assert.Equal(3, plan.SiteCount); // interface宣言・実装宣言・別projectの呼び出し
            Assert.Equal(3, plan.Changes.Count);
            Assert.Equal(source, plan.ExpectedTexts![sourcePath]);
            Assert.Equal(caller, plan.ExpectedTexts[callerPath]);
            Assert.Contains(plan.Changes.Values.SelectMany(edits => edits), edit =>
                edit.NewText.Contains("prefix", StringComparison.Ordinal));

            foreach (var (uri, edits) in plan.Changes)
            {
                var path = LspUri.TryToLocalPath(uri);
                Assert.NotNull(path);
                var current = texts.GetValueOrDefault(path!) ?? await File.ReadAllTextAsync(path!);
                texts[path!] = VimEditorControl.ApplyTextEdits(current, edits);
                await File.WriteAllTextAsync(path!, texts[path!]);
            }

            var updatedSource = await File.ReadAllTextAsync(sourcePath);
            var updatedCaller = await File.ReadAllTextAsync(callerPath);
            Assert.Contains("GetValue(string prefix)", updatedSource, StringComparison.Ordinal);
            Assert.Contains("GetValue(\"generated: \")", updatedCaller, StringComparison.Ordinal);

            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
            var tests = await RunDotnetAsync(root, "test",
                Path.Combine("tests", "Feature.Tests", "Feature.Tests.csproj"),
                "--no-build", "--no-restore", "--nologo", "--verbosity:minimal");
            Assert.True(tests.ExitCode == 0, tests.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Project_fix_all_applies_compiler_and_stylecop_fixes_to_the_fixture()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var projectPath = Path.Combine(root, "src", "Feature", "Feature.csproj");
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            var edited = "using System.Text;\n" + source.Replace(
                "public string GetValue()\n        => _value;",
                "public string GetValue()\n    {\n        Console.WriteLine(_value);\n        return _value;\n    }",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(sourcePath, edited);

            var compilerActions = await CSharpCompilerCodeFixService.GetAsync(
                solution, sourcePath, edited,
                new LspRange(new(0, 0), new(edited.Split('\n').Length, 0)));
            Assert.True(compilerActions.Any(action => action.Title == "using System を追加"),
                string.Join(" / ", compilerActions.Select(action => action.Title)));

            var plan = CSharpFixAllPlanner.Create(solution, projectPath, CSharpFixAllScope.Project);
            Assert.True(plan.IsValid, plan.Error);
            var compilerBatch = await CSharpCompilerCodeFixService.ApplyAllAsync(
                solution, plan.Files,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourcePath] = edited,
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourcePath] = edited,
                });
            Assert.True(compilerBatch.Edit!.Changes[LspUri.FromPath(sourcePath)]
                .Any(edit => edit.NewText.Contains("using System;", StringComparison.Ordinal)),
                string.Join("\n---\n", compilerBatch.Edit.Changes[LspUri.FromPath(sourcePath)].Select(edit => edit.NewText)));
            var result = await CSharpFixAllService.ApplyAsync(
                solution, plan,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourcePath] = edited,
                });

            Assert.Null(result.Error);
            Assert.True(result.ActionsFound >= 2, $"ActionsFound={result.ActionsFound}");
            Assert.NotNull(result.Edit);
            Assert.Equal(edited, result.ExpectedTexts![Path.GetFullPath(sourcePath)]);
            var sourceEdit = result.Edit!.Changes[LspUri.FromPath(sourcePath)];
            var fixedSource = VimEditorControl.ApplyTextEdits(edited, sourceEdit);
            var compilerAfter = await new CSharpCompilerDiagnosticService().AnalyzeAsync(
                solution, sourcePath, fixedSource);
            Assert.True(compilerAfter.Diagnostics.All(diagnostic => diagnostic.Code != "CS0246"),
                $"ActionsFound={result.ActionsFound}, sourceEditCount={sourceEdit.Count}, " +
                $"diagnostics={string.Join(" / ", compilerAfter.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message))}");
            Assert.True(fixedSource.Contains("using System;", StringComparison.Ordinal),
                $"ActionsFound={result.ActionsFound}, fixedSource={fixedSource}");
            Assert.True(fixedSource.Contains("this.value", StringComparison.Ordinal),
                $"ActionsFound={result.ActionsFound}, fixedSource={fixedSource}");

            await File.WriteAllTextAsync(sourcePath, fixedSource);
            var build = await RunDotnetAsync(root, "build", "CSharpIde.sln",
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.True(build.ExitCode == 0, build.Output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Semantic_navigation_resolves_fixture_definition_and_references_across_projects()
    {
        var root = FixtureRoot;
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
        var solution = await solutionService.ReloadAsync();
        var callerPath = Path.Combine(root, "tests", "Feature.Tests", "FeatureTests.cs");
        var caller = await File.ReadAllTextAsync(callerPath);
        var offset = caller.LastIndexOf("GetValue", StringComparison.Ordinal);
        var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
            solution, callerPath, caller, CSharpWorkspaceSourceScope.Solution,
            includeSemanticCompilation: true));

        var definition = await CSharpNavigationService.FindDefinitionAsync(
            callerPath, caller, Position(caller, offset), context.SemanticCompilation!);
        var references = await CSharpNavigationService.FindReferencesAsync(
            callerPath, caller, Position(caller, offset), context.SemanticCompilation!);

        Assert.Null(definition.Error);
        Assert.EndsWith("FeatureService.cs", definition.Location!.Uri,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(references.Error);
        Assert.Contains(references.Locations, location =>
            location.Uri.EndsWith("FeatureTests.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references.Locations, location =>
            location.Uri.EndsWith("FeatureService.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Project_fix_all_builds_one_workspace_edit_for_the_selected_project()
    {
        var sourceRoot = FixtureRoot;
        var root = CopyFixtureToTemp(sourceRoot);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(root);
            using var solutionService = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
            var solution = await solutionService.ReloadAsync();
            var projectPath = Path.Combine(root, "src", "Feature", "Feature.csproj");
            var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
            var source = await File.ReadAllTextAsync(sourcePath);
            Assert.NotNull(solution.ProjectForFile(sourcePath));
            var plan = CSharpFixAllPlanner.Create(solution, projectPath, CSharpFixAllScope.Project);

            var result = await CSharpFixAllService.ApplyAsync(solution, plan);

            Assert.Null(result.Error);
            Assert.True(result.ActionsFound > 0);
            Assert.NotNull(result.Edit);
            var edit = result.Edit!;
            Assert.Equal(plan.Files.Count, result.DocumentsScanned);
            var updated = VimEditorControl.ApplyTextEdits(
                source, edit.Changes[LspUri.FromPath(sourcePath)]);
            Assert.Contains("=> this.value", updated, StringComparison.Ordinal);
            Assert.Equal(plan.Files.Count, result.ExpectedTexts!.Count);
            Assert.Equal(source, result.ExpectedTexts[Path.GetFullPath(sourcePath)]);

            await ApplyWorkspaceEditAsync(edit);
            var build = await RunDotnetAsync(root, "build", projectPath,
                "--no-restore", "--no-incremental", "--nologo", "--verbosity:minimal",
                "-p:NoWarn=SA1101");
            Assert.Equal(0, build.ExitCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Workspace_compilation_includes_analyzer_source_generator_output()
    {
        var root = FixtureRoot;
        var projectPath = Path.Combine(root, "src", "Feature", "Feature.csproj");
        var sourcePath = Path.Combine(root, "src", "Feature", "FeatureService.cs");
        var build = await RunDotnetAsync(root, "build", projectPath,
            "--no-restore", "--nologo", "--verbosity:minimal", "-p:NoWarn=SA1101");
        Assert.Equal(0, build.ExitCode);
        var evaluation = await new MsBuildProjectEvaluator().EvaluateAsync(projectPath, "net10.0", "Debug");
        Assert.Contains(evaluation.ProjectReferences, reference =>
            string.Equals(reference.OutputItemType, "Analyzer", StringComparison.OrdinalIgnoreCase));
        var project = CreateProjectModel(projectPath, evaluation, "net10.0");
        Assert.Contains(project.SelectedTargetFrameworkModel!.AdditionalFiles,
            file => file.FullPath.EndsWith("GeneratorInput.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(project.SelectedTargetFrameworkModel!.Analyzers,
            analyzer => analyzer.FullPath.Contains("FixtureGenerator", StringComparison.OrdinalIgnoreCase));
        var solution = new SolutionModel(
            Path.Combine(root, "CSharpIde.sln"), "CSharpIde", root, [project], ProjectLoadState.Ready);
        var source = await File.ReadAllTextAsync(sourcePath);

        var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
            solution, sourcePath, source,
            includeSemanticCompilation: true));
        Assert.NotNull(context.SemanticCompilation);
        var compilation = context.SemanticCompilation!;

        Assert.Contains(compilation.SyntaxTrees, tree =>
            tree.FilePath?.Contains("FixtureGenerated.g.cs", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(compilation.SyntaxTrees, tree =>
            tree.ToString().Contains("from-analyzer-config", StringComparison.Ordinal));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic =>
            diagnostic.Id == "CS0103" && diagnostic.GetMessage().Contains("FixtureGenerated", StringComparison.Ordinal));
    }

    private static ProjectModel CreateProjectModel(
        string projectPath, ProjectEvaluation evaluation, string targetFramework)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        static ProjectItem ToItem(ProjectItemEvaluation item, string projectDirectory)
            => new(item.Include,
                Path.GetFullPath(Path.Combine(projectDirectory, item.FullPath ?? item.Include)), item.Link);

        var target = new TargetFrameworkModel(targetFramework,
            (evaluation.DefineConstants ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            evaluation.LangVersion ?? "default",
            evaluation.Compile.Select(item => ToItem(item, directory)).ToArray(),
            evaluation.Analyzers.Select(item => ToItem(item, directory)).ToArray(),
            evaluation.AdditionalFiles.Select(item => ToItem(item, directory)).ToArray(),
            evaluation.None.Select(item => ToItem(item, directory)).ToArray())
        {
            References = (evaluation.References ?? []).Select(item => ToItem(item, directory)).ToArray(),
            Nullable = evaluation.Nullable,
        };
        return new ProjectModel(Path.GetFileNameWithoutExtension(projectPath), Path.GetFullPath(projectPath),
            directory, evaluation.ProjectReferences.Select(item => ToItem(item, directory).FullPath).ToArray(),
            [target], targetFramework, evaluation.IsTestProject, ProjectLoadState.Ready)
        {
            PackageReferences = (evaluation.PackageReferences ?? []).Select(item => item.Include).ToArray(),
        };
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(
        string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static async Task ApplyWorkspaceEditAsync(LspWorkspaceEdit edit)
    {
        foreach (var (uri, edits) in edit.Changes)
        {
            var path = LspUri.TryToLocalPath(uri);
            Assert.NotNull(path);
            var current = await File.ReadAllTextAsync(path!);
            await File.WriteAllTextAsync(path!, VimEditorControl.ApplyTextEdits(current, edits));
        }
    }

    private static string CopyFixtureToTemp(string sourceRoot)
    {
        var destination = Path.Combine(Path.GetTempPath(), "Loomo-CSharpFixture-" + Guid.NewGuid().ToString("N"));
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

    private static LspPosition Position(string text, int offset)
    {
        var lines = text[..offset].Split('\n');
        return new LspPosition(lines.Length - 1, lines[^1].Length);
    }
}

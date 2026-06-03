using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools.Implementations;

namespace sk0ya.Loomo.Tests;

public class WorkspaceToolsTests
{
    [Fact]
    public async Task FindFiles_returns_matching_workspace_relative_paths()
    {
        using var workspace = TestWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath!, "src"));
        File.WriteAllText(Path.Combine(workspace.RootPath!, "src", "ShellViewModel.cs"), "class ShellViewModel {}");
        File.WriteAllText(Path.Combine(workspace.RootPath!, "README.md"), "# readme");
        var sut = new FindFilesTool(workspace);

        var result = await sut.ExecuteAsync(Json("""{"pattern":"*ViewModel.cs"}"""), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(Path.Combine("src", "ShellViewModel.cs"), result.Content);
        Assert.DoesNotContain("README.md", result.Content);
    }

    [Fact]
    public async Task FindFiles_skips_generated_directories()
    {
        using var workspace = TestWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath!, "bin"));
        File.WriteAllText(Path.Combine(workspace.RootPath!, "bin", "Generated.cs"), "class Generated {}");
        File.WriteAllText(Path.Combine(workspace.RootPath!, "App.cs"), "class App {}");
        var sut = new FindFilesTool(workspace);

        var result = await sut.ExecuteAsync(Json("""{"pattern":"*.cs"}"""), CancellationToken.None);

        Assert.Contains("App.cs", result.Content);
        Assert.DoesNotContain("Generated.cs", result.Content);
    }

    [Fact]
    public async Task SearchFiles_returns_line_matches_and_honors_file_pattern()
    {
        using var workspace = TestWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath!, "src"));
        File.WriteAllLines(Path.Combine(workspace.RootPath!, "src", "Agent.cs"), new[] { "namespace Demo;", "var needle = true;" });
        File.WriteAllText(Path.Combine(workspace.RootPath!, "src", "notes.txt"), "needle in notes");
        var sut = new SearchFilesTool(workspace);

        var result = await sut.ExecuteAsync(Json("""{"query":"needle","file_pattern":"*.cs"}"""), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains($"{Path.Combine("src", "Agent.cs")}:2:var needle = true;", result.Content);
        Assert.DoesNotContain("notes.txt", result.Content);
    }

    [Fact]
    public async Task SearchFiles_skips_files_larger_than_limit()
    {
        using var workspace = TestWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.RootPath!, "large.txt"), "needle after limit");
        var sut = new SearchFilesTool(workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"query":"needle","max_file_bytes":4}"""),
            CancellationToken.None);

        Assert.Equal("(一致なし)", result.Content);
    }

    [Fact]
    public async Task SearchFiles_reports_when_file_limit_is_reached()
    {
        using var workspace = TestWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.RootPath!, "a.txt"), "no match");
        File.WriteAllText(Path.Combine(workspace.RootPath!, "b.txt"), "needle");
        var sut = new SearchFilesTool(workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"query":"needle","max_files":1}"""),
            CancellationToken.None);

        Assert.Contains("検索上限に達しました", result.Content);
        Assert.DoesNotContain("b.txt", result.Content);
    }

    [Fact]
    public async Task SearchFiles_rejects_paths_outside_workspace()
    {
        using var workspace = TestWorkspace.Create();
        var sut = new SearchFilesTool(workspace);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sut.ExecuteAsync(Json("""{"query":"x","path":".."}"""), CancellationToken.None));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class TestWorkspace : IWorkspaceService, IDisposable
    {
        private readonly string _rootPath;

        private TestWorkspace(string rootPath) => _rootPath = rootPath;

        public string? RootPath => _rootPath;
        public string? SelectedPath { get; set; }

        public static TestWorkspace Create()
        {
            var root = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), $"loomo-tools-{Guid.NewGuid():N}"));
            return new TestWorkspace(root.FullName);
        }

        public void OpenFolder(string rootPath) => throw new NotSupportedException();

        public Task<IReadOnlyList<FileNode>> ListAsync(string path) => throw new NotSupportedException();

        public Task<string> ReadFileAsync(string path) => throw new NotSupportedException();

        public string ResolvePath(string path)
        {
            var full = string.IsNullOrWhiteSpace(path)
                ? _rootPath
                : Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(_rootPath, path));

            if (!IsWithin(_rootPath, full))
                throw new UnauthorizedAccessException(full);

            return full;
        }

        public void Dispose() => Directory.Delete(_rootPath, recursive: true);

        private static bool IsWithin(string root, string candidate)
        {
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var c = Path.GetFullPath(candidate);
            return c.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

#pragma warning disable CS0067
        public event EventHandler<string?>? SelectionChanged;
        public event EventHandler<string?>? RootChanged;
#pragma warning restore CS0067
    }
}

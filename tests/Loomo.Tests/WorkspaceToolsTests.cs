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
    public async Task GetProjectTree_renders_nested_tree_and_skips_generated_dirs()
    {
        using var workspace = TestWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.RootPath!, "src"));
        File.WriteAllText(Path.Combine(workspace.RootPath!, "src", "App.cs"), "class App {}");
        Directory.CreateDirectory(Path.Combine(workspace.RootPath!, "obj"));
        File.WriteAllText(Path.Combine(workspace.RootPath!, "obj", "Generated.cs"), "class Generated {}");
        var sut = new GetProjectTreeTool(workspace);

        var result = await sut.ExecuteAsync(Json("{}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("src/", result.Content);
        Assert.Contains("App.cs", result.Content);
        Assert.DoesNotContain("Generated.cs", result.Content);
    }

    [Fact]
    public async Task GetProjectTree_always_lists_first_level_and_caps_deeper_levels()
    {
        using var workspace = TestWorkspace.Create();
        // 1階層目は max_entries を超えても全て出す（ここでは 3 ファイル + 1 フォルダ）。
        File.WriteAllText(Path.Combine(workspace.RootPath!, "one.cs"), "class One {}");
        File.WriteAllText(Path.Combine(workspace.RootPath!, "two.cs"), "class Two {}");
        File.WriteAllText(Path.Combine(workspace.RootPath!, "three.cs"), "class Three {}");
        var sub = Directory.CreateDirectory(Path.Combine(workspace.RootPath!, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "inner1.cs"), "class Inner1 {}");
        File.WriteAllText(Path.Combine(sub, "inner2.cs"), "class Inner2 {}");
        var sut = new GetProjectTreeTool(workspace);

        var result = await sut.ExecuteAsync(Json("""{"max_entries":1}"""), CancellationToken.None);

        Assert.False(result.IsError);
        // 1階層目は上限に関わらず全件。
        Assert.Contains("one.cs", result.Content);
        Assert.Contains("two.cs", result.Content);
        Assert.Contains("three.cs", result.Content);
        Assert.Contains("sub/", result.Content);
        // 2階層目は上限まで（1件）だけ展開し、残りは省略される。
        Assert.Contains("inner1.cs", result.Content);
        Assert.DoesNotContain("inner2.cs", result.Content);
    }

    [Fact]
    public async Task GetProjectTree_excludes_gitignored_entries()
    {
        using var workspace = TestWorkspace.Create();
        if (!TryGitInit(workspace.RootPath!)) return; // git が無い環境ではスキップ。

        File.WriteAllText(Path.Combine(workspace.RootPath!, ".gitignore"), "secret.txt\nlogs/\n");
        File.WriteAllText(Path.Combine(workspace.RootPath!, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(workspace.RootPath!, "secret.txt"), "ignored");
        Directory.CreateDirectory(Path.Combine(workspace.RootPath!, "logs"));
        File.WriteAllText(Path.Combine(workspace.RootPath!, "logs", "run.log"), "ignored");
        var sut = new GetProjectTreeTool(workspace);

        var result = await sut.ExecuteAsync(Json("{}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("keep.txt", result.Content);
        Assert.DoesNotContain("secret.txt", result.Content);
        Assert.DoesNotContain("logs/", result.Content);
    }

    private static bool TryGitInit(string root)
    {
        try
        {
            foreach (var args in new[] { "init", "config user.email t@t.t", "config user.name t" })
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in args.Split(' ')) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) return false;
                p.WaitForExit();
                if (p.ExitCode != 0) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

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

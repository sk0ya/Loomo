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

public class EditorToolsTests
{
    [Fact]
    public async Task ReplaceTextOnce_replaces_unique_match()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "alpha\nneedle\nomega\n");
        var editor = new CapturingEditor();
        var sut = new ReplaceTextOnceTool(editor, workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"path":"App.cs","old_text":"needle","new_text":"replacement"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\nreplacement\nomega\n", await File.ReadAllTextAsync(path));
        Assert.Equal(path, editor.LastDiffPath);
    }

    [Fact]
    public async Task ReplaceTextOnce_describes_diff_for_approval()
    {
        using var workspace = TestWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath!, "App.cs"), "alpha\nneedle\nomega\n");
        var sut = new ReplaceTextOnceTool(new CapturingEditor(), workspace);

        var summary = sut.DescribeInvocation(
            Json("""{"path":"App.cs","old_text":"needle","new_text":"replacement"}"""));

        Assert.Contains("一意文字列置換", summary);
        Assert.Contains("-needle", summary);
        Assert.Contains("+replacement", summary);
    }

    [Fact]
    public async Task ReplaceTextOnce_rejects_non_unique_match()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "needle\nneedle\n");
        var sut = new ReplaceTextOnceTool(new CapturingEditor(), workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"path":"App.cs","old_text":"needle","new_text":"replacement"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("needle\nneedle\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CreateFile_writes_new_file()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "New.cs");
        var sut = new CreateFileTool(new CapturingEditor(), workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"path":"New.cs","content":"class New {}\n"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("class New {}\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CreateFile_rejects_existing_file()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "class App {}\n");
        var sut = new CreateFileTool(new CapturingEditor(), workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"path":"App.cs","content":"overwritten"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("class App {}\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_applies_multiple_blocks_in_order()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\nfour\n");
        var sut = new ApplyPatchTool(new CapturingEditor(), workspace);

        var patch = "<<<<<<< SEARCH\none\n=======\nONE\n>>>>>>> REPLACE\n"
                  + "<<<<<<< SEARCH\nthree\n=======\nTHREE\n>>>>>>> REPLACE\n";
        var result = await sut.ExecuteAsync(
            Json(JsonSerializer.Serialize(new { path = "App.cs", patch })),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ONE\ntwo\nTHREE\nfour\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_matches_lf_search_against_crlf_file()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\ngamma\r\n");
        var sut = new ApplyPatchTool(new CapturingEditor(), workspace);

        var patch = "<<<<<<< SEARCH\nbeta\n=======\nBETA\n>>>>>>> REPLACE\n";
        var result = await sut.ExecuteAsync(
            Json(JsonSerializer.Serialize(new { path = "App.cs", patch })),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_rejects_non_unique_search()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "dup\ndup\n");
        var sut = new ApplyPatchTool(new CapturingEditor(), workspace);

        var patch = "<<<<<<< SEARCH\ndup\n=======\nX\n>>>>>>> REPLACE\n";
        var result = await sut.ExecuteAsync(
            Json(JsonSerializer.Serialize(new { path = "App.cs", patch })),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("dup\ndup\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_rejects_missing_search()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "one\ntwo\n");
        var sut = new ApplyPatchTool(new CapturingEditor(), workspace);

        var patch = "<<<<<<< SEARCH\nnope\n=======\nX\n>>>>>>> REPLACE\n";
        var result = await sut.ExecuteAsync(
            Json(JsonSerializer.Serialize(new { path = "App.cs", patch })),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_rejects_unclosed_block()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "one\n");
        var sut = new ApplyPatchTool(new CapturingEditor(), workspace);

        var patch = "<<<<<<< SEARCH\none\n=======\nONE\n";
        var result = await sut.ExecuteAsync(
            Json(JsonSerializer.Serialize(new { path = "App.cs", patch })),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("one\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_describes_diff_for_approval()
    {
        using var workspace = TestWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath!, "App.cs"), "one\ntwo\n");
        var sut = new ApplyPatchTool(new CapturingEditor(), workspace);

        var patch = "<<<<<<< SEARCH\ntwo\n=======\nTWO\n>>>>>>> REPLACE\n";
        var summary = sut.DescribeInvocation(
            Json(JsonSerializer.Serialize(new { path = "App.cs", patch })));

        Assert.Contains("パッチ適用", summary);
        Assert.Contains("-two", summary);
        Assert.Contains("+TWO", summary);
    }

    [Fact]
    public async Task GetSelectionText_returns_editor_selection()
    {
        var editor = new CapturingEditor { SelectedText = "selected" };
        var sut = new GetSelectionTextTool(editor);

        var result = await sut.ExecuteAsync(Json("{}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("selected", result.Content);
    }

    [Fact]
    public async Task ReplaceSelection_replaces_unique_selection_in_active_file()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "alpha\nneedle\nomega\n");
        var editor = new CapturingEditor
        {
            ActiveFilePath = path,
            ActiveContent = "alpha\nneedle\nomega\n",
            SelectedText = "needle",
        };
        var sut = new ReplaceSelectionTool(editor, workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"new_text":"replacement"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha\nreplacement\nomega\n", await File.ReadAllTextAsync(path));
        Assert.Equal(path, editor.LastDiffPath);
    }

    [Fact]
    public void ReplaceSelection_describes_diff_for_approval()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        var editor = new CapturingEditor
        {
            ActiveFilePath = path,
            ActiveContent = "alpha\nneedle\nomega\n",
            SelectedText = "needle",
        };
        var sut = new ReplaceSelectionTool(editor, workspace);

        var summary = sut.DescribeInvocation(Json("""{"new_text":"replacement"}"""));

        Assert.Contains("選択範囲置換", summary);
        Assert.Contains("-needle", summary);
        Assert.Contains("+replacement", summary);
    }

    [Fact]
    public async Task ReplaceSelection_rejects_when_no_selection()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        var editor = new CapturingEditor { ActiveFilePath = path, ActiveContent = "alpha\n" };
        var sut = new ReplaceSelectionTool(editor, workspace);

        var result = await sut.ExecuteAsync(Json("""{"new_text":"x"}"""), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ReplaceSelection_rejects_when_no_active_file()
    {
        using var workspace = TestWorkspace.Create();
        var editor = new CapturingEditor { SelectedText = "needle" };
        var sut = new ReplaceSelectionTool(editor, workspace);

        var result = await sut.ExecuteAsync(Json("""{"new_text":"x"}"""), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ReplaceSelection_rejects_non_unique_selection()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "dup\ndup\n");
        var editor = new CapturingEditor
        {
            ActiveFilePath = path,
            ActiveContent = "dup\ndup\n",
            SelectedText = "dup",
        };
        var sut = new ReplaceSelectionTool(editor, workspace);

        var result = await sut.ExecuteAsync(Json("""{"new_text":"X"}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("dup\ndup\n", await File.ReadAllTextAsync(path));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class CapturingEditor : IEditorService
    {
        public string? ActiveFilePath { get; set; }
        public string ActiveContent { get; set; } = string.Empty;
        public string SelectedText { get; set; } = string.Empty;
        public string? LastDiffPath { get; private set; }

        public Task OpenFileAsync(string path) => Task.CompletedTask;
        public Task<string> GetActiveContentAsync() => Task.FromResult(ActiveContent);
        public Task<string> GetSelectedTextAsync() => Task.FromResult(SelectedText);

        public Task<string> ShowDiffAsync(string path, string proposedContent)
        {
            LastDiffPath = path;
            return Task.FromResult(string.Empty);
        }

        public async Task<bool> ApplyEditAsync(string path, string newContent)
        {
            await File.WriteAllTextAsync(path, newContent);
            return true;
        }

        public Task OpenDocumentAsync(EditorDocument document) => Task.CompletedTask;
    }

    private sealed class TestWorkspace : IWorkspaceService, IDisposable
    {
        private readonly string _rootPath;

        private TestWorkspace(string rootPath) => _rootPath = rootPath;

        public string? RootPath => _rootPath;
        public string? SelectedPath { get; set; }

        public static TestWorkspace Create()
        {
            var root = Directory.CreateDirectory(Path.Combine(
                Path.GetTempPath(), $"loomo-editor-tools-{Guid.NewGuid():N}"));
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

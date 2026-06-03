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
    public async Task ReplaceRange_replaces_inclusive_line_range()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\nfour\n");
        var sut = new ReplaceRangeTool(new CapturingEditor(), workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"path":"App.cs","start_line":2,"end_line":3,"replacement":"TWO\nTHREE"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("one\nTWO\nTHREE\nfour\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReplaceRange_describes_diff_for_approval()
    {
        using var workspace = TestWorkspace.Create();
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath!, "App.cs"), "one\ntwo\nthree\nfour\n");
        var sut = new ReplaceRangeTool(new CapturingEditor(), workspace);

        var summary = sut.DescribeInvocation(
            Json("""{"path":"App.cs","start_line":2,"end_line":3,"replacement":"TWO\nTHREE"}"""));

        Assert.Contains("行範囲置換 2-3", summary);
        Assert.Contains("-two", summary);
        Assert.Contains("-three", summary);
        Assert.Contains("+TWO", summary);
        Assert.Contains("+THREE", summary);
    }

    [Fact]
    public async Task ReplaceRange_rejects_out_of_range_line()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.RootPath!, "App.cs");
        await File.WriteAllTextAsync(path, "one\n");
        var sut = new ReplaceRangeTool(new CapturingEditor(), workspace);

        var result = await sut.ExecuteAsync(
            Json("""{"path":"App.cs","start_line":3,"end_line":3,"replacement":"three"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("one\n", await File.ReadAllTextAsync(path));
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

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class CapturingEditor : IEditorService
    {
        public string? ActiveFilePath => null;
        public string SelectedText { get; set; } = string.Empty;
        public string? LastDiffPath { get; private set; }

        public Task OpenFileAsync(string path) => Task.CompletedTask;
        public Task<string> GetActiveContentAsync() => Task.FromResult(string.Empty);
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

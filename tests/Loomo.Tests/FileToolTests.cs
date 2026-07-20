using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Tools.Implementations;

namespace sk0ya.Loomo.Tests;

/// <summary>write_file / edit_file（構造化ファイルツール）の引数正規化・実行・エラー時の挙動。</summary>
public class FileToolTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeWorkspaceService _workspace = new();
    private readonly FakeEditorService _editor = new();

    public FileToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loomo-filetool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path2(string name) => Path.Combine(_dir, name);

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private WriteFileTool Write() => new(_workspace, _editor);
    private EditFileTool Edit() => new(_workspace, _editor);

    // --- write_file ---

    [Theory]
    [InlineData("path", "content")]
    [InlineData("file", "text")]
    [InlineData("filename", "body")]
    public void WriteFile_normalizes_alias_keys(string pathKey, string contentKey)
    {
        var args = Args($"{{\"{pathKey}\":\"a.txt\",\"{contentKey}\":\"hello\"}}");
        var normalized = Write().NormalizeArguments(args);
        Assert.Equal("a.txt", normalized.GetProperty("path").GetString());
        Assert.Equal("hello", normalized.GetProperty("content").GetString());
    }

    [Fact]
    public async Task WriteFile_creates_file_with_content()
    {
        var path = Path2("new.txt");
        var result = await Write().ExecuteAsync(Args($"{{\"path\":\"{Json(path)}\",\"content\":\"line1\\nline2\"}}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("line1\nline2", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteFile_overwrites_existing_and_creates_missing_directories()
    {
        var path = Path2(Path.Combine("sub", "deep.txt"));
        await Write().ExecuteAsync(Args($"{{\"path\":\"{Json(path)}\",\"content\":\"v1\"}}"), CancellationToken.None);
        await Write().ExecuteAsync(Args($"{{\"path\":\"{Json(path)}\",\"content\":\"v2\"}}"), CancellationToken.None);

        Assert.Equal("v2", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteFile_empty_path_returns_recoverable_error()
    {
        var result = await Write().ExecuteAsync(Args("{\"path\":\"\",\"content\":\"x\"}"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("path", result.Content);
    }

    [Fact]
    public async Task WriteFile_surfaces_workspace_confinement_error()
    {
        var tool = new WriteFileTool(new DenyingWorkspace(), _editor);
        var result = await tool.ExecuteAsync(Args("{\"path\":\"../escape.txt\",\"content\":\"x\"}"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("ルート外", result.Content);
    }

    // --- edit_file ---

    [Fact]
    public async Task EditFile_replaces_unique_occurrence()
    {
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "alpha BETA gamma");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"BETA\",\"new_string\":\"delta\"}}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("alpha delta gamma", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_missing_match_returns_error_and_leaves_file_untouched()
    {
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "abc");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"xyz\",\"new_string\":\"q\"}}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("見つかりません", result.Content);
        Assert.Equal("abc", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_case_mismatch_returns_actual_text_hint()
    {
        // multi-file-bump の実故障：実体 "Version: 1.2.3" に小文字の "version: 1.2.3" を指定して
        // not-found → 虚偽の完了報告。エラーに実テキストを添えて、再試行を機械的な複写にする。
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "# Sample\nVersion: 1.2.3\n");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"version: 1.2.3\",\"new_string\":\"version: 2.0.0\"}}"),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Version: 1.2.3", result.Content);
        Assert.Contains("そのまま old_string に指定", result.Content);
        Assert.Equal("# Sample\nVersion: 1.2.3\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_newline_mismatch_returns_actual_text_hint()
    {
        // 実ファイルは CRLF、old_string は LF（モデルは \n で書きがち）→ 実際の CRLF テキストを提示。
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\ngamma");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"alpha\\nbeta\",\"new_string\":\"x\"}}"),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("alpha\r\nbeta", result.Content);
        Assert.Equal("alpha\r\nbeta\r\ngamma", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_no_near_match_keeps_generic_not_found_error()
    {
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "abc");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"xyz\",\"new_string\":\"q\"}}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.DoesNotContain("異なる箇所", result.Content);
        Assert.Contains("コピーして指定", result.Content);
    }

    [Fact]
    public async Task EditFile_ambiguous_match_returns_count_error()
    {
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "x x x");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"x\",\"new_string\":\"y\"}}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("3 箇所", result.Content);
        Assert.Equal("x x x", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_ambiguous_match_lists_containing_lines()
    {
        // replace-all 型の実故障：曖昧エラー後もモデルは同じ短い old_string を再送する。
        // 一致行そのものを列挙して「行をコピーして1箇所ずつ」を機械的にできるようにする。
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "TODO: write docs\nTODO: add tests\nDone: setup project\n");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"TODO\",\"new_string\":\"DONE\"}}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("TODO: write docs", result.Content);
        Assert.Contains("TODO: add tests", result.Content);
        Assert.Contains("行全体を old_string にコピー", result.Content);
        Assert.Contains("write_file", result.Content);
    }

    [Fact]
    public async Task EditFile_double_escaped_old_string_returns_unescaped_hint()
    {
        // insert-json-key 型の実故障：JSON の二重エスケープ（\" が文字として残る）old_string で
        // not-found を繰り返す。剥がした形で一致する箇所があれば実テキストを提示する。
        var path = Path2("edit.json");
        await File.WriteAllTextAsync(path, "{\n  \"name\": \"loomo\"\n}\n");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"\\\\\\\"name\\\\\\\": \\\\\\\"loomo\\\\\\\"\",\"new_string\":\"x\"}}"),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("\"name\": \"loomo\"", result.Content);
        Assert.Contains("そのまま old_string に指定", result.Content);
    }

    [Fact]
    public async Task EditFile_empty_new_string_deletes_match()
    {
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "keepDROPkeep");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"DROP\"}}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("keepkeep", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_empty_old_string_appends_to_end()
    {
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "line1\n");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"\",\"new_string\":\"line2\"}}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("line1\nline2", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_empty_old_string_appends_newline_when_file_lacks_trailing_newline()
    {
        var path = Path2("edit.txt");
        await File.WriteAllTextAsync(path, "line1");

        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(path)}\",\"old_string\":\"\",\"new_string\":\"line2\"}}"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("line1\nline2", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_missing_file_returns_error()
    {
        var result = await Edit().ExecuteAsync(
            Args($"{{\"path\":\"{Json(Path2("nope.txt"))}\",\"old_string\":\"a\",\"new_string\":\"b\"}}"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("存在しません", result.Content);
    }

    /// <summary>JSON 文字列リテラルへ安全に埋め込めるようパス区切りをエスケープする。</summary>
    private static string Json(string raw) => raw.Replace("\\", "\\\\");

    /// <summary>常にルート外として弾くワークスペース（確定処理の確認用）。</summary>
    private sealed class DenyingWorkspace : IWorkspaceService
    {
        public System.Collections.Generic.IReadOnlyList<string> Folders { get; } = new[] { "C:\\root" };
        public string? RootPath => "C:\\root";
        public string? SelectedPath { get; set; }
        public void OpenFolder(string rootPath) { }
        public void AddFolder(string path) { }
        public void RemoveFolder(string path) { }
        public Task<System.Collections.Generic.IReadOnlyList<sk0ya.Loomo.Core.Models.FileNode>> ListAsync(string path, CancellationToken ct = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<sk0ya.Loomo.Core.Models.FileNode>>(Array.Empty<sk0ya.Loomo.Core.Models.FileNode>());
        public Task<string> ReadFileAsync(string path, CancellationToken ct = default) => Task.FromResult("");
        public string ResolvePath(string path)
            => throw new UnauthorizedAccessException($"ワークスペースルート外へのアクセスは許可されていません: {path}");
#pragma warning disable CS0067
        public event EventHandler<string?>? SelectionChanged;
        public event EventHandler<string?>? RootChanged;
        public event EventHandler? FoldersChanged;
#pragma warning restore CS0067
    }
}

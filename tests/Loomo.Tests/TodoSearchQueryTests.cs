using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services.Search;

namespace sk0ya.Loomo.Tests;

public sealed class TodoSearchQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-todo-query-" + Guid.NewGuid().ToString("N"));
    public TodoSearchQueryTests() => Directory.CreateDirectory(_root);
    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_root, name), text);
    private Task<TodoSearchResult> Search(string? code = null, string documents = "", int limit = 2000)
    {
        var workspace = new FakeWorkspaceService(_root);
        return new TodoSearchQuery(new WorkspaceSearchService(workspace), workspace).SearchAsync(
            new(code ?? TodoTreeSettings.DefaultCodeExtensions, documents, null, limit), CancellationToken.None);
    }

    [Fact]
    public async Task コードはコメント内のみを文書全体の文脈で判定する()
    {
        Write("sample.cs", "var s = \"TODO string\"; // FIXME: 修正\n/*\n TODO: 複数行\n*/\nvar raw = \"\"\"\n// TODO raw\n\"\"\";\nvar verbatim = @\"\n// NOTE string\n\";\n/* HACK: 手直し */ var value = \"NOTE string\";\n// TODO: one FIXME: two");
        var result = await Search();
        Assert.Equal(["FIXME", "TODO", "HACK", "TODO", "FIXME"], result.Entries.Select(e => e.Tag));
        Assert.Equal([1, 3, 11, 12, 12], result.Entries.Select(e => e.Hit.Line));
        Assert.Equal("手直し", result.Entries[2].Body);
        Assert.All(result.Entries, e => Assert.Equal(e.Tag, e.Hit.LineText.Substring(e.Hit.Column - 1, e.Tag.Length)));
    }

    [Theory]
    [InlineData("sample.js", "const text = 'TODO'; /* FIXME: fix */\n// NOTE: note", "FIXME,NOTE")]
    [InlineData("sample.py", "text = 'TODO' # FIXME: fix\ntext = '''\n# TODO string\n'''\n# NOTE: note", "FIXME,NOTE")]
    [InlineData("sample.xaml", "<TextBlock Text=\"TODO\" /><!-- FIXME: fix -->\n<!--\nNOTE: note\n-->", "FIXME,NOTE")]
    [InlineData("sample.ps1", "$text = 'TODO' # FIXME: fix\n<#\nNOTE: note\n#>", "FIXME,NOTE")]
    public async Task 言語別の文字列とコメントを区別する(string name, string content, string tags)
    {
        Write(name, content);
        var result = await Search();
        Assert.Equal(tags.Split(','), result.Entries.Select(e => e.Tag));
    }

    [Fact]
    public async Task 拡張子を限定し文書の本文検索は明示指定時だけ行う()
    {
        Write("a.CS", "// TODO: code");
        Write("b.js", "// TODO: javascript");
        Write("c.md", "TODO: document");
        Write("d.json", "{\"value\":\"TODO\"}");
        var defaults = await Search();
        Assert.Equal(2, defaults.Entries.Count);
        var restricted = await Search("*.CS", ".md");
        Assert.Equal(["a.CS", "c.md"], restricted.Entries.Select(e => e.Hit.RelativePath).Order());
        Assert.Empty((await Search("")).Entries);
        Assert.Single((await Search(".cs", ".cs")).Entries);
    }

    [Fact]
    public async Task 拡張子の指定でgitignoreの除外を上書きしない()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Write(".gitignore", "ignored.cs\n");
        Write("ignored.cs", "// TODO: ignored");
        Write("included.cs", "// TODO: included");
        var workspace = new FakeWorkspaceService(_root);
        var backend = new WorkspaceSearchService(workspace);
        var baseline = await backend.GrepAsync("TODO", new(), CancellationToken.None);
        var result = await Search(".cs");
        Assert.Equal(baseline.Select(h => h.FullPath).Order(), result.Entries.Select(e => e.Hit.FullPath).Order());
    }

    [Fact]
    public async Task コメント以外の候補は表示上限を消費しない()
    {
        Write("sample.cs", "var text = \"TODO FIXME HACK\";\n// TODO: first\n// FIXME: second\n// NOTE: third");
        var result = await Search(limit: 2);
        Assert.True(result.Truncated);
        Assert.Equal(["TODO", "FIXME"], result.Entries.Select(e => e.Tag));
    }

    [Fact]
    public void 不正な拡張子や未対応のコメント判定を拒否し設定を維持する()
    {
        TodoSearchQuery.Validate(TodoTreeSettings.DefaultCodeExtensions, "");
        Assert.Throws<ArgumentException>(() => TodoSearchQuery.Validate(".unknown", ""));
        Assert.Throws<ArgumentException>(() => TodoSearchQuery.Validate(".cs", "**/*"));
        var workspace = new FakeWorkspaceService(_root);
        var settings = new LoomoSettings();
        using var vm = new TodoTreeViewModel(new TodoSearchQuery(new WorkspaceSearchService(workspace), workspace), workspace, settings);
        Assert.False(vm.ApplySearchOptions(".unknown", ".md", "**/generated/**"));
        Assert.Equal(TodoTreeSettings.DefaultCodeExtensions, settings.TodoTree.CodeExtensions);
        Assert.NotEmpty(vm.SearchOptionsError);
        Assert.True(vm.ApplySearchOptions("cs, *.JS", "md;txt", "**/generated/**"));
        Assert.Equal(".cs .js", settings.TodoTree.CodeExtensions);
        Assert.Equal(".md .txt", settings.TodoTree.DocumentExtensions);
        Assert.Empty(vm.SearchOptionsError);
    }

    public void Dispose() => Directory.Delete(_root, true);
}

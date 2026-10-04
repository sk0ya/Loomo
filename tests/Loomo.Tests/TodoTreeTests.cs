using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Tests;

public sealed class TodoTreeTests
{
    [Fact]
    public void 同じ行の複数タグと日本語の後ろの列を重複せず返す()
    {
        var hit = new ContentSearchHit("a.cs", "a.cs", 3, 1, "// 日本語 TODO: 修正 FIXME: 確認");
        var entries = TodoTreeViewModel.Parse([hit, hit]);
        Assert.Equal(["TODO", "FIXME"], entries.Select(e => e.Tag));
        Assert.Equal([8, 17], entries.Select(e => e.Hit.Column));
    }
    [Fact]
    public void 単語の一部や小文字はタグとして拾わない()
    {
        var entries = TodoTreeViewModel.Parse([new("a", "a", 1, 1, "TODOS NOTED todo FIXME_flag HACK")]);
        Assert.Equal("HACK", Assert.Single(entries).Tag);
    }
    [Fact]
    public void 既存配置への追加と移動後の保存を維持する()
    {
        var settings = new LoomoSettings();
        settings.ActivityBar.Primary = ["explorer", "git", "solution", "pegboard"];
        settings.ActivityBar.Secondary = ["tabs"];
        var vm = new ActivityBarViewModel(settings);
        Assert.Equal(["tabs", "todo"], vm.SecondaryItems.Select(i => i.Id));
        vm.Move(vm.ItemFor(SidebarPanel.Todo)!, ActivityBarSlot.Primary, 1);
        Assert.Equal("todo", new ActivityBarViewModel(settings).PrimaryItems[1].Id);
    }
    [Fact]
    public async Task 絞り込みとタグ別表示と行への移動を連携する()
    {
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>([
            new("a.cs", "a.cs", 2, 1, "// TODO: alpha"), new("b.cs", "b.cs", 4, 1, "// FIXME: beta")]) };
        using var vm = new TodoTreeViewModel(search, new FakeWorkspaceService("root"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Groups.Count);
        vm.GroupByTag = true;
        Assert.Equal(["FIXME", "TODO"], vm.Groups.Select(g => g.Name));
        vm.Filter = "alpha";
        var entry = Assert.Single(Assert.Single(vm.Groups).Entries);
        ContentSearchHit? opened = null;
        vm.OpenRequested += (_, h) => opened = h;
        vm.OpenCommand.Execute(entry);
        Assert.Equal("a.cs", opened!.FullPath);
        Assert.Equal(2, opened.Line);
        Assert.Equal(4, opened.Column);
    }
    [Fact]
    public async Task キャンセルを無視して返った古い検索結果を採用しない()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<ContentSearchHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new StubSearch { Handler = _ => { started.SetResult(); return pending.Task; } };
        using var vm = new TodoTreeViewModel(search, new FakeWorkspaceService("root"));
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        await started.Task;
        vm.Invalidate();
        pending.SetResult([new("old.cs", "old.cs", 1, 1, "TODO")]);
        await refresh;
        Assert.Empty(vm.Groups);
        Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task 除外条件と検索上限を渡し切り詰めを表示する()
    {
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>(
            Enumerable.Range(1, TodoTreeViewModel.ResultLimit + 1).Select(i => new ContentSearchHit("a", "a", i, 1, "TODO")).ToList()) };
        using var vm = new TodoTreeViewModel(search, new FakeWorkspaceService("root"));
        vm.ExcludeGlob = "**/generated/**";
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("**/generated/**", search.Options!.ExcludeGlob);
        Assert.True(search.Options.CaseSensitive && search.Options.UseRegex);
        Assert.Equal(TodoTreeViewModel.ResultLimit, Assert.Single(vm.Groups).Entries.Count);
        Assert.Contains("先頭", vm.Status);
    }
    [Theory]
    [InlineData("// TODO: 入力を確認する", "入力を確認する")]
    [InlineData("<!-- TODO: 文言を修正 -->", "文言を修正")]
    [InlineData("/* TODO: 手直し */", "手直し")]
    [InlineData("// TODO: first FIXME: second", "first")]
    public void 表示の本文だけを整えて原文と列を維持する(string line, string expected)
    {
        var entry = TodoTreeViewModel.Parse([new("a", "a", 1, 1, line)])[0];
        Assert.Equal(expected, entry.Body);
        Assert.Equal(line, entry.Hit.LineText);
        Assert.Equal(line.IndexOf("TODO", StringComparison.Ordinal) + 1, entry.Hit.Column);
    }
    [Fact]
    public async Task 更新しても選択と折りたたみを維持しフィルターを戻すと展開状態も戻る()
    {
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>([
            new("a.cs", "a.cs", 1, 1, "TODO: one"), new("b.cs", "b.cs", 1, 1, "NOTE: two")]) };
        using var vm = new TodoTreeViewModel(search, new FakeWorkspaceService("root"));
        await vm.RefreshCommand.ExecuteAsync(null);
        var group = vm.Groups[0];
        var entry = group.Entries[0];
        group.IsExpanded = false;
        vm.SetSelection(entry);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Same(group, vm.Groups[0]);
        Assert.Same(entry, vm.SelectedEntry);
        Assert.False(group.IsExpanded);
        vm.Filter = "b.cs";
        Assert.Null(vm.SelectedEntry);
        vm.Filter = "";
        Assert.False(vm.Groups[0].IsExpanded);
    }
    [Fact]
    public async Task タグを複数選んで絞り込み前後移動は表示中だけを巡回する()
    {
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>([
            new("a", "a", 1, 1, "TODO: one"), new("a", "a", 2, 1, "FIXME: two"), new("b", "b", 1, 1, "NOTE: three")]) };
        using var vm = new TodoTreeViewModel(search, new FakeWorkspaceService("root"));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.TagFilters.Single(t => t.Tag == "FIXME").IsEnabled = false;
        Assert.Equal(2, vm.VisibleCount);
        Assert.Equal(1, vm.TagFilters.Single(t => t.Tag == "FIXME").Count);
        var previews = new List<ContentSearchHit>();
        var opened = 0;
        vm.PreviewRequested += (_, hit) => previews.Add(hit);
        vm.OpenRequested += (_, _) => opened++;
        vm.CollapseAllCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal("TODO", vm.SelectedEntry!.Tag);
        Assert.True(vm.Groups[0].IsExpanded);
        vm.NextCommand.Execute(null);
        Assert.Equal("NOTE", vm.SelectedEntry!.Tag);
        vm.NextCommand.Execute(null);
        Assert.Equal("TODO", vm.SelectedEntry!.Tag);
        vm.PreviousCommand.Execute(null);
        Assert.Equal("NOTE", vm.SelectedEntry!.Tag);
        Assert.Equal(4, previews.Count);
        Assert.Equal(0, opened);
        vm.OpenCommand.Execute(vm.SelectedEntry);
        Assert.Equal(1, opened);
        vm.Filter = "no such text";
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.True(vm.IsEmpty);
        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, vm.VisibleCount);
    }
    [Fact]
    public void 表示条件を保存して次のインスタンスへ復元する()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"todo-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LoomoSettings();
            var store = new sk0ya.Loomo.Services.Settings.SettingsStore(path);
            using (var vm = new TodoTreeViewModel(new StubSearch(), new FakeWorkspaceService(), settings, store))
            {
                vm.GroupByTag = true;
                vm.ExcludeGlob = "**/generated/**";
                vm.TagFilters.Single(t => t.Tag == "NOTE").IsEnabled = false;
            }
            var loaded = new LoomoSettings();
            store.Load(loaded);
            using var restored = new TodoTreeViewModel(new StubSearch(), new FakeWorkspaceService(), loaded);
            Assert.True(restored.GroupByTag);
            Assert.Equal("**/generated/**", restored.ExcludeGlob);
            Assert.False(restored.TagFilters.Single(t => t.Tag == "NOTE").IsEnabled);
            Assert.Equal("", restored.Filter);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public async Task フォルダー階層と直下ファイルを分け配下の件数を集計する()
    {
        var root = @"C:\TodoWorkspace";
        var hits = new ContentSearchHit[] {
            new(root + @"\src\api\Server.cs", "src/api/Server.cs", 1, 1, "TODO: api"),
            new(root + @"\src\ui\Window.cs", "src/ui/Window.cs", 2, 1, "FIXME: ui"),
            new(root + @"\src\ui\Window.cs", "src/ui/Window.cs", 3, 1, "NOTE: ui"),
            new(root + @"\README.md", "README.md", 1, 1, "TODO: root"),
        };
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>(hits) };
        using var vm = new TodoTreeViewModel(search, new FakeWorkspaceService(root));
        await vm.RefreshCommand.ExecuteAsync(null);
        var src = Assert.IsType<TodoFolder>(vm.TreeItems[0]);
        Assert.Equal("src", src.Name);
        Assert.Equal(3, src.Count);
        Assert.Equal(new[] { "api", "ui" }, src.Children.Cast<TodoFolder>().Select(f => f.Name));
        Assert.Equal(2, ((TodoFolder)src.Children[1]).Count);
        Assert.Equal("README.md", Assert.IsType<TodoGroup>(vm.TreeItems[1]).Title);
        vm.CollapseAllCommand.Execute(null);
        Assert.False(src.IsExpanded);
        vm.NextCommand.Execute(null);
        Assert.Equal("src/api/Server.cs", vm.SelectedEntry!.Hit.RelativePath);
        Assert.True(src.IsExpanded);
        Assert.True(((TodoFolder)src.Children[0]).IsExpanded);
        Assert.True(((TodoGroup)((TodoFolder)src.Children[0]).Children[0]).IsExpanded);
        vm.NextCommand.Execute(null);
        Assert.Equal("src/ui/Window.cs", vm.SelectedEntry!.Hit.RelativePath);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal("README.md", vm.SelectedEntry!.Hit.RelativePath);
    }

    [Fact]
    public async Task 同名の複数ルートを絶対パスで区別して結果を混ぜない()
    {
        var first = @"C:\one\project";
        var second = @"C:\two\project";
        var workspace = new FakeWorkspaceService(first);
        workspace.AddFolder(second);
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>([
            new(first + @"\src\App.cs", "project/src/App.cs", 1, 1, "TODO: one"),
            new(second + @"\src\App.cs", "project/src/App.cs", 1, 1, "TODO: two")]) };
        using var vm = new TodoTreeViewModel(search, workspace);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.TreeItems.Count);
        Assert.Equal(2, vm.Groups.Count);
        var roots = vm.TreeItems.Cast<TodoFolder>().ToList();
        Assert.Equal(new[] { first, second }, roots.Select(r => r.FullPath));
        Assert.NotEqual(roots[0].Name, roots[1].Name);
        Assert.Equal("one", Assert.Single(roots[0].Entries).Body);
        Assert.Equal("two", Assert.Single(roots[1].Entries).Body);
    }

    [Fact]
    public async Task 自動更新と分類切替と絞り込みでフォルダーの開閉を維持する()
    {
        var root = @"C:\TodoWorkspace";
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>([
            new(root + @"\src\App.cs", "src/App.cs", 1, 1, "TODO: one")]) };
        using var vm = new TodoTreeViewModel(search, new FakeWorkspaceService(root));
        await vm.RefreshCommand.ExecuteAsync(null);
        var folder = Assert.IsType<TodoFolder>(Assert.Single(vm.TreeItems));
        folder.IsExpanded = false;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Same(folder, vm.TreeItems[0]);
        Assert.False(folder.IsExpanded);
        vm.Filter = "missing";
        Assert.Empty(vm.TreeItems);
        vm.Filter = "";
        Assert.False(Assert.IsType<TodoFolder>(vm.TreeItems[0]).IsExpanded);
        vm.GroupByTag = true;
        Assert.IsType<TodoGroup>(vm.TreeItems[0]);
        vm.GroupByTag = false;
        Assert.False(Assert.IsType<TodoFolder>(vm.TreeItems[0]).IsExpanded);
        vm.ExpandAllCommand.Execute(null);
        Assert.True(Assert.IsType<TodoFolder>(vm.TreeItems[0]).IsExpanded);
    }

    [Fact]
    public async Task ドライブ直下をルートにしても表示名が空にならない()
    {
        var workspace = new FakeWorkspaceService(@"C:\");
        workspace.AddFolder(@"D:\");
        var search = new StubSearch { Handler = _ => Task.FromResult<IReadOnlyList<ContentSearchHit>>([
            new(@"C:\src\App.cs", "src/App.cs", 1, 1, "TODO: one"),
            new(@"D:\src\App.cs", "src/App.cs", 1, 1, "TODO: two")]) };
        using var vm = new TodoTreeViewModel(search, workspace);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(new[] { @"C:\", @"D:\" }, vm.TreeItems.Cast<TodoFolder>().Select(f => f.Name));
    }

    private sealed class StubSearch : IWorkspaceSearchService
    {
        public Func<CancellationToken, Task<IReadOnlyList<ContentSearchHit>>> Handler { get; set; } = null!;
        public GrepOptions? Options { get; private set; }
        public Task<IReadOnlyList<ContentSearchHit>> GrepAsync(string query, GrepOptions options, CancellationToken ct, string? searchRoot = null)
        { Options = options; return Handler(ct); }
        public Task<IReadOnlyList<FileSearchHit>> FindFilesAsync(string q, int m, CancellationToken ct, string? root = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdvancedFileSearchHit>> SearchFilesAsync(AdvancedSearchOptions o, CancellationToken ct, string? root = null) => throw new NotSupportedException();
    }
}

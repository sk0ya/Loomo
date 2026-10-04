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

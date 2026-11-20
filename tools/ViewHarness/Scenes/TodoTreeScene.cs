using System.IO;
using System.Windows;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace ViewHarness.Scenes;

/// <summary>TODO ペイン（サイドバー）。実物の <see cref="TodoTreeView"/> と <see cref="TodoTreeViewModel"/> に
/// 固定の検索結果を渡し、ファイル 1 つだけのフォルダーをファイルの行へ畳んだ「src / ui / Window.cs」の行・
/// 長いパス・タグ別・空の状態を撮る（2026-11-20）。</summary>
public sealed class TodoTreeScene : IScene
{
    private const string Root = @"C:\Work\Sample";
    private TodoTreeViewModel? _vm;

    public string Name => "todo-tree";
    public string Description => "TODO ペイン（フォルダー＋ファイル 1 つの行の畳み・タグ別・空の状態）";
    public Size Size => new(320, 520);

    public FrameworkElement Build()
    {
        _vm = new TodoTreeViewModel(new FixedSearch(), new FixedWorkspace());
        _ = _vm.RefreshCommand.ExecuteAsync(null);
        return new TodoTreeView { DataContext = _vm };
    }

    public IEnumerable<SceneStep> Steps(FrameworkElement root) =>
    [
        new SceneStep("by-tag", () => _vm!.GroupByTag = true),
        new SceneStep("empty-filter", () => { _vm!.GroupByTag = false; _vm.Filter = "存在しない文言"; }),
    ];

    private static ContentSearchHit Hit(string relative, int line, string text)
        => new(Path.Combine(Root, relative.Replace('/', '\\')), relative, line, text.IndexOfAny(['T', 'F', 'H', 'N']) + 1, text);

    private sealed class FixedSearch : ITodoSearchService
    {
        public Task<TodoSearchResult> SearchAsync(TodoSearchOptions options, CancellationToken ct)
            => Task.FromResult(new TodoSearchResult(TodoTreeViewModel.Parse([
                Hit("README.md", 3, "- TODO: 使い方の節を書く"),
                Hit("docs/設計/README.md", 12, "- [ ] FIXME: 章番号の対応表を更新する"),
                Hit("src/Loomo.App/Views/Todo/TodoTreeView.xaml.cs", 40, "// TODO: 右クリックメニューの項目をキーボードでも出せるようにする（長い本文の省略を確かめる）"),
                Hit("src/Loomo.App/Views/Todo/TodoTreeView.xaml.cs", 88, "// NOTE: プレビューは 150ms にまとめる"),
                Hit("src/Loomo.App/Services/Search/TodoSearchQuery.cs", 20, "// HACK(koya): 字句器の割れ方に合わせてコメントをつなぐ"),
                Hit("src/Loomo.App/Services/Search/TodoTreeLayout.cs", 5, "// TODO: 並び順を設定できるようにする"),
            ])));
    }

    private sealed class FixedWorkspace : IWorkspaceService
    {
        public IReadOnlyList<string> Folders { get; } = [Root];
        public string? PrimaryFolder => Root;
        public string? SelectedPath { get; set; }
        public void OpenFolder(string rootPath) { }
        public void AddFolder(string path) { }
        public void RemoveFolder(string path) { }
        public Task<IReadOnlyList<FileNode>> ListAsync(string path, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FileNode>>([]);
        public Task<string> ReadFileAsync(string path, CancellationToken ct = default) => Task.FromResult("");
        public string ResolvePath(string path) => Path.Combine(Root, path);
        public event EventHandler<string?>? SelectionChanged { add { } remove { } }
        public event EventHandler<string?>? RootChanged { add { } remove { } }
        public event EventHandler? FoldersChanged { add { } remove { } }
    }
}

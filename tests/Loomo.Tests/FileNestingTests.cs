using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.Tests;

/// <summary>関連ファイルのまとめ表示（VS Code の Explorer File Nesting 相当）の判定（純関数）。</summary>
public sealed class FileNestingTests
{
    private static IReadOnlyDictionary<string, string> Resolve(params string[] names)
        => FileNesting.Create(FileNesting.DefaultPatterns).Resolve(names);

    [Fact]
    public void Xamlのコードビハインドはxamlの下にまとまる()
    {
        var map = Resolve("MainWindow.xaml", "MainWindow.xaml.cs", "App.cs");

        Assert.Equal("MainWindow.xaml", map["MainWindow.xaml.cs"]);
        Assert.False(map.ContainsKey("App.cs"));
        Assert.Single(map);
    }

    [Fact]
    public void Designerとresxの組はresxが優先して親になる()
    {
        var map = Resolve("Resources.resx", "Resources.Designer.cs", "Form1.cs", "Form1.Designer.cs");

        Assert.Equal("Resources.resx", map["Resources.Designer.cs"]);
        Assert.Equal("Form1.cs", map["Form1.Designer.cs"]);
    }

    [Fact]
    public void Packagejsonは固定名のロックファイルを抱える()
    {
        var map = Resolve("package.json", "package-lock.json", "yarn.lock", ".npmrc", "index.js");

        Assert.Equal("package.json", map["package-lock.json"]);
        Assert.Equal("package.json", map["yarn.lock"]);
        Assert.Equal("package.json", map[".npmrc"]);
        Assert.False(map.ContainsKey("index.js"));
    }

    [Fact]
    public void 子のパターンのワイルドカードが効く()
    {
        var map = Resolve("tsconfig.json", "tsconfig.app.json", "tsconfig.spec.json", "tsconfigx.json");

        Assert.Equal("tsconfig.json", map["tsconfig.app.json"]);
        Assert.Equal("tsconfig.json", map["tsconfig.spec.json"]);
        Assert.False(map.ContainsKey("tsconfigx.json"));
    }

    [Fact]
    public void 孫は一番上の親へ寄せて1段にする()
    {
        // a.ts → a.js（*.ts）、a.js → a.js.map（*.js）。a.js.map は a.ts の直下へ。
        var map = Resolve("a.ts", "a.js", "a.js.map");

        Assert.Equal("a.ts", map["a.js"]);
        Assert.Equal("a.ts", map["a.js.map"]);
        Assert.False(map.ContainsKey("a.ts"));
    }

    [Fact]
    public void 親が無ければまとめない()
    {
        var map = Resolve("MainWindow.xaml.cs", "package-lock.json");

        Assert.Empty(map);
    }

    [Fact]
    public void 大文字小文字を区別しない()
    {
        var map = Resolve("Foo.XAML", "foo.xaml.CS");

        Assert.Equal("Foo.XAML", map["foo.xaml.CS"]);
    }

    [Fact]
    public void 先に書いたルールが勝つ()
    {
        var nesting = FileNesting.Create(new KeyValuePair<string, string>[]
        {
            new("b.txt", "c.txt"),
            new("a.txt", "c.txt"),
        });

        Assert.Equal("b.txt", nesting.Resolve(["a.txt", "b.txt", "c.txt"])["c.txt"]);
    }

    [Fact]
    public void 互いに子を指し合っても輪にならない()
    {
        var nesting = FileNesting.Create(new KeyValuePair<string, string>[]
        {
            new("a.x", "a.y"),
            new("a.y", "a.x"),
        });

        var map = nesting.Resolve(["a.x", "a.y"]);

        // a.y は a.x の子になり、a.x は（既に親なので）a.y の子にはならない——どちらかが表に残る。
        Assert.True(map.Count <= 1);
        Assert.False(map.ContainsKey("a.x") && map.ContainsKey("a.y"));
    }

    [Fact]
    public void VSCodeのドル波括弧表記とbasenameを受け付ける()
    {
        var nesting = FileNesting.Create(new KeyValuePair<string, string>[]
        {
            new("*.md", "${capture}.pdf, $(basename).html"),
        });

        var map = nesting.Resolve(["doc.md", "doc.pdf", "doc.html"]);

        Assert.Equal("doc.md", map["doc.pdf"]);
        Assert.Equal("doc.md", map["doc.html"]);
    }

    [Fact]
    public void 親の名前に正規表現の記号があっても字義どおり当てる()
    {
        var map = Resolve("a+b(1).xaml", "a+b(1).xaml.cs", "aab1.xaml.cs");

        Assert.Equal("a+b(1).xaml", map["a+b(1).xaml.cs"]);
        Assert.False(map.ContainsKey("aab1.xaml.cs"));
    }

    [Fact]
    public void 壊れたルールは読み捨てる()
    {
        var nesting = FileNesting.Create(new KeyValuePair<string, string>[]
        {
            new("", "x"),
            new("*.a*", "y"),
            new("sub/x", "y"),
            new("ok", " "),
        });

        Assert.True(nesting.IsEmpty);
    }

    [Fact]
    public void テキストの読み書きが往復する()
    {
        var text = FileNesting.FormatText(FileNesting.DefaultPatterns);
        var parsed = FileNesting.ParseText(text);

        Assert.Equal(FileNesting.DefaultPatterns, parsed);
    }

    [Fact]
    public void テキストの空行とコメントと等号の無い行は無視する()
    {
        var parsed = FileNesting.ParseText("# コメント\n\n*.xaml = $(capture).xaml.cs\r\nこわれた行\n*.xaml = $(capture).g.cs");

        var only = Assert.Single(parsed);
        Assert.Equal("*.xaml", only.Key);
        Assert.Equal("$(capture).g.cs", only.Value);   // 同じ親は後の行で上書き
    }

    [Fact]
    public void 子が変更ありなら無変更の親に配下変更の印を出す()
    {
        Assert.Equal(GitChangeKind.DirectoryChanged,
            FileNodeViewModel.AggregateNestedGitStatus(GitChangeKind.None, [GitChangeKind.None, GitChangeKind.Modified]));
        Assert.Equal(GitChangeKind.Added,
            FileNodeViewModel.AggregateNestedGitStatus(GitChangeKind.Added, [GitChangeKind.Modified]));
        Assert.Equal(GitChangeKind.None,
            FileNodeViewModel.AggregateNestedGitStatus(GitChangeKind.None, [GitChangeKind.Ignored, GitChangeKind.None]));
    }
}

/// <summary>フォルダーツリーへの組み込み：まとめ表示・差分更新での同一インスタンス維持・設定の切替と保存。</summary>
public sealed class FolderTreeFileNestingTests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;

    public FolderTreeFileNestingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"loomo-nesting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "MainWindow.xaml"), "");
        File.WriteAllText(Path.Combine(_root, "MainWindow.xaml.cs"), "");
        File.WriteAllText(Path.Combine(_root, "App.cs"), "");
        _settingsPath = Path.Combine(_root + "-settings", "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
        try { Directory.Delete(Path.GetDirectoryName(_settingsPath)!, recursive: true); } catch { /* 同上 */ }
    }

    private (FolderTreeViewModel Sut, LoomoSettings Settings) CreateSut()
    {
        var settings = new LoomoSettings();
        var workspace = new WorkspaceService(new SafetySettings());
        var sut = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery(),
            settings: settings, settingsStore: new SettingsStore(_settingsPath));
        return (sut, settings);
    }

    private string P(string name) => Path.Combine(_root, name);

    [Fact]
    public async Task 既定で関連ファイルが親の下に畳まれる()
    {
        var (sut, _) = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        Assert.Equal(["App.cs", "MainWindow.xaml"], sut.Nodes.Select(n => n.Name));
        var parent = sut.Nodes.Single(n => n.Name == "MainWindow.xaml");
        Assert.True(parent.HasNestedChildren);
        Assert.False(parent.IsExpanded);   // 既定は畳んだまま
        Assert.Equal(P("MainWindow.xaml.cs"), Assert.Single(parent.Children).FullPath);
    }

    [Fact]
    public async Task 更新しても親と子は同じインスタンスで開閉と選択が残る()
    {
        var (sut, _) = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var parent = sut.Nodes.Single(n => n.Name == "MainWindow.xaml");
        var child = parent.Children.Single();
        parent.IsExpanded = true;
        child.IsSelected = true;

        File.WriteAllText(P("Other.txt"), "");
        sut.RefreshCommand.Execute(null);
        await sut.WhenTreeLoadedAsync();

        var parentAfter = sut.Nodes.Single(n => n.Name == "MainWindow.xaml");
        Assert.Same(parent, parentAfter);
        Assert.Same(child, parentAfter.Children.Single());
        Assert.True(parentAfter.IsExpanded);
        Assert.True(child.IsSelected);
        Assert.Contains(sut.Nodes, n => n.Name == "Other.txt");
    }

    [Fact]
    public async Task 子が増えても減っても差分で反映する()
    {
        var (sut, _) = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();
        var parent = sut.Nodes.Single(n => n.Name == "MainWindow.xaml");

        File.Delete(P("MainWindow.xaml.cs"));
        sut.RefreshCommand.Execute(null);
        await sut.WhenTreeLoadedAsync();
        Assert.Same(parent, sut.Nodes.Single(n => n.Name == "MainWindow.xaml"));
        Assert.False(parent.HasNestedChildren);

        File.WriteAllText(P("MainWindow.xaml.cs"), "");
        sut.RefreshCommand.Execute(null);
        await sut.WhenTreeLoadedAsync();
        Assert.True(parent.HasNestedChildren);
        Assert.DoesNotContain(sut.Nodes, n => n.Name == "MainWindow.xaml.cs");
    }

    [Fact]
    public async Task 設定をOFFにすると平らに並び保存される()
    {
        var (sut, settings) = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        sut.FileNestingEnabled = false;
        await sut.WhenTreeLoadedAsync();

        Assert.Equal(["App.cs", "MainWindow.xaml", "MainWindow.xaml.cs"], sut.Nodes.Select(n => n.Name));
        Assert.All(sut.Nodes, n => Assert.False(n.HasNestedChildren));

        var reloaded = new LoomoSettings();
        new SettingsStore(_settingsPath).Load(reloaded);
        Assert.False(reloaded.Explorer.FileNestingEnabled);
        Assert.Equal(settings.Explorer.FileNestingPatterns, reloaded.Explorer.FileNestingPatterns);
    }

    [Fact]
    public async Task ルールを書き換えるとツリーへ反映され保存される()
    {
        var (sut, _) = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        sut.FileNestingPatternsText = "App.cs = MainWindow.*";
        await sut.WhenTreeLoadedAsync();

        var app = Assert.Single(sut.Nodes);
        Assert.Equal("App.cs", app.Name);
        Assert.Equal(["MainWindow.xaml", "MainWindow.xaml.cs"], app.Children.Select(c => c.Name));

        var reloaded = new LoomoSettings();
        new SettingsStore(_settingsPath).Load(reloaded);
        var rule = Assert.Single(reloaded.Explorer.FileNestingPatterns);
        Assert.Equal("App.cs", rule.Key);
    }

    [Fact]
    public async Task 保存していた選択がまとめ表示の子なら親を開いて選ぶ()
    {
        var (sut, _) = CreateSut();
        sut.SetPendingViewState(null, P("MainWindow.xaml.cs"));
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var parent = sut.Nodes.Single(n => n.Name == "MainWindow.xaml");
        Assert.True(parent.IsExpanded);
        Assert.True(parent.Children.Single().IsSelected);
        Assert.Equal(P("MainWindow.xaml.cs"), sut.CaptureSelectedPath());
    }

    [Fact]
    public void 設定の無い保存ファイルは既定ルールのまま()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, "{ \"theme\": \"Dark\" }");

        var loaded = new LoomoSettings();
        new SettingsStore(_settingsPath).Load(loaded);

        Assert.True(loaded.Explorer.FileNestingEnabled);
        Assert.Equal(FileNesting.DefaultPatterns, loaded.Explorer.FileNestingPatterns);
    }
}

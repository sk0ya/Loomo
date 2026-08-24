using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace sk0ya.Loomo.Tests;

/// <summary>Explorer の重要な配置を XAML の静的契約として検証する。
/// 最近項目がツリーより上へ戻る、またはアドレス欄が編集不可の既定値へ戻る退行を防ぐ。</summary>
public sealed class ExplorerLayoutTests
{
    [Fact]
    public void ExplorerはFolderTreeを先頭に置き最近項目を重複表示しない()
    {
        var xaml = Read("src", "Loomo.App", "Views", "ShellWindow.xaml");
        var start = xaml.IndexOf("<Grid x:Name=\"ExplorerSection\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("<views:GitPanelView", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var section = xaml[start..end];

        var tree = section.IndexOf("<views:FolderTreeView Grid.Row=\"0\"", StringComparison.Ordinal);
        Assert.True(tree >= 0, "FolderTreeView は ExplorerSection の先頭行であること");
        Assert.DoesNotContain("RecentItemsView", section);
        Assert.Contains("ExplorerFolderTreeRow\" Height=\"*\" MinHeight=\"150\"", section);
        Assert.Contains("<GridSplitter x:Name=\"SidebarTabsSplitter\" Grid.Row=\"1\"", section);
        Assert.Contains("<views:TabsView Grid.Row=\"2\"", section);
    }

    [Fact]
    public void 場所Expanderはファイル一覧のフォルダーアイコンで開閉する()
    {
        var xaml = Read("src", "Loomo.App", "Views", "FilesPaneView.xaml");
        Assert.DoesNotContain("RecentItemsView", xaml);
        Assert.Contains("<Grid x:Name=\"ColumnHost\" />", xaml);

        var column = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");
        Assert.Contains("x:Name=\"PlacesButton\"", column);
        Assert.Contains("IsChecked=\"{Binding IsExpanded, ElementName=PlacesExpander, Mode=TwoWay}\"", column);
        Assert.Contains("<Expander x:Name=\"PlacesExpander\" Grid.Column=\"0\" ExpandDirection=\"Right\"", column);
        Assert.Contains("Expanded=\"OnPlacesExpanded\"", column);
        Assert.Contains("<GridSplitter x:Name=\"PlacesSplitter\" Grid.Column=\"1\"", column);
        Assert.Contains("MaxWidth=\"420\"", column);
        Assert.Contains("ItemsSource=\"{Binding Places}\"", column);
    }

    [Fact]
    public void 最近項目は場所Expander内の通常グループである()
    {
        var vm = Read("src", "Loomo.App", "ViewModels", "FilesColumnViewModel.cs");
        Assert.Contains("FilesPlaceGroup(\"最近使ったファイル\"", vm);
        Assert.Contains("FilesPlaceGroup(\"よく使うフォルダー\"", vm);
        Assert.Contains("FilesPlaceKind.RecentFile", vm);
        Assert.Contains("FilesPlaceKind.FrequentFolder", vm);
        Assert.DoesNotContain("RecentSection", vm);
    }

    [Fact]
    public void アドレスバーはファイル一覧が持ちサイドバーには置かない()
    {
        // 住所は「いま見ている場所」なので、その場所を持っているファイル一覧の道具にする。
        // ツリーに置いていたときは、打ったパスがワークスペース切替へ流れて部屋ごと入れ替わった。
        var files = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");
        Assert.Contains("x:Name=\"AddressBox\"", files);
        Assert.Contains("Text=\"{Binding AddressText, UpdateSourceTrigger=PropertyChanged}\"", files);
        Assert.Contains("PreviewKeyDown=\"OnAddressKeyDown\"", files);

        var tree = Read("src", "Loomo.App", "Views", "FolderTreeView.xaml");
        Assert.DoesNotContain("AddressComboBox", tree);
        Assert.DoesNotContain("AddressText", tree);
    }

    private static string Read(params string[] parts)
    {
        var root = RepoRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        // AppContext.BaseDirectory はテストを一時出力先へ分離して実行するとリポジトリ外に
        // なるため、コンパイル時のこのテストファイルの場所を正本にする。
        var sourceDirectory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        var root = sourceDirectory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "sk0ya.Loomo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}

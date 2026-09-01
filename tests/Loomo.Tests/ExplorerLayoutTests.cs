using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace sk0ya.Loomo.Tests;

/// <summary>Explorer の重要な配置を XAML の静的契約として検証する。
/// 最近項目がツリーより上へ戻る、またはアドレス欄が編集不可の既定値へ戻る退行を防ぐ。</summary>
public sealed class ExplorerLayoutTests
{
    [Fact]
    public void ExplorerはFolderTreeを表示し最近項目もCSharpソリューションも重複表示しない()
    {
        var xaml = Read("src", "Loomo.App", "Views", "ShellWindow.xaml");
        var start = xaml.IndexOf("<Grid x:Name=\"ExplorerSection\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("<views:GitPanelView", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var section = xaml[start..end];

        // C# ソリューションツリーは IDE ペイン（DebugView の実行タブ）へ移した。
        Assert.DoesNotContain("<views:CSharpSolutionExplorerView", section);

        var tree = section.IndexOf("<views:FolderTreeView", StringComparison.Ordinal);
        Assert.True(tree >= 0, "FolderTreeView は ExplorerSection の中にあること");
        var tagEnd = section.IndexOf('>', tree);
        Assert.True(tagEnd > tree, "FolderTreeView の開始タグが閉じていること");
        Assert.True(section[tree..tagEnd].Contains("Grid.Row=\"0\"", StringComparison.Ordinal),
            "FolderTreeView はエクスプローラの最上段に置くこと");
        Assert.DoesNotContain("RecentItemsView", section);
        Assert.Contains("ExplorerFolderTreeRow\" Height=\"*\" MinHeight=\"150\"", section);
        Assert.Contains("<GridSplitter x:Name=\"SidebarTabsSplitter\" Grid.Row=\"1\"", section);
        Assert.Contains("<views:TabsView Grid.Row=\"2\"", section);
    }

    /// <summary>C# ソリューションツリーは IDE ペインの実行タブ左列（プロジェクト一覧の下）に置き、
    /// 見出し行のトグルで畳めること。サイドバーへ戻る退行を防ぐ。</summary>
    [Fact]
    public void CSharpソリューションツリーはIDEペインの実行タブに畳める段として置かれる()
    {
        var xaml = Read("src", "Loomo.App", "Views", "DebugView.xaml");
        var start = xaml.IndexOf("<Grid x:Name=\"ProjectPaneContent\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("x:Name=\"ProjectPaneRail\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "実行タブの左列（ProjectPaneContent）があること");
        var column = xaml[start..end];

        Assert.Contains("<RowDefinition x:Name=\"SolutionSectionRow\"", column);
        Assert.Contains("<GridSplitter x:Name=\"SolutionSplitter\" Grid.Row=\"1\"", column);
        var solution = column.IndexOf("<v:CSharpSolutionExplorerView x:Name=\"SolutionSection\"",
            StringComparison.Ordinal);
        Assert.True(solution >= 0, "CSharpSolutionExplorerView は左列にあること");
        var tagEnd = column.IndexOf("/>", solution, StringComparison.Ordinal);
        Assert.True(tagEnd > solution);
        var tag = column[solution..tagEnd];
        Assert.Contains("Grid.Row=\"2\"", tag);
        Assert.Contains("DataContext=\"{Binding SolutionExplorer}\"", tag);

        var view = Read("src", "Loomo.App", "Views", "CSharpSolutionExplorerView.xaml");
        Assert.Contains("x:Name=\"SectionToggle\"", view);
        Assert.Contains("Click=\"OnSectionToggleClick\"", view);
        Assert.Contains("x:Name=\"SectionBody\"", view);
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

    [Fact]
    public void アドレス欄はフォーカスが外れても外側を押しても畳む()
    {
        // 「フォーカスが外れたら畳む」だけでは畳めない道が2つ残っていた——
        // (1) 候補一覧へ降りたあとは入力欄の LostKeyboardFocus がもう鳴らない、
        // (2) フォーカスを取れない要素（余白・見出し・他ペインの地）を押しても
        //     キーボードフォーカスは動かないので何も鳴らない。
        var xaml = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");
        var box = xaml.IndexOf("x:Name=\"AddressBox\"", StringComparison.Ordinal);
        var list = xaml.IndexOf("x:Name=\"AddressSuggestionList\"", StringComparison.Ordinal);
        Assert.True(box >= 0 && list > box);
        Assert.Contains("LostKeyboardFocus=\"OnAddressLostFocus\"", xaml[box..list]);
        Assert.Contains("LostKeyboardFocus=\"OnAddressLostFocus\"", xaml[list..]);

        var code = Read("src", "Loomo.App", "Views", "FilesColumnView.Address.cs");
        Assert.Contains("PreviewMouseDownEvent", code);
        Assert.Contains("handledEventsToo: true", code);
        // 見張りは入力中だけ。畳んだら（＝カラムを閉じたら）ウィンドウから外す。
        Assert.Contains("RemoveHandler(PreviewMouseDownEvent", code);
        var view = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml.cs");
        Assert.Contains("Vm?.CancelAddressEdit();", view);
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

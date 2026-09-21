using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace sk0ya.Loomo.Tests;

/// <summary>Explorer の重要な配置を XAML の静的契約として検証する。
/// 最近項目がツリーより上へ戻る、またはアドレス欄が編集不可の既定値へ戻る退行を防ぐ。</summary>
public sealed class ExplorerLayoutTests
{
    [Fact]
    public void サイドバーは上段と中段の2区画でどちらにもパネルを載せられる()
    {
        var xaml = Read("src", "Loomo.App", "Views", "Shell", "ShellWindow.xaml");
        var start = xaml.IndexOf("<Grid x:Name=\"SidebarContainer\"", StringComparison.Ordinal);
        var end = xaml.IndexOf("<GridSplitter x:Name=\"SidebarSplitter\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var sidebar = xaml[start..end];

        // 2区画（上段／中段）と、その境目のスプリッター。
        Assert.Contains("x:Name=\"PrimarySidebarRow\"", sidebar);
        Assert.Contains("x:Name=\"SecondarySidebarRow\"", sidebar);
        Assert.Contains("<Grid x:Name=\"PrimarySidebarHost\"", sidebar);
        Assert.Contains("<Grid x:Name=\"SecondarySidebarHost\"", sidebar);
        Assert.Contains("<GridSplitter x:Name=\"SidebarSectionSplitter\"", sidebar);

        // パネルのビューは1つずつ宣言し、どちらのホストへ載せるかは code-behind が決める。
        // 「どのパネルか」を XAML の Visibility バインドで決めると、段を移せなくなる。
        Assert.DoesNotContain("ConverterParameter=Explorer", sidebar);
        Assert.DoesNotContain("ConverterParameter=Solution", sidebar);
        foreach (var name in new[] {
            "SidebarFolderTree", "SidebarGitPanel", "SidebarPegboard", "SidebarSolution", "SidebarTabs" })
            Assert.Contains($"x:Name=\"{name}\"", sidebar);

        // 最近項目は中央の FilesPane 内のクイックアクセスへ移したので、サイドバーには重複表示しない。
        // C# ソリューションツリーもフォルダーツリー（住所）とは別の独立した面のまま。
        Assert.DoesNotContain("RecentItemsView", sidebar);
        var tree = sidebar.IndexOf("<views:FolderTreeView", StringComparison.Ordinal);
        var git = sidebar.IndexOf("<views:GitPanelView", StringComparison.Ordinal);
        Assert.True(tree >= 0 && git > tree);
        Assert.DoesNotContain("CSharpSolutionExplorerView", sidebar[tree..git]);

        // IDE ペイン（実行タブ）にはもう置かない。
        var debug = Read("src", "Loomo.App", "Views", "Debugging", "DebugView.xaml");
        Assert.DoesNotContain("CSharpSolutionExplorerView", debug);
        Assert.DoesNotContain("SolutionSectionRow", debug);
    }

    /// <summary>ActivityBar は上段・中段の2本。どちらも同じ項目テンプレートを使い、
    /// 段まるごとが項目の落とし先になる（項目が1つも無い段にも落とせる）こと。</summary>
    [Fact]
    public void ActivityBarは2本ありどちらの段もドロップを受ける()
    {
        var xaml = Read("src", "Loomo.App", "Views", "Shell", "ShellWindow.xaml");
        foreach (var group in new[] { "PrimaryActivityGroup", "SecondaryActivityGroup" })
        {
            var start = xaml.IndexOf($"<Grid x:Name=\"{group}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{group} があること");
            var tag = xaml[start..xaml.IndexOf('>', start)];
            Assert.Contains("AllowDrop=\"True\"", tag);
            Assert.Contains("Drop=\"OnActivityGroupDrop\"", tag);
            Assert.Contains("Background=\"Transparent\"", tag);   // 空の段にも落とせる受け皿
        }
        Assert.Contains("ItemsSource=\"{Binding ActivityBar.PrimaryItems}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding ActivityBar.SecondaryItems}\"", xaml);
        Assert.Contains("<DataTemplate x:Key=\"ActivityBarItemTemplate\">", xaml);
    }

    [Fact]
    public void 場所Expanderはファイル一覧のフォルダーアイコンで開閉する()
    {
        var xaml = Read("src", "Loomo.App", "Views", "Files", "FilesPaneView.xaml");
        Assert.DoesNotContain("RecentItemsView", xaml);
        Assert.Contains("<Grid x:Name=\"ColumnHost\" />", xaml);

        var column = Read("src", "Loomo.App", "Views", "Files", "FilesColumnView.xaml");
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
        var vm = Read("src", "Loomo.App", "ViewModels", "Files", "FilesColumnViewModel.cs");
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
        var files = Read("src", "Loomo.App", "Views", "Files", "FilesColumnView.xaml");
        Assert.Contains("x:Name=\"AddressBox\"", files);
        Assert.Contains("Text=\"{Binding AddressText, UpdateSourceTrigger=PropertyChanged}\"", files);
        Assert.Contains("PreviewKeyDown=\"OnAddressKeyDown\"", files);

        var tree = Read("src", "Loomo.App", "Views", "Files", "FolderTreeView.xaml");
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
        var xaml = Read("src", "Loomo.App", "Views", "Files", "FilesColumnView.xaml");
        var box = xaml.IndexOf("x:Name=\"AddressBox\"", StringComparison.Ordinal);
        var list = xaml.IndexOf("x:Name=\"AddressSuggestionList\"", StringComparison.Ordinal);
        Assert.True(box >= 0 && list > box);
        Assert.Contains("LostKeyboardFocus=\"OnAddressLostFocus\"", xaml[box..list]);
        Assert.Contains("LostKeyboardFocus=\"OnAddressLostFocus\"", xaml[list..]);

        var code = Read("src", "Loomo.App", "Services", "FileSystem", "FilesColumnAddressInteractionController.cs");
        Assert.Contains("UIElement.PreviewMouseDownEvent", code);
        Assert.Contains("handledEventsToo: true", code);
        // 見張りは入力中だけ。畳んだら（＝カラムを閉じたら）ウィンドウから外す。
        Assert.Contains("RemoveHandler(UIElement.PreviewMouseDownEvent", code);
        var view = Read("src", "Loomo.App", "Views", "Files", "FilesColumnView.xaml.cs");
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

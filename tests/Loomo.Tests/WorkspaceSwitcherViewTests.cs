using System;
using System.IO;

namespace sk0ya.Loomo.Tests;

/// <summary>切替ポップアップ（<c>WorkspaceSwitcherView.xaml</c>）の組み立てで、一度やって全滅した形を封じる。
///
/// 行の右クリックメニューは <c>ItemContainerStyle</c> の Setter に置かれていた時期があり、そのあいだ
/// メニューは<em>丸ごと死んでいた</em>——Style の値は全行で共有される1インスタンスなので、そこに書いた
/// <c>Click</c> は結線されず、押してもハンドラが呼ばれない（切替・ピン留め・名前の変更・パスのコピー・
/// 一覧からの削除が全部無反応。VM 側のコマンドは正しいので <see cref="WorkspaceListViewModelTests"/> では
/// 捕まらない）。行ごとの実体になるよう、メニューは<b>行テンプレートの中</b>に置く。</summary>
public class WorkspaceSwitcherViewTests
{
    [Fact]
    public void Row_context_menu_is_not_declared_inside_the_item_container_style()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Loomo.App", "Views", "WorkspaceSwitcherView.xaml"));

        var style = Between(xaml, "<ListBox.ItemContainerStyle>", "</ListBox.ItemContainerStyle>");
        Assert.DoesNotContain("ContextMenu", style);
        Assert.DoesNotContain("Click=", style);

        // 行テンプレート側に居ること（削除まで一式）。
        var template = Between(xaml, "<ListBox.ItemTemplate>", "</ListBox.ItemTemplate>");
        Assert.Contains("<ContextMenu>", template);
        Assert.Contains("Click=\"OnMenuRemove\"", template);
    }

    private static string Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        var end = text.IndexOf(close, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"{open} … {close} が見つからない");
        return text[start..end];
    }

    /// <summary>ビルド出力からリポジトリのルート（.sln のある所）まで遡る。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sk0ya.Loomo.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}

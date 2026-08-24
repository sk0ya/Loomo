using System;
using System.IO;
using System.Runtime.CompilerServices;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>ファイル一覧ペインの「見え方」の契約。ここが崩れると、同じデータを見せている
/// ツリーとファイル一覧で作法が食い違ったり、OS 既定の配色のコントロールがペインの中に
/// 混ざったりする——どちらも実際に一度そうなった。</summary>
public sealed class FilesPanePresentationTests
{
    [Fact]
    public void ツールバーは素のComboBoxを使わずポップアップで選ばせる()
    {
        var xaml = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");

        // 素の ComboBox は OS 既定の配色・行間で描かれ、暗色テーマの中でここだけ浮く。
        Assert.DoesNotContain("<ComboBox", xaml);

        Assert.Contains("x:Name=\"DisplayModeButton\"", xaml);
        Assert.Contains("x:Name=\"DisplayModePopup\"", xaml);
        Assert.Contains("x:Name=\"GroupByButton\"", xaml);
        Assert.Contains("x:Name=\"GroupByPopup\"", xaml);
        Assert.Contains("SelectDisplayModeCommand", xaml);
        Assert.Contains("SelectGroupByCommand", xaml);
    }

    [Fact]
    public void Git状態は名前の色と行右端のバッジで示す()
    {
        var xaml = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");

        // ツリー（FolderTreeView）と同じ作法。バッジを名前の前に置くと、状態のある行だけ
        // 名前が右へずれて一覧の左端が揃わない。
        Assert.Contains("x:Key=\"FilesGitName\"", xaml);
        Assert.Contains("x:Key=\"FilesGitBadge\"", xaml);
        Assert.Contains("DockPanel.Dock=\"Right\" Style=\"{StaticResource FilesGitBadge}\"", xaml);

        // 選択中は Accent 背景の上で状態色が読めないので、名前だけ AccentFg へ戻す。
        Assert.Contains("IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}", xaml);

        // バッジは「状態文字があるとき」出す。EmptyToVis はウォーターマーク用（空のとき出す）で、
        // これを使っていたためバッジは変更のある行でだけ消えていた＝一度も出ていなかった。
        var badgeStyle = Section(xaml, "x:Key=\"FilesGitBadge\"", "</Style>");
        Assert.Contains("GitStatusBadge, Converter={StaticResource NonEmptyToVis}", badgeStyle);
        Assert.DoesNotContain("Converter={StaticResource EmptyToVis}", badgeStyle);
    }

    [Fact]
    public void 状態バッジと名前の色は同じ状態から引く()
    {
        // どの状態でも「名前の色」と「バッジ」の両方が定義されていること。片方だけだと
        // 状態が色でしか分からない／文字でしか分からない行が混ざる。
        foreach (var status in new[] { "Modified", "Added", "Untracked", "Deleted", "Renamed",
                                       "Conflicted", "Staged", "Ignored", "DirectoryChanged" })
        {
            var entry = new FileEntryViewModel(@"C:\ws\a.txt", isDirectory: false, 0, DateTime.Now)
            {
                GitStatus = Enum.Parse<GitChangeKind>(status),
            };
            Assert.False(string.IsNullOrEmpty(entry.GitStatusBadge), $"{status} のバッジが空");
            Assert.False(string.IsNullOrEmpty(entry.GitStatusTooltip), $"{status} の説明が空");
        }

        var clean = new FileEntryViewModel(@"C:\ws\a.txt", isDirectory: false, 0, DateTime.Now);
        Assert.Equal("", clean.GitStatusBadge);
    }

    private static string Section(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startMarker} が見つからない");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"{endMarker} が見つからない");
        return text[start..end];
    }

    [Fact]
    public void アイコン表示の名前は折り返して省略する()
    {
        var xaml = Read("src", "Loomo.App", "Views", "FilesColumnView.xaml");

        // 名前を横並び StackPanel に入れると幅の制約が届かず、TextTrimming が効かないまま
        // セルからはみ出して両端が切れる（実際にそう見えていた）。
        foreach (var layout in new[] { "LargeIconsLayout", "MediumIconsLayout", "SmallIconsLayout" })
        {
            var start = xaml.IndexOf($"x:Name=\"{layout}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{layout} が見つからない");
            var end = xaml.IndexOf("</StackPanel>", start, StringComparison.Ordinal);
            var section = xaml[start..end];
            Assert.Contains("TextWrapping=\"Wrap\"", section);
            Assert.Contains("Style=\"{StaticResource FilesGitName}\"", section);
        }
    }

    [Theory]
    [InlineData("readme.md", false)]
    [InlineData("a.txt", false)]
    [InlineData("noextension", false)]
    [InlineData("src", true)]
    public void 種類は拡張子の大文字化ではなくシェルの種類名を出す(string name, bool isDirectory)
    {
        var text = ShellTypeNames.Describe(name, isDirectory);

        // どの入力でも空欄にはしない（空欄は読み手には欠測に見える）。
        Assert.False(string.IsNullOrWhiteSpace(text));

        if (!OperatingSystem.IsWindows())
            return;

        if (isDirectory)
        {
            // 既定ロケールでは「ファイル フォルダー」。ロケール差を避け、何か返ることだけ見る。
            Assert.NotEqual("", text);
            return;
        }

        var ext = Path.GetExtension(name);
        if (ext.Length > 1)
        {
            // シェルが答えたなら拡張子そのままではないはず。答えなければ従来表記へ落ちる。
            Assert.True(text == ext[1..].ToUpperInvariant() || text.Length > ext.Length,
                $"種類名が期待と違う: {text}");
        }
        else
        {
            Assert.Equal("ファイル", text);
        }
    }

    [Fact]
    public void 種類名は拡張子ごとに一度だけ引く()
    {
        ShellTypeNames.ResetCache();
        var first = ShellTypeNames.ForExtension(".md");
        var second = ShellTypeNames.ForExtension(".md");

        // 一覧の行ごとにシェルを叩かないための約束（同じ拡張子なら同じインスタンスが返る）。
        if (first is null)
            Assert.Null(second);
        else
            Assert.Same(first, second);
    }

    [Fact]
    public void 場所の行はアイコンを持ち同名を親フォルダーで区別する()
    {
        var quick = new FilesPlace("Data", @"C:\Users\koya\Data", FilesPlaceKind.QuickAccess);
        var workspaceFolder = new FilesPlace("Loomo", @"C:\Projects\Loomo", FilesPlaceKind.WorkspaceFolder);
        var recent = new FilesPlace("Roadmap.md", @"C:\Projects\Loomo\Roadmap.md", FilesPlaceKind.RecentFile);

        Assert.NotNull(quick.IconImage);
        Assert.NotNull(recent.IconImage);

        Assert.Equal("koya", quick.Detail);
        Assert.Equal("Loomo", recent.Detail);
        // ワークスペース・ピン留めは名前自体が場所を表すので補足を付けない。
        Assert.Equal("", workspaceFolder.Detail);
    }

    private static string Read(params string[] parts)
    {
        var root = RepoRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var sourceDirectory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        var root = sourceDirectory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "sk0ya.Loomo.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Editor.Controls;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace ViewHarness.Scenes;

/// <summary>エディタの下端の共有フッター（VimStatusBar）をエディタの配色ごとに並べる。
/// フッターの地と文字はエディタのテーマから塗る（2026-11-16、以前は地 #1E1E1E と明るい文字の直書きで、
/// 明色のテーマでもフッターだけ黒いままだった）。上の帯はそのテーマのエディタの地。</summary>
public sealed class EditorFooterScene : IScene
{
    private static readonly string[] Themes =
        ["light", "onelight", "githublight", "solarizedlight", "gruvboxlight", "highcontrast", "monokai"];

    public string Name => "editor-footer";
    public string Description => "エディタのフッター（ステータスバー）をエディタのテーマごとに";
    public Size Size => new(1100, 90 * Themes.Length);

    public FrameworkElement Build()
    {
        var root = new StackPanel();
        var tryGet = typeof(sk0ya.Loomo.App.App).Assembly.GetType("sk0ya.Loomo.App.Services.EditorThemePresets", throwOnError: true)!;
        var method = tryGet.GetMethod("TryGet", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        var modes = new[] { VimMode.Normal, VimMode.Insert, VimMode.Visual };
        for (var i = 0; i < Themes.Length; i++)
        {
            var theme = (EditorTheme)method.Invoke(null, [Themes[i]])!;
            root.Children.Add(new Border
            {
                Height = 40,
                Background = theme.Background,
                Child = new TextBlock
                {
                    Text = $"  {Themes[i]}  — エディタの地",
                    Foreground = theme.Foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var bar = new VimStatusBar { Theme = theme };
            bar.UpdateMode(modes[i % modes.Length]);
            bar.UpdateFile(@"C:\work\ShellWindow.xaml.cs", modified: i % 2 == 0, "dos", "utf-8");
            bar.UpdateGitBranch("main");
            bar.UpdateStatus("\"ShellWindow.xaml.cs\" 1234L written");
            bar.UpdateCursor(new CursorPosition(41, 7), 1234);
            if (i == 0) bar.ShowCompletions(["edit", "enew", "echo"], 1);
            root.Children.Add(bar);
            root.Children.Add(new Border { Height = 10 });
        }
        return root;
    }
}

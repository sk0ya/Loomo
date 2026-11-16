using System.Windows;
using System.Windows.Controls;

namespace ViewHarness.Scenes;

/// <summary>設定 → Claude Code の欄に足した「動きが悪くなったら Claude に直させる」の行（2026-11-17）と、その説明。
/// 既存の IDE 連携の行と同じ並びで置き、文字サイズを上げたときに文字が切れないか・既定の色で読めるかを見る。</summary>
public sealed class ClaudeSettingsScene : IScene
{
    public string Name => "claude-settings";
    public string Description => "設定の Claude Code 欄（IDE 連携・動きが悪くなったら直させる）の行と説明";
    public Size Size => new(560, 440);

    public FrameworkElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "Claude Code",
            Style = (Style)Application.Current.FindResource("FieldHint"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        root.Children.Add(new CheckBox
        {
            IsChecked = true,
            Content = new TextBlock { Text = "Claude Code と IDE 連携する（選択・診断・編集の提案）", TextWrapping = TextWrapping.Wrap },
        });
        root.Children.Add(new TextBlock
        {
            Text = "切り替えは Loomo の再起動後に効きます。",
            Style = (Style)Application.Current.FindResource("FieldHint"),
        });

        root.Children.Add(new CheckBox
        {
            Margin = new Thickness(0, 12, 0, 0),
            IsChecked = false,
            Content = new TextBlock { Text = "動きが悪くなったら Claude に直させる（既定は切）", TextWrapping = TextWrapping.Wrap },
            ToolTip = "動きの記録（§44.3）で悪い方へ変わったら、ターンを終えようとしている Claude に直すよう続けさせます（1 つの頼みごとで最大 2 回）。既定は切。",
        });
        root.Children.Add(new TextBlock
        {
            Text = "ターンの終わりに動きを録るので、少し待ってから Claude が止まります。",
            Style = (Style)Application.Current.FindResource("FieldHint"),
            TextWrapping = TextWrapping.Wrap,
        });
        return root;
    }
}

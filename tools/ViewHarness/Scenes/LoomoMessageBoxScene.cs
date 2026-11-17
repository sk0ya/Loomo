using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.Views;

namespace ViewHarness.Scenes;

/// <summary>Loomo の確認・通知ダイアログ（LoomoMessageBox）の型を撮る。窓の枠は外し、中身だけを並べる。
/// 短い型：通常の確認（OK／キャンセル）・取り消せない削除の確認（危険色・既定はやめる）・誤りの通知（OK だけ）。</summary>
public sealed class LoomoMessageBoxScene : IScene
{
    public string Name => "loomo-messagebox";
    public string Description => "Loomo の確認・通知ダイアログ（確認・削除の確認・誤り）";
    public Size Size => new(600, 1000);

    public FrameworkElement Build() => MessageBoxStack.Build(
        ("ワークスペースの削除", "ワークスペース「Loomo」を一覧から削除しますか？\nフォルダー自体は削除されません（タブ・レイアウトの保存状態は失われます）。", MessageBoxButton.OKCancel, MessageBoxImage.Question),
        ("削除の確認", "src/Loomo.App/Views/Dialogs/InputDialog.xaml を完全に削除します。\nゴミ箱には入りません。", MessageBoxButton.OKCancel, MessageBoxImage.Warning),
        ("リファクタリング", "識別子として使えない名前です。", MessageBoxButton.OK, MessageBoxImage.Error));
}

/// <summary>長い本文（一覧が続く）の型。本文が上限の高さを超えたときスクロールできることを見る。</summary>
public sealed class LoomoMessageBoxLongScene : IScene
{
    public string Name => "loomo-messagebox-long";
    public string Description => "Loomo の確認ダイアログ：長い本文（上限でスクロール・取り消せない確認）";
    public Size Size => new(600, 800);

    public FrameworkElement Build() => MessageBoxStack.Build(
        ("変更の破棄",
         string.Join("\n", Enumerable.Range(1, 14).Select(i => $"  src/Loomo.App/Views/Git/GitSessionView.{i:00}.cs")) +
         "\n\nほか 12 件。ここまでが同じ操作の対象です。",
         MessageBoxButton.YesNo, MessageBoxImage.Warning));
}

internal static class MessageBoxStack
{
    public static FrameworkElement Build(params (string Caption, string Text, MessageBoxButton Buttons, MessageBoxImage Icon)[] items)
    {
        var root = new StackPanel { Margin = new Thickness(12) };
        foreach (var (caption, text, buttons, icon) in items)
        {
            var dialog = LoomoMessageBox.Create(text, caption, buttons, icon, null);
            root.Children.Add(new Border
            {
                Background = (Brush)Application.Current.FindResource("Panel"),
                Margin = new Thickness(0, 0, 0, 12),
                Child = dialog.DetachContentForPreview(),
            });
        }
        return root;
    }
}

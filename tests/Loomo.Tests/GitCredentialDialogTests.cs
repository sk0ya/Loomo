using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Git;

namespace sk0ya.Loomo.Tests;

/// <summary>資格情報の入力欄（§24.101）。形が問いに合っていて、問うた git が居なくなったら閉じる。</summary>
[Collection(WpfViewTests.Name)]
public sealed class GitCredentialDialogTests
{
    private readonly WpfViewHost _host;

    public GitCredentialDialogTests(WpfViewHost host) => _host = host;

    [Theory]
    [InlineData(GitCredentialPromptKind.Secret, "Enter passphrase for key '/c/Users/me/.ssh/id_ed25519': ", false, true, "OK")]
    [InlineData(GitCredentialPromptKind.Text, "Username for 'https://github.com': ", true, false, "OK")]
    [InlineData(GitCredentialPromptKind.YesNo, "Are you sure you want to continue connecting (yes/no/[fingerprint])? ", false, false, "はい")]
    public void 問いに合う形で出て_中止で閉じる(GitCredentialPromptKind kind, string prompt, bool textVisible, bool passwordVisible, string ok)
    {
        _host.Run(() =>
        {
            using var cancel = new CancellationTokenSource();
            GitCredentialDialog? seen = null;
            // モーダルの間にディスパッチャで中を覗き、見た目を撮ってから、問うた git が居なくなったことにする。
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                seen = Application.Current.Windows.OfType<GitCredentialDialog>().FirstOrDefault();
                if (seen is not null)
                    Snapshot(seen, kind);
                cancel.Cancel();
            };
            timer.Start();

            var answer = GitCredentialDialog.Show(null, new GitAskPass.Request(prompt, Context: "git push origin main"), kind, null, cancel.Token);

            Assert.Null(answer);
            Assert.NotNull(seen);
            Assert.False(seen!.IsVisible);
            // 閉じた後なので IsVisible ではなく指定された Visibility を見る。
            Assert.Equal(textVisible, ((TextBox)seen.FindName("InputBox")).Visibility == Visibility.Visible);
            Assert.Equal(passwordVisible, ((PasswordBox)seen.FindName("PasswordInput")).Visibility == Visibility.Visible);
            Assert.Equal(ok, ((Button)seen.FindName("OkButton")).Content);
            Assert.Equal("git push origin main が入力を求めています。", ((TextBlock)seen.FindName("LeadText")).Text);
        });
    }

    /// <summary>見た目の確認用に PNG を残す（%TEMP%\loomo-git-credential-*.png）。</summary>
    private static void Snapshot(Window window, GitCredentialPromptKind kind)
    {
        if (window.Content is not FrameworkElement content || content.ActualWidth <= 0) return;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Path.GetTempPath(), $"loomo-git-credential-{kind}.png"));
        encoder.Save(file);
    }
}

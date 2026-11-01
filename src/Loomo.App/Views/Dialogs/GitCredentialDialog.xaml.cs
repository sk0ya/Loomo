using System.Threading;
using System.Windows;
using sk0ya.Loomo.Core.Git;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// git・ssh が資格情報を求めたときの入力欄（§24.101）。問いの文面はそのまま見せ、パスワード・パスフレーズは伏せる。
/// 問うた git が居なくなったら（時間切れ・中止）<see cref="Show"/> に渡した中止で閉じる。
/// <para>タスクバーにも出す（<c>ShowInTaskbar</c>）。push を押してから Loomo を畳む・ほかのアプリへ移ることはよくあり、
/// 持ち主の窓の陰に隠れたモーダルは、git が時間切れになるまで Loomo の全部の窓を塞いだまま見つけられない。</para>
/// </summary>
public partial class GitCredentialDialog : Window
{
    private GitCredentialPromptKind _kind;
    private string? _answer;

    private GitCredentialDialog()
    {
        InitializeComponent();
    }

    /// <summary>モーダルで聞いて答えを返す（UI スレッドから）。取り消し・閉じられたら null。
    /// 答えの形は <see cref="IGitCredentialPrompter.AskAsync"/> の約束どおり。</summary>
    public static string? Show(Window? owner, GitAskPass.Request request, GitCredentialPromptKind kind,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;
        // 畳まれた窓を持ち主にすると、入力欄も一緒に畳まれて見えない。そのときは持ち主なしで画面の中央に出す。
        if (owner is { WindowState: WindowState.Minimized } or { IsVisible: false })
            owner = null;
        var dialog = new GitCredentialDialog
        {
            Owner = owner,
            _kind = kind,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        dialog.LeadText.Text = $"{(string.IsNullOrWhiteSpace(request.Context) ? "git" : request.Context)} が入力を求めています。";
        dialog.PromptText.Text = (request.Prompt ?? "").Trim();
        switch (kind)
        {
            case GitCredentialPromptKind.Secret:
                dialog.InputBox.Visibility = Visibility.Collapsed;
                dialog.PasswordInput.Visibility = Visibility.Visible;
                break;
            case GitCredentialPromptKind.YesNo:
                dialog.InputBox.Visibility = Visibility.Collapsed;
                dialog.OkButton.Content = "はい";
                dialog.NoButton.Visibility = Visibility.Visible;
                break;
            case GitCredentialPromptKind.Confirm:
                dialog.InputBox.Visibility = Visibility.Collapsed;
                dialog.OkButton.Content = "はい";
                dialog.CancelButton.Content = "いいえ";
                break;
        }
        dialog.Loaded += (_, _) =>
        {
            // 裏で git が聞いてきたときも手前へ（OS が前面化を許さなければ、タスクバーで点滅する）。
            dialog.Activate();
            if (kind == GitCredentialPromptKind.Secret) dialog.PasswordInput.Focus();
            else if (kind == GitCredentialPromptKind.Text) dialog.InputBox.Focus();
            else dialog.OkButton.Focus();
        };

        using var registration = cancellationToken.Register(
            () => dialog.Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        return dialog.ShowDialog() == true ? dialog._answer : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _answer = _kind switch
        {
            // 合言葉は前後の空白も中身のうち。削らない。
            GitCredentialPromptKind.Secret => PasswordInput.Password,
            GitCredentialPromptKind.YesNo => "yes",
            GitCredentialPromptKind.Confirm => "",
            _ => InputBox.Text.Trim(),
        };
        DialogResult = true;
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _answer = "no";
        DialogResult = true;
    }
}

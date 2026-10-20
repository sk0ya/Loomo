using System.ComponentModel;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ターミナルサポートペイン（§39）。ロジックは <see cref="TerminalSupportViewModel"/> に集約し、ここは Claude の会話と
/// コマンドの出力の見せ直しを描く WebView（<see cref="TerminalSupportWebPage"/>）の世話だけ：見えている間だけ本文を流し、
/// 見えないうちは作らない。
/// </summary>
public partial class TerminalSupportPaneView : UserControl
{
    private readonly TerminalSupportWebPage _conversation;
    private readonly TerminalSupportWebPage _output;
    private Func<string>? _theme;
    private TerminalSupportViewModel? _vm;
    /// <summary>会話のページにいま出している節。替わったら先頭から見せる。</summary>
    private string? _shownNodeId;

    public TerminalSupportPaneView()
    {
        InitializeComponent();
        _conversation = new TerminalSupportWebPage(ConversationHost, Dispatcher, "claude", "Claude", followTurns: false);
        _conversation.LinkClicked += (_, href) => PageLinkClicked?.Invoke(this, href);
        _output = new TerminalSupportWebPage(OutputHost, Dispatcher, "output", "出力", followTurns: false);
        _output.LinkClicked += (_, href) => PageLinkClicked?.Invoke(this, href);
        DataContextChanged += (_, _) => Attach(DataContext as TerminalSupportViewModel);
        ConversationHost.IsVisibleChanged += (_, _) => ShowConversation();
        ConversationTree.SelectedItemChanged += (_, e) => _ = _vm?.SelectTreeItemAsync(e.NewValue as ClaudeTreeItem);
        OutputHost.IsVisibleChanged += (_, _) => ShowOutput();
    }

    /// <summary>ページ（会話・出力）の本文のリンクが押された（ファイル・URL）。</summary>
    public event EventHandler<string>? PageLinkClicked;

    /// <summary>部屋のプレビューと同じ道具（WebView2 のファクトリ・一時ページの置き場）と、Markdown の配色を渡す。
    /// このペインは XAML から生えて DI が届かないので、ShellWindow がここで渡す（Diff ペインと同じ）。</summary>
    public void ConfigureConversation(IEditorSupportViewFactory factory, string previewFolder, Func<string> theme)
    {
        _conversation.Configure(factory, previewFolder);
        _output.Configure(factory, previewFolder);
        _theme = theme;
        ShowConversation();
        ShowOutput();
    }

    private void Attach(TerminalSupportViewModel? vm)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        ShowConversation();
        ShowOutput();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalSupportViewModel.ConversationBody) or nameof(TerminalSupportViewModel.IsClaude))
            ShowConversation();
        else if (e.PropertyName is nameof(TerminalSupportViewModel.OutputBody) or nameof(TerminalSupportViewModel.IsShell))
            ShowOutput();
    }

    private void ShowConversation()
    {
        // 見えていないうちは WebView を作らない（Claude の居ないタブ・隠れたペインで Chromium を起こさない）。
        if (_vm is not { IsClaude: true } vm || _theme is null || !ConversationHost.IsVisible)
            return;
        // ツリーで別の節を選んだら先頭から。同じ節の書き足し（作業中のターン）は読んでいる位置のまま。
        var fromTop = _shownNodeId is not null && _shownNodeId != vm.ConversationNodeId;
        _shownNodeId = vm.ConversationNodeId;
        _ = _conversation.ShowAsync(vm.ConversationBody, _theme(), fromTop, followTurns: vm.ShowsWholeConversation);
    }

    private void ShowOutput()
    {
        // 出力のページの本文が替わるのは別の回を選んだときだけ（1回分の出力は変わらない）なので、いつも先頭から見せる。
        if (_vm is not { IsShell: true } vm || _theme is null || !OutputHost.IsVisible || vm.OutputBody.Length == 0)
            return;
        _ = _output.ShowAsync(vm.OutputBody, _theme(), fromTop: true);
    }
}

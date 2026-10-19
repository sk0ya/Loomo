using System.ComponentModel;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ターミナルサポートペイン（§39）。ロジックは <see cref="TerminalSupportViewModel"/> に集約し、ここは Claude の会話を
/// 描く WebView（<see cref="ClaudeConversationView"/>）の世話だけ：見えている間だけ本文を流し、見えないうちは作らない。
/// </summary>
public partial class TerminalSupportPaneView : UserControl
{
    private readonly ClaudeConversationView _conversation;
    private Func<string>? _theme;
    private TerminalSupportViewModel? _vm;

    public TerminalSupportPaneView()
    {
        InitializeComponent();
        _conversation = new ClaudeConversationView(ConversationHost, Dispatcher);
        _conversation.LinkClicked += (_, href) => ConversationLinkClicked?.Invoke(this, href);
        DataContextChanged += (_, _) => Attach(DataContext as TerminalSupportViewModel);
        ConversationHost.IsVisibleChanged += (_, _) => ShowConversation();
    }

    /// <summary>会話の本文のリンクが押された（ファイル・URL）。</summary>
    public event EventHandler<string>? ConversationLinkClicked;

    /// <summary>部屋のプレビューと同じ道具（WebView2 のファクトリ・一時ページの置き場）と、Markdown の配色を渡す。
    /// このペインは XAML から生えて DI が届かないので、ShellWindow がここで渡す（Diff ペインと同じ）。</summary>
    public void ConfigureConversation(IEditorSupportViewFactory factory, string previewFolder, Func<string> theme)
    {
        _conversation.Configure(factory, previewFolder);
        _theme = theme;
        ShowConversation();
    }

    private void Attach(TerminalSupportViewModel? vm)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        ShowConversation();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalSupportViewModel.ConversationBody) or nameof(TerminalSupportViewModel.IsClaude))
            ShowConversation();
    }

    private void ShowConversation()
    {
        // 見えていないうちは WebView を作らない（Claude の居ないタブ・隠れたペインで Chromium を起こさない）。
        if (_vm is not { IsClaude: true } vm || _theme is null || !ConversationHost.IsVisible)
            return;
        _ = _conversation.ShowAsync(vm.ConversationBody, _theme());
    }
}

using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 のページ内検索と検索バーの操作をまとめて扱う。</summary>
internal sealed class BrowserFindController
{
    private readonly BrowserViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private readonly Func<CoreWebView2?> _core;
    private readonly TextBox _findBox;
    private readonly Action _ensureVisible;
    private readonly Action _focusBrowser;
    private bool _unavailable;

    internal BrowserFindController(
        BrowserViewModel viewModel,
        Dispatcher dispatcher,
        Func<CoreWebView2?> core,
        TextBox findBox,
        Action ensureVisible,
        Action focusBrowser)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _core = core;
        _findBox = findBox;
        _ensureVisible = ensureVisible;
        _focusBrowser = focusBrowser;
    }

    internal void Hook(CoreWebView2 core)
    {
        if (_unavailable) return;
        try
        {
            var find = core.Find;
            find.MatchCountChanged += (_, _) => _dispatcher.BeginInvoke(() => UpdateLabel(core));
            find.ActiveMatchIndexChanged += (_, _) => _dispatcher.BeginInvoke(() => UpdateLabel(core));
        }
        catch
        {
            // 古い WebView2 ランタイムには Find API が無い。検索バーに理由を出す。
            _unavailable = true;
        }
    }

    internal void Open()
    {
        _ensureVisible();
        _viewModel.IsFindOpen = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _findBox.Focus();
            _findBox.SelectAll();
        }));
        if (!string.IsNullOrEmpty(_viewModel.FindTerm))
            _ = ApplyAsync();
    }

    internal void Close()
    {
        if (!_viewModel.IsFindOpen) return;
        try { _core()?.Find.Stop(); }
        catch { /* 未対応ランタイム */ }
        _viewModel.CloseFind();
        _focusBrowser();
    }

    internal bool HandleKey(KeyEventArgs e)
    {
        switch (BrowserChromePolicy.ResolveFindAction(e.Key, Keyboard.Modifiers))
        {
            case BrowserFindKeyAction.Next:
                Step(1);
                break;
            case BrowserFindKeyAction.Previous:
                Step(-1);
                break;
            case BrowserFindKeyAction.Close:
                Close();
                break;
            case null:
                return false;
        }
        e.Handled = true;
        return true;
    }

    internal async Task ApplyAsync()
    {
        if (_core() is not { } core) return;
        if (_unavailable)
        {
            _viewModel.FindLabel = "この WebView2 では未対応";
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(_viewModel.FindTerm))
            {
                core.Find.Stop();
                _viewModel.FindLabel = "";
                return;
            }
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = _viewModel.FindTerm;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;
            await core.Find.StartAsync(options);
            UpdateLabel(core);
        }
        catch
        {
            // 一時的な失敗で API 未対応とは判断せず、次の検索操作を受け付ける。
            _viewModel.FindLabel = "検索できませんでした";
        }
    }

    internal void Step(int step)
    {
        if (_core() is not { } core || _unavailable) return;
        try
        {
            if (step >= 0)
                core.Find.FindNext();
            else
                core.Find.FindPrevious();
            UpdateLabel(core);
        }
        catch { /* 遷移中など一時的な失敗は次の操作に任せる */ }
    }

    private void UpdateLabel(CoreWebView2 core)
    {
        try { _viewModel.SetFindMatches(core.Find.ActiveMatchIndex, core.Find.MatchCount); }
        catch { /* セッション終了直後などは読めない */ }
    }
}

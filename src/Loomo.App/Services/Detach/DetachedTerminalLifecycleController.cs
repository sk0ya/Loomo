using System.Windows.Media;
using sk0ya.Loomo.App.Detach;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Services;

/// <summary>切り離しターミナルのタイトル追従とメイン帯へ戻す処理をまとめる。</summary>
internal static class DetachedTerminalLifecycleController
{
    internal static DetachedItem CreateItem(
        DetachKind kind,
        TerminalTabView view,
        ImageSource? icon,
        Func<TerminalTab> createMainTab,
        Action<TerminalTab> adoptMainTab)
    {
        DetachedItem? item = null;
        void OnTitleChanged(object? _, string title)
            => item!.Title = string.IsNullOrWhiteSpace(title) ? "Terminal" : title;

        item = new DetachedItem(
            kind,
            string.IsNullOrWhiteSpace(view.HeaderTitle) ? "Terminal" : view.HeaderTitle,
            view,
            icon,
            dispose: () =>
            {
                view.HeaderTitleChanged -= OnTitleChanged;
                _ = view.CloseAsync();
            })
        {
            Return = new DetachReturn(TabEntryKind.Terminal, () =>
            {
                view.HeaderTitleChanged -= OnTitleChanged;
                adoptMainTab(createMainTab());
            })
        };
        view.HeaderTitleChanged += OnTitleChanged;
        return item;
    }
}

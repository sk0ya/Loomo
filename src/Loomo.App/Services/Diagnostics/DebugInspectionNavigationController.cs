using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ／TSデバッグのテスト表示要求とコールスタック移動を調整する。</summary>
internal sealed class DebugInspectionNavigationController(
    TabControl outerTabs,
    Func<bool> isTestsTabSelected,
    Action ensureTestsDiscovered,
    Func<DebugManagerViewModelBase?> resolveViewModel)
{
    public void OnOuterTabChanged(SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, outerTabs) && isTestsTabSelected())
            ensureTestsDiscovered();
    }

    public void ActivateSelectedFrame(MouseButtonEventArgs e)
    {
        if (WpfTreeTraversal.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null)
            return;
        if (resolveViewModel()?.Inspection is { } inspection)
            inspection.ActivateFrame(inspection.SelectedFrame);
    }
}

using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ切替ポップアップの対象選択と、実行・デバッグ操作を調整する。</summary>
internal sealed class DebugSwitcherController(
    Func<DebugViewModel?> resolveViewModel,
    Action close,
    Action<string> run,
    Action openIdePane)
{
    public void PrepareForOpen()
    {
        if (resolveViewModel() is not { } viewModel)
            return;
        viewModel.Refresh();
        viewModel.Profiles.RefreshRunTargets();
    }

    public void Close() => close();

    public void RunCurrent()
    {
        if (resolveViewModel()?.Profiles.SelectedProjectPath is not { } project)
            return;
        close();
        run(project);
    }

    public void SelectCurrentTarget(object sender)
    {
        if (SelectTarget(sender) is not null)
            close();
    }

    public void DebugTarget(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectTarget(sender) is null || resolveViewModel() is not { } viewModel)
            return;
        close();
        if (viewModel.Launch.StartCommand.CanExecute(null))
            viewModel.Launch.StartCommand.Execute(null);
    }

    public void RunTarget(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectTarget(sender) is not { RunProjectPath: { } project })
            return;
        close();
        run(project);
    }

    public void OpenIdePane()
    {
        close();
        openIdePane();
    }

    private DebugRunTargetItem? SelectTarget(object sender)
    {
        if (resolveViewModel() is not { } viewModel
            || sender is not FrameworkElement { Tag: DebugRunTargetItem item })
            return null;
        viewModel.Profiles.SelectRunTarget(item);
        return item;
    }
}

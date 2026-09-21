using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>Shell のサイドバーと独立設定ウィンドウの表示状態を同期する。</summary>
internal sealed class ShellWindowStateController
{
    private const double SplitterThickness = 6;

    private readonly Window _owner;
    private readonly ShellViewModel _viewModel;
    private readonly ColumnDefinition _sidebarColumn;
    private readonly ColumnDefinition _sidebarSplitterColumn;
    private readonly FrameworkElement _sidebarContainer;
    private readonly UIElement _sidebarSplitter;
    private readonly Action<SidebarPanel> _recordTrailPanel;
    private readonly Action _focusSidebar;
    private readonly Action _captureFocusReturnOrigin;
    private readonly Action _restoreFocusReturnOrigin;
    private GridLength _savedSidebarWidth = new(220);
    private SettingsWindow? _settingsWindow;

    public ShellWindowStateController(
        Window owner,
        ShellViewModel viewModel,
        ColumnDefinition sidebarColumn,
        ColumnDefinition sidebarSplitterColumn,
        FrameworkElement sidebarContainer,
        UIElement sidebarSplitter,
        Action<SidebarPanel> recordTrailPanel,
        Action focusSidebar,
        Action captureFocusReturnOrigin,
        Action restoreFocusReturnOrigin)
    {
        _owner = owner;
        _viewModel = viewModel;
        _sidebarColumn = sidebarColumn;
        _sidebarSplitterColumn = sidebarSplitterColumn;
        _sidebarContainer = sidebarContainer;
        _sidebarSplitter = sidebarSplitter;
        _recordTrailPanel = recordTrailPanel;
        _focusSidebar = focusSidebar;
        _captureFocusReturnOrigin = captureFocusReturnOrigin;
        _restoreFocusReturnOrigin = restoreFocusReturnOrigin;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _owner.Closed += OnOwnerClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var effects = ShellPropertyTransitionPolicy.Resolve(_viewModel, e.PropertyName);
        if (effects.ApplySidebarVisibility)
            ApplySidebarVisibility(_viewModel.IsSidebarVisible);
        if (effects.RecordPanelTrail)
            _recordTrailPanel(_viewModel.ActivePanel);
        if (effects.FocusSidebar)
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, _focusSidebar);
        if (effects.ApplySettingsWindowState)
            ApplySettingsWindowState(_viewModel.IsSettingsOverlayOpen);
        if (effects.ActivateSettingsWindow)
            _settingsWindow?.Activate();
    }

    private void ApplySettingsWindowState(bool open)
    {
        if (!open)
        {
            _settingsWindow?.Close();
            return;
        }

        if (_settingsWindow is null)
        {
            _captureFocusReturnOrigin();
            _settingsWindow = new SettingsWindow
            {
                Owner = _owner,
                DataContext = _viewModel,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _viewModel.IsSettingsOverlayOpen = false;
                _restoreFocusReturnOrigin();
            };
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
    }

    private void ApplySidebarVisibility(bool visible)
    {
        if (visible)
        {
            _sidebarColumn.MinWidth = 120;
            _sidebarColumn.Width = _savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(220);
            _sidebarSplitterColumn.Width = new GridLength(SplitterThickness);
            _sidebarContainer.Visibility = Visibility.Visible;
            _sidebarSplitter.Visibility = Visibility.Visible;
            return;
        }

        _savedSidebarWidth = _sidebarColumn.Width;
        _sidebarColumn.MinWidth = 0;
        _sidebarColumn.Width = new GridLength(0);
        _sidebarSplitterColumn.Width = new GridLength(0);
        _sidebarContainer.Visibility = Visibility.Collapsed;
        _sidebarSplitter.Visibility = Visibility.Collapsed;
    }

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _owner.Closed -= OnOwnerClosed;
        _settingsWindow = null;
    }
}

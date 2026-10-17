using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>Shell のサイドバーと独立ウィンドウ（設定・キーボードショートカット）の表示状態を同期する。</summary>
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
    private readonly Action<ActivityBarSlot> _focusSidebar;
    private readonly Action _captureFocusReturnOrigin;
    private readonly Action _restoreFocusReturnOrigin;
    private GridLength _savedSidebarWidth = new(SidebarWidthPolicy.DefaultWidth);
    /// <summary>直前に列が出ていたか。幅の記録・復元をしてよいのは「出ている⇄畳んでいる」が
    /// 実際に切り替わった瞬間だけ——上段と中段のどちらが変わってもこの経路は通るので、
    /// 出たままの再入で書き戻すと、人がスプリッターで広げた今の幅を古い記録で潰してしまう。</summary>
    private bool _sidebarColumnShown;
    private SettingsWindow? _settingsWindow;
    private SettingsWindow? _keybindingsWindow;

    public ShellWindowStateController(
        Window owner,
        ShellViewModel viewModel,
        ColumnDefinition sidebarColumn,
        ColumnDefinition sidebarSplitterColumn,
        FrameworkElement sidebarContainer,
        UIElement sidebarSplitter,
        Action<SidebarPanel> recordTrailPanel,
        Action<ActivityBarSlot> focusSidebar,
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

        var width = viewModel.ActivityBar.SavedState.SidebarWidth;
        _savedSidebarWidth = new GridLength(double.IsFinite(width) && width >= 120
            ? width : SidebarWidthPolicy.DefaultWidth);
        _sidebarColumn.Width = _savedSidebarWidth;
        _sidebarColumnShown = _viewModel.IsSidebarColumnVisible;
        ApplySidebarVisibility(_sidebarColumnShown);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _owner.Closed += OnOwnerClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var effects = ShellPropertyTransitionPolicy.Resolve(_viewModel, e.PropertyName);
        if (effects.ApplySidebarVisibility)
            ApplySidebarVisibility(_viewModel.IsSidebarColumnVisible);
        if (effects.RecordPanelTrail is { } panel)
            _recordTrailPanel(panel);
        if (effects.FocusSidebarSlot is { } slot)
            _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _focusSidebar(slot));
        if (effects.ApplySettingsWindowState)
            ApplySettingsWindowState(_viewModel.IsSettingsOverlayOpen);
        if (effects.ActivateSettingsWindow)
            _settingsWindow?.Activate();
        if (effects.ApplyKeybindingsWindowState)
            ApplyKeybindingsWindowState(_viewModel.IsKeybindingsWindowOpen);
    }

    private void ApplySettingsWindowState(bool open)
        => ApplyToolWindowState(open, ref _settingsWindow,
            () => new SettingsWindow(new SettingsView(), "設定") { DataContext = _viewModel },
            () => _viewModel.IsSettingsOverlayOpen = false);

    private void ApplyKeybindingsWindowState(bool open)
        => ApplyToolWindowState(open, ref _keybindingsWindow,
            () => new SettingsWindow(new KeybindingsView(), "キーボードショートカット")
            {
                DataContext = _viewModel.Keyboard,
                Width = 880,
                Height = 680,
            },
            () => _viewModel.IsKeybindingsWindowOpen = false);

    /// <summary>独立ウィンドウを開く／閉じる。フォーカスの戻り先は、最初の 1 枚を開く前に覚え、
    /// 最後の 1 枚が閉じたときに戻す（両方開いていて片方を閉じただけなら、残った方に居させる）。</summary>
    private void ApplyToolWindowState(bool open, ref SettingsWindow? window,
        Func<SettingsWindow> create, Action markClosed)
    {
        if (!open)
        {
            window?.Close();
            return;
        }

        if (window is null)
        {
            if (_settingsWindow is null && _keybindingsWindow is null)
                _captureFocusReturnOrigin();
            var created = create();
            created.Owner = _owner;
            created.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            created.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, created)) _settingsWindow = null;
                if (ReferenceEquals(_keybindingsWindow, created)) _keybindingsWindow = null;
                markClosed();
                if (_settingsWindow is null && _keybindingsWindow is null)
                    _restoreFocusReturnOrigin();
            };
            window = created;
            created.Show();
        }

        window.Activate();
    }

    private void ApplySidebarVisibility(bool visible)
    {
        var wasShown = _sidebarColumnShown;
        _sidebarColumnShown = visible;

        if (visible)
        {
            _sidebarColumn.MinWidth = 120;
            // 畳んでいたものを開き直すときだけ幅を書き戻す。出たまま区画が入れ替わっただけなら
            // 今の幅がそのまま正しい。
            if (!wasShown)
                _sidebarColumn.Width = SidebarWidthPolicy.Restore(_savedSidebarWidth);
            _sidebarSplitterColumn.Width = new GridLength(SplitterThickness);
            _sidebarContainer.Visibility = Visibility.Visible;
            _sidebarSplitter.Visibility = Visibility.Visible;
            return;
        }

        // 覚えるのも畳む瞬間だけ。二度目に入って 0 を覚えてしまわないようにする。
        if (wasShown)
            _savedSidebarWidth = CurrentSidebarWidth();
        _sidebarColumn.MinWidth = 0;
        _sidebarColumn.Width = new GridLength(0);
        _sidebarSplitterColumn.Width = new GridLength(0);
        _sidebarContainer.Visibility = Visibility.Collapsed;
        _sidebarSplitter.Visibility = Visibility.Collapsed;
    }

    /// <summary>いまの列幅（決め方は <see cref="SidebarWidthPolicy.Remember"/>）。</summary>
    private GridLength CurrentSidebarWidth()
        => SidebarWidthPolicy.Remember(_sidebarColumn.Width, _sidebarColumn.ActualWidth);

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        var width = _sidebarColumnShown ? CurrentSidebarWidth() : _savedSidebarWidth;
        if (width.Value >= 120 && (!_sidebarColumnShown || _sidebarColumn.Width.Value > 0))
            _viewModel.ActivityBar.SavedState.SidebarWidth = width.Value;
        _viewModel.ActivityBar.Persist();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _owner.Closed -= OnOwnerClosed;
        _settingsWindow = null;
        _keybindingsWindow = null;
    }
}

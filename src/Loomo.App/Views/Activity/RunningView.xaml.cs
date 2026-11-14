using System.ComponentModel;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>サイドバーの「動いているもの」面（<see cref="SidebarPanel.Running"/>・§24.54）の中身。</summary>
public partial class RunningView : UserControl
{
    private RunningViewModel? _vm;
    private readonly TerminalPeekPopup _peek = new();

    /// <summary>覗き窓を出す行（押したボタン・右クリックした行）。</summary>
    private UIElement? _peekAnchor;

    public RunningView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as RunningViewModel);
    }

    private void Attach(RunningViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RunningViewModel.Peek) || _vm is null) return;
        if (_vm.Peek is { } peek)
            _peek.Show(_peekAnchor ?? this, peek, () => _vm?.ClosePeek());
        else
            _peek.Close();
    }

    private void OnPeekClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RunningItemViewModel item } button)
            OpenPeek(item, FindRow(button) ?? button);
    }

    private void OnPeekMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RunningItemViewModel item, Parent: ContextMenu { PlacementTarget: UIElement row } })
            OpenPeek(item, row);
    }

    private void OpenPeek(RunningItemViewModel item, UIElement anchor)
    {
        _peekAnchor = anchor;
        _peek.RememberFocus();
        _vm?.RequestPeekCommand.Execute(item);
    }

    /// <summary>ボタンが載っている行（テンプレートの RowRoot）。窓を行の右に揃えて出すため。</summary>
    private static UIElement? FindRow(DependencyObject from)
    {
        for (var current = from; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Grid { Name: "RowRoot" } row)
                return row;
        return null;
    }
}

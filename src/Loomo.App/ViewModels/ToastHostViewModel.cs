using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class ToastEntryViewModel : ObservableObject
{
    public ToastEntryViewModel(Guid id, string message, ToastKind kind)
    {
        Id = id;
        Message = message;
        Kind = kind;
    }

    public Guid Id { get; }
    public string Message { get; }
    public ToastKind Kind { get; }
}

/// <summary>右下に積み上げて表示する非モーダルなトースト通知の一覧。<see cref="ToastService"/> の
/// イベントを購読し、一定時間で自動的に消える（手動で閉じることもできる）。</summary>
public sealed partial class ToastHostViewModel : ObservableObject
{
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(5);

    public ObservableCollection<ToastEntryViewModel> Items { get; } = new();

    public ToastHostViewModel()
    {
        ToastService.Requested += OnRequested;
    }

    private void OnRequested(object? sender, ToastRequestedEventArgs e)
    {
        var entry = new ToastEntryViewModel(Guid.NewGuid(), e.Message, e.Kind);
        Items.Add(entry);

        var timer = new DispatcherTimer { Interval = DisplayDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Items.Remove(entry);
        };
        timer.Start();
    }

    [RelayCommand]
    private void Dismiss(ToastEntryViewModel? entry)
    {
        if (entry is not null)
            Items.Remove(entry);
    }
}

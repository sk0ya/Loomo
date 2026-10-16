using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class ToastEntryViewModel : ObservableObject
{
    public ToastEntryViewModel(Guid id, string message, NotificationKind kind)
    {
        Id = id;
        Message = message;
        Kind = kind;
    }

    public Guid Id { get; }
    public string Message { get; }
    public NotificationKind Kind { get; }
}

/// <summary>右下に積み上げて表示する非モーダルなトースト通知の一覧。<see cref="NotificationService"/> の
/// 知らせのうち <see cref="Notification.ShowToast"/> のものを出し、一定時間で自動的に消える（手動で閉じることもできる）。
/// 消えたあとも軌跡バー右端の通知欄（<see cref="NotificationCenterViewModel"/>）の履歴には残る。</summary>
public sealed partial class ToastHostViewModel : ObservableObject
{
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(5);

    public ObservableCollection<ToastEntryViewModel> Items { get; } = new();

    public ToastHostViewModel()
    {
        NotificationService.Posted += OnPosted;
    }

    private void OnPosted(object? sender, NotificationPostedEventArgs e)
    {
        if (!e.Notification.ShowToast)
            return;
        var entry = new ToastEntryViewModel(e.Notification.Id, e.Notification.Message, e.Notification.Kind);
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

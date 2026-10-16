using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>通知欄の履歴の1行。</summary>
public sealed partial class NotificationEntryViewModel : ObservableObject
{
    public NotificationEntryViewModel(Notification notification) => Notification = notification;

    public Notification Notification { get; }
    public string Message => Notification.Message;
    public NotificationKind Kind => Notification.Kind;
    public string? Source => Notification.Source;
    public bool HasSource => !string.IsNullOrEmpty(Notification.Source);
    public string TimeLabel => Notification.Timestamp.ToString("HH:mm");
    public string? ActionLabel => Notification.Action?.Label;
    public bool HasAction => Notification.Action is not null;

    [ObservableProperty]
    private bool _isRead;
}

/// <summary>軌跡バー右端の通知欄（設計書 §21.5c）。<see cref="NotificationService"/> の知らせを新しい順に持ち、
/// 畳んだ状態では最新1件と未読件数を、開くと履歴を見せる。トーストは5秒で消えるので、見逃したものを
/// 後から確かめられる場所がここ。履歴はセッション内だけ（再起動で空になる）で、古いものから
/// <see cref="Capacity"/> 件を超えた分を捨てる。</summary>
public sealed partial class NotificationCenterViewModel : ObservableObject
{
    public const int Capacity = 100;

    public NotificationCenterViewModel() : this(subscribe: true) { }

    /// <param name="subscribe">false ならグローバルな <see cref="NotificationService.Posted"/> を購読しない
    /// （テストで <see cref="Receive"/> を直接呼ぶ用。静的イベントは並列テスト間で混ざる）。</param>
    public NotificationCenterViewModel(bool subscribe)
    {
        if (subscribe)
            NotificationService.Posted += (_, e) => Receive(e.Notification);
    }

    /// <summary>新しい順。</summary>
    public ObservableCollection<NotificationEntryViewModel> Items { get; } = new();

    public NotificationEntryViewModel? Latest => Items.Count > 0 ? Items[0] : null;
    public bool HasItems => Items.Count > 0;
    public int UnreadCount => Items.Count(i => !i.IsRead);
    public bool HasUnread => UnreadCount > 0;

    /// <summary>未読バッジの文字。2桁を超えたら「99+」で幅を据え置く。</summary>
    public string UnreadBadge => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    /// <summary>畳んだ通知欄に出す1行。</summary>
    public string Summary => Latest?.Message ?? "通知はありません";

    /// <summary>履歴のポップアップが開いているか。開いたら全件を既読にする（見えた＝読んだ）。</summary>
    [ObservableProperty]
    private bool _isOpen;

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
            MarkAllRead();
    }

    public void Receive(Notification notification)
    {
        // 履歴を開いて見ている最中に届いたものは、その場で見えているので既読扱い。
        Items.Insert(0, new NotificationEntryViewModel(notification) { IsRead = IsOpen });
        while (Items.Count > Capacity)
            Items.RemoveAt(Items.Count - 1);
        RaiseSummaryChanged();
    }

    [RelayCommand]
    private void MarkAllRead()
    {
        foreach (var item in Items)
            item.IsRead = true;
        RaiseSummaryChanged();
    }

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        IsOpen = false;
        RaiseSummaryChanged();
    }

    [RelayCommand]
    private void Remove(NotificationEntryViewModel? entry)
    {
        if (entry is null || !Items.Remove(entry))
            return;
        // 最後の1件を消したら欄ごと畳まれる（§21.5c）ので、宙に浮いたポップアップを残さない。
        if (Items.Count == 0)
            IsOpen = false;
        RaiseSummaryChanged();
    }

    /// <summary>通知に添えられた操作を実行する。押したら役目を終えたものとして履歴から外し、ポップアップを閉じる
    /// （同じ「再試行」を二度押せないようにする）。</summary>
    [RelayCommand]
    private void RunAction(NotificationEntryViewModel? entry)
    {
        if (entry?.Notification.Action is not { } action)
            return;
        IsOpen = false;
        Items.Remove(entry);
        RaiseSummaryChanged();
        try { action.Run(); }
        catch (Exception ex) { ToastService.Error($"「{action.Label}」に失敗しました: {ex.Message}"); }
    }

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(Latest));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadBadge));
        OnPropertyChanged(nameof(Summary));
    }
}

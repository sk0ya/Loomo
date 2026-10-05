using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 「留守中に起きたこと」のカード（右下・トーストの上）。§24.22。
/// <para>トーストと違って<b>自動では消えない</b>——離れていた人が戻って最初に読むもので、5秒で消えたら
/// 読む前に無くなる。閉じるのは × か、次の離席から戻ったとき（新しいまとめで置き換わる）。
/// 行を押すと、その出来事の「次の一手」を開く（開き方は ShellWindow が決める）。</para>
/// </summary>
public sealed partial class AwaySummaryViewModel : ObservableObject
{
    public ObservableCollection<AwayItem> Items { get; } = new();

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _header = "";

    [ObservableProperty]
    private string _counts = "";

    /// <summary>行が押された（ShellWindow が種類ごとの開き方を持つ）。</summary>
    public event EventHandler<AwayItem>? ItemOpenRequested;

    public void Show(AwaySummary summary)
    {
        Items.Clear();
        foreach (var item in summary.Items)
            Items.Add(item);
        Header = summary.Header;
        Counts = summary.Counts;
        IsVisible = Items.Count > 0;
    }

    [RelayCommand]
    private void Close()
    {
        IsVisible = false;
        Items.Clear();
    }

    [RelayCommand]
    private void Open(AwayItem? item)
    {
        if (item is not null)
            ItemOpenRequested?.Invoke(this, item);
    }
}

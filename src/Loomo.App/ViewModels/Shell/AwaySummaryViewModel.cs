using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 「留守中に起きたこと」のカード（右下・トーストの上）。§24.22。
/// <para>トーストと違って<b>自動では消えない</b>——離れていた人が戻って最初に読むもので、5秒で消えたら
/// 読む前に無くなる。閉じるのは ×・Esc、手を付けずに置いた時間（留守中のまとめだけ・ShellWindow が数える）、
/// 次の離席から戻ったとき（新しいまとめで置き換わる）。
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

    /// <summary>「コピー」が押された（中身は <see cref="AwaySummary.ToMarkdown"/>。クリップボードは ShellWindow が触る）。</summary>
    public event EventHandler<string>? CopyRequested;

    private AwaySummary? _summary;

    /// <summary>直前の留守中のまとめ（§24.22）。閉じた後でも「もう一度表示」で出し直せるよう、この日のまとめ
    /// （§24.23）で置き換わっても別に覚えておく。</summary>
    public AwaySummary? LastAway { get; private set; }

    /// <summary>戻ったときに<b>向こうから</b>出したまとめ。手を付けずに置けば消え、Esc でも閉じる。自分で開いた
    /// もの（「この日のまとめ」§24.23・「もう一度表示」）は読みに来たものなので、× で閉じるまで残す。</summary>
    public bool AutoDismiss { get; private set; }

    /// <summary>ポインタがカードの上にある（読んでいる最中なので自動では消さない）。カードが立てる。</summary>
    [ObservableProperty]
    private bool _isPointerOver;

    /// <param name="onRequest">人が自分で開いた（「もう一度表示」・この日のまとめ）。自動では消さない。</param>
    public void Show(AwaySummary summary, bool onRequest = false)
    {
        _summary = summary;
        AutoDismiss = summary.Title is null && !onRequest;
        if (summary.Title is null)
            LastAway = summary;
        Items.Clear();
        foreach (var item in summary.Items)
            Items.Add(item);
        Header = summary.Header;
        Counts = summary.Counts;
        IsVisible = Items.Count > 0;
        Shown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>まとめを出した（置き換えも含む。自動で消えるまでの時間を数え直す合図）。</summary>
    public event EventHandler? Shown;

    [RelayCommand]
    private void Copy()
    {
        if (_summary is { IsEmpty: false } summary)
            CopyRequested?.Invoke(this, summary.ToMarkdown());
    }

    [RelayCommand]
    private void Close()
    {
        // 畳んだカードには MouseLeave が届かないことがある。立ったままだと次のまとめが自動で消えなくなる
        IsPointerOver = false;
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

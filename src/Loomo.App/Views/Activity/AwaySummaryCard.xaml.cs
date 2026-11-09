namespace sk0ya.Loomo.App.Views;

/// <summary>「留守中に起きたこと」のカード（§24.22）。中身は <see cref="AwaySummaryViewModel"/>。</summary>
public partial class AwaySummaryCard : UserControl
{
    public AwaySummaryCard()
    {
        InitializeComponent();
        // ポインタが乗っている間は読んでいる最中——自動で消える時間を数えない
        MouseEnter += (_, _) => SetPointerOver(true);
        MouseLeave += (_, _) => SetPointerOver(false);
        // 中身の VM が替わったら、前の VM の印は下ろし、新しい VM にはいまの状態を渡す
        DataContextChanged += (_, e) => {
            if (e.OldValue is AwaySummaryViewModel old)
                old.IsPointerOver = false;
            SetPointerOver(IsMouseOver);
        };
    }

    private void SetPointerOver(bool value)
    {
        if (DataContext is AwaySummaryViewModel vm)
            vm.IsPointerOver = value;
    }
}

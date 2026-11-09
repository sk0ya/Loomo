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
    }

    private void SetPointerOver(bool value)
    {
        if (DataContext is AwaySummaryViewModel vm)
            vm.IsPointerOver = value;
    }
}

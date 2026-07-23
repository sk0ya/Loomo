namespace sk0ya.Loomo.App.ViewModels;

/// <summary>テストエクスプローラのビュー（DebugTestsView）が DataContext に要求する最小の窓口。
/// dotnet 版（<see cref="DebugTestsViewModel"/>）と TS 版（<see cref="TsDebugTestsViewModel"/>）が実装し、
/// 同じビューを両ペインで共有する（残りのバインドは同名メンバーで解決）。</summary>
internal interface ITestExplorer
{
    /// <summary>テスト行のダブルクリック：そのテストのソース位置へジャンプする。</summary>
    void NavigateToTestSource(TestItemViewModel? t);

    /// <summary>テストタブが表示されたときの保険的な収集。</summary>
    void EnsureTestsDiscovered();
}

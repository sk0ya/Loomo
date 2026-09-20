using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>テストエクスプローラのビュー（DebugTestsView）と、エディタのガターのテスト実行▶／結果グリフ
/// （<see cref="Services.EditorTestGlyphMap"/>）が DataContext／窓口に要求する最小の面。
/// dotnet 版（<see cref="DebugTestsViewModel"/>）と TS 版（<see cref="TsDebugTestsViewModel"/>）が実装し、
/// 同じビューを両ペインで共有する（残りのバインドは同名メンバーで解決）。</summary>
internal interface ITestExplorer
{
    /// <summary>テスト行のダブルクリック：そのテストのソース位置へジャンプする。</summary>
    void NavigateToTestSource(TestItemViewModel? t);

    /// <summary>テストタブが表示されたときの保険的な収集。</summary>
    void EnsureTestsDiscovered();

    /// <summary>収集済みのテスト（フラットな全件）。エディタのガターへ出すグリフはここから引く。</summary>
    IReadOnlyList<TestItemViewModel> TestItems { get; }

    /// <summary>一覧そのもの、または各行の状態が変わったとき（UI スレッドで発火）。
    /// エディタのガターは <c>LoadFile</c> でグリフを捨てるので、ホストはこれを契機に再送する。</summary>
    event Action? TestsChanged;

    /// <summary>1 件だけ実行する（ガターの ▶／コマンドパレットから）。ビルド中・デバッグ中などで
    /// 実行を始められなかったときは <c>false</c>——押したのに無反応にしないよう、呼び出し側が理由を返す。</summary>
    Task<bool> RunTestAsync(TestItemViewModel test);
}

using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>言語サーバーが作った題名をそのままメニューに載せると、WPF が <c>_</c> を
/// アクセスキー指定と解釈して1文字消す（§32.4）。</summary>
public sealed class MenuHeaderTextTests
{
    [Fact]
    public void 識別子に含まれるアンダースコアが消えない()
        => Assert.Equal("Introduce local for 'foo__bar'", MenuHeaderText.Escape("Introduce local for 'foo_bar'"));

    [Fact]
    public void アンダースコアを含まない題名は変わらない()
        => Assert.Equal("メソッドの抽出", MenuHeaderText.Escape("メソッドの抽出"));

    [Fact]
    public void 連続するアンダースコアもすべて二重化する()
        => Assert.Equal("a____b", MenuHeaderText.Escape("a__b"));
}

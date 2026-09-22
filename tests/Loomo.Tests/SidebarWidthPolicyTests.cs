using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>サイドバー列を畳むときに覚える幅。人がスプリッターで決めた幅（Width）が正で、
/// 実測（ActualWidth）はその瞬間たまたま画面に収まっていた幅でしかない——絶対幅の列でも、
/// 窓を狭めたり袖を出したりして中央列に押されれば Width より細く配置される。</summary>
public sealed class SidebarWidthPolicyTests
{
    [Fact]
    public void 押されて細く配置されていても人が決めた幅を覚える()
    {
        // 400px に広げたあと窓を狭め、200px で配置されている状態で畳む。
        var remembered = SidebarWidthPolicy.Remember(new GridLength(400), actualWidth: 200);

        Assert.Equal(new GridLength(400), remembered);
    }

    [Fact]
    public void 幅がピクセルで取れないときだけ実測へ落ちる()
    {
        Assert.Equal(new GridLength(180), SidebarWidthPolicy.Remember(GridLength.Auto, actualWidth: 180));
        Assert.Equal(new GridLength(180), SidebarWidthPolicy.Remember(new GridLength(1, GridUnitType.Star), 180));
    }

    [Fact]
    public void 畳んだ状態の0幅は覚えない()
    {
        // 二度目に畳む経路を通っても、0 を覚えて列を潰さない。
        Assert.Equal(new GridLength(SidebarWidthPolicy.DefaultWidth),
            SidebarWidthPolicy.Remember(new GridLength(0), actualWidth: 0));
    }

    [Fact]
    public void 覚えていない幅は既定で開き直す()
    {
        Assert.Equal(new GridLength(SidebarWidthPolicy.DefaultWidth),
            SidebarWidthPolicy.Restore(new GridLength(0)));
        Assert.Equal(new GridLength(SidebarWidthPolicy.DefaultWidth),
            SidebarWidthPolicy.Restore(GridLength.Auto));
        Assert.Equal(new GridLength(360), SidebarWidthPolicy.Restore(new GridLength(360)));
    }
}

using System.Windows.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// マウスの戻る/進むボタンの宛先判定（<see cref="MouseNavigationPolicy"/>）の検証。
/// 回帰の核心は「ブラウザペインの上での戻る/進むを EditorSupport へ流さない」こと
/// （ウィンドウ全体で受けている入力なので、ここで取り違えるとブラウザの戻るが効かなくなる）。
/// </summary>
public class MouseNavigationPolicyTests
{
    [Fact]
    public void Resolve_ブラウザの上の戻るはブラウザへ_回帰()
    {
        var command = MouseNavigationPolicy.Resolve(MouseButton.XButton1, PaneKind.Browser);
        Assert.Equal(new MouseNavigationCommand(MouseNavigationTarget.Browser, Back: true), command);
    }

    [Fact]
    public void Resolve_ブラウザの上の進むはブラウザへ_回帰()
    {
        var command = MouseNavigationPolicy.Resolve(MouseButton.XButton2, PaneKind.Browser);
        Assert.Equal(new MouseNavigationCommand(MouseNavigationTarget.Browser, Back: false), command);
    }

    [Theory]
    [InlineData(PaneKind.Editor)]
    [InlineData(PaneKind.EditorSupport)]
    [InlineData(PaneKind.Terminal)]
    [InlineData(null)]
    public void Resolve_ブラウザ以外はEditorSupportのプレビュー履歴へ(PaneKind? pane)
    {
        var command = MouseNavigationPolicy.Resolve(MouseButton.XButton1, pane);
        Assert.Equal(new MouseNavigationCommand(MouseNavigationTarget.EditorSupport, Back: true), command);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public void Resolve_戻る進む以外のボタンには手を出さない(MouseButton button)
    {
        Assert.Null(MouseNavigationPolicy.Resolve(button, PaneKind.Browser));
    }
}

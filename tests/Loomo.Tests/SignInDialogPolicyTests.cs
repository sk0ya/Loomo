using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>サインイン（WAM）のアカウント選択を引き上げる判定（§34.12.1）。窓の構成は実測（Windows 11 23H2）。</summary>
public sealed class SignInDialogPolicyTests
{
    private static readonly (string, string)[] WamPicker =
    [
        ("ApplicationFrameTitleBarWindow", "explorer"),
        ("Windows.UI.Core.CoreWindow", "Microsoft.AAD.BrokerPlugin"),
        ("ApplicationFrameTitleBarWindow", "explorer"),
        ("ApplicationFrameInputSinkWindow", "explorer"),
    ];

    [Fact]
    public void Loomoが前面のときWAMのアカウント選択を引き上げる()
        => Assert.True(SignInDialogPolicy.ShouldRaise(WamPicker, loomoIsForeground: true));

    [Fact]
    public void Loomoが前面でなければ触らない()
        => Assert.False(SignInDialogPolicy.ShouldRaise(WamPicker, loomoIsForeground: false));

    [Fact]
    public void ほかのUWPアプリの枠は引き上げない()
        => Assert.False(SignInDialogPolicy.ShouldRaise(
        [
            ("ApplicationFrameTitleBarWindow", "explorer"),
            ("Windows.UI.Core.CoreWindow", "SystemSettings"),
        ], loomoIsForeground: true));

    [Fact]
    public void 中身がまだ付いていない枠は引き上げない()
        => Assert.False(SignInDialogPolicy.ShouldRaise(
        [
            ("ApplicationFrameTitleBarWindow", "explorer"),
            ("ApplicationFrameInputSinkWindow", "explorer"),
        ], loomoIsForeground: true));

    private static readonly DateTime T0 = new(2026, 11, 11, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 引き上げた直後にLoomoを触ったらもう一度前に出す()
        => Assert.True(SignInDialogPolicy.MayRaiseAgain(T0, 1, T0.AddSeconds(1)));

    [Fact]
    public void しばらく経ってからLoomoへ戻したのは人の選択なので追いかけない()
        => Assert.False(SignInDialogPolicy.MayRaiseAgain(T0, 1, T0.AddSeconds(10)));

    [Fact]
    public void 何度も触るなら取り合いにしない()
        => Assert.False(SignInDialogPolicy.MayRaiseAgain(T0, SignInDialogPolicy.MaxRaises, T0.AddMilliseconds(500)));
}

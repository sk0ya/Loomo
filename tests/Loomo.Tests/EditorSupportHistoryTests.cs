using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 「戻る・進む」ファイル履歴（<see cref="EditorSupportHistory"/>）の検証。ブラウザ相当の back/forward で、
/// 通常遷移は forward を捨てること・連続重複を無視すること・上限で最古を落とすこと・タブを閉じたときの
/// 除去が核心。
/// </summary>
public class EditorSupportHistoryTests
{
    [Fact]
    public void 初期状態は戻る進むともに不可()
    {
        var h = new EditorSupportHistory();
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
        Assert.Null(h.GoBack());
        Assert.Null(h.GoForward());
    }

    [Fact]
    public void Navigate後にGoBackで前ファイルへ戻れる()
    {
        var h = new EditorSupportHistory();
        h.Navigate("a");
        h.Navigate("b");
        h.Navigate("c");

        Assert.True(h.CanGoBack);
        Assert.Equal("b", h.GoBack());
        Assert.Equal("a", h.GoBack());
        Assert.False(h.CanGoBack);
        Assert.Null(h.GoBack());
    }

    [Fact]
    public void GoBack後にGoForwardでやり直せる()
    {
        var h = new EditorSupportHistory();
        h.Navigate("a");
        h.Navigate("b");
        h.Navigate("c");

        Assert.Equal("b", h.GoBack());
        Assert.Equal("a", h.GoBack());
        Assert.True(h.CanGoForward);
        Assert.Equal("b", h.GoForward());
        Assert.Equal("c", h.GoForward());
        Assert.False(h.CanGoForward);
        Assert.Null(h.GoForward());
    }

    [Fact]
    public void 戻った後の新規Navigateはforwardを捨てる()
    {
        var h = new EditorSupportHistory();
        h.Navigate("a");
        h.Navigate("b");
        h.Navigate("c");
        h.GoBack();            // current=b, forward=[c]
        Assert.True(h.CanGoForward);

        h.Navigate("d");       // forward 破棄・current=d
        Assert.False(h.CanGoForward);
        Assert.Equal("b", h.GoBack());
    }

    [Fact]
    public void 連続する同一パスは記録しない()
    {
        var h = new EditorSupportHistory();
        h.Navigate("a");
        h.Navigate("a");       // 同一連続 → 無視
        h.Navigate("A");       // 大小無視でも同一 → 無視
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void 空やnullは記録しない()
    {
        var h = new EditorSupportHistory();
        h.Navigate(null);
        h.Navigate("");
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void 上限を超えると最古が落ちる()
    {
        var h = new EditorSupportHistory(capacity: 2);
        h.Navigate("a");
        h.Navigate("b");
        h.Navigate("c");
        h.Navigate("d");       // back=[b,c]（a は上限で脱落）、current=d

        Assert.Equal("c", h.GoBack());
        Assert.Equal("b", h.GoBack());
        Assert.False(h.CanGoBack); // a は残っていない
    }

    [Fact]
    public void Removeで閉じたファイルを履歴から除く()
    {
        var h = new EditorSupportHistory();
        h.Navigate("a");
        h.Navigate("b");
        h.Navigate("c");       // back=[a,b], current=c

        h.Remove("B");         // 大小無視で b を除去
        Assert.Equal("a", h.GoBack());
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void Removeで現在ファイルを消すと以降のNavigateがbackへ積まない()
    {
        var h = new EditorSupportHistory();
        h.Navigate("a");       // current=a
        h.Remove("a");         // current=null
        h.Navigate("b");       // current だった a は積まれない
        Assert.False(h.CanGoBack);
    }

    [Fact]
    public void Clearで全消去()
    {
        var h = new EditorSupportHistory();
        h.Navigate("a");
        h.Navigate("b");
        h.GoBack();
        h.Clear();
        Assert.False(h.CanGoBack);
        Assert.False(h.CanGoForward);
    }
}

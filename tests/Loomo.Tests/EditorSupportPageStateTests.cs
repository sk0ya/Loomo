using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// WebView2 に載っているページの状態機械。更新ループが正しく回っていても、ここが
/// 「差し替え先はまだある」と誤って答えれば<b>投げたメッセージは誰にも届かず、ペインは古い表示のまま</b>になる。
/// ＝ EditorSupport が固まるもう半分の原因。CoreWebView2 を抱えていて触れなかったので切り出した。
/// </summary>
public class EditorSupportPageStateTests
{
    private static EditorSupportPageId Page(string key) => new(null, key);
    private static EditorSupportPageId Uri(string uri) => new(uri, null);

    [Fact]
    public void 読み込み中は本文差し替えを許さない()
    {
        var state = new EditorSupportPageState();
        state.BeginLoad(Page("a"));

        // ナビゲートを投げただけの段階で setBody を送っても、ページがまだ無いので何も起きない
        // ＝古い表示のまま固まる。鍵は「読み終えている」ときだけ返す。
        Assert.Null(state.ReadyPageKey);
        Assert.False(state.CanPatchBody("a"));
    }

    [Fact]
    public void 読み込み完了後は同じ鍵のときだけ本文差し替えできる()
    {
        var state = new EditorSupportPageState();
        state.BeginLoad(Page("a"));
        state.Completed(success: true);

        Assert.Equal("a", state.ReadyPageKey);
        Assert.True(state.CanPatchBody("a"));
        Assert.False(state.CanPatchBody("b"));    // 別ファイル・別テーマ＝土台が違う
        Assert.False(state.CanPatchBody(null));   // 鍵の無い提供者へは差し替えない
    }

    [Fact]
    public void 失敗したページは同一性を捨てるので次は必ず作り直しになる()
    {
        var state = new EditorSupportPageState();
        state.BeginLoad(Page("a"));
        state.Completed(success: true);
        state.BeginLoad(Page("a"));
        state.Completed(success: false);

        Assert.Equal(EditorSupportPageStatus.Failed, state.Status);
        Assert.Null(state.ReadyPageKey);
        Assert.False(state.CanPatchBody("a"));
    }

    [Fact]
    public void 初回だけは成功しても組み直しを頼む()
    {
        // WebView2 は最初のページを載せても描画が出てこないことがある（＝真っ白なまま固まる）。
        var state = new EditorSupportPageState();

        state.BeginLoad(Page("a"));
        Assert.Equal(EditorSupportPageAction.ReloadCurrentPage, state.Completed(success: true));

        Assert.True(state.BeginCurrentPageReload());
        Assert.Null(state.ReadyPageKey); // 再読込中は本文差し替えを許さない

        state.BeginLoad(Page("b"));
        Assert.Equal(EditorSupportPageAction.None, state.Completed(success: true));
    }

    [Fact]
    public void インメモリページはReloadではなくフル再構築する()
    {
        var state = new EditorSupportPageState();

        state.BeginLoad(new EditorSupportPageId(null, "a", CanReload: false));

        Assert.Equal(EditorSupportPageAction.RequestReload, state.Completed(success: true));
    }

    [Fact]
    public void 同じページの二度目の失敗では組み直しを頼まない()
    {
        var state = FirstRenderHealed();

        state.BeginLoad(Uri("file:///broken.pdf"));
        Assert.Equal(EditorSupportPageAction.RequestReload, state.Completed(success: false));

        state.BeginLoad(Uri("file:///broken.pdf"));
        // 失敗 → 組み直し → また失敗、を延々と繰り返さない（回り続けるのも固まって見える）。
        Assert.Equal(EditorSupportPageAction.None, state.Completed(success: false));
    }

    [Fact]
    public void 別のページなら失敗の記憶を持ち越さない()
    {
        var state = FirstRenderHealed();
        state.BeginLoad(Uri("file:///broken.pdf"));
        state.Completed(success: false);

        state.BeginLoad(Uri("file:///other.pdf"));

        Assert.Equal(EditorSupportPageAction.RequestReload, state.Completed(success: false));
    }

    [Fact]
    public void 完了イベントが来ないまま見張りが鳴ったら失敗として畳む()
    {
        // WebView2 の完了イベントは（プロセス落ち・不正 URI・描画中断で）来ないことがある。
        // Loading のまま放置すると同じページを二度と読み直さない＝固まる。
        var state = FirstRenderHealed();
        state.BeginLoad(Page("a"));

        Assert.Equal(EditorSupportPageAction.RequestReload, state.WatchdogFired());
        Assert.Equal(EditorSupportPageStatus.Failed, state.Status);
        Assert.Null(state.ReadyPageKey);
    }

    [Fact]
    public void 読み込み済みのページでは見張りが鳴っても何もしない()
    {
        var state = FirstRenderHealed();
        state.BeginLoad(Page("a"));
        state.Completed(success: true);

        // 完了後に鳴った古いタイマー（止め損ね）で、成立している表示を壊さない。
        Assert.Equal(EditorSupportPageAction.None, state.WatchdogFired());
        Assert.Equal("a", state.ReadyPageKey);
    }

    [Fact]
    public void 読み終えた同じURIだけ再ナビゲートを省く()
    {
        var state = new EditorSupportPageState();
        state.BeginLoad(Uri("file:///doc.pdf"));

        Assert.False(state.IsShowing("file:///doc.pdf"));   // まだ読み込み中：省かない
        state.Completed(success: true);
        Assert.True(state.IsShowing("FILE:///DOC.PDF"));    // 大小は問わない
        Assert.False(state.IsShowing("file:///other.pdf"));

        state.BeginLoad(Uri("file:///doc.pdf"));
        state.Completed(success: false);
        // 失敗した PDF がガードに引っかかって永久に読み直されない、を潰す。
        Assert.False(state.IsShowing("file:///doc.pdf"));
    }

    [Fact]
    public void ビューを張り替えたら載せていたページを忘れる()
    {
        var state = FirstRenderHealed();
        state.BeginLoad(Page("a"));
        state.Completed(success: true);

        state.Reset();

        Assert.Equal(EditorSupportPageStatus.Idle, state.Status);
        Assert.Null(state.ReadyPageKey);
        Assert.False(state.CanPatchBody("a"));
    }

    /// <summary>初回描画の取りこぼし対策（1回だけの組み直し）を消化した状態。</summary>
    private static EditorSupportPageState FirstRenderHealed()
    {
        var state = new EditorSupportPageState();
        state.BeginLoad(Page("warmup"));
        state.Completed(success: true);
        return state;
    }
}

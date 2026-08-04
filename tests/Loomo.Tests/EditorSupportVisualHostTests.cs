using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ビジュアル提供者を「工場」と「表示インスタンス」に分けた契約のテスト。
/// ここが崩れると、切り離しウィンドウでの複製（単一親制約）と、
/// UI スレッドでの重い読み込み（＝アプリが固まる）が戻ってくる。
/// </summary>
public class EditorSupportVisualHostTests
{
    [Fact]
    public void Each_host_gets_its_own_visual_so_two_surfaces_can_show_it_at_once() => RunSta(() =>
    {
        var provider = new FakeProvider();
        using var pane = new EditorSupportVisualHost();
        using var detached = new EditorSupportVisualHost();

        var inPane = pane.GetOrCreate(provider);
        var inDetached = detached.GetOrCreate(provider);

        Assert.NotSame(inPane, inDetached);
        Assert.NotSame(inPane.View, inDetached.View);   // 同じ要素を2か所へ載せると WPF が落ちる
    });

    [Fact]
    public void Same_host_reuses_one_visual_per_provider() => RunSta(() =>
    {
        var provider = new FakeProvider();
        using var host = new EditorSupportVisualHost();

        Assert.Same(host.GetOrCreate(provider), host.GetOrCreate(provider));
        Assert.Equal(1, provider.Created);
    });

    [Fact]
    public void Search_highlight_reaches_visuals_created_later() => RunSta(() =>
    {
        // 実体は遅延生成なので、条件を配った時点でまだ存在しない実体がある。
        // ホストが条件を覚えていないと、その実体だけ永久に塗られない。
        var provider = new FakeProvider();
        using var host = new EditorSupportVisualHost();

        host.SetSearchHighlight("needle", caseSensitive: true, useRegex: false);
        var visual = (FakeVisual)host.GetOrCreate(provider);

        Assert.Equal("needle", visual.Term);
        Assert.True(visual.CaseSensitive);
    });

    [Fact]
    public void Search_highlight_reaches_visuals_created_earlier() => RunSta(() =>
    {
        var provider = new FakeProvider();
        using var host = new EditorSupportVisualHost();
        var visual = (FakeVisual)host.GetOrCreate(provider);

        host.SetSearchHighlight("later", caseSensitive: false, useRegex: true);

        Assert.Equal("later", visual.Term);
        Assert.True(visual.UseRegex);
    });

    [Fact]
    public void Content_edits_are_forwarded_to_the_owning_surface() => RunSta(() =>
    {
        var provider = new FakeProvider();
        EditorSupportContentEdited? received = null;
        using var host = new EditorSupportVisualHost((_, e) => received = e);

        var visual = (FakeVisual)host.GetOrCreate(provider);
        visual.RaiseEdit(new EditorSupportContentEdited("a.csv", "edited"));

        Assert.Equal("edited", received?.Text);
    });

    [Fact]
    public void Dispose_releases_every_visual() => RunSta(() =>
    {
        var provider = new FakeProvider();
        var host = new EditorSupportVisualHost();
        var visual = (FakeVisual)host.GetOrCreate(provider);

        host.Dispose();

        Assert.True(visual.Disposed);
        Assert.Empty(host.Visuals);
    });

    /// <summary>WPF 要素を作るので STA が要る（本番も UI スレッドで作られる）。</summary>
    private static void RunSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex is not null) throw ex;
    }

    private sealed class FakeProvider : IEditorSupportVisualProvider
    {
        public int Created { get; private set; }
        public IReadOnlyCollection<string> SupportedExtensions => [".fake"];
        public string DescribeTitle(string filePath) => "Fake";
        public IEditorSupportVisual CreateVisual()
        {
            Created++;
            return new FakeVisual();
        }
    }

    private sealed class FakeVisual : IEditorSupportVisual, IEditorSupportSearchHighlightTarget
    {
        public FrameworkElement View { get; } = new Grid();
        public string? Term { get; private set; }
        public bool CaseSensitive { get; private set; }
        public bool UseRegex { get; private set; }
        public bool Disposed { get; private set; }

        public event EventHandler<EditorSupportContentEdited>? ContentEdited;

        public Task<Action> PrepareAsync(string filePath, string text, CancellationToken ct)
            => Task.FromResult<Action>(() => { });

        public void ApplySearchHighlight(string term, bool caseSensitive, bool useRegex)
        {
            Term = term;
            CaseSensitive = caseSensitive;
            UseRegex = useRegex;
        }

        public void RaiseEdit(EditorSupportContentEdited e) => ContentEdited?.Invoke(this, e);

        public void Dispose() => Disposed = true;
    }
}

using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.App.Services;

/// <summary>パスをどう見せるか（§44.2「パスから開く」）。</summary>
internal enum ViewerTargetKind
{
    /// <summary>そのパスが無い。</summary>
    Missing,
    /// <summary>HTML の頁（<c>.html</c>・<c>index.html</c> のあるフォルダー）。そのまま出す。</summary>
    Page,
    /// <summary>EditorSupport が見せられる形式（PDF・画像・Markdown・CSV…）。同じ表示で出す。</summary>
    Preview,
    /// <summary>UI のあるプログラム（<c>.exe</c>）。起動して窓をペインに重ねる。</summary>
    Program,
    /// <summary>Claude Code が作った、拡張子ごとの Viewer で見せる（作ってあるとき。フォルダーは作るかを聞く）。</summary>
    Generated,
    /// <summary>見せ方の決まっていないテキスト。エディタに読むだけで出す（見せ方は頼めば Claude Code が作る）。</summary>
    Text,
    /// <summary>http(s) の URL。ブラウザと同じ中身をタブに出す。</summary>
    Web,
}

/// <param name="Page">頁として出すファイル（フォルダーを渡したときは中の <c>index.html</c>）。</param>
/// <param name="ViewerId">Generated のときの Viewer の id（<see cref="ViewerPath.IdFor"/>）。</param>
internal sealed record ViewerTarget(ViewerTargetKind Kind, string Path, string? Page = null, string? ViewerId = null)
{
    public bool IsDirectory => Directory.Exists(Path);
}

/// <summary>渡されたパスの見せ方を決める。ファイルの中身は読まない（拡張子・存在・EditorSupport の受け持ちだけ）。</summary>
internal static class ViewerTargets
{
    /// <summary>入力欄やドロップの文字列をフルパスへ（前後の空白・引用符、<c>%VAR%</c>）。解けなければ null。</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"').Trim());
        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile)
            text = uri.LocalPath;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(text))
                return null;
            var full = System.IO.Path.GetFullPath(text);
            var root = System.IO.Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>タブの題（ファイル名・フォルダー名。ドライブそのものはそのまま）。</summary>
    public static string NameOf(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : path;
    }

    /// <summary>http(s) の URL なら、その形のまま返す。</summary>
    public static string? WebUrl(string? input)
    {
        var text = input?.Trim().Trim('"').Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    /// <param name="store">あれば、拡張子ごとに作った Viewer を先に使う。</param>
    public static ViewerTarget Classify(string path, EditorSupportResolver? resolver, ViewerStore? store = null)
    {
        if (WebUrl(path) is { } url)
            return new ViewerTarget(ViewerTargetKind.Web, url);
        if (Directory.Exists(path))
        {
            var index = System.IO.Path.Combine(path, "index.html");
            return File.Exists(index)
                ? new ViewerTarget(ViewerTargetKind.Page, path, index)
                : new ViewerTarget(ViewerTargetKind.Generated, path, ViewerId: ViewerPath.IdFor(path, isDirectory: true));
        }
        if (!File.Exists(path))
            return new ViewerTarget(ViewerTargetKind.Missing, path);

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".html" or ".htm" or ".xhtml")
            return new ViewerTarget(ViewerTargetKind.Page, path, path);
        if (ext == ".exe")
            return new ViewerTarget(ViewerTargetKind.Program, path);
        // EditorSupport が受け持つ形式。バイナリの受け皿（Hex）と、エディタの中身に頼るコードのアウトラインは除く。
        var selection = resolver?.Resolve(path);
        if (selection is { Kind: EditorSupportKind.Provider, Provider: { } provider } && provider is not HexEditorSupport)
            return new ViewerTarget(ViewerTargetKind.Preview, path);
        // 見せ方の決まっていないもの：その拡張子の Viewer を作ってあればそれ、無ければテキストはエディタ・バイナリは Hex。
        var id = ViewerPath.IdFor(path, isDirectory: false);
        if (store?.Find(id).Manifest is not null)
            return new ViewerTarget(ViewerTargetKind.Generated, path, ViewerId: id);
        return selection?.Provider is HexEditorSupport
            ? new ViewerTarget(ViewerTargetKind.Preview, path, ViewerId: id)
            : new ViewerTarget(ViewerTargetKind.Text, path, ViewerId: id);
    }

    /// <summary>入力欄の下に出す「どう出すか」の 1 行。</summary>
    public static string Describe(ViewerTarget target, ViewerStore store) => target.Kind switch
    {
        ViewerTargetKind.Missing => "そのパスは見つかりません",
        ViewerTargetKind.Page => "HTML の頁としてそのまま出します（部屋の API は使えません。ネットには出られます）",
        ViewerTargetKind.Preview when target.ViewerId is not null => "バイナリとして Hex で出します（見せ方は Claude Code に作ってもらうこともできます）",
        ViewerTargetKind.Preview => "EditorSupport と同じ表示で出します",
        ViewerTargetKind.Text => "テキストとしてエディタに出します（読むだけ。見せ方は Claude Code に作ってもらうこともできます）",
        ViewerTargetKind.Web => "ブラウザと同じ中身をこのタブに出します",
        ViewerTargetKind.Program => "プログラムを起動して、その窓を Viewer のタブに重ねます",
        ViewerTargetKind.Generated when target.ViewerId is { } id && store.Find(id).Manifest is { } manifest
            => $"前に作った Viewer「{manifest.Title}」で出します",
        ViewerTargetKind.Generated => target.IsDirectory
            ? "フォルダーを見せる Viewer はまだありません（開いたあと、Claude Code に作ってもらえます）"
            : $"Claude Code に、{ExtensionLabel(target.Path)} を見せる Viewer を作ってもらいます（数十秒〜数分）",
        _ => "",
    };

    public static string ExtensionLabel(string path)
        => System.IO.Path.GetExtension(path) is { Length: > 1 } ext ? ext.ToLowerInvariant() + " のファイル" : "拡張子の無いファイル";
}

/// <summary>
/// Viewer のタブの共通部分：載せる先（<see cref="Host"/>）・見えているか・出来事。<see cref="Rehost"/> で別の入れ物
/// （ペイン ⇄ 切り離した窓）へ移せる——WebView2（コンポジション版）は窓をまたいで載せ替えると空表示になるので、
/// 移すときは中身を捨てて、移った先で見えたときに作り直す。
/// </summary>
internal abstract class ViewerTabBase : IViewerTab
{
    protected ViewerTabBase(string? targetPath, Panel host, Dispatcher dispatcher)
    {
        TargetPath = targetPath;
        Host = host;
        Dispatcher = dispatcher;
        Host.IsVisibleChanged += OnHostVisibleChanged;
    }

    protected Panel Host { get; private set; }
    protected Dispatcher Dispatcher { get; }
    protected bool Visible { get; private set; }
    protected bool Disposed { get; private set; }

    /// <summary>前に出ていて、入れ物も見えている（中身を作ってよい）。</summary>
    protected bool CanShow => Visible && Host.IsVisible && !Disposed;

    public Guid TabId { get; } = Guid.NewGuid();
    public string? TargetPath { get; }
    public virtual string Key => TargetPath is { } path ? ViewerPath.KeyOf(path) : TabId.ToString();
    public virtual string Title => TargetPath is { } path ? ViewerTargets.NameOf(path) : "Viewer";
    public virtual string ToolTip => TargetPath ?? Title;
    public ViewerTabStatus Status { get; private set; } = ViewerTabStatus.Pending;

    public event EventHandler? Changed;
    public event EventHandler? CloseRequested;

    /// <summary>「Claude Code に見せ方を作ってもらう」を押した。差し替えは入れ物（ペイン・切り離した窓）がする。</summary>
    public event EventHandler? GenerateRequested;

    /// <summary>見せ方を Claude Code に作ってもらえるタブか（テキスト・Hex で出しているもの）。</summary>
    public virtual bool CanGenerate => false;

    public void RequestGenerate() => GenerateRequested?.Invoke(this, EventArgs.Empty);

    protected void SetStatus(ViewerTabStatus status)
    {
        Status = status;
        RaiseChanged();
    }

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    protected void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void SetVisible(bool visible)
    {
        Visible = visible;
        OnShownChanged();
    }

    /// <summary>別の入れ物へ移す（中身は捨て、見えたときに作り直す）。</summary>
    public void Rehost(Panel host)
    {
        Unmount();
        Host.IsVisibleChanged -= OnHostVisibleChanged;
        Host = host;
        Host.IsVisibleChanged += OnHostVisibleChanged;
        OnShownChanged();
    }

    private void OnHostVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => OnShownChanged();

    /// <summary>見える・見えないが変わった：見えるなら中身を作って出し、見えないなら隠す。</summary>
    protected abstract void OnShownChanged();

    /// <summary>入れ物から中身を外して捨てる。</summary>
    protected abstract void Unmount();

    public virtual Task RefreshAsync(bool reload = false) => Task.CompletedTask;
    public virtual Task PeekAsync() => Task.CompletedTask;
    public virtual void Focus() { }
    public abstract UIElement? BuildCard();

    public virtual void Dispose()
    {
        if (Disposed)
            return;
        Disposed = true;
        Host.IsVisibleChanged -= OnHostVisibleChanged;
        Unmount();
        Changed = null;
        CloseRequested = null;
        GenerateRequested = null;
    }
}

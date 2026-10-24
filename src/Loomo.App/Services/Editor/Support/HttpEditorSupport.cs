using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Http;

namespace sk0ya.Loomo.App.Services;

/// <summary>応答を部屋の素材にする依頼（ペグボードへ・エディタで開く）。<see cref="Title"/> は出典の1行。</summary>
public sealed record HttpResponseMaterial(string Title, string Text, HttpExchangeResult Result);

/// <summary>
/// .http / .rest（REST Client・JetBrains HTTP Client と同じ書式）を EditorSupport に置く提供者（§24.30）。
/// 左にファイルの中のリクエスト一覧、押すと送って下に応答を出す。
/// <para>
/// <b>送受信と応答はこの工場（DI シングルトン）が持つ</b>——表示インスタンスは1表示面ぶんの見た目だけ。
/// ペインと切り離しウィンドウが同じファイルを出していても、送ったら両方に同じ応答が出るし、
/// ペインを閉じて開き直しても（実体が作り直されても）最後の応答は残る。<c># @name</c> の応答を後の
/// リクエストから引く（<c>{{login.response.body.$.token}}</c>）のもここが覚えている応答から。
/// </para>
/// </summary>
public sealed class HttpEditorSupport : IEditorSupportVisualProvider, IDisposable
{
    private static readonly string[] Extensions = [".http", ".rest"];

    private readonly HttpRequestRunner _runner = new();
    private readonly Dictionary<(string File, string Key), HttpExchangeResult> _results = new();
    private readonly Dictionary<(string File, string Key), CancellationTokenSource> _running = new();

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"HTTP: {Path.GetFileName(filePath)}";

    public IEditorSupportVisual CreateVisual() => new HttpRequestVisual(this);

    /// <summary>送り始めた・応答が届いた（表示インスタンスはこれで行を更新する）。UI スレッドで発火する。</summary>
    public event EventHandler<HttpRequestStateChanged>? StateChanged;

    /// <summary>応答をペグボードへ残したい。</summary>
    public event EventHandler<HttpResponseMaterial>? PegboardRequested;

    /// <summary>応答の本文をエディタの新しいタブで開きたい。</summary>
    public event EventHandler<HttpResponseMaterial>? OpenInEditorRequested;

    /// <summary>応答の中の URL をブラウザペインで開きたい（§24.30.1）。</summary>
    public event EventHandler<string>? OpenUrlRequested;

    /// <summary>応答の中の URL を、同じ .http へ GET として書き足したい（§24.30.1）。</summary>
    public event EventHandler<HttpAppendRequest>? AppendRequestRequested;

    /// <summary>リクエストを呼び分ける鍵。名前があれば名前、無ければメソッドと URL（同じものが並べば何番目か）。</summary>
    public static string KeyOf(HttpRequestDocument document, HttpRequestBlock block)
    {
        if (block.Name is { } name)
            return "@" + name;
        var plain = $"{block.Method} {block.Url}";
        var occurrence = document.Requests.Take(block.Index)
            .Count(r => r.Name is null && $"{r.Method} {r.Url}" == plain);
        return occurrence == 0 ? plain : $"{plain} ({occurrence + 1})";
    }

    public HttpExchangeResult? LastResult(string filePath, string key)
        => _results.TryGetValue((Normalize(filePath), key), out var result) ? result : null;

    public bool IsRunning(string filePath, string key) => _running.ContainsKey((Normalize(filePath), key));

    /// <summary>送る。同じリクエストが走っていれば取り消して送り直す。</summary>
    public async Task<HttpExchangeResult?> SendAsync(string filePath, HttpRequestDocument document, HttpRequestBlock block)
    {
        var file = Normalize(filePath);
        var key = KeyOf(document, block);
        Cancel(filePath, key);
        using var cts = new CancellationTokenSource();
        _running[(file, key)] = cts;
        Raise(file, key);
        try
        {
            var folder = Path.GetDirectoryName(file);
            var dotenv = LoadDotEnv(folder);
            var resolver = new HttpVariableResolver(
                document.Variables,
                dotenv: name => dotenv.TryGetValue(name, out var v) ? v : null,
                responses: name => document.Find(name) is { } named
                    && LastResult(file, KeyOf(document, named)) is { StatusCode: not null } r
                        ? r.ToNamedResponse()
                        : null);
            var prepared = HttpRequestRunner.Prepare(block, resolver, folder);
            var result = await _runner.SendAsync(prepared, cts.Token);
            // 取り消されて別の送信に置き換わったなら、古い結果で新しい方を上書きしない
            if (_running.TryGetValue((file, key), out var current) && ReferenceEquals(current, cts))
                _results[(file, key)] = result;
            return result;
        }
        finally
        {
            if (_running.TryGetValue((file, key), out var current) && ReferenceEquals(current, cts))
                _running.Remove((file, key));
            Raise(file, key);
        }
    }

    public void Cancel(string filePath, string key)
    {
        if (_running.Remove((Normalize(filePath), key), out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    internal void RequestPegboard(HttpResponseMaterial material) => PegboardRequested?.Invoke(this, material);

    internal void RequestOpenInEditor(HttpResponseMaterial material) => OpenInEditorRequested?.Invoke(this, material);

    internal void RequestOpenUrl(string url) => OpenUrlRequested?.Invoke(this, url);

    internal void RequestAppend(HttpAppendRequest request) => AppendRequestRequested?.Invoke(this, request);

    /// <summary>キャレットの行のリクエストを送るよう、そのファイルを出している表示へ頼む（パレット・§24.30）。
    /// 表示がまだ無くても、この工場で送るので応答は残り、表示が作られたときに出る。</summary>
    public async Task<HttpExchangeResult?> SendAtLineAsync(string filePath, string text, int line)
    {
        var document = HttpRequestFile.Parse(text);
        if (document.RequestAt(line) is not { } block)
            return null;
        FocusRequested?.Invoke(this, new HttpRequestStateChanged(Normalize(filePath), KeyOf(document, block)));
        return await SendAsync(filePath, document, block);
    }

    /// <summary>この行を選んで見せてほしい（キャレットから送ったとき）。</summary>
    public event EventHandler<HttpRequestStateChanged>? FocusRequested;

    private void Raise(string file, string key) => StateChanged?.Invoke(this, new HttpRequestStateChanged(file, key));

    private static Dictionary<string, string> LoadDotEnv(string? folder)
    {
        if (folder is null)
            return new();
        var path = Path.Combine(folder, ".env");
        try
        {
            return File.Exists(path)
                ? new Dictionary<string, string>(DotEnvFile.Parse(File.ReadAllText(path)))
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    internal static string Normalize(string filePath)
    {
        try { return Path.GetFullPath(filePath).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return filePath.ToLowerInvariant();
        }
    }

    public void Dispose()
    {
        foreach (var cts in _running.Values)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _running.Clear();
        _runner.Dispose();
    }
}

/// <summary>どのファイルのどのリクエストの状態が変わったか（<see cref="HttpEditorSupport.Normalize"/> 済みのパス）。</summary>
public sealed record HttpRequestStateChanged(string File, string Key);

/// <summary>どの .http の、どのリクエストの後ろへ、どの URL を足すか。</summary>
/// <param name="Key">区画の鍵（<see cref="HttpEditorSupport.KeyOf"/>）。表示の行は打鍵に遅れうるので、書き足す側がいまの本文で引き直す。</param>
public sealed record HttpAppendRequest(string File, string Key, HttpRequestBlock After, string Url);

/// <summary>.http の1表示面。見た目は <see cref="Views.HttpRequestView"/>、状態は <see cref="HttpRequestPanelViewModel"/>。</summary>
public sealed class HttpRequestVisual : IEditorSupportVisual
{
    private readonly HttpEditorSupport _owner;
    private readonly HttpRequestPanelViewModel _vm;
    private readonly Views.HttpRequestView _view;

    public HttpRequestVisual(HttpEditorSupport owner)
    {
        _owner = owner;
        _vm = new HttpRequestPanelViewModel(owner);
        _view = new Views.HttpRequestView { DataContext = _vm };
        _owner.StateChanged += OnStateChanged;
        _owner.FocusRequested += OnFocusRequested;
    }

    public FrameworkElement View => _view;

    // 表示から本文へは書き戻さない（リクエストはエディタで書く）。
    public event EventHandler<EditorSupportContentEdited>? ContentEdited
    {
        add { }
        remove { }
    }

    public async Task<Action> PrepareAsync(string filePath, string text, CancellationToken ct)
    {
        var document = await Task.Run(() => HttpRequestFile.Parse(text), ct);
        ct.ThrowIfCancellationRequested();
        return () => _vm.Update(filePath, document);
    }

    private void OnStateChanged(object? sender, HttpRequestStateChanged e) => _vm.Refresh(e);

    private void OnFocusRequested(object? sender, HttpRequestStateChanged e) => _vm.Select(e);

    public void Dispose()
    {
        _owner.StateChanged -= OnStateChanged;
        _owner.FocusRequested -= OnFocusRequested;
    }
}

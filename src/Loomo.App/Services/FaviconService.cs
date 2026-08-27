using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media.Imaging;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ブックマーク・履歴・候補の行に出す<b>サイトのアイコン（favicon）</b>を引く。
///
/// <para>★ の一点張りから「見ればどのサイトか分かる」へ変えるための道具（設計書 §21.5.1）。
/// タブの favicon（<see cref="TabIconService"/>）は<b>開いている</b> WebView2 から貰えるが、
/// ブックマークは開いていないページの絵が要るので、ここは自分で取りに行く。</para>
///
/// <para><b>速さは3段の入れ子で作る</b>——上の段で当たったら下は触らない。
/// <list type="number">
/// <item>メモリ（<c>_resolved</c>）：同じホストなら何行あっても引き当ては辞書1回。
///   取れなかったことも <c>null</c> として覚えるので、無いサイトを何度も取りに行かない。</item>
/// <item>ディスク（<c>%APPDATA%/Loomo/favicons</c>）：32px の PNG に揃えて置く。
///   次の起動でも通信ゼロで出る。取れなかったホストは <c>.miss</c> の空ファイルで覚え、
///   一定期間（<see cref="MissRetry"/>）は再挑戦しない。</item>
/// <item>通信：ここまで外れたときだけ。<b>ブックマークの行だけ</b>が使える
///   （履歴・アドレス欄の候補は <c>allowNetwork: false</c>＝キャッシュにあるものだけ出す）——
///   打つたびに数十件の取得が走ると、候補が降りる速さそのものが壊れる。</item>
/// </list></para>
///
/// <para><b>鍵はホスト</b>（<c>example.com</c>）で URL 単位ではない。favicon はサイトに1枚なので、
/// 数百件のブックマークでも取得はホストの数——これが一番効く節約。</para>
///
/// <para>さらに、人がページを開いたときに WebView2 が持っている favicon をそのまま
/// <see cref="Harvest"/> でこの置き場へ写す（通信ゼロ）。よく行くサイトほど、
/// ブックマークに追加した瞬間にはもう絵がある。</para>
/// </summary>
public sealed class FaviconService
{
    /// <summary>保存・表示に揃える一辺（px）。行に出すのは 14〜16 DIP なので、
    /// 高 DPI でも足りるだけの 32px を上限に縮めて置く（大きい絵をそのまま抱えない）。</summary>
    public const int IconPixels = 32;

    private const int MaxDownloadBytes = 256 * 1024;
    private const int MaxHtmlBytes = 128 * 1024;

    /// <summary>取れなかったホストを再び取りに行くまでの間隔。</summary>
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    /// <summary>置き場の絵が古びたと見なすまでの期間。サイトが絵を差し替えたとき、
    /// <b>次の起動でも古い絵が出続けない</b>ようにするための期限——ただし期限切れでも
    /// 取りに行きはしない（人がそのページを開いて <see cref="Harvest"/> が来たときに、
    /// 通信ゼロで写し直すだけ）。</summary>
    private static readonly TimeSpan IconRefresh = TimeSpan.FromDays(30);

    /// <summary>1件あたりの待ち時間。アイコンは「出れば嬉しい」ものなので、長く待たない。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>1ホストを諦めるまでの締切。<b>1件ごとの待ち時間だけでは足りない</b>——
    /// 取りに行く道は https/http × (/favicon.ico, HTML) の4本あるので、繋がらないホスト1つが
    /// 6秒×4＝24秒、同時4本しかない取得の口を塞ぐ。数百件のブックマークでは、
    /// そういうホストが数個続いただけで手前の絵が何分も出て来なくなる。</summary>
    private static readonly TimeSpan HostDeadline = TimeSpan.FromSeconds(10);

    /// <summary>同時に走らせる取得の数。ブックマーク一覧を開いた瞬間に数十ホストへ
    /// いっせいに繋ぎに行かないための栓。</summary>
    private const int MaxConcurrentDownloads = 4;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _network = new(MaxConcurrentDownloads, MaxConcurrentDownloads);

    /// <summary>結論（絵、または「無い」を表す null）。<b>取れなかったことも覚える</b>のが要点。</summary>
    private readonly ConcurrentDictionary<string, ImageSource?> _resolved = new(StringComparer.Ordinal);

    /// <summary>取得中のもの。同じホストの行が同時に何行来ても取得は1回で済ませる。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> _inflight = new(StringComparer.Ordinal);

    /// <summary>
    /// <b>置き場に無かった</b>ホスト。「絵が無いサイト」（<c>_resolved</c> の null）とは<b>別物</b>で、
    /// 意味は「ディスクを見たが無かった＝取りに行けば分かるかもしれない」。
    /// <para>
    /// これが無いと、アドレス欄の候補（<c>allowNetwork: false</c>）は結論を何も覚えないので、
    /// 打鍵のたびに全行ぶんの <see cref="LoadFromDisk"/> が走る——1文字ごとに数十回の
    /// <c>File.Exists</c>＋復号。かといって「絵が無い」として覚えると、あとから来た
    /// ブックマークの行が取りに行かなくなる（それが <c>_resolved</c> を汚してはいけない理由）。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _diskMisses = new(StringComparer.Ordinal);

    /// <summary>取りに行く手。<b>テストが差し替える唯一の穴</b>——ここが実通信のままだと、
    /// 「繋がらなかった」を作るのに実際の DNS/HTTP に頼ることになり、串（プロキシ）や
    /// ISP の代理応答で結論が変わってしまう。</summary>
    private Func<string, Task<SiteFetch>> _fetch;

    public FaviconService() : this(DefaultCacheDirectory()) { }

    public FaviconService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        _fetch = DownloadAsync;
    }

    /// <summary>取りに行く手を差し替える（テスト専用）。</summary>
    internal void UseFetchForTests(Func<string, Task<SiteFetch>> fetch) => _fetch = fetch;

    /// <summary>取りに行った結果。<c>Reached</c> は<b>相手が答えたか</b>——
    /// 「繋がったが絵は無い」と「そもそも繋がらない」を分けるためだけにある
    /// （前者しか <c>.miss</c> を書いて良くない）。</summary>
    internal readonly record struct SiteFetch(BitmapSource? Icon, bool Reached);

    public static string DefaultCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "favicons");

    /// <summary>すでに手元にある絵だけを返す（同期・通信もディスクも触らない）。</summary>
    public ImageSource? TryGetCached(string? url)
    {
        var key = SiteKey(url);
        return key is not null && _resolved.TryGetValue(key, out var icon) ? icon : null;
    }

    /// <summary>そのアドレスのサイトアイコンを取る。
    /// <paramref name="allowNetwork"/> が false なら手元（メモリ・ディスク）にあるものだけを返す。
    /// <b>例外は投げない</b>——呼び出し側は表示のためだけに待つので、失敗は「絵が無い」で足りる。</summary>
    public Task<ImageSource?> GetAsync(string? url, bool allowNetwork)
    {
        var key = SiteKey(url);
        if (key is null)
            return Task.FromResult<ImageSource?>(null);
        if (_resolved.TryGetValue(key, out var known))
            return Task.FromResult(known);
        // 手元だけで良いなら、置き場に無いと分かっているホストはそこで終わり（ディスクを触らない）。
        if (!allowNetwork && _diskMisses.ContainsKey(key))
            return Task.FromResult<ImageSource?>(null);

        // 束ねる鍵に<b>取りに行って良いかを含める</b>。ホストだけで束ねると、先に来た手元だけの
        // 引き（履歴・候補）に後から来たブックマークの行が相乗りして null を受け取り、
        // その行は二度と取りに行かない（行 VM は一度頼んだら覚えるので、絵は出ないまま残る）。
        var slot = allowNetwork ? key + "|net" : key;   // '|' はホスト名に現れない
        return _inflight.GetOrAdd(slot, _ => new Lazy<Task<ImageSource?>>(
            () => ResolveAsync(key, slot, allowNetwork), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>開いているページの favicon（WebView2 が既に持っているもの）を置き場へ写す。
    /// 通信を1回も増やさずに、よく行くサイトのぶんが溜まっていく。</summary>
    public void Harvest(string? pageUrl, byte[] pngBytes) => _ = HarvestAsync(pageUrl, pngBytes);

    /// <summary><see cref="Harvest"/> の待てる版（テスト用。実際の呼び出しは投げっぱなしで良い）。</summary>
    internal Task HarvestAsync(string? pageUrl, byte[] pngBytes)
    {
        var key = SiteKey(pageUrl);
        if (key is null || pngBytes.Length == 0)
            return Task.CompletedTask;
        // 「取れなかった」と覚えているホスト（値が null）は<b>写す価値が一番高い</b>——
        // ここで弾くと、取りに行っては 403 で断られるサイトを人が開いて絵を持って来ても
        // ★ のままになる（キーの有無で見ると、まさにそれが起きる）。
        // すでに絵があるときも、置き場のぶんが古びていれば写し直す——絵を差し替えたサイトが
        // 起動のたびに古い絵へ戻る（＝ディスクの写しに期限が無い）のを止めるため。
        var known = _resolved.TryGetValue(key, out var existing) && existing is not null;
        if (known && !IsDiskIconStale(key))
            return Task.CompletedTask;
        // 復号・縮小・書き出しは UI スレッドでやらない（ナビゲーションのたびに来る）。
        return Task.Run(() =>
        {
            var icon = DecodeIcon(pngBytes);
            if (icon is null)
                return;
            _resolved[key] = icon;
            if (IsDiskIconStale(key) && EncodePng(icon) is { } png)
                SaveToDisk(key, png);
        });
    }

    private async Task<ImageSource?> ResolveAsync(string key, string slot, bool allowNetwork)
    {
        ImageSource? icon = null;
        // 「このサイトには絵が無い」と<b>言い切れた</b>か。言い切れないまま覚えると、
        // その結論はもう覆らない（_resolved に期限は無い）。
        var absent = false;
        try
        {
            icon = await Task.Run(() => (ImageSource?)LoadFromDisk(key)).ConfigureAwait(false);
            if (icon is null)
                _diskMisses[key] = 0;
            if (icon is null && allowNetwork)
            {
                if (HasFreshMiss(key))
                {
                    absent = true;      // 直近に確かめて「無い」と分かっている
                }
                else
                {
                    var fetched = await _fetch(key).ConfigureAwait(false);
                    if (fetched.Icon is { } downloaded)
                    {
                        icon = downloaded;
                        if (EncodePng(downloaded) is { } png)
                            SaveToDisk(key, png);
                    }
                    else if (fetched.Reached)
                    {
                        // 「無い」と言い切って良いのは<b>相手が答えた</b>ときだけ。圏外や
                        // captive portal で繋がらなかったぶんまで覚えると、線が戻っても
                        // ★ のまま（ディスクなら7日、メモリなら再起動まで）になる。
                        absent = true;
                        if (!(_resolved.TryGetValue(key, out var landed) && landed is not null))
                            MarkMiss(key);
                    }
                }
            }
        }
        catch
        {
            // 絵が出ないだけ。ブラウズは続く（＝言い切れていないので覚えない）。
        }

        // 「無い」を覚えるのは言い切れたときだけ。手元だけ見て外れたぶんや、届かなかったぶんまで
        // 覚えると、あとからブックマークの行が来ても・線が戻っても二度と取りに行かなくなる。
        // ただし<b>塗り潰さない</b>——取りに行っている10秒のあいだに、人がそのサイトを開いて
        // Harvest が絵を入れていることがある（403 で断られるサイトほど、まさにそれが起きる）。
        // TryAdd なら、後から入ったその絵に譲れる。
        if (icon is not null)
            _resolved[key] = icon;
        else if (absent && !_resolved.TryAdd(key, null)
                 && _resolved.TryGetValue(key, out var harvested))
            icon = harvested;
        _inflight.TryRemove(slot, out _);
        return icon;
    }

    // ── 取りに行く ───────────────────────────────────────────────────
    /// <summary>ブラウザと同じ順で当たる：まず <c>/favicon.ico</c>、外れたら HTML の
    /// <c>&lt;link rel="icon"&gt;</c>。https で繋がったのに絵が無いときは http を試さない
    /// （繋がらなかったときだけ落とす）。</summary>
    private async Task<SiteFetch> DownloadAsync(string authority)
    {
        await _network.WaitAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(HostDeadline);
        var reached = false;
        try
        {
            foreach (var scheme in new[] { "https", "http" })
            {
                if (!Uri.TryCreate($"{scheme}://{authority}/", UriKind.Absolute, out var root))
                    continue;
                var direct = await FetchIconAsync(new Uri(root, "/favicon.ico"), deadline.Token)
                    .ConfigureAwait(false);
                reached |= direct.Reached;
                if (direct.Icon is not null)
                    return direct;

                var html = await FetchHtmlAsync(root, deadline.Token).ConfigureAwait(false);
                reached |= html.Reached;
                if (html.Text is null)
                    continue;                 // 読めなかった → 次のスキームへ
                foreach (var href in ParseIconLinks(html.Text))
                {
                    // HTML に直接埋まっている絵はその場で開ける（取りに行かない）。
                    if (TryDecodeDataUri(href) is { } inline)
                        return new SiteFetch(inline, true);
                    if (!Uri.TryCreate(root, href, out var iconUri))
                        continue;
                    var linked = await FetchIconAsync(iconUri, deadline.Token).ConfigureAwait(false);
                    reached |= linked.Reached;
                    if (linked.Icon is not null)
                        return linked;
                }
                return new SiteFetch(null, true);   // 繋がったが絵は無かった
            }
            return new SiteFetch(null, reached);
        }
        catch
        {
            return new SiteFetch(null, reached);
        }
        finally
        {
            _network.Release();
        }
    }

    private static async Task<SiteFetch> FetchIconAsync(Uri uri, CancellationToken deadline)
    {
        // 絵は途中まででは使えないので、上限を超えたら捨てる（切り詰めない）。
        var (bytes, reached) = await FetchAsync(uri, MaxDownloadBytes, truncate: false, deadline)
            .ConfigureAwait(false);
        if (bytes is null)
            return new SiteFetch(null, reached);
        // SVG は WPF が読めない。HTML は「404 を 200 で返す」サイトの本文なので、どちらも絵ではない。
        return new SiteFetch(LooksLikeMarkup(bytes) ? null : DecodeIcon(bytes), reached);
    }

    private static async Task<(string? Text, bool Reached)> FetchHtmlAsync(Uri uri, CancellationToken deadline)
    {
        // <head> はページの頭にあるので、上限までで<b>切り詰めて</b>使う——捨ててはいけない。
        // 本文が数百 KB あるページ（Qiita など）は珍しくなく、捨てると icon の在り処ごと落ちる（実測）。
        var (bytes, reached) = await FetchAsync(uri, MaxHtmlBytes, truncate: true, deadline)
            .ConfigureAwait(false);
        if (bytes is null)
            return (null, reached);
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            var headEnd = text.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
            return (headEnd > 0 ? text[..headEnd] : text, reached);
        }
        catch
        {
            return (null, reached);
        }
    }

    /// <summary>上限まで読んで返す（相手の言う長さを信用せず、実際に読んだ量で切る）。
    /// <paramref name="truncate"/> が true なら上限までの前半を返し、false なら超えた時点で捨てる。
    /// 返り値の <c>Reached</c> は<b>相手が答えたか</b>——404 も大きすぎも「答え」で、
    /// そもそも繋がらなかった（例外・時間切れ）のとは区別する。</summary>
    private static async Task<(byte[]? Bytes, bool Reached)> FetchAsync(Uri uri, int maxBytes, bool truncate,
        CancellationToken deadline)
    {
        var reached = false;
        try
        {
            // 1件ごとの待ち時間と、ホストごとの締切のどちらか早い方で切る。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            cts.CancelAfter(RequestTimeout);
            using var response = await Http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            reached = true;
            if (!response.IsSuccessStatusCode)
                return (null, true);
            if (!truncate && response.Content.Headers.ContentLength > maxBytes)
                return (null, true);

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length < maxBytes)
                    continue;
                if (!truncate)
                    return (null, true);
                break;      // 前半だけで足りる（残りは受け取らずに切る）
            }
            return (buffer.ToArray(), true);
        }
        catch
        {
            return (null, reached);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 2,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        // 素の HttpClient を弾くサイトがある（UA 無しは bot 扱い）。
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Loomo/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*,text/html;q=0.8,*/*;q=0.5");
        return client;
    }

    // ── 置き場（ディスク） ───────────────────────────────────────────
    private BitmapSource? LoadFromDisk(string key)
    {
        try
        {
            var path = IconPath(key);
            return File.Exists(path) ? DecodeIcon(File.ReadAllBytes(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private void SaveToDisk(string key, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = IconPath(key);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
            _diskMisses.TryRemove(key, out _);   // 置き場に入った＝「無い」の記憶は捨てる
            var miss = MissPath(key);
            if (File.Exists(miss))
                File.Delete(miss);
        }
        catch
        {
            // 置けなくても表示はできている（次回また取りに行くだけ）。
        }
    }

    /// <summary>置き場の絵が無い、または古びた（<see cref="IconRefresh"/> 超え）か。</summary>
    private bool IsDiskIconStale(string key)
    {
        try
        {
            var path = IconPath(key);
            return !File.Exists(path)
                || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= IconRefresh;
        }
        catch
        {
            return true;    // 見られないなら書きに行く（書けなければそこでまた諦める）
        }
    }

    private bool HasFreshMiss(string key)
    {
        try
        {
            var path = MissPath(key);
            return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MissRetry;
        }
        catch
        {
            return false;
        }
    }

    private void MarkMiss(string key)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllBytes(MissPath(key), Array.Empty<byte>());
        }
        catch
        {
            // 覚えられなくても実害は「次も取りに行く」だけ。
        }
    }

    private string IconPath(string key) => Path.Combine(_cacheDirectory, CacheFileName(key) + ".png");
    private string MissPath(string key) => Path.Combine(_cacheDirectory, CacheFileName(key) + ".miss");

    // ── 純関数（テストの対象） ───────────────────────────────────────
    /// <summary>アイコンを引く鍵＝ホスト（既定でないポートは含む）。http/https 以外は null。
    /// <b>URL ごとではなくサイトごと</b>に持つのが、この機能の速さの正体。</summary>
    public static string? SiteKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return string.IsNullOrEmpty(uri.Host) ? null : uri.Authority.ToLowerInvariant();
    }

    /// <summary>鍵をファイル名に落とす。読めるように綴りは残しつつ、ファイル名に使えない字は伏せ、
    /// 伏せたことで別ホストが同じ名前にならないよう短い指紋を足す。</summary>
    internal static string CacheFileName(string key)
    {
        var safe = new StringBuilder(key.Length);
        foreach (var ch in key)
            safe.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_');
        var name = safe.ToString();
        if (name.Length > 60)
            name = name[..60];
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"{name}-{Convert.ToHexString(hash)[..8].ToLowerInvariant()}";
    }

    /// <summary>HTML の head から <c>&lt;link rel="…icon…" href="…"&gt;</c> を拾う。
    /// 素の正規表現で足りる——ここで欲しいのは1本の href だけで、外れたら「絵が無い」で済むから。
    /// <c>.svg</c> は WPF が描けないので最初から外す。普通の icon を先に、
    /// apple-touch-icon（大きい絵）は後回しに並べる。</summary>
    internal static IReadOnlyList<string> ParseIconLinks(string html)
    {
        var plain = new List<string>();
        var apple = new List<string>();
        foreach (Match tag in Regex.Matches(html, "<link\\b[^>]*>",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var rel = Attribute(tag.Value, "rel");
            if (rel is null || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                continue;
            var href = Attribute(tag.Value, "href");
            if (string.IsNullOrWhiteSpace(href)
                || href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))
                continue;
            (rel.Contains("apple", StringComparison.OrdinalIgnoreCase) ? apple : plain).Add(href);
        }
        plain.AddRange(apple);
        return plain;
    }

    /// <summary>タグから属性を1つ取る。<b>名前の左を境界で留める</b>のが要点——留めないと
    /// <c>&lt;link data-rel="x" rel="icon" …&gt;</c> の <c>rel</c> として先に現れる <c>data-rel</c> を拾い、
    /// 「icon ではない」と判断して<b>本物のアイコン指定を捨てる</b>（そのうえ「絵が無いサイト」として
    /// 7日覚える）。<c>href</c> と <c>data-href</c> も同じ形。</summary>
    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, "(?:^|\\s)" + name + "\\s*=\\s*(\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;
        for (var i = 2; i <= 4; i++)
            if (match.Groups[i].Success)
                return WebUtility.HtmlDecode(match.Groups[i].Value).Trim();
        return null;
    }

    /// <summary>HTML に直接埋まっている絵（<c>data:image/png;base64,…</c>）をその場で開く。
    /// これを見ないと <c>Uri</c> ごと HTTP で取りに行って例外→「絵が無い」と覚えてしまう
    /// ——絵はもう手元にあるのに。base64 でないもの（URL エンコードの SVG 等）は対象外。</summary>
    private static BitmapSource? TryDecodeDataUri(string href)
    {
        if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
        var comma = href.IndexOf(',');
        if (comma < 0 || !href[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return DecodeIcon(Convert.FromBase64String(href[(comma + 1)..].Trim()));
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeMarkup(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64)).TrimStart();
        return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase);
    }

    // ── 画像 ─────────────────────────────────────────────────────────
    /// <summary>バイト列を1枚の凍結済みビットマップにする。<c>.ico</c> は何枚も入っているので
    /// <see cref="IconPixels"/> に一番近い（できれば大きい方の）1枚を選ぶ——小さい絵を引き伸ばすと汚い。
    ///
    /// <para><b>最後に画素を写し取って復号器から切り離す</b>のが要点。取得は背景スレッドで走るので、
    /// <see cref="BitmapDecoder"/> にぶら下がったままの <see cref="BitmapFrame"/> を UI スレッドへ渡すと
    /// 「別のスレッドが所有している」で落ちる（凍結しても復号器の側の紐は切れない・実測）。
    /// 写した後は完全に自前の画素なので、凍結して何枚の行からでも共有できる。</para></summary>
    private static BitmapSource? DecodeIcon(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .Where(f => f.PixelWidth > 0 && f.PixelHeight > 0)
                .OrderBy(f => f.PixelWidth >= IconPixels ? 0 : 1)
                .ThenBy(f => Math.Abs(f.PixelWidth - IconPixels))
                .FirstOrDefault();
            if (frame is null)
                return null;
            if (frame.CanFreeze)
                frame.Freeze();

            BitmapSource source = frame;
            if (source.PixelWidth > IconPixels)
            {
                var scale = IconPixels / (double)source.PixelWidth;
                source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var detached = new WriteableBitmap(source);   // ここで画素を写して復号器から切る
            detached.Freeze();
            return detached;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? EncodePng(BitmapSource source)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>行1つぶんの favicon の面倒（<b>一度だけ</b>頼む・届いたら知らせる）をまとめたもの。
/// ブックマークの行とブックマークバーの項目という別々の VM が同じ振る舞いを要るので、
/// 継承ではなく持たせる形にしてある。
///
/// <para><b>頼むのは束ねた時ではなく、最初に絵を訊かれた時</b>（＝行が描かれた時）。一覧の行 VM は
/// ページを1枚開くたびに作り直されるので、束ね直しただけでは取りに行かない——地味だが一番効く。
/// ただし<b>「画面に見えた時」ではない</b>：ブックマークの一覧は仮想化していない
/// <c>ItemsControl</c> なので、開いた瞬間に全行が描かれる（＝ホストの数だけ一斉に頼まれる）。
/// 一斉に走らせないための栓は <see cref="FaviconService"/> 側（同時4本・ホストごと10秒の締切）が持つ。
/// 横へ開くサブメニューだけは <c>Popup</c> なので、開くまで本当に描かれない。</para></summary>
internal sealed class FaviconSlot
{
    private readonly FaviconService? _icons;
    private readonly string? _url;
    private readonly bool _allowNetwork;
    private readonly Action _notify;
    private bool _requested;

    public FaviconSlot(FaviconService? icons, string? url, bool allowNetwork, Action notify)
    {
        _icons = icons;
        _url = url;
        _allowNetwork = allowNetwork;
        _notify = notify;
    }

    public ImageSource? Icon { get; private set; }

    /// <summary>絵を返す（まだなら取りに行かせる）。届いたら <c>notify</c> で知らせる。</summary>
    public ImageSource? Get()
    {
        if (_requested || _icons is null)
            return Icon;
        _requested = true;
        var task = _icons.GetAsync(_url, _allowNetwork);
        if (task.IsCompletedSuccessfully)
        {
            // 手元にあった＝この場で返せる。通知は出さない
            // （バインドの評価中に PropertyChanged を投げ返さないため）。
            Icon = task.Result;
            return Icon;
        }
        Await(task);
        return Icon;
    }

    private async void Await(Task<ImageSource?> task)
    {
        try
        {
            var icon = await task.ConfigureAwait(true);
            if (icon is null)
                return;
            Icon = icon;
            _notify();
        }
        catch
        {
            // 絵が出ないだけ。
        }
    }
}

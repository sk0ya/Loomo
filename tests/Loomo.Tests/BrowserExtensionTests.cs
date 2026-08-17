using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ブラウザペインの拡張機能（§21.5.2）——crx の展開と、ストアの URL/ID の解釈、manifest の読み取り。
/// WebView2 への登録はシェル側の仕事なので、ここではファイルとして完結する部分だけを見る。
/// </summary>
public class BrowserExtensionTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-crx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ===== crx の展開 =====

    /// <summary>ZIP をひとつ作る（名前→中身）。</summary>
    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value > 0 ? (byte)(b | 0x80) : b);
        } while (value > 0);
        return bytes.ToArray();
    }

    private static byte[] LengthDelimited(int fieldNumber, byte[] payload)
        => Varint((ulong)((fieldNumber << 3) | 2)).Concat(Varint((ulong)payload.Length)).Concat(payload).ToArray();

    /// <summary>CRX3 を組み立てる（ヘッダは公開鍵ひとつぶんの最小 protobuf）。</summary>
    private static byte[] BuildCrx3(byte[] zip, byte[]? publicKey)
        => BuildCrx3(zip, publicKey is null ? Array.Empty<byte[]>() : new[] { publicKey }, crxId: null);

    /// <summary>署名を複数持つ CRX3（ストアの crx はこの形）。<paramref name="crxId"/> を入れると
    /// 「どの鍵が ID を決めているか」がヘッダに書かれる。</summary>
    private static byte[] BuildCrx3(byte[] zip, byte[][] publicKeys, byte[]? crxId)
    {
        var header = publicKeys
            .SelectMany(key => LengthDelimited(2, LengthDelimited(1, key)))   // sha256_with_rsa { public_key }
            .ToArray();
        if (crxId is not null)
            header = header.Concat(LengthDelimited(10000, LengthDelimited(1, crxId))).ToArray();
        return new byte[] { (byte)'C', (byte)'r', (byte)'2', (byte)'4' }
            .Concat(BitConverter.GetBytes(3u))
            .Concat(BitConverter.GetBytes((uint)header.Length))
            .Concat(header)
            .Concat(zip)
            .ToArray();
    }

    private static byte[] BuildCrx2(byte[] zip, byte[] publicKey, byte[] signature)
        => new byte[] { (byte)'C', (byte)'r', (byte)'2', (byte)'4' }
            .Concat(BitConverter.GetBytes(2u))
            .Concat(BitConverter.GetBytes((uint)publicKey.Length))
            .Concat(BitConverter.GetBytes((uint)signature.Length))
            .Concat(publicKey)
            .Concat(signature)
            .Concat(zip)
            .ToArray();

    [Fact]
    public void CRX3を展開すると中身が出る()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var crx = BuildCrx3(BuildZip(("manifest.json", "{\"name\":\"テスト\"}"), ("js/main.js", "console.log(1)")), null);

        CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.Equal("{\"name\":\"テスト\"}", File.ReadAllText(Path.Combine(destination, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(destination, "js", "main.js")));
    }

    [Fact]
    public void CRX2も展開できる()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var key = Encoding.ASCII.GetBytes("PUBLICKEY");
        var crx = BuildCrx2(BuildZip(("manifest.json", "{}")), key, new byte[] { 1, 2, 3, 4 });

        var result = CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.True(File.Exists(Path.Combine(destination, "manifest.json")));
        Assert.Equal(Convert.ToBase64String(key), result.PublicKey);
    }

    /// <summary>公開鍵を manifest の key へ書き戻すのが要点——これが無いと展開先のパスから ID が作られ、
    /// 置き場所を変えるたびに別の拡張機能になる（保存した設定も権限も失う）。</summary>
    [Fact]
    public void 署名の公開鍵をmanifestのkeyへ書き戻す()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var key = Encoding.ASCII.GetBytes("PUBLIC-KEY-BYTES");
        var crx = BuildCrx3(BuildZip(("manifest.json", "{\"name\":\"テスト\",\"version\":\"1.0\"}")), key);

        var result = CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.Equal(Convert.ToBase64String(key), result.PublicKey);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "manifest.json")))!.AsObject();
        Assert.Equal(Convert.ToBase64String(key), manifest["key"]!.GetValue<string>());
        Assert.Equal("テスト", manifest["name"]!.GetValue<string>());
    }

    /// <summary>ストアの crx には発行元の署名も一緒に載っている。先頭の鍵をそのまま採ると
    /// <b>ストアと違う ID</b> になるので、crx_id（公開鍵の SHA256 の先頭16バイト）で選び直す。</summary>
    [Fact]
    public void 署名が複数あるときはIDを決めている鍵を選ぶ()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var publisherKey = Encoding.ASCII.GetBytes("PUBLISHER-KEY");
        var extensionKey = Encoding.ASCII.GetBytes("EXTENSION-KEY");
        var crxId = System.Security.Cryptography.SHA256.HashData(extensionKey).Take(16).ToArray();
        var crx = BuildCrx3(BuildZip(("manifest.json", "{}")), new[] { publisherKey, extensionKey }, crxId);

        var result = CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.Equal(Convert.ToBase64String(extensionKey), result.PublicKey);
    }

    /// <summary>crx_id が読めないときは先頭の鍵で妥協する（ID は変わり得るが拡張機能としては動く）。</summary>
    [Fact]
    public void crx_idが無ければ先頭の鍵を使う()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var first = Encoding.ASCII.GetBytes("FIRST");
        var crx = BuildCrx3(BuildZip(("manifest.json", "{}")),
            new[] { first, Encoding.ASCII.GetBytes("SECOND") }, crxId: null);

        var result = CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.Equal(Convert.ToBase64String(first), result.PublicKey);
    }

    [Fact]
    public void 自分でkeyを宣言しているmanifestは書き換えない()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var crx = BuildCrx3(BuildZip(("manifest.json", "{\"key\":\"MINE\"}")), Encoding.ASCII.GetBytes("OTHER"));

        CrxArchive.Extract(new MemoryStream(crx), destination);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(destination, "manifest.json")))!.AsObject();
        Assert.Equal("MINE", manifest["key"]!.GetValue<string>());
    }

    /// <summary>ストアが署名検証用に入れる _metadata は、展開済み読み込みでは検証に失敗して
    /// 拡張機能ごと読み込めなくなる。</summary>
    [Fact]
    public void _metadataは展開しない()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var crx = BuildCrx3(BuildZip(
            ("manifest.json", "{}"), ("_metadata/verified_contents.json", "[]")), null);

        CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.False(Directory.Exists(Path.Combine(destination, "_metadata")));
    }

    /// <summary>ZIP の中の名前は信用しない（`../` で展開先の外へ書き出せてしまう）。</summary>
    [Fact]
    public void 展開先の外へは書き出さない()
    {
        var root = TempDirectory();
        var destination = Path.Combine(root, "ext");
        var crx = BuildCrx3(BuildZip(("manifest.json", "{}"), ("../escaped.txt", "x")), null);

        CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "manifest.json")));
    }

    [Fact]
    public void crxでないものは弾く()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        Assert.Throws<InvalidDataException>(
            () => CrxArchive.Extract(new MemoryStream(Encoding.ASCII.GetBytes("PK\u0003\u0004not a crx")), destination));
    }

    /// <summary>ダウンロードが途中で切れた crx（ZIP が壊れている）で<b>今まで動いていた実体を失わない</b>。
    /// 先に消してから展開すると、入れ直しのつもりが WebView2 には登録されたまま実体だけ消える
    /// ——いちばん困る壊れ方をする。</summary>
    [Fact]
    public void 壊れたcrxでは既存の展開済みフォルダーを壊さない()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        CrxArchive.Extract(
            new MemoryStream(BuildCrx3(BuildZip(("manifest.json", "{\"name\":\"前の版\"}")), null)), destination);
        // ZIP の中身だけ切り落とした crx（ヘッダは正しい）。
        var broken = BuildCrx3(BuildZip(("manifest.json", "{}")).Take(20).ToArray(), null);

        Assert.ThrowsAny<Exception>(() => CrxArchive.Extract(new MemoryStream(broken), destination));

        Assert.Equal("{\"name\":\"前の版\"}", File.ReadAllText(Path.Combine(destination, "manifest.json")));
        Assert.False(Directory.Exists(destination + CrxArchive.StagingSuffix));   // 置き場を残さない
    }

    /// <summary>入れ直し（更新）は、既存を丸ごと<b>置き換える</b>——古い版のファイルが残ってはいけない。
    /// 実装は既存を消さずに退避してから入れ替えるので、退避先（<c>&lt;ID&gt;.old-*</c>）も残さないことまで見る。</summary>
    [Fact]
    public void 同じ場所へ入れ直すと古い版のファイルが残らない()
    {
        var root = TempDirectory();
        var destination = Path.Combine(root, "ext");
        CrxArchive.Extract(new MemoryStream(BuildCrx3(
            BuildZip(("manifest.json", "{\"name\":\"前の版\"}"), ("old.js", "//古い")), null)), destination);

        CrxArchive.Extract(new MemoryStream(BuildCrx3(
            BuildZip(("manifest.json", "{\"name\":\"新しい版\"}")), null)), destination);

        Assert.Equal("{\"name\":\"新しい版\"}", File.ReadAllText(Path.Combine(destination, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(destination, "old.js")));
        Assert.Equal(new[] { destination }, Directory.GetDirectories(root));   // 退避先も置き場も残さない
    }

    /// <summary>CRX2 の長さは <c>uint</c>。int で足すと桁あふれで範囲検査をすり抜け、例外の型が変わって
    /// <see cref="BrowserExtensionStore.DownloadAsync"/> の Chrome→Edge の乗り換えが効かなくなる。</summary>
    [Fact]
    public void CRX2の桁あふれる長さはInvalidDataExceptionで弾く()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var crx = new byte[] { (byte)'C', (byte)'r', (byte)'2', (byte)'4' }
            .Concat(BitConverter.GetBytes(2u))
            .Concat(BitConverter.GetBytes(0x7FFFFFFFu))   // 公開鍵長
            .Concat(BitConverter.GetBytes(0x7FFFFFFFu))   // 署名長（int で足すと負になる）
            .Concat(BuildZip(("manifest.json", "{}")))
            .ToArray();

        Assert.Throws<InvalidDataException>(() => CrxArchive.Extract(new MemoryStream(crx), destination));
    }

    // ===== ストアの URL/ID =====

    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/bitwarden/nngceckbapebfimnlniiiahkandclblb",
        "nngceckbapebfimnlniiiahkandclblb", BrowserExtensionStoreKind.Chrome)]
    [InlineData("https://chrome.google.com/webstore/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm?hl=ja",
        "cjpalhdlnbpafiamejdnhcphjbkeiagm", BrowserExtensionStoreKind.Chrome)]
    [InlineData("https://microsoftedge.microsoft.com/addons/detail/bitwarden/jbkfoedolllekgbhcbcoahefnbanhhlh",
        "jbkfoedolllekgbhcbcoahefnbanhhlh", BrowserExtensionStoreKind.Edge)]
    [InlineData("nngceckbapebfimnlniiiahkandclblb", "nngceckbapebfimnlniiiahkandclblb", BrowserExtensionStoreKind.Chrome)]
    public void ストアのURLとIDから拡張機能IDを取り出す(string input, string expected, BrowserExtensionStoreKind kind)
    {
        Assert.True(BrowserExtensionStore.TryParseStoreId(input, out var id, out var parsedKind));
        Assert.Equal(expected, id);
        Assert.Equal(kind, parsedKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/detail/foo")]
    [InlineData("nngceckbapebfimnlniiiahkandclbl")]     // 31 文字
    [InlineData("NNGCECKBAPEBFIMNLNIIIAHKANDCLBLB")]    // 大文字は ID ではない
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]    // a〜p の外
    public void 拡張機能IDに見えないものは受けない(string input)
        => Assert.False(BrowserExtensionStore.TryParseStoreId(input, out _, out _));

    /// <summary>促しバーを出す判断は<b>ストアのホストに限る</b>。貼り付けを受ける
    /// <see cref="BrowserExtensionStore.TryParseStoreId"/> より狭い——32文字の英字が入っているだけの
    /// 無関係なページで「追加しますか」を出さないため。</summary>
    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin/cjpalhdlnbpafiamejdnhcphjbkeiagm", true)]
    [InlineData("https://chrome.google.com/webstore/detail/x/cjpalhdlnbpafiamejdnhcphjbkeiagm", true)]
    [InlineData("https://microsoftedge.microsoft.com/addons/detail/x/jbkfoedolllekgbhcbcoahefnbanhhlh", true)]
    [InlineData("https://chromewebstore.google.com/category/extensions", false)]   // 一覧ページ
    [InlineData("https://example.com/detail/x/cjpalhdlnbpafiamejdnhcphjbkeiagm", false)]
    [InlineData("cjpalhdlnbpafiamejdnhcphjbkeiagm", false)]                        // 貼り付けはこちらでは受けない
    [InlineData(null, false)]
    public void ストアの拡張機能ページだけを促しの対象にする(string? url, bool expected)
        => Assert.Equal(expected, BrowserExtensionStore.TryParseStoreDetail(url, out _, out _));

    [Fact]
    public void 配布URLはストアごとに変わる()
    {
        const string id = "nngceckbapebfimnlniiiahkandclblb";
        var chrome = BrowserExtensionStore.DownloadUrl(id, BrowserExtensionStoreKind.Chrome, "120.0.0.0");
        var edge = BrowserExtensionStore.DownloadUrl(id, BrowserExtensionStoreKind.Edge, "120.0.0.0");

        Assert.Contains("clients2.google.com", chrome);
        Assert.Contains("prodversion=120.0.0.0", chrome);
        Assert.Contains(id, chrome);
        Assert.Contains("edge.microsoft.com", edge);
        Assert.Contains(id, edge);
    }

    // ===== manifest =====

    [Fact]
    public void MV3のactionからポップアップとアイコンを読む()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "manifest.json"), """
            {
              "name": "テスト拡張", "version": "2.1",
              "action": { "default_popup": "popup.html", "default_icon": { "16": "i16.png", "48": "i48.png" } }
            }
            """);

        var manifest = BrowserExtensionStore.ReadManifest(directory)!;

        Assert.Equal("テスト拡張", manifest.Name);
        Assert.Equal("2.1", manifest.Version);
        Assert.Equal("popup.html", manifest.PopupPath);
        Assert.Equal("i48.png", manifest.IconPath);
    }

    /// <summary>MV2 の browser_action も見る（新しい形だけ見ると、古い拡張機能のボタンが無反応になる）。</summary>
    [Fact]
    public void MV2のbrowser_actionも読む()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"name\":\"旧式\",\"browser_action\":{\"default_popup\":\"ui/popup.html\"},\"icons\":{\"128\":\"i.png\"}}");

        var manifest = BrowserExtensionStore.ReadManifest(directory)!;

        Assert.Equal("ui/popup.html", manifest.PopupPath);
        Assert.Equal("i.png", manifest.IconPath);
    }

    [Fact]
    public void ポップアップを持たない拡張機能はnullになる()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\"name\":\"内容スクリプトだけ\",\"version\":\"1\"}");

        var manifest = BrowserExtensionStore.ReadManifest(directory)!;

        Assert.Null(manifest.PopupPath);
        Assert.Null(manifest.OptionsPath);
    }

    /// <summary>設定画面は manifest からしか辿れない（WebView2 に chrome://extensions は無い）。
    /// MV3 の <c>options_ui.page</c> と、古い <c>options_page</c> の両方を読む。</summary>
    [Fact]
    public void 設定画面のページを読む()
    {
        var mv3 = TempDirectory();
        File.WriteAllText(Path.Combine(mv3, "manifest.json"),
            "{\"name\":\"新式\",\"options_ui\":{\"page\":\"dashboard.html\",\"open_in_tab\":true}}");
        var mv2 = TempDirectory();
        File.WriteAllText(Path.Combine(mv2, "manifest.json"), "{\"name\":\"旧式\",\"options_page\":\"options.html\"}");

        Assert.Equal("dashboard.html", BrowserExtensionStore.ReadManifest(mv3)!.OptionsPath);
        Assert.Equal("options.html", BrowserExtensionStore.ReadManifest(mv2)!.OptionsPath);
    }

    [Fact]
    public void manifestが無ければnull()
        => Assert.Null(BrowserExtensionStore.ReadManifest(TempDirectory()));

    // ===== 出所の記録 =====

    [Fact]
    public void 出所を覚えて引き直せる()
    {
        var root = Path.Combine(TempDirectory(), "BrowserExtensions");
        var store = new BrowserExtensionStore(root, () => new System.Net.Http.HttpClient());

        store.Remember(new BrowserExtensionRecord { Id = "abc", FolderPath = store.FolderFor("abc"), StoreId = "abc" });
        store.Remember(new BrowserExtensionRecord { Id = "def", FolderPath = @"C:\my\ext" });

        var records = store.LoadRecords();
        Assert.Equal(2, records.Count);
        Assert.Equal(@"C:\my\ext", records.Single(r => r.Id == "def").FolderPath);
    }

    /// <summary>削除で消してよい実体は<b>こちらが展開したものだけ</b>。
    /// 使う側が指定したフォルダーはその人の持ち物なので触らない。</summary>
    [Fact]
    public void 削除で消すのは自分が展開したフォルダーだけ()
    {
        var temp = TempDirectory();
        var root = Path.Combine(temp, "BrowserExtensions");
        var store = new BrowserExtensionStore(root, () => new System.Net.Http.HttpClient());

        var downloaded = store.FolderFor("abc");
        Directory.CreateDirectory(downloaded);
        File.WriteAllText(Path.Combine(downloaded, "manifest.json"), "{}");
        var own = Path.Combine(temp, "my-ext");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "manifest.json"), "{}");

        store.Remember(new BrowserExtensionRecord { Id = "abc", FolderPath = downloaded, StoreId = "abc" });
        store.Remember(new BrowserExtensionRecord { Id = "def", FolderPath = own });
        store.Forget("abc");
        store.Forget("def");

        Assert.False(Directory.Exists(downloaded));
        Assert.True(Directory.Exists(own));
        Assert.Empty(store.LoadRecords());
    }

    /// <summary>記録が読めないときに空と見なすと、掃除が展開済みの拡張機能を<b>全部消す</b>。
    /// 書き込みの途中で落ちれば切れた JSON は現実に起こり得るので、そのときは何もしない。</summary>
    [Fact]
    public void 記録が壊れているときは掃除しない()
    {
        var temp = TempDirectory();
        var root = Path.Combine(temp, "BrowserExtensions");
        var store = new BrowserExtensionStore(root, () => new System.Net.Http.HttpClient());
        var folder = store.FolderFor("keep");
        Directory.CreateDirectory(folder);
        store.Remember(new BrowserExtensionRecord { Id = "keep", FolderPath = folder, StoreId = "keep" });
        File.WriteAllText(Path.Combine(temp, "browser-extensions.json"), "[{\"id\":\"keep\"");   // 切れた JSON

        store.CleanOrphanFolders();

        Assert.True(Directory.Exists(folder));
    }

    /// <summary>「掃除しない」の用心は<b>書く側でも守る</b>。読めない記録を空と見なして書き直すと、
    /// 記録は readable な嘘（その1件だけ）になり、次の掃除が他の展開済み拡張機能をまとめて消す。</summary>
    [Fact]
    public void 記録が壊れているときは書き足さない()
    {
        var temp = TempDirectory();
        var root = Path.Combine(temp, "BrowserExtensions");
        var store = new BrowserExtensionStore(root, () => new System.Net.Http.HttpClient());
        var keep = store.FolderFor("keep");
        Directory.CreateDirectory(keep);
        var recordPath = Path.Combine(temp, "browser-extensions.json");
        File.WriteAllText(recordPath, "[{\"id\":\"keep\"");   // 切れた JSON

        Assert.False(store.Remember(new BrowserExtensionRecord { Id = "new", FolderPath = store.FolderFor("new") }));
        Assert.False(store.Forget("keep"));

        Assert.Equal("[{\"id\":\"keep\"", File.ReadAllText(recordPath));   // 壊れたまま＝嘘の全体像を作らない
        store.CleanOrphanFolders();
        Assert.True(Directory.Exists(keep));
    }

    /// <summary>導入の最中は掃除しない（記録が書かれるのは登録の後なので、展開中のフォルダーが
    /// 「記録に無い＝取り残し」に見える）。</summary>
    [Fact]
    public void 掃除しない削除では展開中のフォルダーを消さない()
    {
        var temp = TempDirectory();
        var store = new BrowserExtensionStore(Path.Combine(temp, "BrowserExtensions"),
            () => new System.Net.Http.HttpClient());
        var installing = store.FolderFor("installing");
        Directory.CreateDirectory(installing);
        store.Remember(new BrowserExtensionRecord { Id = "abc", FolderPath = store.FolderFor("abc") });

        Assert.True(store.Forget("abc", cleanFolders: false));

        Assert.True(Directory.Exists(installing));
        Assert.Empty(store.LoadRecords());
    }

    /// <summary>置き換えは一時ファイル経由。書き途中の <c>.tmp</c> が残らないことも見ておく。</summary>
    [Fact]
    public void 記録の保存は一時ファイル経由で置き換える()
    {
        var temp = TempDirectory();
        var store = new BrowserExtensionStore(Path.Combine(temp, "BrowserExtensions"),
            () => new System.Net.Http.HttpClient());

        store.Remember(new BrowserExtensionRecord { Id = "abc", FolderPath = @"C:\x" });

        Assert.Single(store.LoadRecords());
        Assert.False(File.Exists(Path.Combine(temp, "browser-extensions.json.tmp")));
    }

    /// <summary>crx_id の長さが SHA256 と合わない壊れたヘッダで、展開ごと落とさない。</summary>
    [Fact]
    public void 壊れたcrx_idでも展開は続ける()
    {
        var destination = Path.Combine(TempDirectory(), "ext");
        var key = Encoding.ASCII.GetBytes("KEY");
        var crx = BuildCrx3(BuildZip(("manifest.json", "{}")), new[] { key }, crxId: new byte[64]);

        var result = CrxArchive.Extract(new MemoryStream(crx), destination);

        Assert.True(File.Exists(Path.Combine(destination, "manifest.json")));
        Assert.Equal(Convert.ToBase64String(key), result.PublicKey);   // 突き合わせできず先頭の鍵へ落ちる
    }

    /// <summary>削除の直後は WebView2 がフォルダーを掴んでいて消せないことがある。
    /// 記録に無いフォルダーは後から拾い直す（残ると数十MB がいつまでも居座る）。</summary>
    [Fact]
    public void 記録に無い展開フォルダーは後から掃除する()
    {
        var root = Path.Combine(TempDirectory(), "BrowserExtensions");
        var store = new BrowserExtensionStore(root, () => new System.Net.Http.HttpClient());
        var kept = store.FolderFor("keep");
        var orphan = store.FolderFor("orphan");
        Directory.CreateDirectory(kept);
        Directory.CreateDirectory(orphan);
        store.Remember(new BrowserExtensionRecord { Id = "keep", FolderPath = kept, StoreId = "keep" });

        store.CleanOrphanFolders();

        Assert.True(Directory.Exists(kept));
        Assert.False(Directory.Exists(orphan));
    }
}

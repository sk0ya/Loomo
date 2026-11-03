using System.IO;
using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ターミナルサポートの画像（§39.10）：Claude の記録の画像の在りか（貼った画像・ツールが見た画像、行と何枚目）と引き直し、
/// 出力の文字から画像ファイルの名前を拾う、置き場が同じ画像を1回だけ書く、会話・出力のページに並べる。
/// </summary>
public sealed class TerminalSupportImageTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"loomo-tsimg-{Guid.NewGuid():N}");

    // 1×1 の PNG。
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC";

    public TerminalSupportImageTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    private static object Image(string data = Png, string mediaType = "image/png")
        => new { type = "image", source = new { type = "base64", data, media_type = mediaType } };

    private static string PromptWithImage(string text, string at, params object[] images)
        => JsonSerializer.Serialize(new
        {
            type = "user", timestamp = at,
            message = new { role = "user", content = new object[] { new { type = "text", text } }.Concat(images).ToArray() },
        });

    private static string ReadCall(string id, string path, string at)
        => JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = at,
            message = new
            {
                id = "m-" + id, role = "assistant",
                content = new object[] { new { type = "tool_use", id, name = "Read", input = new { file_path = path } } },
                usage = new { input_tokens = 1, output_tokens = 1, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
            },
        });

    private static string ImageResult(string toolUseId, string at, string data = Png)
        => JsonSerializer.Serialize(new
        {
            type = "user", timestamp = at,
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolUseId, content = new[] { Image(data) } } } },
        });

    private static string Reply(string text, string at)
        => JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = at,
            message = new
            {
                id = "r-" + at, role = "assistant", content = new object[] { new { type = "text", text } },
                usage = new { input_tokens = 1, output_tokens = 1, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
            },
        });

    private static string[] Session(string secondPng) =>
    [
        PromptWithImage("[Image #1] [Image #2] この画面を見て", "2026-11-03T01:00:00Z", Image(), Image(secondPng, "image/jpeg")),
        ReadCall("t1", @"C:\ws\shot.png", "2026-11-03T01:00:01Z"),
        ImageResult("t1", "2026-11-03T01:00:02Z"),
        Reply("見ました。", "2026-11-03T01:00:03Z"),
        PromptWithImage("次", "2026-11-03T01:01:00Z"),
        Reply("はい。", "2026-11-03T01:01:01Z"),
    ];

    [Fact]
    public void Digest_TurnImages_PromptAndToolImagesWithLineAndIndex()
    {
        var digest = ClaudeSessionDigest.Build("s1", @"C:\t\s1.jsonl", DateTime.UtcNow, Session(Png));

        var first = digest.Turns[0];
        Assert.Equal(3, first.Images.Count);
        // 貼った画像は頼みごとの行そのもの（1行目）に、文書順に 0, 1。
        Assert.Equal([(1, 0, "image/png", true), (1, 1, "image/jpeg", true)],
            first.Images.Where(i => i.FromPrompt).Select(i => (i.Line, i.Index, i.MediaType, i.FromPrompt)));
        // Read の結果の画像は、その呼び出しの id つき。
        var seen = Assert.Single(first.Images, i => !i.FromPrompt);
        Assert.Equal((3, 0, "t1"), (seen.Line, seen.Index, seen.ToolUseId));
        Assert.Empty(digest.Turns[1].Images);
    }

    [Fact]
    public void Lookup_ReadImage_ReturnsTheNthImageOfTheLine()
    {
        const string second = "/9j/AAEC";   // 中身の区別がつけば足りる（JPEG の頭 FF D8 FF …）
        var path = Path.Combine(_base, "s1.jsonl");
        File.WriteAllLines(path, Session(second));
        var digest = ClaudeSessionDigest.Read(path)!;
        var images = digest.Turns[0].Images;

        Assert.Equal(Convert.FromBase64String(Png), ClaudeTranscriptLookup.ReadImage(images[0]));
        Assert.Equal(Convert.FromBase64String(second), ClaudeTranscriptLookup.ReadImage(images[1]));
        Assert.Equal(Convert.FromBase64String(Png), ClaudeTranscriptLookup.ReadImage(images[2]));
        Assert.Null(ClaudeTranscriptLookup.ReadImage(images[0] with { Index = 5 }));
    }

    [Fact]
    public void ConversationBody_ShowsPastedAndViewedImages_FileCaptionLinksToTheReadFile()
    {
        var digest = ClaudeSessionDigest.Build("s1", @"C:\t\s1.jsonl", DateTime.UtcNow, Session(Png));

        var body = ClaudeConversationDocument.BuildBody(digest, _ => true, p => p, working: false,
            imageUrl: image => $"https://page.loomo/tsimg/{image.Line}-{image.Index}.png");

        Assert.Contains("https://page.loomo/tsimg/1-0.png", body);
        Assert.Contains("https://page.loomo/tsimg/1-1.png", body);
        Assert.Contains("https://page.loomo/tsimg/3-0.png", body);
        Assert.Contains("Claude が見た画像", body);
        Assert.Contains(new Uri(@"C:\ws\shot.png").AbsoluteUri, body);
        // 体裁は1回だけ（並びごとに style を重ねない）。
        Assert.Equal(1, CountOf(body, ".tsi-row {"));
        // 画像の口が無ければ出さない。
        Assert.DoesNotContain("tsimg", ClaudeConversationDocument.BuildBody(digest, _ => true, p => p, working: false));
    }

    [Fact]
    public void ConversationBody_UnreadableImagesAreLeftOut()
    {
        var digest = ClaudeSessionDigest.Build("s1", @"C:\t\s1.jsonl", DateTime.UtcNow, Session(Png));

        var body = ClaudeConversationDocument.BuildBody(digest, _ => true, p => p, working: false, imageUrl: _ => null);

        Assert.DoesNotContain("<img", body);
        Assert.DoesNotContain("Claude が見た画像", body);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ===== 出力の文字から画像ファイルを拾う =====

    [Fact]
    public void FindPaths_RelativeAbsoluteQuoted_OnlyExisting_InOrder_NoDuplicates()
    {
        var shots = Directory.CreateDirectory(Path.Combine(_base, "shots"));
        var a = Path.Combine(shots.FullName, "a.png");
        var spaced = Path.Combine(_base, "with space.jpg");
        var abs = Path.Combine(_base, "abs.webp");
        foreach (var file in new[] { a, spaced, abs })
            File.WriteAllBytes(file, [1]);
        var text = $"""
            saved shots/a.png
            "{spaced}" を書きました
            {abs}
            missing.png が見つかりません
            again: shots\a.png
            notes.txt
            """;

        var found = TerminalOutputImages.FindPaths(text, _base, File.Exists);

        Assert.Equal([a, spaced, abs], found);
    }

    [Fact]
    public void FindPaths_GitBashDrivePath()
    {
        var file = Path.Combine(_base, "g.png");
        File.WriteAllBytes(file, [1]);
        var bash = "/" + char.ToLowerInvariant(file[0]) + file[2..].Replace('\\', '/');

        Assert.Equal([file], TerminalOutputImages.FindPaths("wrote " + bash, null, File.Exists));
    }

    [Fact]
    public void FindPaths_CapsTheCount()
    {
        var text = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"img{i}.png"));

        Assert.Equal(TerminalOutputImages.MaxImages, TerminalOutputImages.FindPaths(text, _base, _ => true).Count);
    }

    [Fact]
    public void FindPaths_WordsThatMerelyEndLikeAnImageAreNotTakenWithoutAFile()
        => Assert.Empty(TerminalOutputImages.FindPaths("version 1.png-ish error:x.png", _base, File.Exists));

    // ===== 置き場 =====

    [Fact]
    public void Store_WritesOncePerContent_AndCopiesFiles()
    {
        var store = new TerminalSupportImageStore(_base);
        var data = Convert.FromBase64String(Png);

        var first = store.ForBytes(data, "image/png");
        var again = store.ForBytes(data, "image/png");
        Assert.NotNull(first);
        Assert.Equal(first, again);
        Assert.StartsWith("https://page.loomo/tsimg/", first);
        Assert.Single(Directory.GetFiles(Path.Combine(_base, TerminalSupportImageStore.FolderName)));

        var source = Path.Combine(_base, "pic.jpg");
        File.WriteAllBytes(source, data);
        var copied = store.ForFile(source)!;
        Assert.EndsWith(".jpg", copied);
        var name = copied[(copied.LastIndexOf('/') + 1)..];
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_base, TerminalSupportImageStore.FolderName, name)));

        Assert.Null(store.ForFile(Path.Combine(_base, "nope.png")));
        Assert.Null(store.ForBytes([], "image/png"));
    }

    // ===== 出力の見せ直し =====

    [Fact]
    public void OutputPresentation_PutsInlineAndFileImagesAboveTheBody()
    {
        var store = new TerminalSupportImageStore(_base);
        var data = Convert.FromBase64String(Png);
        var file = Path.Combine(_base, "out.png");
        File.WriteAllBytes(file, data);
        var output = new TerminalCommandOutput("make-shot", "wrote out.png", 0, DateTime.UtcNow, false,
            WorkingDirectory: _base,
            Images: [new TerminalInlineImage(() => data, "image/png", 1, 1, "sixel", DateTime.UtcNow)]);
        var plain = new TerminalOutputPresentation(TerminalOutputShape.Plain, "テキスト", "<pre>wrote out.png</pre>");

        var shown = TerminalOutputImagePresentation.With(plain, output, store);

        Assert.Contains("端末に出た画像", shown.Body);
        Assert.Contains("Sixel · 1×1", shown.Body);
        Assert.Contains("出力に出てきた画像", shown.Body);
        Assert.EndsWith("<pre>wrote out.png</pre>", shown.Body);
        // 画像が無ければ元のまま。
        var none = output with { Text = "nothing", Images = null };
        Assert.Same(plain, TerminalOutputImagePresentation.With(plain, none, store));
    }
}

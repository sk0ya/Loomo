using System.Collections.Generic;
using System.IO;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Mermaid 単体ファイル（.mmd / .mermaid）のライブプレビュー。生の mermaid 記法テキストを
/// <c>&lt;pre class="mermaid"&gt;</c> で包んで <see cref="MarkdownPage"/> へ渡すだけ。ページには
/// mermaid ブートストラップ（<c>.mermaid</c> 要素があれば <c>assets.loomo/mermaid.min.js</c> を遅延
/// ロードして <c>mermaid.run()</c>）が常駐しているので追加配線は不要で図が描かれる。Markdown
/// プレビューと同じく <see cref="IEditorSupportIncrementalHtmlProvider"/> なので、編集中は本文
/// （&lt;pre class="mermaid"&gt;）だけを差し替えてフル再ナビゲート（＝チカチカ）を避ける。テーマは
/// Markdown プレビューと同じ <c>Appearance.MarkdownPreviewTheme</c> に合わせる。表示専用（書き戻しなし）。
/// </summary>
public sealed class MermaidEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".mmd", ".mermaid"];

    public MermaidEditorSupport(AiSettings settings)
    {
        _settings = settings;
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Mermaid: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => MarkdownPage.BuildPage(RenderBody(filePath, text), DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme);

    // 生テキストは textContent として mermaid が読むので HTML エンコードでよい（そのまま埋め込むと
    // <, > を含む記法がタグとして解釈されて壊れる／XSS になり得る）。空テキストは空の図ブロックにする。
    public string RenderBody(string filePath, string text)
        => $"<pre class=\"mermaid\">{MarkdownRenderer.Encode(text ?? "")}</pre>";

    // ページの体裁（対象ファイル・テーマ）だけを鍵にする。本文そのものは含めない＝同じファイルを
    // 同じテーマで編集している間は本文差し替えだけで更新できる。
    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport ペインへ表示するコンテンツの提供者。アクティブなエディタタブのファイルに
/// 対応する提供者が登録されていれば、EditorSupport ペインが自動でその内容を表示する
/// （Markdown ならプレビュー等）。新しい拡張子へ対応するには、表示方式に応じて
/// <see cref="IEditorSupportHtmlProvider"/>（WebView2 へ HTML）か
/// <see cref="IEditorSupportVisualProvider"/>（WPF コントロールをそのまま表示）を実装し、
/// App.xaml.cs の DI へ追加登録するだけでよい。
/// </summary>
public interface IEditorSupportProvider
{
    /// <summary>この provider が担当する拡張子（例: ".md"）。</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>ペインのヘッダーへ出す表示名（例: "Preview: README.md"）。</summary>
    string DescribeTitle(string filePath);
}

/// <summary>HTML を生成して EditorSupport ペインの WebView2 へ表示する提供者（Markdown プレビュー等）。</summary>
public interface IEditorSupportHtmlProvider : IEditorSupportProvider
{
    /// <summary>エディタの現在テキストから、表示用の完全な HTML ドキュメントを生成する。</summary>
    string RenderHtml(string filePath, string text);
}

/// <summary>
/// 同一ページ内で本文（&lt;body&gt; の中身）だけを差し替えて更新できる HTML 提供者。
/// 編集ごとにページをフル再ナビゲートすると WebView2 が真っ白に再構築されて<b>チカチカ</b>するため、
/// ページの体裁（テーマ・base href 等）が変わらない編集中は本文だけを差し替える。
/// ShellWindow は <see cref="PageContextKey"/> が前回と同じ間は <see cref="RenderBody"/> の結果を
/// その場へ流し込み、鍵が変わったとき（別ファイル・テーマ変更等）だけ <see cref="RenderHtml"/> で再構築する。
/// </summary>
public interface IEditorSupportIncrementalHtmlProvider : IEditorSupportHtmlProvider
{
    /// <summary>その場差し替え用の本文（&lt;body&gt; の中身だけ）。<see cref="RenderHtml"/> と同じ内容を生成する。</summary>
    string RenderBody(string filePath, string text);

    /// <summary>
    /// フル再構築が要るかを判定するためのページ体裁の鍵（対象ファイル・テーマ・base href 等）。
    /// 鍵が同じなら本文差し替えで足り、変われば再ナビゲートする。テキスト本文は鍵に含めない。
    /// </summary>
    string PageContextKey(string filePath);
}

/// <summary>
/// ファイルそのものを EditorSupport ペインの WebView2 へ直接ナビゲートして表示する提供者
/// （PDF・SVG・HTML 等、ブラウザが標準で開けるもの）。エディタ本文ではなくファイルパスを
/// そのまま開くので、テキストの内容には依存しない（バイナリでもよい）。
/// </summary>
public interface IEditorSupportUriProvider : IEditorSupportProvider
{
    /// <summary>WebView2 がナビゲートする URI（通常はファイルの <c>file://</c> URI）。</summary>
    string ResolveNavigationUri(string filePath);
}

/// <summary>
/// WPF コントロールをそのまま EditorSupport ペインへ表示する提供者（CSV/TSV グリッド等）。
/// ビューは提供者側が1つ保持して使い回す（ペインは1枚なので同時表示は常に1つ）。
/// </summary>
public interface IEditorSupportVisualProvider : IEditorSupportProvider
{
    /// <summary>ペインへ載せるビューを生成（初回）または再利用して返す。UI スレッドで呼ばれる。</summary>
    FrameworkElement GetOrCreateView();

    /// <summary>エディタの現在テキストをビューへ反映する。UI スレッドで呼ばれる。</summary>
    Task UpdateAsync(string filePath, string text);

    /// <summary>
    /// ビュー内での編集をエディタ本文へ書き戻すための通知（編集できない提供者は発火しなくてよい）。
    /// ShellWindow が購読し、追従中のエディタタブのテキストを差し替える。UI スレッドで発火する。
    /// </summary>
    event EventHandler<EditorSupportContentEdited>? ContentEdited;
}

/// <summary>ビジュアル提供者内の編集結果（エディタ本文へ書き戻す完全なテキスト）。</summary>
public sealed record EditorSupportContentEdited(string FilePath, string Text);

/// <summary>登録された <see cref="IEditorSupportProvider"/> からファイルに対応するものを解決する。</summary>
public sealed class EditorSupportRegistry
{
    private readonly IReadOnlyDictionary<string, IEditorSupportProvider> _providersByExtension;

    public EditorSupportRegistry(IEnumerable<IEditorSupportProvider> providers)
    {
        var map = new Dictionary<string, IEditorSupportProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            foreach (var extension in provider.SupportedExtensions)
            {
                var normalized = NormalizeExtension(extension);
                if (map.TryGetValue(normalized, out var existing))
                    throw new InvalidOperationException(
                        $"EditorSupport provider for '{normalized}' is already registered: " +
                        $"{existing.GetType().Name}, {provider.GetType().Name}");

                map.Add(normalized, provider);
            }
        }

        _providersByExtension = map;
    }

    /// <summary>ファイルに対応する最初の提供者を返す。未対応・パス無しは null。</summary>
    public IEditorSupportProvider? Resolve(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var extension = Path.GetExtension(filePath);
        return string.IsNullOrWhiteSpace(extension)
            ? null
            : _providersByExtension.GetValueOrDefault(NormalizeExtension(extension));
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Extension must not be empty.", nameof(extension));
        return normalized.StartsWith(".", StringComparison.Ordinal)
            ? normalized
            : "." + normalized;
    }
}

/// <summary>
/// プレビューの相対パス画像解決に使う、仮想ホストのマップ先フォルダと base href の決定。
/// base href を埋め込む <see cref="MarkdownEditorSupport"/> と、実際にマップする ShellWindow の
/// 両方がここを通ることで、判断が食い違わないようにする。
/// </summary>
public static class MarkdownPreviewPaths
{
    /// <summary>
    /// ファイルがワークスペースルート配下なら、ルートをマップ先にして base href をファイルの
    /// フォルダ位置（例: https://preview.loomo/docs/）にする。これで <c>../assets/img.png</c> のように
    /// ルート内で上のフォルダへ遡る画像も解決できる。ルート未設定・ルート外（別ドライブ含む）は
    /// 従来どおりファイルのフォルダをマップ先にする。
    /// </summary>
    public static (string MapFolder, string BaseHref) Resolve(string? workspaceRoot, string filePath)
    {
        var fileDir = Path.GetDirectoryName(filePath) ?? "";
        var hostRoot = $"https://{MarkdownRenderer.PreviewVirtualHost}/";

        if (string.IsNullOrWhiteSpace(workspaceRoot) || fileDir.Length == 0)
            return (fileDir, hostRoot);

        var rel = Path.GetRelativePath(workspaceRoot, fileDir);
        var outsideRoot = rel == ".."
            || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(rel); // 別ドライブだと GetRelativePath は絶対パスを返す

        if (outsideRoot)
            return (fileDir, hostRoot);

        var href = rel == "."
            ? hostRoot
            : hostRoot + string.Join('/',
                rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Select(Uri.EscapeDataString)) + "/";
        return (workspaceRoot, href);
    }
}

/// <summary>Markdown（.md / .markdown）のライブプレビュー。</summary>
public sealed class MarkdownEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private readonly IWorkspaceService _workspace;
    private static readonly string[] Extensions = [".md", ".markdown"];

    public MarkdownEditorSupport(AiSettings settings, IWorkspaceService workspace)
    {
        _settings = settings;
        _workspace = workspace;
    }

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Preview: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => MarkdownRenderer.RenderToHtml(
            text,
            DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme,
            // 相対パス画像の解決先。ShellWindow が同じ Resolve のマップ先を仮想ホストへ割り当てる。
            baseHref: MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).BaseHref);

    public string RenderBody(string filePath, string text) => MarkdownRenderer.RenderToBody(text);

    // ページの体裁が変わる要素（対象ファイル・テーマ・base href）だけを鍵にする。本文の変更は
    // 含めない＝同じファイルを編集している間は本文差し替えで更新できる。
    public string PageContextKey(string filePath)
        => string.Join(
            "\n",
            filePath,
            _settings.Appearance.MarkdownPreviewTheme,
            MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).BaseHref);
}

/// <summary>
/// ブラウザ（WebView2/Chromium）が標準で開けるファイル（PDF・SVG・HTML 等）を、ペインの
/// WebView2 へファイルの <c>file://</c> URI で直接ナビゲートして表示する提供者。レンダリングは
/// ブラウザ任せなので、専用ビューア無しで「とりあえず開ける」ものをまとめて受け持つ。
/// </summary>
public sealed class BrowserEditorSupport : IEditorSupportUriProvider
{
    // Chromium が外部プラグイン無しでそのまま描画できる拡張子。画像のラスタ形式は
    // ImageEditorSupport（ズーム・パン付き）が受け持つので、ここでは扱わない。
    private static readonly string[] Extensions =
        [".pdf", ".svg", ".html", ".htm", ".xhtml", ".mht", ".mhtml"];

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath)
    {
        var label = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        return label.Length == 0
            ? $"Browser: {Path.GetFileName(filePath)}"
            : $"{label}: {Path.GetFileName(filePath)}";
    }

    public string ResolveNavigationUri(string filePath)
        => new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
}

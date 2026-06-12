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
    /// <summary>このファイルに対応できるか（拡張子などで判定する）。</summary>
    bool CanSupport(string filePath);

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
    private readonly IReadOnlyList<IEditorSupportProvider> _providers;

    public EditorSupportRegistry(IEnumerable<IEditorSupportProvider> providers)
        => _providers = providers.ToList();

    /// <summary>ファイルに対応する最初の提供者を返す。未対応・パス無しは null。</summary>
    public IEditorSupportProvider? Resolve(string? filePath)
        => string.IsNullOrWhiteSpace(filePath)
            ? null
            : _providers.FirstOrDefault(p => p.CanSupport(filePath));
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
public sealed class MarkdownEditorSupport : IEditorSupportHtmlProvider
{
    private readonly AiSettings _settings;
    private readonly IWorkspaceService _workspace;

    public MarkdownEditorSupport(AiSettings settings, IWorkspaceService workspace)
    {
        _settings = settings;
        _workspace = workspace;
    }

    public bool CanSupport(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() is ".md" or ".markdown";

    public string DescribeTitle(string filePath) => $"Preview: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => MarkdownRenderer.RenderToHtml(
            text,
            DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme,
            // 相対パス画像の解決先。ShellWindow が同じ Resolve のマップ先を仮想ホストへ割り当てる。
            baseHref: MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).BaseHref);
}

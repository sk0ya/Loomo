using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport ペインへ表示するコンテンツの提供者。アクティブなエディタタブのファイルに
/// 対応する提供者が登録されていれば、EditorSupport ペインが自動でその内容を表示する
/// （Markdown ならプレビュー等）。新しい拡張子へ対応するには、この実装を App.xaml.cs の DI へ
/// 追加登録するだけでよい。
/// </summary>
public interface IEditorSupportProvider
{
    /// <summary>このファイルに対応できるか（拡張子などで判定する）。</summary>
    bool CanSupport(string filePath);

    /// <summary>ペインのヘッダーへ出す表示名（例: "Preview: README.md"）。</summary>
    string DescribeTitle(string filePath);

    /// <summary>エディタの現在テキストから、表示用の完全な HTML ドキュメントを生成する。</summary>
    string RenderHtml(string filePath, string text);
}

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

/// <summary>Markdown（.md / .markdown）のライブプレビュー。</summary>
public sealed class MarkdownEditorSupport : IEditorSupportProvider
{
    private readonly AiSettings _settings;

    public MarkdownEditorSupport(AiSettings settings) => _settings = settings;

    public bool CanSupport(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() is ".md" or ".markdown";

    public string DescribeTitle(string filePath) => $"Preview: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => MarkdownRenderer.RenderToHtml(text, DescribeTitle(filePath), _settings.Appearance.MarkdownPreviewTheme);
}

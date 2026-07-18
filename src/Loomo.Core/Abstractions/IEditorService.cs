using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>エディタ（sk0ya.Editor）への操作を抽象化。</summary>
public interface IEditorService
{
    Task OpenFileAsync(string path, CancellationToken ct = default);
    Task<string> GetActiveContentAsync(CancellationToken ct = default);
    Task<string> GetSelectedTextAsync(CancellationToken ct = default);
    /// <summary>保存コールバック付きの編集可能な仮想ドキュメントを開く。</summary>
    Task OpenDocumentAsync(EditorDocument document, CancellationToken ct = default);
    string? ActiveFilePath { get; }
}

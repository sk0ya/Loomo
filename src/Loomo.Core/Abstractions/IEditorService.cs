using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>エディタ（sk0ya.Editor）への操作を抽象化。</summary>
public interface IEditorService
{
    Task OpenFileAsync(string path);
    Task<string> GetActiveContentAsync();
    Task<string> GetSelectedTextAsync();
    /// <summary>保存コールバック付きの編集可能な仮想ドキュメントを開く。</summary>
    Task OpenDocumentAsync(EditorDocument document);
    string? ActiveFilePath { get; }
}

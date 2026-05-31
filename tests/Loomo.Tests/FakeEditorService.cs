using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>テスト用の no-op <see cref="IEditorService"/>。エディタ実体を持たない。</summary>
internal sealed class FakeEditorService : IEditorService
{
    public string? ActiveFilePath => null;
    public Task OpenFileAsync(string path) => Task.CompletedTask;
    public Task<string> GetActiveContentAsync() => Task.FromResult(string.Empty);
    public Task<string> GetSelectedTextAsync() => Task.FromResult(string.Empty);
    public Task<string> ShowDiffAsync(string path, string proposedContent) => Task.FromResult(string.Empty);
    public Task<bool> ApplyEditAsync(string path, string newContent) => Task.FromResult(true);
    public Task OpenDocumentAsync(EditorDocument document) => Task.CompletedTask;
}

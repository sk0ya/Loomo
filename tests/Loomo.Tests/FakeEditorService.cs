using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>テスト用の no-op <see cref="IEditorService"/>。エディタ実体を持たない。</summary>
internal sealed class FakeEditorService : IEditorService
{
    public string? ActiveFilePath => null;
    public Task OpenFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetActiveContentAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<string> GetSelectedTextAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task OpenDocumentAsync(EditorDocument document, CancellationToken ct = default) => Task.CompletedTask;
}

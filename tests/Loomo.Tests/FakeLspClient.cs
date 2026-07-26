using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// プロセスを起動しない <see cref="ILspClient"/>。LSP セッション（プール・参照カウント・書き手移譲・
/// 診断のファンアウト）をプロトコル実装抜きで検証するために、送られた通知だけを記録する。
/// </summary>
internal sealed class FakeLspClient : ILspClient
{
    public sealed record Notification(string Kind, string Uri, string Text, int Version);

    public string Executable { get; }
    public string Root { get; }
    public List<Notification> Sent { get; } = [];
    public int InitializeCount;
    public IReadOnlyList<string>? LastWorkspaceFolders;
    public bool Disposed;

    public FakeLspClient(string executable, string root)
    {
        Executable = executable;
        Root = root;
    }

    public bool IsRunning { get; private set; } = true;
    public bool SupportsFoldingRange => true;
    public bool SupportsWorkspaceSymbol { get; set; } = true;
    public bool SupportsRangeFormatting => true;
    public bool SupportsSemanticTokens => true;
    public bool SupportsSelectionRange => true;
    public bool SupportsWorkspaceDiagnostics { get; set; } = true;
    public SemanticTokensLegend? SemanticTokensLegend => null;

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;
    public event Action? Exited;

    /// <summary>サーバーのクラッシュを模す。</summary>
    public void Kill()
    {
        IsRunning = false;
        Exited?.Invoke();
    }

    public void PublishDiagnostics(string uri, IReadOnlyList<LspDiagnostic> diagnostics)
        => DiagnosticsChanged?.Invoke(this, new DiagnosticsChangedEventArgs(uri, diagnostics));

    public Task InitializeAsync(string rootUri) => InitializeAsync(rootUri, null);

    public Task InitializeAsync(string rootUri, IReadOnlyList<string>? workspaceFolderPaths)
    {
        Interlocked.Increment(ref InitializeCount);
        LastWorkspaceFolders = workspaceFolderPaths;
        return Task.CompletedTask;
    }

    public Task OpenDocumentAsync(string uri, string languageId, string text)
    {
        lock (Sent) Sent.Add(new Notification("didOpen", uri, text, 1));
        return Task.CompletedTask;
    }

    public Task ChangeDocumentAsync(string uri, int version, string text)
    {
        lock (Sent) Sent.Add(new Notification("didChange", uri, text, version));
        return Task.CompletedTask;
    }

    public Task CloseDocumentAsync(string uri)
    {
        lock (Sent) Sent.Add(new Notification("didClose", uri, "", 0));
        return Task.CompletedTask;
    }

    public int CountOf(string kind, string? uri = null)
    {
        lock (Sent)
            return Sent.Count(n => n.Kind == kind && (uri is null || n.Uri == uri));
    }

    public List<LspSymbolInformation> WorkspaceSymbols { get; } = [];

    public Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspSymbolInformation>>(WorkspaceSymbols);

    // ── 以下は本テスト群では使わない（既定値を返すだけ） ──────────────────────
    public Task<IReadOnlyList<LspCompletionItem>> GetCompletionAsync(string uri, LspPosition position, CancellationToken ct = default) => Empty<LspCompletionItem>();
    public Task<LspHover?> GetHoverAsync(string uri, LspPosition position, CancellationToken ct = default) => Task.FromResult<LspHover?>(null);
    public Task<(string Uri, int Line, int Column)?> GetDefinitionAsync(string uri, LspPosition position, CancellationToken ct = default) => Task.FromResult<(string, int, int)?>(null);
    public Task<LspSignatureHelp?> GetSignatureHelpAsync(string uri, LspPosition position, CancellationToken ct = default) => Task.FromResult<LspSignatureHelp?>(null);
    public Task<IReadOnlyList<LspTextEdit>> GetFormattingEditsAsync(string uri, int tabSize, bool insertSpaces, CancellationToken ct = default) => Empty<LspTextEdit>();
    public Task<IReadOnlyList<LspTextEdit>> GetRangeFormattingEditsAsync(string uri, LspRange range, int tabSize, bool insertSpaces, CancellationToken ct = default) => Empty<LspTextEdit>();
    public Task<LspWorkspaceEdit?> GetRenameAsync(string uri, LspPosition position, string newName, CancellationToken ct = default) => Task.FromResult<LspWorkspaceEdit?>(null);
    public Task<IReadOnlyList<LspLocation>> GetReferencesAsync(string uri, LspPosition position, bool includeDeclaration = true, CancellationToken ct = default) => Empty<LspLocation>();
    public Task<IReadOnlyList<LspFoldingRange>> GetFoldingRangesAsync(string uri, CancellationToken ct = default) => Empty<LspFoldingRange>();
    public Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(string uri, CancellationToken ct = default) => Empty<DocumentSymbol>();
    public Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(string uri, LspRange range, CancellationToken ct = default) => Empty<LspCodeAction>();
    public Task<IReadOnlyList<InlayHint>> GetInlayHintsAsync(string uri, LspRange range, CancellationToken ct = default) => Empty<InlayHint>();
    public Task<SemanticToken[]?> GetSemanticTokensAsync(string uri, CancellationToken ct = default) => Task.FromResult<SemanticToken[]?>(null);
    public Task<LspWorkspaceDiagnosticResult?> GetWorkspaceDiagnosticsAsync(CancellationToken ct = default) => Task.FromResult<LspWorkspaceDiagnosticResult?>(null);
    public Task<CallHierarchyItem?> PrepareCallHierarchyAsync(string uri, LspPosition pos, CancellationToken ct = default) => Task.FromResult<CallHierarchyItem?>(null);
    public Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item, CancellationToken ct = default) => Task.FromResult<CallHierarchyIncomingCall[]?>(null);
    public Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item, CancellationToken ct = default) => Task.FromResult<CallHierarchyOutgoingCall[]?>(null);
    public Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(string uri, LspPosition pos, CancellationToken ct = default) => Task.FromResult<TypeHierarchyItem?>(null);
    public Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item, CancellationToken ct = default) => Task.FromResult<TypeHierarchyItem[]?>(null);
    public Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item, CancellationToken ct = default) => Task.FromResult<TypeHierarchyItem[]?>(null);
    public Task<IReadOnlyList<DocumentHighlight>?> RequestDocumentHighlightAsync(string uri, int line, int character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DocumentHighlight>?>(null);
    public Task<IReadOnlyList<LspSelectionRange>?> RequestSelectionRangesAsync(string uri, IReadOnlyList<LspPosition> positions, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LspSelectionRange>?>(null);

    public void Dispose()
    {
        Disposed = true;
        IsRunning = false;
    }

    private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>([]);
}

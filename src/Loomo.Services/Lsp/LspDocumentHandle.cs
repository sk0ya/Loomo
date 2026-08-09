using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>
/// LSP 文書の参照検索で、宣言自身を結果へ含めるか指定するための Loomo 拡張口。
/// <see cref="ILspDocument.RequestReferencesAsync(int, int)"/> は Editor.Controls 側の
/// 既定値（宣言を含める）をそのまま使うため、EditorSupport の「使用箇所」ではこれを使って
/// 宣言を除外する。
/// </summary>
public interface ILspReferenceQuery
{
    Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(
        int line, int character, bool includeDeclaration, CancellationToken ct = default);
}

/// <summary>
/// 1ビューぶんの文書ハンドル。文書スコープの要求を、そのとき文書が載っているサーバーへ素通しする。
/// <see cref="Dispose"/> は「このビューぶんの参照を手放す」だけで、<c>didClose</c> が飛ぶのは
/// 最後のハンドルが消えたとき（<see cref="LspDocumentTable"/> が判断する）。
///
/// <para><b>スレッド:</b> イベントは背景スレッド（JSON-RPC 読み取りスレッド）で発火する。
/// ディスパッチャへのマーシャリングは購読側の責務。</para>
/// </summary>
internal sealed class LspDocumentHandle : ILspDocument, ILspReferenceQuery
{
    private readonly LspDocumentEntry _entry;
    private int _disposed;

    internal LspDocumentHandle(LspDocumentEntry entry, bool isWriter)
    {
        _entry = entry;
        IsWriter = isWriter;
    }

    public string Uri => _entry.Uri;
    public string FilePath => _entry.FilePath;
    public string LanguageId => _entry.LanguageId;

    public bool IsConnected => _disposed == 0 && _entry.Client.IsRunning;
    public bool IsReady => _disposed == 0 && _entry.Opened && _entry.Client.IsRunning;

    /// <summary>このハンドルがテキストの正本か。表示専用の2枚目以降は false（§30.3.4）。</summary>
    public bool IsWriter { get; internal set; }
    public int? Version => _entry.Version;

    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => _entry.Diagnostics;

    public bool ServerSupportsFoldingRange => _entry.Client.Client.SupportsFoldingRange;
    public bool ServerSupportsRangeFormatting => _entry.Client.Client.SupportsRangeFormatting;
    public bool ServerSupportsSelectionRange => _entry.Client.Client.SupportsSelectionRange;
    public bool ServerSupportsWorkspaceDiagnostics => _entry.Client.Client.SupportsWorkspaceDiagnostics;
    public IReadOnlyList<string> CompletionTriggerCharacters => _entry.Client.Client.CompletionTriggerCharacters;
    public IReadOnlyList<string> ServerCodeActionKinds => _entry.Client.Client.CodeActionKinds;
    public bool ServerSupportsCodeActionResolve => _entry.Client.Client.SupportsCodeActionResolve;

    public event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged;
    public event Action? StateChanged;
    public event Action<string>? StatusMessage;

    internal void RaiseDiagnostics(IReadOnlyList<LspDiagnostic> d) => DiagnosticsChanged?.Invoke(d);
    internal void RaiseStateChanged() => StateChanged?.Invoke();
    internal void RaiseStatus(string message) => StatusMessage?.Invoke(message);

    public void UpdateText(string text)
    {
        if (!IsWriter || _disposed != 0) return;
        _entry.UpdateText(text);
    }

    public Task<IReadOnlyList<LspCompletionItem>> RequestCompletionAsync(int line, int character, CancellationToken ct = default) =>
        IsReady
            ? _entry.Client.Client.GetCompletionAsync(Uri, new LspPosition(line, character), ct)
            : Empty<LspCompletionItem>();

    public Task<LspHover?> RequestHoverAsync(int line, int character) =>
        IsReady
            ? _entry.Client.Client.GetHoverAsync(Uri, new LspPosition(line, character))
            : Task.FromResult<LspHover?>(null);

    public Task<(string Uri, int Line, int Column)?> RequestDefinitionAsync(int line, int character) =>
        IsReady
            ? _entry.Client.Client.GetDefinitionAsync(Uri, new LspPosition(line, character))
            : Task.FromResult<(string Uri, int Line, int Column)?>(null);

    public Task<LspSignatureHelp?> RequestSignatureHelpAsync(int line, int character, CancellationToken ct = default) =>
        IsReady
            ? _entry.Client.Client.GetSignatureHelpAsync(Uri, new LspPosition(line, character), ct)
            : Task.FromResult<LspSignatureHelp?>(null);

    public Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName) =>
        IsReady
            ? _entry.Client.Client.GetRenameAsync(Uri, new LspPosition(line, character), newName)
            : Task.FromResult<LspWorkspaceEdit?>(null);

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character) =>
        RequestReferencesAsync(line, character, includeDeclaration: true);

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(
        int line, int character, bool includeDeclaration, CancellationToken ct = default) =>
        IsReady
            ? _entry.Client.Client.GetReferencesAsync(
                Uri, new LspPosition(line, character), includeDeclaration, ct)
            : Empty<LspLocation>();

    public Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character)
    {
        if (!IsReady) return Empty<LspCodeAction>();
        var pos = new LspPosition(line, character);
        return _entry.Client.Client.GetCodeActionsAsync(Uri, new LspRange(pos, pos));
    }

    public Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(
        LspRange range, IReadOnlyList<string>? only, CancellationToken ct = default) =>
        IsReady
            ? _entry.Client.Client.GetCodeActionsAsync(Uri, range, only, CurrentDiagnostics, ct)
            : Empty<LspCodeAction>();

    public Task<LspCodeAction?> ResolveCodeActionAsync(LspCodeAction action, CancellationToken ct = default) =>
        IsReady
            ? _entry.Client.Client.ResolveCodeActionAsync(action, ct)
            : Task.FromResult<LspCodeAction?>(null);

    public Task<bool> ExecuteCommandAsync(LspCodeActionCommand command, CancellationToken ct = default) =>
        IsReady
            ? _entry.Client.Client.ExecuteCommandAsync(command, ct)
            : Task.FromResult(false);

    public Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize, bool insertSpaces) =>
        IsReady
            ? _entry.Client.Client.GetFormattingEditsAsync(Uri, tabSize, insertSpaces)
            : Empty<LspTextEdit>();

    public Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize, bool insertSpaces) =>
        IsReady && ServerSupportsRangeFormatting
            ? _entry.Client.Client.GetRangeFormattingEditsAsync(Uri, range, tabSize, insertSpaces)
            : Empty<LspTextEdit>();

    public Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync() =>
        IsReady ? _entry.Client.Client.GetDocumentSymbolsAsync(Uri) : Empty<DocumentSymbol>();

    public Task<IReadOnlyList<LspFoldingRange>> RequestFoldingRangesAsync() =>
        IsReady && ServerSupportsFoldingRange
            ? _entry.Client.Client.GetFoldingRangesAsync(Uri)
            : Empty<LspFoldingRange>();

    public Task<IReadOnlyList<InlayHint>> RequestInlayHintsAsync(int startLine, int endLine) =>
        IsReady
            ? _entry.Client.Client.GetInlayHintsAsync(
                Uri, new LspRange(new LspPosition(startLine, 0), new LspPosition(endLine, 0)))
            : Empty<InlayHint>();

    public Task<SemanticToken[]?> RequestSemanticTokensAsync() =>
        IsReady
            ? _entry.Client.Client.GetSemanticTokensAsync(Uri)
            : Task.FromResult<SemanticToken[]?>(null);

    public Task<IReadOnlyList<DocumentHighlight>?> RequestDocumentHighlightAsync(int line, int character, CancellationToken ct = default) =>
        IsReady
            ? _entry.Client.Client.RequestDocumentHighlightAsync(Uri, line, character, ct)
            : Task.FromResult<IReadOnlyList<DocumentHighlight>?>(null);

    public async Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character)
    {
        if (!IsReady || !ServerSupportsSelectionRange) return null;
        var ranges = await _entry.Client.Client.RequestSelectionRangesAsync(
            Uri, [new LspPosition(line, character)]);
        return ranges is { Count: > 0 } ? ranges[0] : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DiagnosticsChanged = null;
        StateChanged = null;
        StatusMessage = null;
        _entry.Table.ReleaseHandle(_entry, this);
    }

    private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>([]);
}

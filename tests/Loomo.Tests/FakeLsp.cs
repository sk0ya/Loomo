using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 言語サーバーの偽物。<b>EditorSupport の描画がどう分岐するか</b>を確かめるためだけのもので、
/// 使うのは「準備できているか」「documentSymbols が何を返すか（無応答も含む）」
/// 「呼び出し階層と参照が何を返すか」の3点だけ。残りの口は呼ばれたら落とす——
/// 黙って空を返すと、テストが通っているのに実際は別経路を見ていた、が起こる。
/// </summary>
internal sealed class FakeLspDocument : ILspDocument, ILspReferenceQuery
{
    private readonly TaskCompletionSource<IReadOnlyList<DocumentSymbol>>? _pendingSymbols;
    private readonly IReadOnlyList<DocumentSymbol>? _symbols;

    /// <param name="symbols">documentSymbols の応答。null なら<b>永久に返さない</b>（無応答サーバー）。</param>
    public FakeLspDocument(string filePath, IReadOnlyList<DocumentSymbol>? symbols, bool ready = true)
    {
        FilePath = filePath;
        Uri = new Uri(filePath).AbsoluteUri;
        IsReady = ready;
        _symbols = symbols;
        if (symbols is null)
            _pendingSymbols = new TaskCompletionSource<IReadOnlyList<DocumentSymbol>>();
    }

    /// <summary>documentSymbols が呼ばれた回数（コールドスタートの取り直しを数える）。</summary>
    public int DocumentSymbolRequests { get; private set; }

    /// <summary>2回目以降の応答を差し替える（コールドスタート＝最初は空、あとから構造が出る、の再現）。</summary>
    public IReadOnlyList<DocumentSymbol>? LaterSymbols { get; set; }

    public IReadOnlyList<LspLocation> References { get; set; } = [];
    public bool? LastIncludeDeclaration { get; private set; }

    public string Uri { get; }
    public string FilePath { get; }
    public string LanguageId => "csharp";
    public bool IsConnected => true;
    public bool IsReady { get; }
    public bool IsWriter => true;
    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => [];
    public bool ServerSupportsFoldingRange => false;
    public bool ServerSupportsRangeFormatting => false;
    public bool ServerSupportsSelectionRange => false;
    public bool ServerSupportsWorkspaceDiagnostics => false;

    public Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync()
    {
        DocumentSymbolRequests++;
        if (_pendingSymbols is not null)
            return _pendingSymbols.Task;   // 無応答：期限切れに任せる
        return Task.FromResult(
            DocumentSymbolRequests > 1 && LaterSymbols is not null ? LaterSymbols : _symbols!);
    }

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character)
        => Task.FromResult(References);

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(
        int line, int character, bool includeDeclaration, CancellationToken ct = default)
    {
        LastIncludeDeclaration = includeDeclaration;
        return Task.FromResult(References);
    }

    public void UpdateText(string text) { }
    public void Dispose() { }

    public event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged { add { } remove { } }
    public event Action? StateChanged { add { } remove { } }
    public event Action<string>? StatusMessage { add { } remove { } }

    // ── 以下は EditorSupport の描画では使わない口 ──────────────────────────
    public Task<IReadOnlyList<LspCompletionItem>> RequestCompletionAsync(int line, int character, CancellationToken ct = default) => throw Unused();
    public Task<LspHover?> RequestHoverAsync(int line, int character) => throw Unused();
    public Task<(string Uri, int Line, int Column)?> RequestDefinitionAsync(int line, int character) => throw Unused();
    public Task<LspSignatureHelp?> RequestSignatureHelpAsync(int line, int character, CancellationToken ct = default) => throw Unused();
    public Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName) => throw Unused();
    public Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character) => throw Unused();
    public Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize, bool insertSpaces) => throw Unused();
    public Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize, bool insertSpaces) => throw Unused();
    public Task<IReadOnlyList<LspFoldingRange>> RequestFoldingRangesAsync() => throw Unused();
    public Task<IReadOnlyList<InlayHint>> RequestInlayHintsAsync(int startLine, int endLine) => throw Unused();
    public Task<SemanticToken[]?> RequestSemanticTokensAsync() => throw Unused();
    public Task<IReadOnlyList<DocumentHighlight>?> RequestDocumentHighlightAsync(int line, int character, CancellationToken ct = default) => throw Unused();
    public Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character) => throw Unused();

    private static NotSupportedException Unused()
        => new("EditorSupport の描画がこの LSP 要求を呼ぶのは想定外です。");
}

/// <summary>ワークスペーススコープの偽物（②呼び出しパネルの取得だけ）。</summary>
internal sealed class FakeLspWorkspace : ILspWorkspace
{
    /// <summary>prepareCallHierarchy の応答（null＝呼び出し階層を持たないシンボル）。</summary>
    public CallHierarchyItem? HierarchyItem { get; set; }
    public CallHierarchyIncomingCall[] Incoming { get; set; } = [];
    public CallHierarchyOutgoingCall[] Outgoing { get; set; } = [];

    public Task<CallHierarchyItem?> PrepareCallHierarchyAsync(string uri, int line, int character)
        => Task.FromResult(HierarchyItem);
    public Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item)
        => Task.FromResult<CallHierarchyIncomingCall[]?>(Incoming);
    public Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item)
        => Task.FromResult<CallHierarchyOutgoingCall[]?>(Outgoing);

    public ILspDocument? OpenDocument(string filePath, string initialText) => null;
    public bool IsServerAvailableFor(string extension) => true;
    public event Action<string, IReadOnlyList<LspDiagnostic>>? DiagnosticsPublished { add { } remove { } }
    public event Action? ServerStateChanged { add { } remove { } }

    public Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, bool isClass, CancellationToken ct = default) => throw Unused();
    public Task<LspWorkspaceDiagnosticResult?> RequestWorkspaceDiagnosticsAsync(CancellationToken ct = default) => throw Unused();
    public Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(string uri, int line, int character) => throw Unused();
    public Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item) => throw Unused();
    public Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item) => throw Unused();

    private static NotSupportedException Unused()
        => new("EditorSupport の描画がこのワークスペース要求を呼ぶのは想定外です。");
}

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// コマンドパレットの非同期検索を組み立てる。WPF には依存せず、入力のキャンセルと
/// ファイル・本文・LSP シンボル・コマンドの横断検索を一か所で管理する。
/// </summary>
public sealed class PaletteSearchCoordinator
{
    private readonly IWorkspaceSearchService _search;
    private CancellationTokenSource? _searchCts;

    public PaletteSearchCoordinator(IWorkspaceSearchService search) => _search = search;

    public void Cancel() => _searchCts?.Cancel();

    internal static IReadOnlyList<IEditorLspManager> ConnectedCodeManagers(
        Views.EditorTab? active, IReadOnlyList<Views.EditorTab> tabs,
        CodeEditorSupport support, Func<Views.EditorTab, IEditorLspManager?> getManager)
    {
        var seen = new HashSet<IEditorLspManager>();
        var result = new List<IEditorLspManager>();
        void Add(Views.EditorTab? tab)
        {
            if (tab is null || !support.CanHandle(tab.PeekFilePath)) return;
            var manager = getManager(tab);
            if (manager is { IsConnected: true } && seen.Add(manager)) result.Add(manager);
        }
        Add(active);
        foreach (var tab in tabs) Add(tab);
        return result;
    }

    internal static IReadOnlyList<PaletteCommand> TerminalMatches(
        TerminalTabView? view, string query, Action<TerminalMatch, TerminalTabView> select)
    {
        static PaletteCommand Status(string text) => new("ターミナル検索", text, static () => { });
        if (view is null) return new[] { Status("ターミナルがありません") };
        if (string.IsNullOrWhiteSpace(query)) return new[] { Status("入力してターミナル内を検索") };
        var matches = view.FindMatches(query, caseSensitive: false);
        if (matches.Count == 0) return new[] { Status("一致なし") };
        return matches.Take(200).Select(match => new PaletteCommand(
            $"行 {match.LineIndex + 1}", match.LineText.Trim(), () => select(match, view))).ToList();
    }

    public async Task<IReadOnlyList<PaletteCommand>?> SearchLatestAsync(
        PaletteMode mode,
        string query,
        IReadOnlyList<PaletteCommand> commands,
        Func<IReadOnlyList<IEditorLspManager>> lspManagers,
        Func<FileSearchHit, PaletteCommand> fileEntry,
        Func<ContentSearchHit, string, PaletteCommand> grepEntry,
        Func<LspSymbolInformation, string, PaletteCommand> symbolEntry)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;

        try
        {
            await Task.Delay(120, ct);
            var items = mode switch
            {
                PaletteMode.File => (await _search.FindFilesAsync(query, 50, ct)).Select(fileEntry).ToList(),
                PaletteMode.Grep => await BuildGrepMatchesAsync(query, grepEntry, ct),
                PaletteMode.Class => await BuildSymbolMatchesAsync(query, true, lspManagers(), symbolEntry, ct),
                PaletteMode.Symbol => await BuildSymbolMatchesAsync(query, false, lspManagers(), symbolEntry, ct),
                _ => await BuildAllMatchesAsync(query, commands, lspManagers(), fileEntry, symbolEntry, ct),
            };
            return ct.IsCancellationRequested ? null : items;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<PaletteCommand>> BuildGrepMatchesAsync(
        string query, Func<ContentSearchHit, string, PaletteCommand> entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(query))
            return Array.Empty<PaletteCommand>();
        var hits = await _search.GrepAsync(query, new GrepOptions(MaxResults: 200), ct);
        return hits.Select(hit => entry(hit, query)).ToList();
    }

    private static async Task<IReadOnlyList<PaletteCommand>> BuildSymbolMatchesAsync(
        string query,
        bool isClass,
        IReadOnlyList<IEditorLspManager> managers,
        Func<LspSymbolInformation, string, PaletteCommand> entry,
        CancellationToken ct)
    {
        var label = isClass ? "クラス" : "シンボル";
        if (string.IsNullOrWhiteSpace(query))
            return new[] { Status($"入力して{label}を検索") };
        if (managers.Count == 0)
            return new[] { Status("言語サーバーが未接続です（対象コードのファイルを開いてください）") };

        var symbols = await MergeWorkspaceSymbolsAsync(managers, query, isClass, ct);
        if (ct.IsCancellationRequested)
            return Array.Empty<PaletteCommand>();
        return symbols.Count == 0
            ? new[] { Status("一致なし") }
            : symbols.Take(200).Select(symbol => entry(symbol, label)).ToList();
    }

    private async Task<IReadOnlyList<PaletteCommand>> BuildAllMatchesAsync(
        string query,
        IReadOnlyList<PaletteCommand> commands,
        IReadOnlyList<IEditorLspManager> managers,
        Func<FileSearchHit, PaletteCommand> fileEntry,
        Func<LspSymbolInformation, string, PaletteCommand> symbolEntry,
        CancellationToken ct)
    {
        var items = new List<PaletteCommand>();
        var files = await _search.FindFilesAsync(query, 12, ct);
        if (ct.IsCancellationRequested)
            return items;
        items.AddRange(files.Select(fileEntry));

        if (managers.Count > 0)
        {
            var classes = await MergeWorkspaceSymbolsAsync(managers, query, true, ct);
            if (ct.IsCancellationRequested)
                return items;
            items.AddRange(classes.Take(10).Select(symbol => symbolEntry(symbol, "クラス")));

            var symbols = await MergeWorkspaceSymbolsAsync(managers, query, false, ct);
            if (ct.IsCancellationRequested)
                return items;
            items.AddRange(symbols.Where(symbol => !IsClassKind(symbol.Kind)).Take(10)
                .Select(symbol => symbolEntry(symbol, "シンボル")));
        }

        items.AddRange(PaletteFilter.Filter(commands, query).Take(8));
        return items;
    }

    internal static async Task<IReadOnlyList<LspSymbolInformation>> MergeWorkspaceSymbolsAsync(
        IReadOnlyList<IEditorLspManager> managers, string query, bool isClass, CancellationToken ct)
    {
        var merged = new List<LspSymbolInformation>();
        var seen = new HashSet<(string Name, string Uri, int Line)>();
        foreach (var manager in managers)
        {
            IReadOnlyList<LspSymbolInformation> symbols;
            try { symbols = await manager.GetWorkspaceSymbolsAsync(query, isClass, ct); }
            catch { continue; }
            if (ct.IsCancellationRequested)
                break;
            foreach (var symbol in symbols)
            {
                var key = (symbol.Name ?? "", symbol.Location?.Uri ?? "", symbol.Location?.Range?.Start?.Line ?? 0);
                if (seen.Add(key))
                    merged.Add(symbol);
            }
        }
        return merged;
    }

    private static PaletteCommand Status(string text) => new("シンボル検索", text, static () => { });

    private static bool IsClassKind(SymbolKind kind)
        => kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum;
}

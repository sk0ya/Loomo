using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services.Lsp;

public sealed record LspSourceFixAllResult(
    LspWorkspaceEdit? Edit,
    int DocumentsScanned,
    int ActionsFound,
    string? Error = null);

internal static class LspWorkspaceCompatibility
{
    public const string SourceFixAllKind = "source.fixAll";
}

/// <summary>
/// Loomo が所有する LSP セッション（設計書 §30）。言語サーバーのプロセス・プロトコル・文書同期を
/// **ワークスペース単位**で持ち、エディタコントロールへは <see cref="ILspDocument"/> のハンドルだけを渡す。
///
/// <para>これが単一の入口になるので、検索ペイン・アウトライン・診断集約といった
/// **エディタタブを経由しない消費者**が LSP を直接使える。以前はセッションがタブに紐付いていたため、
/// 「<c>.cs</c> タブを1枚も開いていないとクラス検索が 0 件」といった意味の壊れ方をしていた。</para>
///
/// <para><b>スレッド:</b> スレッドセーフ。<see cref="DiagnosticsPublished"/>/<see cref="ServerStateChanged"/>
/// と、配下の <see cref="ILspDocument"/> のイベントは**背景スレッドで発火する**。
/// ディスパッチャへのマーシャリングは購読側（LspViewBridge・Problems ペイン・EditorSupport）の責務。</para>
/// </summary>
public sealed class LspWorkspaceService : ILspWorkspace, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly ISolutionModelService? _solution;
    private readonly LspServerTable _servers;
    private readonly LspClientPool _pool;
    private readonly LspDocumentTable _documents;
    private readonly object _gate = new();

    private string[] _folderSignature;
    private string _projectContextSignature;
    private bool _disposed;

    /// <param name="connect">サーバー接続の生成。既定は実プロセス起動。テストが差し替える。</param>
    public LspWorkspaceService(
        IWorkspaceService workspace,
        LspServerTable servers,
        Func<LspServerDef, string, ILspClient>? connect = null,
        ISolutionModelService? solution = null)
    {
        _workspace = workspace;
        _solution = solution;
        _servers = servers;
        _folderSignature = CurrentFolders();
        _projectContextSignature = ProjectContextSignature(solution?.Current);

        _pool = new LspClientPool(FoldersForRoot, Log, connect);
        _documents = new LspDocumentTable(
            _pool,
            ext => _servers.GetForExtension(ext),
            ResolveRoot,
            Log,
            (uri, diagnostics) => DiagnosticsPublished?.Invoke(uri, diagnostics));

        _pool.DiagnosticsPublished += OnDiagnosticsPublished;
        _pool.ApplyEditRequested += (_, e) => ApplyEditRequested?.Invoke(this, e);
        _pool.ClientDied += pooled => _ = _documents.OnClientDiedAsync(pooled);
        _pool.StateChanged += () => ServerStateChanged?.Invoke();
        _servers.Changed += ext => _ = _documents.ReopenExtensionAsync(ext);
        _workspace.FoldersChanged += OnFoldersChanged;
        if (_solution is not null) _solution.Changed += OnSolutionChanged;
    }

    public event Action<string, IReadOnlyList<LspDiagnostic>>? DiagnosticsPublished;
    public event Action? ServerStateChanged;

    /// <summary>サーバー起点の <c>workspace/applyEdit</c>。コマンド型リファクタリング
    /// （tsserver の「関数へ抽出」等）の編集はこの経路でしか返ってこない。
    /// <b>背景スレッドで発火し、購読側が戻るまでサーバーは待っている</b>——
    /// 購読側は UI スレッドへマーシャルしてよいが、そこで LSP 応答を待ってはならない。</summary>
    public event EventHandler<LspApplyEditEventArgs>? ApplyEditRequested;

    public IReadOnlyList<LspServerRuntimeStatus> ServerStatuses => _pool.Statuses;
    public bool RestartServer(string executable) => _pool.Restart(executable);

    public ILspDocument? OpenDocument(string filePath, string initialText)
        => _disposed ? null : _documents.Open(filePath, initialText);

    public bool IsServerAvailableFor(string extension) => _documents.IsServerAvailableFor(extension);

    // ── ワークスペーススコープの問い合わせ ──────────────────────────────────

    public async Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(
        string query, bool isClass, CancellationToken ct = default)
    {
        var clients = await EnsureWorkspaceServersAsync(ct);
        var merged = new List<LspSymbolInformation>();
        var seen = new HashSet<(string Name, string Uri, int Line)>();

        foreach (var pooled in clients.Where(c => c.Client.SupportsWorkspaceSymbol))
        {
            IReadOnlyList<LspSymbolInformation> symbols;
            try { symbols = await pooled.Client.GetWorkspaceSymbolsAsync(query, ct); }
            catch { continue; }
            if (ct.IsCancellationRequested) break;

            foreach (var symbol in SymbolSearchFilter.FilterByKind(symbols, isClass))
            {
                var key = (symbol.Name ?? "", symbol.Location?.Uri ?? "",
                           symbol.Location?.Range?.Start?.Line ?? 0);
                if (seen.Add(key)) merged.Add(symbol);
            }
        }
        return merged;
    }

    public async Task<LspWorkspaceDiagnosticResult?> RequestWorkspaceDiagnosticsAsync(CancellationToken ct = default)
    {
        var clients = await EnsureWorkspaceServersAsync(ct);
        var capable = clients.Where(c => c.Client.SupportsWorkspaceDiagnostics).ToArray();
        if (capable.Length == 0) return null;

        var tasks = capable.Select(c => c.Client.GetWorkspaceDiagnosticsAsync(ct)).ToArray();
        await Task.WhenAll(tasks);

        var results = tasks.Select(t => t.Result).OfType<LspWorkspaceDiagnosticResult>().ToArray();
        if (results.Length == 0) return null;

        return LspWorkspaceDiagnosticAggregator.CreateResult(results.SelectMany(r => r.Documents));
    }

    /// <summary>
    /// プロジェクト／ソリューションのCompile項目を順に問い合わせ、source.fixAllのWorkspaceEditを
    /// 一つへ統合する。Editor側は文書単位のUIを持つが、C#のFix allはプロジェクト境界を知る必要が
    /// あるため、ファイル列の列挙とLSPセッションの共有はこのワークスペース所有者へ置く。
    /// </summary>
    public async Task<LspSourceFixAllResult> RequestSourceFixAllAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
        => await RequestSourceFixAllAsync(filePaths, null, ct);

    /// <summary>未保存エディタ本文を優先してsource.fixAllを問い合わせる。指定された本文は
    /// 既存のLSP文書へdidChangeとして反映してから要求するため、サーバーがディスク本文を
    /// 参照する競合を避ける。</summary>
    public async Task<LspSourceFixAllResult> RequestSourceFixAllAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlyDictionary<string, string>? currentTexts,
        CancellationToken ct = default)
    {
        var changes = new Dictionary<string, List<LspTextEdit>>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var operations = new List<LspFileOperation>();
        var operationKeys = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        var actionsFound = 0;

        foreach (var rawPath in filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(rawPath)) continue;

            var text = TryGetCurrentText(currentTexts, rawPath, out var currentText)
                ? currentText ?? string.Empty
                : await File.ReadAllTextAsync(rawPath, ct);
            using var document = OpenDocument(rawPath, text);
            if (document is null)
                continue;
            if (TryGetCurrentText(currentTexts, rawPath, out currentText))
                _documents.UpdateOpenDocumentText(rawPath, currentText);
            if (!await WaitUntilReadyAsync(document, ct))
                continue;

            scanned++;
            var range = FullDocumentRange(text);
            IReadOnlyList<LspCodeAction> actions;
            try
            {
                actions = await document.RequestCodeActionsAsync(
                    range, [LspWorkspaceCompatibility.SourceFixAllKind], ct);
            }
            catch
            {
                continue;
            }

            foreach (var original in actions)
            {
                ct.ThrowIfCancellationRequested();
                var action = original;
                if (action.Kind is { Length: > 0 } kind &&
                    !LspCodeActionKinds.Matches(kind, LspWorkspaceCompatibility.SourceFixAllKind) &&
                    !LspCodeActionKinds.Matches(LspWorkspaceCompatibility.SourceFixAllKind, kind))
                    continue;
                if (action.Edit is null && action.NeedsResolve)
                    action = await document.ResolveCodeActionAsync(action, ct) ?? action;
                if (action.Edit is not { } edit || edit.Changes.Count == 0)
                    continue; // command-only source.fixAll needs a server-specific applyEdit path.

                actionsFound++;
                if (MergeWorkspaceEdit(edit, changes, versions, operations, operationKeys)
                    is { } mergeError)
                    return new LspSourceFixAllResult(null, scanned, actionsFound, mergeError);
            }
        }

        if (changes.Count == 0 && operations.Count == 0)
            return new LspSourceFixAllResult(null, scanned, actionsFound);

        var merged = changes.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<LspTextEdit>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return new LspSourceFixAllResult(
            new LspWorkspaceEdit(merged, versions.Count == 0 ? null : versions, operations),
            scanned,
            actionsFound);
    }

    private static bool TryGetCurrentText(
        IReadOnlyDictionary<string, string>? currentTexts,
        string path,
        out string text)
    {
        if (currentTexts is not null && currentTexts.TryGetValue(path, out text!))
            return true;
        var match = currentTexts?.FirstOrDefault(pair =>
            string.Equals(Path.GetFullPath(pair.Key), path, StringComparison.OrdinalIgnoreCase));
        if (match is { } found)
        {
            text = found.Value;
            return true;
        }
        text = "";
        return false;
    }

    private static async Task<bool> WaitUntilReadyAsync(
        ILspDocument document, CancellationToken ct)
    {
        const int maxWaitMs = 45_000;
        var waited = 0;
        while (!document.IsReady && document.IsConnected && waited < maxWaitMs)
        {
            await Task.Delay(100, ct);
            waited += 100;
        }
        return document.IsReady && document.IsConnected;
    }

    private static LspRange FullDocumentRange(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lastNewline = normalized.LastIndexOf('\n');
        var line = normalized.Count(c => c == '\n');
        var character = lastNewline < 0 ? normalized.Length : normalized.Length - lastNewline - 1;
        return new LspRange(new LspPosition(0, 0), new LspPosition(line, character));
    }

    private static string? MergeWorkspaceEdit(
        LspWorkspaceEdit edit,
        Dictionary<string, List<LspTextEdit>> changes,
        Dictionary<string, int?> versions,
        List<LspFileOperation> operations,
        HashSet<string> operationKeys)
    {
        foreach (var (uri, edits) in edit.Changes)
        {
            if (!changes.TryGetValue(uri, out var target))
                changes[uri] = target = [];
            foreach (var candidate in edits)
            {
                if (target.Contains(candidate)) continue;
                if (target.Any(existing => RangesOverlap(existing.Range, candidate.Range)))
                    return $"{uri}: source.fixAllの編集範囲が競合しています。";
                target.Add(candidate);
            }
        }

        if (edit.DocumentVersions is not null)
            foreach (var (uri, version) in edit.DocumentVersions)
            {
                if (versions.TryGetValue(uri, out var existing) &&
                    existing is not null && version is not null && existing != version)
                    return $"{uri}: source.fixAllの文書版が競合しています。";
                versions[uri] = version;
            }

        if (edit.FileOperations is not null)
            foreach (var operation in edit.FileOperations)
            {
                var key = $"{operation.Kind}|{operation.Uri}|{operation.NewUri}|{operation.Overwrite}|{operation.IgnoreIfExists}|{operation.Recursive}|{operation.IgnoreIfNotExists}";
                if (operationKeys.Add(key)) operations.Add(operation);
            }
        return null;
    }

    private static bool RangesOverlap(LspRange left, LspRange right)
    {
        var leftEmpty = ComparePositions(left.Start, left.End) == 0;
        var rightEmpty = ComparePositions(right.Start, right.End) == 0;
        if (leftEmpty && rightEmpty)
            return ComparePositions(left.Start, right.Start) == 0;
        return ComparePositions(left.Start, right.End) < 0 &&
               ComparePositions(right.Start, left.End) < 0;
    }

    private static int ComparePositions(LspPosition left, LspPosition right)
        => left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Character.CompareTo(right.Character);

    public async Task<CallHierarchyItem?> PrepareCallHierarchyAsync(string uri, int line, int character)
    {
        var client = ClientForUri(uri);
        if (client is null) return null;
        return await client.PrepareCallHierarchyAsync(uri, new LspPosition(line, character));
    }

    public async Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item)
    {
        var client = ClientForUri(item.Uri);
        return client is null ? null : await client.GetIncomingCallsAsync(item);
    }

    public async Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item)
    {
        var client = ClientForUri(item.Uri);
        return client is null ? null : await client.GetOutgoingCallsAsync(item);
    }

    public async Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(string uri, int line, int character)
    {
        var client = ClientForUri(uri);
        if (client is null) return null;
        return await client.PrepareTypeHierarchyAsync(uri, new LspPosition(line, character));
    }

    public async Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item)
    {
        var client = ClientForUri(item.Uri);
        return client is null ? null : await client.GetSupertypesAsync(item);
    }

    public async Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item)
    {
        var client = ClientForUri(item.Uri);
        return client is null ? null : await client.GetSubtypesAsync(item);
    }

    // ── 解決とライフサイクル ────────────────────────────────────────────────

    /// <summary>
    /// URI に対応するサーバー。まず開いている文書から、無ければ拡張子から解決して**起動せずに**
    /// プール内の該当クライアントを探す（呼び出し階層は既に開いている文書の続きなので起動は要らない）。
    /// </summary>
    private ILspClient? ClientForUri(string uri)
    {
        if (_documents.Find(uri) is { } entry && entry.Client.IsRunning)
            return entry.Client.Client;

        var localPath = LspUri.TryToLocalPath(uri);
        if (localPath is null) return null;
        var def = _servers.GetForExtension(Path.GetExtension(localPath));
        if (def is null) return null;
        return _pool.Running.FirstOrDefault(c =>
            string.Equals(c.Executable, def.Executable, StringComparison.OrdinalIgnoreCase))?.Client;
    }

    /// <summary>
    /// ワークスペーススコープの問い合わせに答えられるサーバーを揃える。まだ1本も動いていなければ、
    /// ワークスペースルートのプロジェクトマーカー（<c>*.sln</c>/<c>package.json</c>/<c>Cargo.toml</c>…）
    /// から**そのワークスペースの言語**を割り出して起動する。
    /// これがあるので「タブを1枚も開いていなくてもクラス検索が効く」。
    /// </summary>
    private async Task<IReadOnlyList<PooledLspClient>> EnsureWorkspaceServersAsync(CancellationToken ct)
    {
        var running = _pool.Running;
        if (running.Count == 0)
        {
            foreach (var ext in DetectWorkspaceExtensions())
            {
                if (ct.IsCancellationRequested) break;
                var def = _servers.GetForExtension(ext);
                if (def is null) continue;
                var root = _workspace.Folders.FirstOrDefault() ?? Environment.CurrentDirectory;
                // 参照カウントは 0 のまま（文書が無いので）。5分アイドルで自動的に落ちる。
                var pooled = _pool.Acquire(def, Path.GetFullPath(root));
                if (pooled is not null) _pool.Release(pooled);
            }
            running = _pool.Running;
        }

        // 初期化が終わっていないクライアントに問い合わせても無駄なので待ち合わせる。
        await Task.WhenAll(running.Select(c => c.Ready));
        return running.Where(c => c.IsRunning).ToList();
    }

    /// <summary>ワークスペースルートに置かれたプロジェクトマーカーから、扱う言語の拡張子を推定する。</summary>
    private IEnumerable<string> DetectWorkspaceExtensions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _workspace.Folders)
        {
            foreach (var (marker, ext) in ProjectMarkers)
                if (HasMarker(folder, marker) && seen.Add(ext))
                    yield return ext;
        }
    }

    private static bool HasMarker(string folder, string marker)
    {
        try
        {
            return marker.StartsWith('*')
                ? Directory.EnumerateFiles(folder, marker).Any()
                : File.Exists(Path.Combine(folder, marker));
        }
        catch
        {
            return false;   // 消えた／権限の無いフォルダーは読み飛ばす
        }
    }

    private static readonly (string Marker, string Extension)[] ProjectMarkers =
    [
        ("*.sln", ".cs"),
        ("*.slnx", ".cs"),
        ("*.csproj", ".cs"),
        ("package.json", ".ts"),
        ("tsconfig.json", ".ts"),
        ("Cargo.toml", ".rs"),
        ("go.mod", ".go"),
        ("pyproject.toml", ".py"),
        ("requirements.txt", ".py"),
    ];

    /// <summary>
    /// このファイルを担当するサーバーのワークスペースルート＝**そのファイルを含むワークスペース
    /// フォルダー**。プールのキーの一部なので、同じフォルダーのファイルは同じ値になり、
    /// フォルダー1つにつきサーバー1本になる。
    ///
    /// <para><b>以前は常にプライマリを返し、実フォルダー一覧を <c>initialize</c> の
    /// <c>workspaceFolders</c> で全件渡していた（1本で全ルートを見る構成）。これをやめた理由は実測</b>
    /// ——同じファイル・同じ範囲でも <c>workspaceFolders</c> が2件あると Roslyn が
    /// 「メソッドの抽出」を返さなくなる（[Loomo] なら5秒で2件、[Loomo, AimAssist] だと120秒待っても
    /// 0件。§32.4.4）。構文だけで済むリファクタリングは出るのに、データフロー解析を要するものだけが
    /// 落ちるため、原因が非常に見えにくい。</para>
    ///
    /// <para>フォルダー同士が祖先/子孫にならないことは <see cref="IWorkspaceService.AddFolder"/> が
    /// 保証しているので、フォルダーごとにルートを分けても担当範囲は重ならない
    /// （§30.0-6 が禁じた「含んでいるフォルダーをルートに選ぶ」には当たらない）。
    /// どのフォルダーにも属さない場合と未オープン時はファイルのディレクトリへフォールバックする。</para>
    /// </summary>
    private string ResolveRoot(string filePath)
    {
        if (_workspace.FolderFor(filePath) is { } folder) return Path.GetFullPath(folder);

        var folders = _workspace.Folders;
        return folders.Count > 0
            ? Path.GetFullPath(folders[0])
            : Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Environment.CurrentDirectory;
    }

    /// <summary><c>initialize</c> で通知するフォルダー。**そのサーバーのルート1件だけ**にする
    /// （複数渡すと Roslyn の抽出系リファクタリングが返らなくなる。<see cref="ResolveRoot"/> 参照）。</summary>
    private static string[] FoldersForRoot(string root) => [root];

    private string[] CurrentFolders() => _workspace.Folders.ToArray();

    private void OnFoldersChanged(object? sender, EventArgs e)
    {
        var next = CurrentFolders();
        lock (_gate)
        {
            if (_folderSignature.SequenceEqual(next, StringComparer.OrdinalIgnoreCase)) return;
            _folderSignature = next;
        }
        // ワークスペースが変わったサーバーは即時終了する（アイドル維持はしない）。抱えたままだと
        // Roslyn が前のソリューションのメモリを保持し続け、しかもプロジェクト一覧が古いままになる。
        Log("[LSP] workspace folders changed — shutting down all servers");
        _documents.Clear();
        _pool.DisposeAll();
    }

    /// <summary>
    /// C# プロジェクトの解析対象 TFM／Build構成が変わったとき、既存の Roslyn セッションを再初期化する。
    ///
    /// <para>LSP の標準仕様には「現在選択中の TargetFramework」という共通設定が無く、
    /// サーバーごとに initialization option の形が違う。そのため汎用 Editor 側へ C# 固有設定を
    /// 持ち込まず、ここでは少なくとも旧セッションのプロジェクト／診断キャッシュを破棄して
    /// 最新テキストで開き直す。将来サーバー固有の設定を追加するときも、この C# サービスから
    /// セッション境界を越えて渡す場所を一箇所にできる。</para>
    /// </summary>
    private void OnSolutionChanged(object? sender, SolutionModel model)
    {
        if (_disposed) return;
        var next = ProjectContextSignature(model);
        lock (_gate)
        {
            if (string.Equals(_projectContextSignature, next, StringComparison.Ordinal)) return;
            _projectContextSignature = next;
        }

        var csharp = _servers.GetForExtension(".cs");
        if (csharp is null) return;
        Log($"[LSP] C# project context changed ({next}); restarting {csharp.Executable}");
        _pool.Restart(csharp.Executable);
    }

    private static string ProjectContextSignature(SolutionModel? model)
        => model is null
            ? ""
            : $"configuration={model.EffectiveConfiguration}|" + string.Join("|", model.Projects
                .OrderBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.FullPath}={p.SelectedTargetFramework ?? "(none)"}"));

    private void OnDiagnosticsPublished(string uri, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        _documents.OnDiagnostics(uri, diagnostics);
        DiagnosticsPublished?.Invoke(uri, diagnostics);
    }

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "editor-lsp-debug.log");
    private static readonly bool DiagnosticLogEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SK0YA_EDITOR_IDE_DIAG"), "1", StringComparison.Ordinal);

    private static void Log(string message)
    {
        if (!DiagnosticLogEnabled) return;
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.FoldersChanged -= OnFoldersChanged;
        if (_solution is not null) _solution.Changed -= OnSolutionChanged;
        _documents.Clear();
        _pool.Dispose();
    }
}

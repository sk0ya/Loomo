namespace sk0ya.Loomo.App.Services;

/// <summary>documentSymbols の取得結果。無応答（期限切れ）を「シンボルが無い」と区別するための旗を持つ。</summary>
internal sealed record DocumentSymbolsResult(IReadOnlyList<DocumentSymbol> Symbols, bool TimedOut)
{
    public static DocumentSymbolsResult Empty { get; } = new(Array.Empty<DocumentSymbol>(), false);
}

/// <summary>コードEditorSupport用のLSP解析。WPF Viewに依存しない。</summary>
public static class CodeEditorSupportAnalysis
{
    /// <summary>
    /// LSP 要求1件あたりの応答上限。言語サーバーが黙ったとき、<c>await</c> は<b>永久に戻らない</b>——
    /// 描画がその場で止まり、ペインは古い内容を抱えたまま「固まる」。期限を切ることで、
    /// 応答が無くても描画は必ず完了し、案内表示へ落ちる。
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// LSP 要求に上限時間とキャンセルを被せる。期限切れは <paramref name="fallback"/> を返し、
    /// 要求そのものは放置する（後から返ってきても誰も見ないので捨てられる）。
    /// キャンセルは <see cref="OperationCanceledException"/> として上へ伝える
    /// （＝新しい描画に追い越された、というシグナル）。
    /// </summary>
    private static Task<(T? Value, bool TimedOut)> WithLimitAsync<T>(
        Task<T> request, CancellationToken ct, string label)
        => WithLimitAsync(request, RequestTimeout, ct, label);

    /// <summary>上限時間を明示する版（テストが 8 秒待たずに検証できるようにするための口）。</summary>
    internal static async Task<(T? Value, bool TimedOut)> WithLimitAsync<T>(
        Task<T> request, TimeSpan limit, CancellationToken ct, string label)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = Task.Delay(limit, linked.Token);
        var finished = await Task.WhenAny(request, timeout);
        if (ReferenceEquals(finished, request))
        {
            linked.Cancel();   // 待機中の Task.Delay を畳む（タイマーを残さない）
            return (await request, false);
        }
        ct.ThrowIfCancellationRequested();
        CodeSupportDiag.Log($"  {label}: timeout after {limit.TotalSeconds:0}s");
        return (default, true);
    }

    internal static async Task<DocumentSymbolsResult> RequestDocumentSymbolsSafeAsync(
        ILspDocument lsp, CancellationToken ct)
    {
        try
        {
            var (symbols, timedOut) = await WithLimitAsync(
                lsp.RequestDocumentSymbolsAsync(), ct, "documentSymbols");
            return new DocumentSymbolsResult(symbols ?? Array.Empty<DocumentSymbol>(), timedOut);
        }
        catch (OperationCanceledException) { throw; }
        catch { return DocumentSymbolsResult.Empty; }
    }

    public static int CurrentMemberLine1(IReadOnlyList<OutlineNode> roots, CaretInfo caret)
        => CurrentMemberLine1(roots, caret.Line, caret.Column);

    /// <summary>キャレット位置の型を持ち込まずに呼べる版（描画本体はエディタの型に依存しない）。</summary>
    public static int CurrentMemberLine1(IReadOnlyList<OutlineNode> roots, int line0, int col0)
    {
        var member = CodeOutline.FindEnclosing(roots, line0, col0);
        return member is null ? 0 : member.Line0 + 1;
    }

    public static bool LspMatchesFile(ILspDocument lsp, string filePath)
    {
        var current = lsp.FilePath;
        if (string.IsNullOrEmpty(current))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(current), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static IReadOnlyList<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static bool CaretInRange(LspRange range, int line0, int col0)
    {
        var start = range.Start;
        var end = range.End;
        if (start is null || end is null || line0 < start.Line || line0 > end.Line)
            return false;
        if (line0 == start.Line && col0 < start.Character)
            return false;
        return line0 != end.Line || col0 <= end.Character;
    }

    /// <summary>
    /// 呼び出し元/呼び出し先を持つシンボルだけを callHierarchy の展開対象にする。
    /// Roslyn はフィールド等にも prepareCallHierarchy の項目を返すことがあるが、その項目を
    /// incomingCalls/outgoingCalls に渡すとサーバー内部例外になるため、呼び出し可能な種別に限定する。
    /// </summary>
    public static bool SupportsCallHierarchy(int kind)
        => (SymbolKind)kind is SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor;

    /// <summary>
    /// 参照・呼び出し元・呼び出し先をまとめて取る。参照は文書スコープなのでハンドル
    /// (<paramref name="lsp"/>)、呼び出し階層はワークスペーススコープなので
    /// <paramref name="workspace"/> へ投げる（設計書 §30.3.1 の責務表どおり）。
    /// </summary>
    internal static async Task<(CallPanels Panels, LspRange? SymbolRange)> FetchCallPanelsAsync(
        ILspWorkspace workspace, ILspDocument lsp, int line0, int col0, CancellationToken ct)
    {
        // ここの4本（references / prepare / incoming / outgoing）はすべて上限時間つき。
        // 1本でも応答が返らないと②パネルが永久に埋まらず、描画も終わらない。
        async Task<List<CallReference>> FetchReferencesAsync()
        {
            var list = new List<CallReference>();
            try
            {
                var (refs, _) = await WithLimitAsync(
                    lsp.RequestReferencesAsync(line0, col0), ct, "references");
                foreach (var r in refs ?? (IReadOnlyList<LspLocation>)Array.Empty<LspLocation>())
                    if (r is not null)
                        list.Add(new CallReference("", r.Uri ?? "", r.Range?.Start?.Line ?? 0));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return list;
        }

        var referencesTask = FetchReferencesAsync();
        var incoming = new List<CallReference>();
        var outgoing = new List<CallReference>();
        LspRange? symbolRange = null;
        string? target = null;
        var prepareSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;

        try
        {
            var (item, _) = await WithLimitAsync(
                workspace.PrepareCallHierarchyAsync(lsp.Uri, line0, col0), ct, "prepareCallHierarchy");
            CodeSupportDiag.Log($"  prepareCallHierarchy {prepareSw?.ElapsedMilliseconds ?? 0}ms item={(item is null ? "null" : item.Name)}");
            if (item is not null && SupportsCallHierarchy(item.Kind))
            {
                symbolRange = item.SelectionRange;
                target = item.Name;

                async Task<List<CallReference>> FetchIncomingAsync()
                {
                    var list = new List<CallReference>();
                    try
                    {
                        var (calls, _) = await WithLimitAsync(
                            workspace.GetIncomingCallsAsync(item), ct, "incomingCalls");
                        foreach (var c in calls ?? Array.Empty<CallHierarchyIncomingCall>())
                            if (c?.From is { } from)
                                list.Add(new CallReference(from.Name ?? "", from.Uri ?? "", from.SelectionRange?.Start?.Line ?? 0));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    return list;
                }

                async Task<List<CallReference>> FetchOutgoingAsync()
                {
                    var list = new List<CallReference>();
                    try
                    {
                        var (calls, _) = await WithLimitAsync(
                            workspace.GetOutgoingCallsAsync(item), ct, "outgoingCalls");
                        foreach (var c in calls ?? Array.Empty<CallHierarchyOutgoingCall>())
                            if (c?.To is { } to)
                                list.Add(new CallReference(to.Name ?? "", to.Uri ?? "", to.SelectionRange?.Start?.Line ?? 0));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    return list;
                }

                var callsSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;
                var incomingTask = FetchIncomingAsync();
                var outgoingTask = FetchOutgoingAsync();
                await Task.WhenAll(incomingTask, outgoingTask);
                incoming = incomingTask.Result;
                outgoing = outgoingTask.Result;
                CodeSupportDiag.Log($"  incoming+outgoing {callsSw?.ElapsedMilliseconds ?? 0}ms");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        var refsSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;
        var references = await referencesTask;
        CodeSupportDiag.Log($"  references(await) {refsSw?.ElapsedMilliseconds ?? 0}ms count={references.Count}");
        return (new CallPanels(incoming, outgoing, references, target), symbolRange);
    }
}

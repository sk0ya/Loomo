using System.Diagnostics;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>Quick Fix 候補を同じ本文版のLSP診断へ問い合わせ、必要な場合だけC#ホストへ委譲する。</summary>
internal static class QuickFixRequestController
{
    internal static async Task<IReadOnlyList<LspCodeAction>> RequestAsync(
        VimEditorControl control,
        LspRange range,
        TimeSpan timeout,
        Func<VimEditorControl, string, string, EditorDiagnosticSession> ensureSession,
        Func<VimEditorControl, LspRange, IReadOnlyList<string>?, Task<IReadOnlyList<LspCodeAction>>> requestCSharp,
        Action<string> log)
    {
        var totalClock = Stopwatch.StartNew();
        var filePath = control.FilePath ?? "(unknown)";
        var source = control.Text;
        var isCSharp = string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);
        var session = isCSharp && filePath != "(unknown)"
            ? ensureSession(control, filePath, source)
            : null;
        var version = session?.Version ?? 0;
        log($"quickfix start file={filePath} range={range.Start.Line}:{range.Start.Character}-{range.End.Line}:{range.End.Character}");
        IReadOnlyList<LspCodeAction> actions = [];
        if (!isCSharp)
        {
            if (control.LspDocument is not { IsReady: true, IsConnected: true } lspDocument ||
                !string.Equals(lspDocument.Text, source, StringComparison.Ordinal))
                return [];
            var currentLspVersion = lspDocument.Version;
            using var cts = new CancellationTokenSource(timeout);
            var lspClock = Stopwatch.StartNew();
            try
            {
                actions = await lspDocument.RequestCodeActionsAsync(
                    range, [LspCodeActionKinds.QuickFix], cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log($"quickfix lsp error file={filePath} message={ex.Message}"); }
            finally
            {
                log($"quickfix lsp file={filePath} elapsed={lspClock.ElapsedMilliseconds}ms actions={actions.Count}");
            }
            if (!string.Equals(control.Text, source, StringComparison.Ordinal) ||
                lspDocument.Version != currentLspVersion || !ReferenceEquals(control.LspDocument, lspDocument))
                return [];
            return actions;
        }

        var snapshot = session is not null && session.TryGetCurrent(filePath, source, out var current)
            ? current
            : null;
        if (snapshot is not null &&
            control.LspDocument is { IsReady: true, IsConnected: true } document &&
            document.Version is { } documentVersion &&
            snapshot.LanguageServerVersion == documentVersion &&
            string.Equals(document.Text, source, StringComparison.Ordinal))
        {
            // 表示に使っている診断を返した文書版へだけ要求する。版の無い／古いLSP診断は使わない。
            using var cts = new CancellationTokenSource(timeout);
            var lspClock = Stopwatch.StartNew();
            try
            {
                actions = await document.RequestCodeActionsAsync(
                    range, [LspCodeActionKinds.QuickFix], cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log($"quickfix lsp error file={filePath} message={ex.Message}"); }
            finally
            {
                log($"quickfix lsp file={filePath} elapsed={lspClock.ElapsedMilliseconds}ms actions={actions.Count}");
            }
            if (session!.Version != version || session.SnapshotId != snapshot.SnapshotId ||
                !string.Equals(control.Text, source, StringComparison.Ordinal) ||
                document.Version != documentVersion)
                return [];
            if (actions.Count > 0)
            {
                log($"quickfix done file={filePath} source=lsp total={totalClock.ElapsedMilliseconds}ms");
                return actions;
            }
        }

        // LSPが候補を返さない場合だけ、StyleCop／ローカルRoslynをフォールバックとして使う。
        var hostClock = Stopwatch.StartNew();
        if (session is null) return [];
        try
        {
            actions = await requestCSharp(control, range, [LspCodeActionKinds.QuickFix]);
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex) { log($"quickfix host error file={filePath} message={ex.Message}"); }
        finally
        {
            log($"quickfix host file={filePath} elapsed={hostClock.ElapsedMilliseconds}ms actions={actions.Count}");
        }
        if (session.Version != version || !string.Equals(control.Text, source, StringComparison.Ordinal))
            return [];
        log($"quickfix done file={filePath} source=host total={totalClock.ElapsedMilliseconds}ms actions={actions.Count}");
        return actions;
    }
}

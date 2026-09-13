using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>
/// CodeLensを表示へ渡す前に、実行可能な command を持つものだけへ確定する。
/// 公開Editor.Controls 1.0.85との組み合わせでも、未解決レンズの既定表示
/// 「CodeLens」が壊れたリンクとして残らないようにする。
/// </summary>
internal static class LspCodeLensExecutionFilter
{
    public static async Task<IReadOnlyList<LspCodeLens>> ResolveExecutableAsync(
        IReadOnlyList<LspCodeLens> lenses,
        bool supportsResolve,
        Func<LspCodeLens, CancellationToken, Task<LspCodeLens?>> resolve,
        CancellationToken ct = default)
    {
        var resolved = new LspCodeLens?[lenses.Count];
        var tasks = lenses.Select(async (lens, index) =>
        {
            ct.ThrowIfCancellationRequested();
            if (HasExecutableCommand(lens))
            {
                resolved[index] = lens;
                return;
            }

            if (!supportsResolve || !lens.NeedsResolve)
                return;

            try
            {
                var candidate = await resolve(lens, ct);
                if (candidate is not null && HasExecutableCommand(candidate))
                    resolved[index] = candidate;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 1件のresolve失敗で、同じ応答の有効なCodeLensまで失わない。
            }
        });

        await Task.WhenAll(tasks);
        return resolved.OfType<LspCodeLens>().ToArray();
    }

    private static bool HasExecutableCommand(LspCodeLens lens) =>
        lens.Command is { Command.Length: > 0 };
}

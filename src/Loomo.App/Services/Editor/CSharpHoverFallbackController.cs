using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Services;

/// <summary>LSP が hover を返さない場合に C# の意味モデルから説明を取得する。</summary>
internal sealed class CSharpHoverFallbackController(
    Func<SolutionModel?> getSolution,
    Func<IReadOnlyDictionary<string, string>> getOpenTexts)
{
    internal Task<string?> RequestAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!CSharpLspFallbackController.IsTarget(path))
            return Task.FromResult<string?>(null);

        var openTexts = getOpenTexts();
        return Task.Run(() => CSharpHoverService.Get(
            getSolution(), path, source, line, character, openTexts), cancellationToken);
    }
}

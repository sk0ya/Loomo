using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Tests;

public sealed class LspCodeLensExecutionFilterTests
{
    [Fact]
    public async Task Resolve_only_returns_lenses_with_executable_commands()
    {
        static LspRange Range(int line) => new(
            new LspPosition(line, 0), new LspPosition(line, 1));

        var unresolved = new LspCodeLens(
            Range(1), DataJson: "{\"id\":1}", RawJson: "{}");
        var unresolvedWithoutCommand = new LspCodeLens(
            Range(2), DataJson: "{\"id\":2}", RawJson: "{}");
        var alreadyExecutable = new LspCodeLens(
            Range(3), new LspCodeActionCommand("already.run", "Already"), RawJson: "{}");
        var resolved = new LspCodeLens(
            Range(1), new LspCodeActionCommand("test.run", "Run tests"));
        var resolveCalls = 0;

        var visible = await LspCodeLensExecutionFilter.ResolveExecutableAsync(
            [unresolved, unresolvedWithoutCommand, alreadyExecutable],
            supportsResolve: true,
            resolve: (lens, _) =>
            {
                resolveCalls++;
                return Task.FromResult<LspCodeLens?>(ReferenceEquals(lens, unresolved) ? resolved : null);
            });

        Assert.Equal(2, resolveCalls);
        Assert.Equal([resolved, alreadyExecutable], visible);
    }

    [Fact]
    public async Task Unresolved_lenses_are_dropped_when_server_does_not_support_resolve()
    {
        var unresolved = new LspCodeLens(
            new LspRange(new LspPosition(1, 0), new LspPosition(1, 1)),
            DataJson: "{\"id\":1}", RawJson: "{}");

        var visible = await LspCodeLensExecutionFilter.ResolveExecutableAsync(
            [unresolved],
            supportsResolve: false,
            resolve: (_, _) => throw new InvalidOperationException("resolve must not be called"));

        Assert.Empty(visible);
    }
}

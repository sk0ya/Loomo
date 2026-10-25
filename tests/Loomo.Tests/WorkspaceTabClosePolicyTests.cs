using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>TABS とペインの見出しの「閉じる」の範囲（§24.89 の前後を含む）。</summary>
public sealed class WorkspaceTabClosePolicyTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();
    private static readonly Guid[] Editors = [A, B, C, D];

    private static IReadOnlyList<Guid> Plan(Guid selected, WorkspaceTabCloseScope scope)
        => WorkspaceTabClosePolicy.CreatePlan(TabEntryKind.Editor, selected, scope, [], Editors, []).TabIds;

    [Fact]
    public void これより後は並びで選んだタブより後ろだけを閉じる()
    {
        Assert.Equal(new[] { C, D }, Plan(B, WorkspaceTabCloseScope.After));
        Assert.Empty(Plan(D, WorkspaceTabCloseScope.After));
    }

    [Fact]
    public void これより前は並びで選んだタブより前だけを閉じる()
    {
        Assert.Equal(new[] { A, B }, Plan(C, WorkspaceTabCloseScope.Before));
        Assert.Empty(Plan(A, WorkspaceTabCloseScope.Before));
    }

    [Fact]
    public void 選んだタブが並びに無ければ前後どちらも何も閉じない()
    {
        var missing = Guid.NewGuid();
        Assert.Empty(Plan(missing, WorkspaceTabCloseScope.Before));
        Assert.Empty(Plan(missing, WorkspaceTabCloseScope.After));
    }

    [Fact]
    public void 他の種別のタブは数えない()
    {
        var terminal = Guid.NewGuid();
        var plan = WorkspaceTabClosePolicy.CreatePlan(
            TabEntryKind.Editor, A, WorkspaceTabCloseScope.After, [terminal], Editors, []);
        Assert.DoesNotContain(terminal, plan.TabIds);
        Assert.Equal(TabEntryKind.Editor, plan.Kind);
    }

    [Fact]
    public void 他のタブとすべては今までどおり()
    {
        Assert.Equal(new[] { A, C, D }, Plan(B, WorkspaceTabCloseScope.Others));
        Assert.Equal(Editors, Plan(B, WorkspaceTabCloseScope.All));
        Assert.Equal(new[] { B }, Plan(B, WorkspaceTabCloseScope.Selected));
    }
}

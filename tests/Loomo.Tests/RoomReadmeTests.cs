using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>初めて開く部屋で README を開いておく（§24.98）。</summary>
public sealed class RoomReadmeTests
{
    private static string? Find(params string[] names)
        => RoomReadme.Find(@"C:\repo", _ => true, _ => names.Select(n => Path.Combine(@"C:\repo", n)));

    [Fact]
    public void Markdownを先に_実名で返す()
    {
        Assert.Equal(@"C:\repo\readme.MD", Find("README.txt", "readme.MD", "a.cs"));
        Assert.Equal(@"C:\repo\README.txt", Find("README.txt", "notes.md"));
        Assert.Null(Find("a.cs", "READMEX.md"));
    }

    [Fact]
    public void 初めての復元でタブが無いときだけREADMEを当てる()
    {
        var fresh = new WorkspaceSnapshot { RootPath = @"C:\repo" };
        var plan = WorkspaceSessionCoordinator.ResolveEditorTabRestorePlan(fresh, _ => @"C:\repo\README.md");
        Assert.Equal(@"C:\repo\README.md", Assert.Single(plan.Snapshots).FilePath);

        // 一度復元した部屋（タブを全部閉じた）には出し直さない。
        var returned = new WorkspaceSnapshot { RootPath = @"C:\repo", EditorTabsInitialized = true };
        plan = WorkspaceSessionCoordinator.ResolveEditorTabRestorePlan(returned, _ => @"C:\repo\README.md");
        Assert.True(string.IsNullOrEmpty(Assert.Single(plan.Snapshots).FilePath));

        // 旧形式の単一エディタに未保存の本文があれば、そちらが優先。
        var draft = new WorkspaceSnapshot { RootPath = @"C:\repo" };
        draft.Editor.Text = "書きかけ";
        plan = WorkspaceSessionCoordinator.ResolveEditorTabRestorePlan(draft, _ => @"C:\repo\README.md");
        Assert.True(string.IsNullOrEmpty(Assert.Single(plan.Snapshots).FilePath));
    }
}

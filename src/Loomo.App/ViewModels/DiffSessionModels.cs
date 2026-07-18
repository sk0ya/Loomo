using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed record DiffRowVm(string Kind, string Text);

public sealed record DiffHunkVm(int Index, string HeaderLine, string Summary, bool IsStaged)
{
    public string ActionLabel => IsStaged ? "アンステージ" : "ステージ";
}

public sealed record DiffSideRowVm(
    string LeftKind, string LeftText, string RightKind, string RightText,
    string LeftLine, string RightLine);

public sealed class DiffFileItem
{
    public required string FullPath { get; init; }
    public required string DisplayPath { get; init; }
    public required string Badge { get; init; }
    public string Stats { get; init; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public bool IsAi { get; init; }
    public bool IsNew { get; init; }
    public string? OldContent { get; init; }
    public string? NewContent { get; init; }
    public bool CanRevert => IsAi && (IsNew || OldContent is not null);
    public GitChangeEntry? Entry { get; init; }
    public bool IsStaged { get; init; }
    public GitCommitFileChange? CommitFile { get; init; }
}

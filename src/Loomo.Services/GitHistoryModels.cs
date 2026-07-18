namespace sk0ya.Loomo.Services;

/// <summary>git log --graph の1行。枝の継続行では Hash が null。</summary>
public sealed record GitLogRow(
    string Graph,
    string? Hash,
    string? ShortHash,
    string? Author,
    string? Date,
    string? Refs,
    string? Subject)
{
    public bool IsCommit => Hash is not null;
}

/// <summary>スタッシュ1件。</summary>
public sealed record GitStashEntry(string Ref, string Description);

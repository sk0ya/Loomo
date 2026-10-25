using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// プルの前の確認（§24.64・GitButler 0.20.1 の「Upstream integration」）。上流と分かれている（受け取るものも、こちらだけのものもある）
/// ときにプルを押すと、取り込む前に「何が来るか」と「ぶつかるか」を見せて、マージかリベースかを選ばせる。分かれていなければ出さない
/// （黙って早送りで済む）。衝突の判定は §24.40 と同じ merge-tree で、作業ツリーには触らない。
/// </summary>
public sealed record GitPullPreview(
    string Upstream, int Incoming, int Outgoing,
    IReadOnlyList<GitIncomingCommit> Commits, IReadOnlyList<string> ConflictFiles, string? ForecastError = null)
{
    public string Summary => $"{Upstream} と分かれています — 受け取る {Incoming} 件・こちらだけ {Outgoing} 件";

    public IReadOnlyList<string> CommitLines => Commits.Select(c => $"{c.ShortHash}  {c.Subject} — {c.Author}").ToList();

    public string? MoreCommits => Incoming > Commits.Count ? $"ほか {Incoming - Commits.Count} 件" : null;

    public bool HasConflicts => ConflictFiles.Count > 0;

    public string ConflictLabel => ForecastError is not null
        ? $"衝突するかは確かめられませんでした（{ForecastError}）"
        : HasConflicts
            ? $"⚠ 取り込むと衝突 {ConflictFiles.Count} ファイル"
            : "衝突しない見込み（作業ツリーには触らずに確かめた結果）";

    public string? ConflictFilesText => HasConflicts
        ? string.Join("\n", ConflictFiles.Take(10).Select(path => "  " + path))
          + (ConflictFiles.Count > 10 ? $"\n  ほか {ConflictFiles.Count - 10} ファイル" : "")
        : null;
}

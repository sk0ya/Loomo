using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「問題」一覧から抜粋タブ（§35.3）を開く要求。<see cref="ProblemCount"/> は抜粋に入れた問題の件数
/// （同じ行の問題は抜粋では1か所にまとまるので、抜粋の数とは一致しない）。<see cref="OutsideCount"/> は
/// ワークスペースの外にあるため外した件数（SDK の targets のような、その場で書き換えるべきでないファイル）。</summary>
public sealed record ProblemExcerptPlan(
    string Title, IReadOnlyList<ExcerptRequest> Requests, int ProblemCount, int OutsideCount);

/// <summary>
/// 「問題」一覧のどの行を抜粋タブへ流すかの選び方（純粋関数）。並びは一覧に見えている順（エラーを含む
/// ファイルが先・ファイル内は行順）のまま渡す——<see cref="ExcerptPlanner"/> はファイルを最初に現れた順に
/// 並べるので、抜粋タブも一覧と同じ順になる。ワークスペースの内外の判定は呼び出し側の述語に任せる
/// （<c>IWorkspaceService.Contains</c>。マルチルートを自前の前方一致で判定しない）。
/// </summary>
public static class ProblemExcerpts
{
    /// <summary>一覧に表示中の問題すべて（重要度・発生源・範囲のフィルターを通ったもの）。</summary>
    public static ProblemExcerptPlan Displayed(
        IEnumerable<ProblemFileGroup> groups, System.Func<string, bool> inWorkspace)
        => Build("表示中", groups.SelectMany(g => g.Items), inWorkspace);

    /// <summary>ファイル見出し1つ分の問題。</summary>
    public static ProblemExcerptPlan ForFile(ProblemFileGroup group, System.Func<string, bool> inWorkspace)
        => Build(group.FileName, group.Items, inWorkspace);

    /// <summary>選んだ問題と同じ診断コードの問題すべて（例: 全ファイルの CS8618）。コードを持たない診断
    /// （コード欄が発生源名の代用）はコードで同一視できないので、同じ発生源名かつ同じメッセージのものに絞る。
    /// <paramref name="candidates"/> は一覧の表示順に並んだ候補。</summary>
    public static ProblemExcerptPlan SameCode(
        ProblemItemViewModel target, IEnumerable<ProblemItemViewModel> candidates,
        System.Func<string, bool> inWorkspace)
        => Build(target.HasCode ? target.Code : Abbreviate(target.Message),
            candidates.Where(c => IsSameCode(target, c)), inWorkspace);

    /// <summary>「同じコードの問題」か。コードは大文字小文字を区別しない（MSBuild は cs8618 とも書きうる）。</summary>
    public static bool IsSameCode(ProblemItemViewModel target, ProblemItemViewModel candidate)
    {
        if (target.HasCode != candidate.HasCode) return false;
        if (!string.Equals(target.Code, candidate.Code, System.StringComparison.OrdinalIgnoreCase)) return false;
        return target.HasCode || string.Equals(target.Message, candidate.Message, System.StringComparison.Ordinal);
    }

    private static ProblemExcerptPlan Build(
        string label, IEnumerable<ProblemItemViewModel> items, System.Func<string, bool> inWorkspace)
    {
        var requests = new List<ExcerptRequest>();
        var outside = 0;
        foreach (var item in items)
        {
            // 抜粋はファイルの行に結び付くので、絶対パスでないもの（ビルド出力の相対パスの取りこぼし）は外す。
            if (!Path.IsPathFullyQualified(item.FilePath) || !inWorkspace(item.FilePath))
            {
                outside++;
                continue;
            }
            requests.Add(new ExcerptRequest(item.FilePath, System.Math.Max(0, item.Line1 - 1)));
        }
        return new ProblemExcerptPlan($"問題: {label} ({requests.Count})", requests, requests.Count, outside);
    }

    /// <summary>コードの無い診断の題は、メッセージの頭だけにする（タブの題が一覧の1行より長くならないように）。</summary>
    private static string Abbreviate(string message)
    {
        var firstLine = message.Split('\n')[0].Trim();
        return firstLine.Length <= 24 ? firstLine : firstLine[..24] + "…";
    }
}

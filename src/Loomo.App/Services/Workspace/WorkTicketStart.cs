using System.Text;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.App.Services;

/// <summary>課題の出所。</summary>
public enum WorkTicketSource { AzureBoards, GitHub }

/// <summary>作業を始める課題 1 つ（Azure Boards の Work Item か GitHub の Issue）。</summary>
public sealed record WorkTicket(WorkTicketSource Source, int Number, string Title, string Url);

/// <summary>
/// 課題から作業を始める（§24.56）ときの名前と言葉（純ロジック・テスト対象）。
/// <para>GitKraken Desktop 12.4 の「Issue からエージェントのセッションを始める」と同じ流れ——ブランチ → ワークツリー → 部屋 →
/// （望めば）Claude Code。ブランチ名に課題の番号を入れ、Claude への頼みごとに <c>AB#番号 タイトル</c> を入れるので、
/// ハーネスペイン（§37.3.2）はその Run を課題に結び付けられる。</para>
/// </summary>
public static class WorkTicketStart
{
    /// <summary>ブランチ名に入れるタイトル由来の部分の長さの上限。</summary>
    public const int MaxSlugChars = 40;

    /// <summary>指示や一覧で課題を名指す形：<c>AB#1234 タイトル</c>／<c>#12 タイトル</c>。</summary>
    public static string Mention(WorkTicket ticket)
        => ticket.Source == WorkTicketSource.AzureBoards
            ? $"{HarnessTickets.AzureBoardsPrefix}{ticket.Number} {ticket.Title.Trim()}"
            : $"#{ticket.Number} {ticket.Title.Trim()}";

    /// <summary>ブランチ名の初期値：<c>ab1234-fix-login</c>／<c>issue-12-fix-login</c>。タイトルに英数字が無ければ番号だけ。</summary>
    public static string BranchName(WorkTicket ticket)
    {
        var head = ticket.Source == WorkTicketSource.AzureBoards ? $"ab{ticket.Number}" : $"issue-{ticket.Number}";
        var slug = Slug(ticket.Title);
        return slug.Length == 0 ? head : $"{head}-{slug}";
    }

    /// <summary>タイトルから ASCII の英数字の語だけを拾い、小文字のハイフン区切りにする（日本語は捨てる——
    /// ブランチ名とフォルダー名に使うので、どのシェルでも引用なしで打てる形に留める）。</summary>
    public static string Slug(string? title)
    {
        var builder = new StringBuilder();
        var pendingDash = false;
        foreach (var c in title ?? "")
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                if (pendingDash && builder.Length > 0)
                {
                    if (builder.Length + 1 >= MaxSlugChars) break;
                    builder.Append('-');
                }
                pendingDash = false;
                if (builder.Length >= MaxSlugChars) break;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingDash = true;
            }
        }
        return builder.ToString().Trim('-');
    }

    /// <summary>Claude Code への最初の頼みごと（1 行。プロンプトの改行は pwsh の引数で崩れるので入れない）。</summary>
    public static string ClaudePrompt(WorkTicket ticket)
        => string.IsNullOrWhiteSpace(ticket.Url)
            ? $"{Mention(ticket)} に取り組んでください。"
            : $"{Mention(ticket)} に取り組んでください。課題: {ticket.Url}";

    /// <summary>可視ターミナル（pwsh）へ打つ 1 行。単一引用符で囲み、中の <c>'</c> は <c>''</c> に重ねる。</summary>
    public static string PowerShellClaudeCommand(string prompt)
        => "claude '" + prompt.Replace("\r", " ").Replace("\n", " ").Replace("'", "''") + "'";
}

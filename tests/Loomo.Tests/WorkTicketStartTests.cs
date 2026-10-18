using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>課題から作業を始める（§24.56）——ブランチ名・名指しの形・Claude への頼みごとと pwsh の引用。</summary>
public class WorkTicketStartTests
{
    private static WorkTicket Board(string title) => new(WorkTicketSource.AzureBoards, 1234, title, "https://dev.azure.com/o/p/_workitems/edit/1234");
    private static WorkTicket Issue(string title) => new(WorkTicketSource.GitHub, 12, title, "https://github.com/o/r/issues/12");

    [Fact]
    public void Branch_name_carries_number_and_ascii_words()
    {
        Assert.Equal("ab1234-fix-login-timeout", WorkTicketStart.BranchName(Board("Fix login: timeout!")));
        Assert.Equal("issue-12-add-csv-export", WorkTicketStart.BranchName(Issue("Add CSV export")));
    }

    [Fact]
    public void Japanese_title_leaves_only_the_number()
    {
        Assert.Equal("ab1234", WorkTicketStart.BranchName(Board("ログイン画面がタイムアウトする")));
        Assert.Equal("ab1234-api", WorkTicketStart.BranchName(Board("API のタイムアウト")));
    }

    [Fact]
    public void Slug_is_bounded_and_has_no_trailing_dash()
    {
        var slug = WorkTicketStart.Slug(string.Join(" ", Enumerable.Repeat("word", 30)));
        Assert.True(slug.Length <= WorkTicketStart.MaxSlugChars);
        Assert.False(slug.EndsWith('-'));
    }

    [Fact]
    public void Mention_uses_harness_prefix_for_boards()
    {
        Assert.Equal("AB#1234 Fix", WorkTicketStart.Mention(Board(" Fix ")));
        Assert.Equal("#12 Add", WorkTicketStart.Mention(Issue("Add")));
    }

    [Fact]
    public void Claude_command_is_single_line_and_quotes_apostrophes()
    {
        var prompt = WorkTicketStart.ClaudePrompt(Issue("Don't crash"));
        Assert.Equal("#12 Don't crash に取り組んでください。課題: https://github.com/o/r/issues/12", prompt);
        Assert.Equal("claude '#12 Don''t crash に取り組んでください。課題: https://github.com/o/r/issues/12'",
            WorkTicketStart.PowerShellClaudeCommand(prompt));
        Assert.DoesNotContain("\n", WorkTicketStart.PowerShellClaudeCommand("a\r\nb"));
    }
}

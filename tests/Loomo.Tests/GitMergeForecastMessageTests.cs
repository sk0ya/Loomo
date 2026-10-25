using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>マージ・リベースの前の衝突の予告の文面（§24.88）。</summary>
public sealed class GitMergeForecastMessageTests
{
    private static GitConflictForecast Forecast(params string[] files) => new("feature", "h", "t", files);

    [Fact]
    public void 衝突しないならマージは確認を挟まない()
    {
        Assert.Null(GitMergeForecastMessage.MergeConfirmation("feature", Forecast()));
        Assert.Null(GitMergeForecastMessage.MergeConfirmation("feature", null));
    }

    [Fact]
    public void 衝突するならファイルを並べてマージするかを聞く()
    {
        var message = GitMergeForecastMessage.MergeConfirmation("feature", Forecast("a.txt", "src/b.cs"));

        Assert.NotNull(message);
        Assert.Contains("feature", message);
        Assert.Contains("2 個のファイルが衝突します", message);
        Assert.Contains("・a.txt", message);
        Assert.Contains("・src/b.cs", message);
    }

    [Fact]
    public void 多すぎるファイルは上限まで並べて残りを数で言う()
    {
        var files = Enumerable.Range(1, GitMergeForecastMessage.MaxFiles + 3).Select(i => $"f{i}.txt").ToArray();

        var text = GitMergeForecastMessage.DescribeConflicts(Forecast(files))!;

        Assert.Contains($"・f{GitMergeForecastMessage.MaxFiles}.txt", text);
        Assert.DoesNotContain($"・f{GitMergeForecastMessage.MaxFiles + 1}.txt", text);
        Assert.Contains("ほか 3 件", text);
    }

    [Fact]
    public void リベースは衝突すると分かったときだけ添え_途中の別の衝突もあり得ると言う()
    {
        Assert.Null(GitMergeForecastMessage.RebaseNote(Forecast()));
        var note = GitMergeForecastMessage.RebaseNote(Forecast("a.txt"));
        Assert.NotNull(note);
        Assert.Contains("コミットごと", note);
    }
}

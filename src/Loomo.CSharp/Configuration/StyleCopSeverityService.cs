using System.Text;
using System.Text.RegularExpressions;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>StyleCop severityの明示変更結果。</summary>
public sealed record StyleCopSeverityChangeResult(
    bool Succeeded,
    string FilePath,
    bool CreatedFile,
    string? Error = null);

/// <summary>
/// StyleCopのseverityをプロジェクト直下の.editorconfigへ書き込む。
/// 上位ディレクトリの共有設定は暗黙に変更せず、存在しなければプロジェクト専用ファイルを作る。
/// </summary>
public sealed class StyleCopSeverityService
{
    private static readonly Regex RuleId = new(
        "^SA\\d{4}$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Severity = new(
        "^(none|silent|suggestion|warning|error)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public StyleCopSeverityChangeResult SetSeverity(
        ProjectModel project, string ruleId, string severity)
    {
        var path = Path.Combine(project.Directory, ".editorconfig");
        if (project.State != ProjectLoadState.Ready)
            return Failure(path, "プロジェクトがready状態ではありません。");

        var id = ruleId.Trim().ToUpperInvariant();
        var normalizedSeverity = severity.Trim().ToLowerInvariant();
        if (!RuleId.IsMatch(id))
            return Failure(path, "ルールIDはSA0000形式で入力してください。");
        if (!Severity.IsMatch(normalizedSeverity))
            return Failure(path, "severityはnone／silent／suggestion／warning／errorのいずれかです。");

        try
        {
            Directory.CreateDirectory(project.Directory);
            var existed = File.Exists(path);
            var original = existed ? File.ReadAllText(path) : "";
            var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var key = $"dotnet_diagnostic.{id}.severity";
            var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();

            var existingIndex = lines.FindIndex(line =>
                Regex.IsMatch(line, $"^\\s*{Regex.Escape(key)}\\s*[:=]", RegexOptions.IgnoreCase));
            if (existingIndex >= 0)
            {
                var indent = Regex.Match(lines[existingIndex], "^\\s*").Value;
                lines[existingIndex] = $"{indent}{key} = {normalizedSeverity}";
            }
            else
            {
                var sectionIndex = lines.FindIndex(line =>
                    line.Trim().Equals("[*.cs]", StringComparison.OrdinalIgnoreCase));
                if (sectionIndex < 0)
                {
                    TrimTrailingEmptyLines(lines);
                    if (lines.Count > 0) lines.Add("");
                    lines.Add("[*.cs]");
                    lines.Add($"{key} = {normalizedSeverity}");
                }
                else
                {
                    var insertAt = sectionIndex + 1;
                    while (insertAt < lines.Count &&
                           !IsSectionHeader(lines[insertAt])) insertAt++;
                    while (insertAt > sectionIndex + 1 && lines[insertAt - 1].Length == 0)
                        insertAt--;
                    lines.Insert(insertAt, $"{key} = {normalizedSeverity}");
                }
            }

            var content = string.Join(newline, lines);
            if (original.EndsWith("\r\n", StringComparison.Ordinal) ||
                original.EndsWith('\n'))
                content += newline;
            WriteAtomically(path, content, original.StartsWith('\uFEFF'));
            return new(true, path, !existed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(path, $".editorconfigを書き込めません: {ex.Message}");
        }
    }

    private static bool IsSectionHeader(string line)
        => line.TrimStart().StartsWith("[", StringComparison.Ordinal);

    private static void TrimTrailingEmptyLines(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
    }

    private static void WriteAtomically(string path, string content, bool withBom)
    {
        var temporary = path + ".loomo-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(withBom));
            if (File.Exists(path))
            {
                try { File.Replace(temporary, path, null); }
                catch (PlatformNotSupportedException) { File.Move(temporary, path, true); }
                catch (IOException) { File.Move(temporary, path, true); }
            }
            else
                File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static StyleCopSeverityChangeResult Failure(string path, string error)
        => new(false, path, false, error);
}

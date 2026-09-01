using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace sk0ya.Loomo.CSharp.Testing;

/// <summary>VSTestのTRXから得たC#テストの実行状態。</summary>
public enum CSharpTestStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>TRXから取り出したテスト1件の結果。UIの状態型には依存しない。</summary>
public readonly record struct CSharpTrxResult(
    string Name,
    CSharpTestStatus Status,
    string? Message,
    string? SourcePath,
    int Line,
    TimeSpan? Duration = null);

/// <summary>TRX（VSTest形式XML）をC#テスト結果へ変換するパーサ。
/// テスト一覧との突き合わせやUI反映はホスト側が担当する。</summary>
public static class CSharpTrxResultParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly Regex LocationPattern = new(
        @"\sin\s+(?<path>.+?):line\s+(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>TRXを読み込み、結果一覧を返す。読めなければエラーを返して空配列にする。</summary>
    public static IReadOnlyList<CSharpTrxResult> Parse(string trxPath, out string? error)
    {
        error = null;
        XDocument document;
        try { document = XDocument.Load(trxPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Xml.XmlException or ArgumentException)
        {
            error = ex.Message;
            return Array.Empty<CSharpTrxResult>();
        }

        var results = new List<CSharpTrxResult>();
        foreach (var result in document.Descendants(Ns + "UnitTestResult"))
        {
            var name = (string?)result.Attribute("testName");
            if (string.IsNullOrEmpty(name)) continue;

            var status = ((string?)result.Attribute("outcome")) switch
            {
                "Passed" => CSharpTestStatus.Passed,
                "Failed" => CSharpTestStatus.Failed,
                _ => CSharpTestStatus.Skipped,
            };

            string? message = null;
            string? sourcePath = null;
            var line = 0;
            var errorInfo = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
            if (errorInfo is not null)
            {
                message = ((string?)errorInfo.Element(Ns + "Message"))?.Trim();
                if (message is not null && message.IndexOf('\n') is var newline && newline >= 0)
                    message = message[..newline].Trim();

                if ((string?)errorInfo.Element(Ns + "StackTrace") is { } stack)
                {
                    var location = LocationPattern.Match(stack);
                    if (location.Success && int.TryParse(
                            location.Groups["line"].Value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out var parsedLine))
                    {
                        sourcePath = location.Groups["path"].Value.Trim();
                        line = parsedLine;
                    }
                }
            }

            TimeSpan? duration = TimeSpan.TryParse(
                (string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var parsedDuration)
                ? parsedDuration
                : null;
            results.Add(new CSharpTrxResult(name, status, message, sourcePath, line, duration));
        }

        return results;
    }
}

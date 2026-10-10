using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace sk0ya.Loomo.Services.Testing;

/// <summary>JUnit XML（pytest の <c>--junitxml</c>）のパース。pytest 本体に組み込みのレポートなので、
/// プラグイン（pytest-json-report 等）の導入を前提にしない。
/// <para><b>pytest の流儀で読む</b>——<c>classname</c> は rootdir からのドット区切りモジュール名（＋クラス名）、
/// <c>name</c> は関数名（パラメータ化なら <c>test_x[1-2]</c>）。実行側は <c>-o junit_family=xunit1</c> を付け、
/// <c>file</c>（rootdir 相対）と <c>line</c>（<b>0 始まり</b>）も出させる。<c>file</c> が無いレポート（xunit2）でも
/// 読めるよう、<c>classname</c> の前方を「存在する .py」まで削ってファイルを割り出す退避路を持つ。</para>
/// <para>結果の <see cref="ScriptTestResult.Title"/> は pytest のノード ID 後半（<c>TestCls::test_x</c>、入れ子クラスは
/// <c>Outer::Inner::test_x</c>）で、<see cref="PytestDiscovery"/> の出す形と一致する。</para></summary>
public static partial class JUnitXmlResultParser
{
    /// <summary>トレースバックの「<c>path.py:行: 例外名</c>」行。pytest の long 表示は失敗箇所をこの形で締める。</summary>
    [GeneratedRegex(@"^(?<p>(?:[A-Za-z]:)?[^:\r\n]*\.py):(?<l>\d+):", RegexOptions.Multiline)]
    private static partial Regex TracebackLocation();

    /// <param name="rootDir">相対パスの基準（pytest の rootdir＝実行時の <c>--rootdir</c>）。</param>
    /// <param name="fileExists"><c>file</c> 属性が無いときの割り出しに使う（テストで差し替える）。</param>
    public static IReadOnlyList<ScriptTestResult> Parse(string xml, string rootDir, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var list = new List<ScriptTestResult>();
        var doc = XDocument.Parse(xml);
        foreach (var tc in doc.Descendants("testcase"))
        {
            var rawName = (string?)tc.Attribute("name") ?? "";
            var classname = (string?)tc.Attribute("classname") ?? "";
            var fileAttr = (string?)tc.Attribute("file");

            // パラメータ化：test_x[1-2] → 行は test_x（メソッド単位）、ケース名は元のまま。
            var bracket = rawName.IndexOf('[');
            var baseName = bracket > 0 ? rawName[..bracket] : rawName;
            var caseName = bracket > 0 ? rawName : null;

            var (relFile, classPart) = fileAttr is { Length: > 0 }
                ? (fileAttr, ClassPartFromFile(classname, fileAttr))
                : SplitClassname(classname, rootDir, fileExists);
            var filePath = Combine(rootDir, relFile);
            var title = classPart.Length > 0 ? classPart.Replace(".", "::") + "::" + baseName : baseName;

            var failure = tc.Element("failure") ?? tc.Element("error");
            var outcome = failure is not null ? ScriptTestOutcome.Failed
                : tc.Element("skipped") is not null ? ScriptTestOutcome.Skipped
                : ScriptTestOutcome.Passed;

            string? message = null;
            string? failurePath = null;
            var failureLine = 0;
            if (failure is not null)
            {
                message = FirstLine((string?)failure.Attribute("message")) ?? FirstLine(failure.Value);
                (failurePath, failureLine) = FindFailureLocation(failure.Value, rootDir, filePath);
            }

            TimeSpan? duration = double.TryParse((string?)tc.Attribute("time"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
                    ? TimeSpan.FromSeconds(seconds) : null;
            var declarationLine = int.TryParse((string?)tc.Attribute("line"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var line0) && line0 >= 0 ? line0 + 1 : 0;

            list.Add(new ScriptTestResult(filePath, title, outcome, message, duration, caseName,
                failurePath, failureLine, declarationLine));
        }
        return list;
    }

    /// <summary><c>file</c> 属性があるとき：<c>classname</c> からモジュール部（ファイルのドット表記）を落とし、クラス部だけを返す。</summary>
    private static string ClassPartFromFile(string classname, string relFile)
    {
        var module = Path.ChangeExtension(relFile, null)!.Replace('\\', '.').Replace('/', '.');
        if (string.Equals(classname, module, StringComparison.Ordinal)) return "";
        if (classname.StartsWith(module + ".", StringComparison.Ordinal)) return classname[(module.Length + 1)..];

        // rootdir の取り方が違ってモジュール名の前方がずれたとき：ファイル名（拡張子なし）の後ろをクラス部とみなす。
        var stem = Path.GetFileNameWithoutExtension(relFile);
        var segments = classname.Split('.');
        var at = Array.LastIndexOf(segments, stem);
        return at >= 0 ? string.Join('.', segments.Skip(at + 1)) : "";
    }

    /// <summary><c>file</c> 属性が無いとき：<c>a.b.test_x.TestCls</c> の前方を、rootdir に .py として存在するところまで取る。
    /// どこも存在しなければ全体をモジュールとみなす（ベストエフォート）。</summary>
    private static (string RelFile, string ClassPart) SplitClassname(string classname, string rootDir,
        Func<string, bool> fileExists)
    {
        var segments = classname.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length; i >= 1; i--)
        {
            var rel = string.Join(Path.DirectorySeparatorChar, segments.Take(i)) + ".py";
            if (fileExists(Combine(rootDir, rel)))
                return (rel, string.Join('.', segments.Skip(i)));
        }
        return (string.Join(Path.DirectorySeparatorChar, segments) + ".py", "");
    }

    /// <summary>失敗本文から位置を拾う。テストファイル自身の行があればその最後（＝アサーションの行）、
    /// 無ければ最後に現れた位置（最も内側のフレーム）。</summary>
    private static (string? Path, int Line) FindFailureLocation(string text, string rootDir, string testFile)
    {
        string? lastPath = null;
        var lastLine = 0;
        string? ownPath = null;
        var ownLine = 0;
        foreach (Match m in TracebackLocation().Matches(text))
        {
            if (!int.TryParse(m.Groups["l"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)
                || line <= 0) continue;
            var path = Combine(rootDir, m.Groups["p"].Value.Trim());
            lastPath = path;
            lastLine = line;
            if (string.Equals(path, testFile, StringComparison.OrdinalIgnoreCase)) { ownPath = path; ownLine = line; }
        }
        return ownPath is not null ? (ownPath, ownLine) : (lastPath, lastLine);
    }

    private static string Combine(string rootDir, string path)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(rootDir, path)); }
        catch { return path; }
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) return line;
        }
        return null;
    }
}

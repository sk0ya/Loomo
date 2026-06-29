using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>TRX から取り出したテスト 1 件の結果（名前・状態・失敗メッセージ先頭行・スタックから拾った位置）。</summary>
public readonly record struct TrxResult(string Name, TestStatus Status, string? Message, string? SourcePath, int Line);

/// <summary>TRX（VSTest 形式 XML）を読み、各 <c>UnitTestResult</c> を <see cref="TrxResult"/> へ写すパーサ。
/// 一覧との突き合わせ（テオリのケース集約・未知テストの追加）は呼び出し側が行う。</summary>
public static class TrxResultParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly Regex LocRe = new(@"\sin\s+(.+?):line\s+(\d+)", RegexOptions.Compiled);

    /// <summary>TRX を読み込み、結果一覧を返す。読めなければ例外メッセージを <paramref name="error"/> に入れて空を返す。</summary>
    public static IReadOnlyList<TrxResult> Parse(string trxPath, out string? error)
    {
        error = null;
        XDocument doc;
        try { doc = XDocument.Load(trxPath); }
        catch (Exception ex) { error = ex.Message; return Array.Empty<TrxResult>(); }

        var results = new List<TrxResult>();
        foreach (var r in doc.Descendants(Ns + "UnitTestResult"))
        {
            var name = (string?)r.Attribute("testName");
            if (string.IsNullOrEmpty(name)) continue;

            var status = ((string?)r.Attribute("outcome")) switch
            {
                "Passed" => TestStatus.Passed,
                "Failed" => TestStatus.Failed,
                _ => TestStatus.Skipped,  // NotExecuted など
            };

            string? msg = null, path = null;
            var line1 = 0;
            var err = r.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
            if (err is not null)
            {
                msg = ((string?)err.Element(Ns + "Message"))?.Trim();
                if (msg is not null)
                {
                    var nl = msg.IndexOf('\n');  // 失敗メッセージは先頭行だけ一覧に出す
                    if (nl >= 0) msg = msg[..nl].Trim();
                }
                var stack = (string?)err.Element(Ns + "StackTrace");
                if (stack is not null)
                {
                    var lm = LocRe.Match(stack);
                    if (lm.Success) { path = lm.Groups[1].Value.Trim(); line1 = int.Parse(lm.Groups[2].Value); }
                }
            }

            results.Add(new TrxResult(name, status, msg, path, line1));
        }
        return results;
    }
}

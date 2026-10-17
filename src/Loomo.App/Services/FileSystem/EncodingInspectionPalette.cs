using System.Globalization;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 文字コードの調査結果（<see cref="EncodingReport"/>）をパレットの一覧へ並べる（純ロジック）。
/// 先頭に文字コードと改行の要約、続けて見つけたもの（見えない文字・向きの制御文字・文字化けの跡）を1件ずつ。
/// 見つけたものは場所（<see cref="PaletteTarget"/>）を持つので、選ぶと右の欄にその行が出て、Enter でそこへ飛ぶ。
/// </summary>
public static class EncodingInspectionPalette
{
    public const string Category = "文字コード";

    /// <param name="report">調査結果。</param>
    /// <param name="filePath">調べたファイル。</param>
    /// <param name="hasUnsavedChanges">エディタに未保存の変更があるか（調べたのはディスク上の内容だと断る）。</param>
    /// <param name="jump">場所へ飛ぶ動作を作る（ShellWindow のパレットの確定と同じもの）。</param>
    /// <param name="convert">文字コード・改行を変換する動作を作る（null なら変換の項目を出さない）。</param>
    public static List<PaletteCommand> Build(EncodingReport report, string filePath, bool hasUnsavedChanges,
        Func<PaletteTarget, Action> jump, Func<TextConversion, Action>? convert = null)
    {
        var items = new List<PaletteCommand>();
        var savedNote = hasUnsavedChanges
            ? Environment.NewLine + "※ 調べたのはディスク上の内容です（エディタの未保存の変更は含みません）。"
            : "";

        if (!report.IsDecoded)
        {
            items.Add(new PaletteCommand(Category, "文字コードを判定できませんでした", () => { })
            {
                Badge = "不明",
                Detail = "BOM が無く、UTF-8 としても Shift_JIS としても読めません（バイナリ、またはほかの文字コード）。" + savedNote,
            });
            return items;
        }

        items.Add(new PaletteCommand(Category, report.EncodingName!, () => { })
        {
            Badge = report.HasBom ? "BOM あり" : "BOM なし",
            Detail = DescribeEncoding(report) + savedNote,
        });

        var lineEndings = report.LineEndingSummary + (report.EndsWithNewline || report.LineEndingSummary == "改行なし"
            ? "" : " ・ 最後の行に改行なし");
        items.Add(new PaletteCommand("改行", lineEndings, () => { })
        {
            Badge = report.HasMixedLineEndings ? "混在" : null,
            Detail = report.HasMixedLineEndings
                ? "改行の形式が混ざっています。差分ツールやシェルスクリプトで行が合わない・余計な ^M が出る原因になります。"
                : "改行の形式はそろっています。",
        });

        if (convert is not null)
            items.AddRange(ConversionItems(report, convert, hasUnsavedChanges));

        if (report.Findings.Count == 0)
        {
            items.Add(new PaletteCommand("見つけたもの", "見えない文字・文字化けの跡は見つかりませんでした", () => { })
            {
                Detail = "全角スペース・ゼロ幅文字・制御文字・表示の向きを変える文字・置換文字（U+FFFD）・"
                    + "別の文字コードで読んだ跡（縺繧… や Ã©…）を探しました。",
            });
            return items;
        }

        foreach (var finding in report.Findings)
        {
            var target = new PaletteTarget(filePath, finding.Line, finding.Column);
            items.Add(new PaletteCommand(KindLabel(finding.Kind), EncodingInspector.Label(finding), jump(target))
            {
                Target = target,
                Badge = KindBadge(finding.Kind),
            });
        }
        if (report.Truncated)
            items.Add(new PaletteCommand("見つけたもの",
                string.Create(CultureInfo.InvariantCulture, $"先頭の {EncodingInspector.MaxFindings} 件だけを出しています"), () => { }));
        return items;
    }

    /// <summary>
    /// 変換の項目。いまの形と同じになる変換は出さない（押しても何も変わらない項目を並べない）。
    /// 変換はディスク上のファイルを書き換え、変換前の本文をローカル履歴に残す。
    /// </summary>
    private static IEnumerable<PaletteCommand> ConversionItems(EncodingReport report, Func<TextConversion, Action> convert,
        bool hasUnsavedChanges)
    {
        var note = "変換前の本文はローカル履歴に「変換する前」として残ります。"
            + (hasUnsavedChanges ? Environment.NewLine + "※ 未保存の変更があるので、保存してから変換してください。" : "");

        if (report.EncodingName != "UTF-8")
            yield return Conversion(TextConversion.ToUtf8, "UTF-8（BOM なし）に変換して保存",
                "いまの文字コードで読んだ本文を、BOM なしの UTF-8 で書き直します。" + Environment.NewLine + note);
        if (report.EncodingName != "UTF-8 (BOM)")
            yield return Conversion(TextConversion.ToUtf8WithBom, "UTF-8（BOM 付き）に変換して保存",
                "いまの文字コードで読んだ本文を、BOM 付きの UTF-8 で書き直します（古い Windows のツール向け）。"
                + Environment.NewLine + note);
        if (report.CrLf > 0 || report.Cr > 0)
            yield return Conversion(TextConversion.ToLf, "改行を LF にそろえて保存",
                "CRLF・CR をすべて LF にします（文字コードはそのまま）。" + Environment.NewLine + note);
        if (report.Lf > 0 || report.Cr > 0)
            yield return Conversion(TextConversion.ToCrLf, "改行を CRLF にそろえて保存",
                "LF・CR をすべて CRLF にします（文字コードはそのまま）。" + Environment.NewLine + note);

        PaletteCommand Conversion(TextConversion kind, string title, string detail)
            => new("変換", title, convert(kind)) { Detail = detail };
    }

    private static string DescribeEncoding(EncodingReport report) => report.EncodingName switch
    {
        "UTF-8" => "BOM なしの UTF-8。いまの標準的な形です。",
        "UTF-8 (BOM)" => "BOM 付きの UTF-8。Windows の古いツールは BOM で UTF-8 を見分けますが、"
            + "Unix 系のツール（シェルの shebang・一部の JSON パーサ）は BOM を文字として読んでしまいます。",
        "Shift_JIS" => "Shift_JIS（cp932）。UTF-8 として読むと化けます。検索・置換は文字コードを保ったまま扱います。",
        { } name when name.StartsWith("UTF-16", StringComparison.Ordinal) => "UTF-16。テキスト系のツールの多くはそのままでは読めません。",
        _ => "",
    };

    private static string KindLabel(EncodingFindingKind kind) => kind switch
    {
        EncodingFindingKind.Bidi => "向きの制御",
        EncodingFindingKind.Mojibake => "文字化け",
        _ => "見えない文字",
    };

    private static string? KindBadge(EncodingFindingKind kind) => kind switch
    {
        EncodingFindingKind.Bidi => "要注意",
        EncodingFindingKind.Mojibake => "化け?",
        _ => null,
    };
}

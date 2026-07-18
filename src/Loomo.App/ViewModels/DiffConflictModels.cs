
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>通常行1行（コンフリクトの外側、両者で共通の内容）。クリック不可。行番号は3列それぞれの
/// 「その側の版のファイルにおける絶対行番号」（マーカー行は数えない。Ours版/解決結果/Theirs 版で
/// コンフリクトの行数が違うと、同じ内容の行でも列ごとに番号がずれる — Rider の3-way merge と同じ）。</summary>
public sealed record ConflictOrdinaryLineVm(int OursNumber, int ResultNumber, int TheirsNumber, string Text);

/// <summary>通常行のまとまり（コンフリクトとコンフリクトの間の地の文）。View は1行ずつ要素を作らず、
/// 改行で結合したガター/本文文字列を TextBlock にそのまま流す（大きいファイルでも要素数が行数に比例しない）。</summary>
public sealed record ConflictOrdinaryBlockVm(IReadOnlyList<ConflictOrdinaryLineVm> Lines)
{
    /// <summary>Ours 列の行番号ガター（"12\n13\n14" 形式）。</summary>
    public string OursGutterText { get; } = string.Join('\n', Lines.Select(l => l.OursNumber));

    /// <summary>Result 列の行番号ガター。未解決コンフリクトは0行と数えるので、解決が進むと振り直される。</summary>
    public string ResultGutterText { get; } = string.Join('\n', Lines.Select(l => l.ResultNumber));

    /// <summary>Theirs 列の行番号ガター。</summary>
    public string TheirsGutterText { get; } = string.Join('\n', Lines.Select(l => l.TheirsNumber));

    /// <summary>本文（各行を改行で結合。TextBlock は埋め込み改行をそのまま描画する）。</summary>
    public string BodyText { get; } = string.Join('\n', Lines.Select(l => l.Text));
}

/// <summary>コンフリクトの Ours/Theirs ペイン内の1行。<see cref="Kind"/> は <c>"Context"</c>
/// （もう一方の側にも同じ内容の行がある＝共通）か <c>"Distinct"</c>（この側にしかない＝差分）。
/// <see cref="LineNumber"/> はその側の版のファイルにおける絶対行番号（通常行と同じ数え方の続き）。</summary>
public sealed record ConflictSideLineVm(int LineNumber, string Text, string Kind);

/// <summary>
/// コンフリクト1件（採用操作の対象）。Rider の3-way マージ画面と同じ考え方で、Ours（読み取り専用）/
/// Result（自由編集）/ Theirs（読み取り専用）の3ペインとして表示する。«/» で Ours・Theirs を Result へ
/// その場で取り込めるほか、Result 欄へ直接手で書いて「適用」してもよい。Result の既定は空（未解決）。
/// </summary>
public sealed partial class ConflictRegionVm : ObservableObject
{
    public ConflictRegionVm(
        int index, string oursLabel, string theirsLabel,
        IReadOnlyList<string> oursLines, IReadOnlyList<string> theirsLines,
        int oursStartLine, int resultStartLine, int theirsStartLine)
    {
        Index = index;
        OursLabel = oursLabel;
        TheirsLabel = theirsLabel;
        ResultStartLine = resultStartLine;
        _resultLineNumberText = resultStartLine.ToString();

        // Ours→Theirs の行diffを、Ours にしか無い行／Theirs にしか無い行のハイライトに使う
        // （通常の新旧diffではなく「この側だけの内容か」という身元の意味で Distinct を付ける）。
        var diff = DiffUtil.ComputeFull(string.Join('\n', oursLines), string.Join('\n', theirsLines));
        OursDisplayLines = BuildSideLines(diff, skip: DiffLineKind.Added, start: oursStartLine);
        TheirsDisplayLines = BuildSideLines(diff, skip: DiffLineKind.Removed, start: theirsStartLine);
    }

    private static IReadOnlyList<ConflictSideLineVm> BuildSideLines(
        IReadOnlyList<DiffLine> diff, DiffLineKind skip, int start)
    {
        var result = new List<ConflictSideLineVm>();
        var n = start;
        foreach (var line in diff)
        {
            if (line.Kind == skip) continue;
            result.Add(new ConflictSideLineVm(n, line.Text, line.Kind == DiffLineKind.Context ? "Context" : "Distinct"));
            n++;
        }
        return result;
    }

    /// <summary><see cref="ParsedConflictFile.Regions"/> 内での位置（解決 API 呼び出し・ナビゲーションの識別子）。</summary>
    public int Index { get; }
    public string OursLabel { get; }
    public string TheirsLabel { get; }

    /// <summary>Ours ペインの表示行（そのコンフリクト内でのみの相対行番号＋差分ハイライト種別）。</summary>
    public IReadOnlyList<ConflictSideLineVm> OursDisplayLines { get; }
    /// <summary>Theirs ペインの表示行。</summary>
    public IReadOnlyList<ConflictSideLineVm> TheirsDisplayLines { get; }

    /// <summary>Result 欄先頭行の絶対行番号（解決結果のファイルでこのコンフリクトが始まる位置）。</summary>
    public int ResultStartLine { get; }

    /// <summary>中央（Result）ペインの編集中テキスト。既定は空＝未解決。</summary>
    [ObservableProperty] private string _resultText = "";

    /// <summary>Result ペインの行番号ガター（"5\n6\n7" 形式・<see cref="ResultStartLine"/> 始まりの絶対番号。
    /// TextBlock にそのままバインドすれば改行として描画される）。</summary>
    [ObservableProperty] private string _resultLineNumberText;

    partial void OnResultTextChanged(string value)
    {
        var count = value.Length == 0 ? 1 : value.Replace("\r\n", "\n").Split('\n').Length;
        ResultLineNumberText = string.Join('\n', Enumerable.Range(ResultStartLine, count));
    }

    /// <summary>前へ/次へナビゲーションの現在地か（枠を強調表示する）。</summary>
    [ObservableProperty] private bool _isCurrent;
}

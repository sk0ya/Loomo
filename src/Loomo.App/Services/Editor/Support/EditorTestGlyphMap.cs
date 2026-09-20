using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Editor.Controls.Rendering;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタのガターに出す「テスト実行 ▶ ／結果」グリフの組み立て（WPF 非依存・そのまま単体テストできる）。
///
/// <para>入力はテストエクスプローラの行（<see cref="TestItemViewModel"/>）で、出るのはそのファイル 1 枚ぶんの
/// <see cref="EditorTestGlyph"/> 一覧。<b>行の基準がここで 1 始まり→0 始まりへ変わる</b>——テスト側の
/// <see cref="TestItemViewModel.DeclarationLine"/> は 1 始まり（ソース走査・スタックトレースの流儀）、
/// エディタのバッファ行はブレークポイントと同じ 0 始まりなので、変換はこの 1 箇所だけで行う。</para>
///
/// <para>対象ファイルの判定は<b>ワークスペースに問う</b>（<see cref="IWorkspaceService.Contains"/>）。
/// プライマリフォルダーと前方一致させると、あとから追加したフォルダーのテストが黙って対象外になる
/// （マルチルート。設計書 §32.10）。同じテストがどのフォルダーに居ても同じように ▶ が出る。</para>
///
/// <para>結果グリフの元になる状態は「テストの状態」そのもので、<b>グリフ列は全置換</b>で送る前提
/// （エディタ側 API がそうなっている）。だからここは常にそのファイルの全グリフを作る。パス比較は
/// <see cref="TestItemViewModel.NormalizedDeclarationPath"/>（設定時に正規化済み）を使い、
/// 打鍵ごとの再送でテスト件数ぶんの <c>Path.GetFullPath</c> を呼ばない。</para></summary>
internal static class EditorTestGlyphMap
{
    /// <summary>キャレットから上方向へ「今いるテスト」を探す上限行数。テストメソッド 1 個の長さを超えて
    /// 遡ると、末尾のヘルパにキャレットを置いただけで無関係なテストが実行対象になるため区切る。</summary>
    internal const int MaxCaretLookback = 200;

    /// <summary><paramref name="filePath"/> のガターへ出すグリフ一覧（行の昇順）。テストが 1 件も無ければ空。
    /// 列そのものを出すかどうかは <see cref="EditorTestGlyphColumns"/> が決める（0 件で畳むとちらつくため）。</summary>
    public static IReadOnlyList<EditorTestGlyph> Build(IWorkspaceService workspace,
        IReadOnlyList<TestItemViewModel>? tests, string? filePath)
    {
        var byLine = GroupByLine(workspace, tests, filePath);
        if (byLine.Count == 0) return Array.Empty<EditorTestGlyph>();

        var glyphs = new List<EditorTestGlyph>(byLine.Count);
        foreach (var (line1, items) in byLine.OrderBy(p => p.Key))
            glyphs.Add(new EditorTestGlyph(line1 - 1, KindOf(items), Tooltip(items)));
        return glyphs;
    }

    /// <summary>そのバッファ行（0 始まり）に紐づくテスト（▶ クリックの実行対象）。無ければ空。</summary>
    public static IReadOnlyList<TestItemViewModel> TestsAt(IWorkspaceService workspace,
        IReadOnlyList<TestItemViewModel>? tests, string? filePath, int line0)
        => GroupByLine(workspace, tests, filePath).TryGetValue(line0 + 1, out var items)
            ? items
            : Array.Empty<TestItemViewModel>();

    /// <summary>キャレット行（0 始まり）から実行するテストを 1 件選ぶ（コマンドパレットの「カーソル行のテストを実行」）。
    /// その行に宣言があればそれ、無ければ<b>キャレットより上で最も近い宣言</b>——本文の中にキャレットを置いた
    /// ままでも「今いるテスト」を実行できるようにするため。<see cref="MaxCaretLookback"/> 行より上は見ない
    /// （テストの外にいるのに実行対象が出てしまうため）。見つからなければ null（コマンド自体を出さない）。</summary>
    public static TestItemViewModel? TestForCaret(IWorkspaceService workspace,
        IReadOnlyList<TestItemViewModel>? tests, string? filePath, int line0)
    {
        var byLine = GroupByLine(workspace, tests, filePath);
        if (byLine.Count == 0) return null;
        var line1 = line0 + 1;
        var best = byLine.Keys.Where(k => k <= line1 && line1 - k <= MaxCaretLookback).DefaultIfEmpty(0).Max();
        return best > 0 ? byLine[best][0] : null;
    }

    /// <summary>1 行に複数のテストが宣言されている場合の集約（テストペインのグループ集計と同じ流儀：
    /// 1 件でも失敗なら失敗、実行中があれば実行中、1 件も走っていなければ ▶、成功があれば成功、残りはスキップ）。</summary>
    internal static TestGlyphKind KindOf(IReadOnlyList<TestItemViewModel> items)
    {
        if (items.Any(t => t.Status == TestStatus.Failed)) return TestGlyphKind.Failed;
        if (items.Any(t => t.Status == TestStatus.Running)) return TestGlyphKind.Running;
        if (items.All(t => t.Status == TestStatus.NotRun)) return TestGlyphKind.Run;
        if (items.Any(t => t.Status == TestStatus.Passed)) return TestGlyphKind.Passed;
        return TestGlyphKind.Skipped;
    }

    /// <summary>ツールチップ（1 行に複数あれば改行で連ねる）。</summary>
    internal static string Tooltip(IReadOnlyList<TestItemViewModel> items)
        => string.Join("\n", items.Select(Describe));

    /// <summary>テスト 1 件ぶんの文言（状態・所要時間・失敗メッセージ・クリックの意味）。</summary>
    internal static string Describe(TestItemViewModel t)
    {
        var time = FormatDuration(t.Duration);
        return t.Status switch
        {
            TestStatus.Running => $"実行中… {t.DisplayName}",
            TestStatus.Passed => $"✓ 成功: {t.DisplayName}{time}\nクリックで再実行",
            TestStatus.Failed => $"✗ 失敗: {t.DisplayName}{time}"
                + (string.IsNullOrWhiteSpace(t.Message) ? "" : $"\n{t.Message}")
                + "\nクリックで再実行",
            TestStatus.Skipped => $"⊘ スキップ: {t.DisplayName}\nクリックで再実行",
            _ => $"▶ テストを実行: {t.DisplayName}",
        };
    }

    /// <summary>所要時間の表記（前後の括弧込み。不明なら空文字）。<b>丸めてから</b>単位を選ぶ
    /// ——先に閾値で分けると 999.6ms が「1000 ms」と出る。</summary>
    internal static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d || d < TimeSpan.Zero) return "";
        var ms = Math.Round(d.TotalMilliseconds, MidpointRounding.AwayFromZero);
        return ms < 1000
            ? $"（{ms.ToString("0", CultureInfo.InvariantCulture)} ms）"
            : $"（{(d.TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture)} 秒）";
    }

    /// <summary>そのファイルのテストを宣言行（1 始まり）でまとめる。ワークスペース外・位置不明のテストは落とす。</summary>
    private static Dictionary<int, List<TestItemViewModel>> GroupByLine(IWorkspaceService workspace,
        IReadOnlyList<TestItemViewModel>? tests, string? filePath)
    {
        var result = new Dictionary<int, List<TestItemViewModel>>();
        if (tests is null || tests.Count == 0) return result;
        if (string.IsNullOrWhiteSpace(filePath) || !workspace.Contains(filePath)) return result;

        var target = WorkspacePaths.Normalize(filePath);
        foreach (var t in tests)
        {
            if (t.DeclarationLine <= 0 || t.NormalizedDeclarationPath.Length == 0) continue;
            if (!string.Equals(t.NormalizedDeclarationPath, target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!result.TryGetValue(t.DeclarationLine, out var bucket))
                result[t.DeclarationLine] = bucket = new List<TestItemViewModel>();
            bucket.Add(t);
        }
        return result;
    }
}

/// <summary>ガターの<b>テスト列を出すか</b>の記憶（WPF 非依存・単体テスト可能）。
///
/// <para>「いまグリフが 1 件以上あるか」で決めると本文左端がちらつく。テスト一覧は保存契機で
/// 再走査され（<c>TestSourceWatcher</c>）、クラスの <c>{</c> を消した途中・属性をコメントアウト・
/// <c>[Fact]</c> を打ちかけ、といった状態ではパーサが拾えず<b>未実行のテストが一斉に消える</b>
/// （<c>ApplyDiscovered</c> は未実行の行を掃除する）。そのたびに列が畳まれ、直すとまた開く
/// ＝本文が左右に動く。</para>
///
/// <para>そこで「いま 0 件か」ではなく「<b>このファイルはテストソースか</b>（一度でもテストが見つかったか）」で
/// 決める。ワークスペースが変わったら <see cref="Reset"/> で忘れる。</para></summary>
internal sealed class EditorTestGlyphColumns
{
    private readonly HashSet<string> _testSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>このファイルでガターのテスト列を有効にするか。1 件でも見つかったファイルは記憶し、
    /// 以後 0 件になっても列を畳まない。</summary>
    public bool ShouldEnable(string? filePath, int glyphCount)
    {
        var key = WorkspacePaths.Normalize(filePath);
        if (key.Length == 0) return false;
        if (glyphCount > 0) _testSources.Add(key);
        return _testSources.Contains(key);
    }

    /// <summary>記憶を捨てる（ワークスペース切替）。</summary>
    public void Reset() => _testSources.Clear();
}

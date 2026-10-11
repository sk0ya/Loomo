using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services.Testing;

/// <summary>テスト 1 件の結果の種別（ランナー非依存）。xfail・todo・pending 等はすべて <see cref="Skipped"/> に寄せる。</summary>
public enum ScriptTestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>ランナーの機械可読レポート（jest 互換 JSON／JUnit XML）から読んだ結果 1 件。
/// <para><see cref="FilePath"/> は絶対パス、<see cref="Title"/> はファイル内での識別名——JS は describe 連結
/// （<c>a &gt; b &gt; テスト名</c>）、pytest はノード ID の後半（<c>TestCls::test_x</c>）。探索側
/// （<see cref="ScriptDiscoveredTest.Title"/>）と同じ形なので、<c>ファイル::タイトル</c> で行と突き合わせられる。</para>
/// <para><see cref="CaseName"/> はパラメータ化テストの 1 ケース（pytest の <c>test_x[1-2]</c>）のときだけ入り、
/// <see cref="Title"/> の側は引数を落とした名前になっている——メソッド単位の行へ集約するため（dotnet のテオリと同じ扱い）。</para>
/// <para><see cref="FailurePath"/>/<see cref="FailureLine"/> は失敗のトレースバックから拾った位置（1 始まり。無ければ null/0）、
/// <see cref="DeclarationLine"/> はレポートが宣言行を持つとき（pytest xunit1 の <c>line</c>）の 1 始まりの行。</para></summary>
public sealed record ScriptTestResult(
    string FilePath,
    string Title,
    ScriptTestOutcome Outcome,
    string? Message,
    TimeSpan? Duration = null,
    string? CaseName = null,
    string? FailurePath = null,
    int FailureLine = 0,
    int DeclarationLine = 0);

/// <summary>ソース走査で見つけたテスト 1 件（ビルド・ランナー起動を伴わない）。<see cref="Line1"/> は 1 始まりの宣言行。</summary>
public sealed record ScriptDiscoveredTest(string FilePath, string Title, int Line1, bool IsParameterized);

/// <summary>実行対象 1 つ。<see cref="File"/> はプロジェクト相対のファイル、<see cref="Selector"/> はそのファイル内の
/// 1 件の指定（JS＝葉タイトル、pytest＝ノード ID 後半。null＝ファイル全体）。</summary>
public sealed record TestTarget(string File, string? Selector = null);

/// <summary>1 回の実行の依頼。<see cref="FileScope"/> はプロジェクト相対のファイル（null＝プロジェクト全体）、
/// <see cref="TestSelector"/> はそのファイル内の 1 件の指定（JS＝葉タイトル、pytest＝ノード ID 後半）。
/// <see cref="Targets"/> は複数ファイル・複数件をまとめて走らせるとき（失敗だけの再実行）で、指定すると前の 2 つより優先する。
/// <see cref="ResultFile"/> はランナーにレポートを書かせる先（実行ごとに一意名）。</summary>
public sealed record TestRunRequest(string ProjectDir, string ResultFile, string? FileScope = null,
    string? TestSelector = null, IReadOnlyList<TestTarget>? Targets = null)
{
    /// <summary>実際に走らせる対象。空＝プロジェクト全体。</summary>
    public IReadOnlyList<TestTarget> EffectiveTargets
        => Targets ?? (FileScope is { } file ? [new TestTarget(file, TestSelector)] : []);
}

/// <summary>
/// テストランナー 1 種（vitest / jest / pytest）の口。<b>検出・コマンド組み立て・レポートの読み取り</b>の 3 つだけを持ち、
/// 実行そのもの（可視ターミナルで走らせる）と結果のテスト一覧への反映は App 側の共通処理が受け持つ——
/// ランナーを足すときに UI へ手を入れずに済むように、ここは純関数に寄せてある。
/// <para>dotnet はこの口に<b>乗せていない</b>。MSBuild 評価済みのプロジェクトモデル・TRX・testhost のデバッグ attach・
/// coverlet と、契約がこれより広く、無理に寄せると既存の挙動を崩すため（設計書 §28.14）。</para>
/// </summary>
public interface ITestRunner
{
    /// <summary>識別子（<c>vitest</c> / <c>jest</c> / <c>pytest</c>）。表示にもそのまま使う。</summary>
    string Id { get; }

    /// <summary>レポートファイルの拡張子（<c>.json</c> / <c>.xml</c>）。</summary>
    string ResultFileExtension { get; }

    /// <summary><paramref name="directory"/> がこのランナーのプロジェクトの根か。null＝違う。
    /// <see cref="TestRunnerMatch.Strong"/> は設定そのもの（package.json の依存・設定ファイル・<c>[tool.pytest…]</c>）、
    /// <see cref="TestRunnerMatch.Weak"/> は「その言語のプロジェクトではある」程度の手掛かり（conftest.py 等）。</summary>
    TestRunnerMatch? Detect(string directory);

    /// <summary>可視ターミナルへ流す PowerShell のコマンド行を組み立てる。</summary>
    string BuildCommand(TestRunRequest request, TestToolLocator tools);

    /// <summary>レポート本文を読む。<paramref name="projectDir"/> は相対パスの基準（pytest の rootdir）。
    /// 壊れたレポートは例外で返す（呼び出し側が「読めません」と出す）。</summary>
    IReadOnlyList<ScriptTestResult> ParseResults(string content, string projectDir);
}

/// <summary>検出の確度。強い一致は最寄りのものが勝ち、弱い一致だけのときは最も外側のものを採る
/// （<see cref="TestProjectLocator"/>）。</summary>
public enum TestRunnerMatch
{
    Weak,
    Strong,
}

/// <summary>PowerShell のクォート（コマンド組み立ての共通部品）。</summary>
internal static class PwshQuote
{
    /// <summary>シングルクォート（内部の ' は '' に）。変数展開・バッククォートを無効化する。</summary>
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";
}

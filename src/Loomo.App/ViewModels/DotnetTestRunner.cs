using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary><c>dotnet test</c>（TRX ロガー付き）の実行と、その結果のテスト一覧への反映をまとめたヘルパ。</summary>
internal static class DotnetTestRunner
{
    /// <summary>TRX の出力先ディレクトリ（毎回上書き）。<c>%TEMP%/Loomo/test-results</c>。</summary>
    private static readonly string ResultsDir = Path.Combine(Path.GetTempPath(), "Loomo", "test-results");

    /// <summary>テストのビルド出力を既定 <c>bin</c> の外へ逃がす MSBuild 引数。起動中の Loomo 自身が既定 bin の
    /// .exe/.dll をロックし <c>dotnet test</c> のビルドが失敗するのを防ぐ。<c>BaseOutputPath</c> は相対なのでプロジェクトごとに分かれる。</summary>
    private const string BuildRedirect = "/p:BaseOutputPath=artifacts/loomo-test/";

    /// <summary><c>dotnet test</c> を TRX ロガー付きで実行する。<paramref name="filterExpr"/> が非 null なら
    /// <c>--filter</c> を付ける。出力はコンソールへ流し、生成された TRX のパスを返す（生成されなければ null）。</summary>
    public static async Task<string?> RunAsync(ITerminalService terminal, IDebugSession session,
        string target, string? filterExpr, string label)
    {
        string trx;
        try
        {
            Directory.CreateDirectory(ResultsDir);
            trx = Path.Combine(ResultsDir, "loomo.trx");
            if (File.Exists(trx)) File.Delete(trx);  // 前回分を残さない
        }
        catch (Exception ex)
        {
            session.Append(DebugOutputCategory.Important, $"テスト結果フォルダを準備できません: {ex.Message}");
            return null;
        }

        var filterArg = filterExpr is null ? "" : $" --filter \"{filterExpr}\"";
        session.Append(DebugOutputCategory.Important, label);
        var result = await terminal.RunCommandAsync(
            $"$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test \"{target}\"{filterArg} --nologo {BuildRedirect} " +
            $"--logger \"trx;LogFileName=loomo.trx\" --results-directory \"{ResultsDir}\"",
            CancellationToken.None);
        session.WriteConsole(result.Output);
        return File.Exists(trx) ? trx : null;
    }

    /// <summary>TRX を読み、各結果を名前で突き合わせて行のステータス・失敗メッセージ・ソース位置を更新する。
    /// テオリ等のケース（<c>FQN(args)</c>）は引数を落とした名前でメソッド単位の行へ集約する。一覧に無いテストは追加する。</summary>
    public static void ApplyTrx(string trxPath, IDebugSession session, ObservableCollection<TestItemViewModel> tests)
    {
        var results = TrxResultParser.Parse(trxPath, out var error);
        if (error is not null)
        {
            session.Append(DebugOutputCategory.Important, $"テスト結果(TRX)を読めません: {error}");
            return;
        }

        foreach (var r in results)
        {
            var item = tests.FirstOrDefault(t => string.Equals(t.FullyQualifiedName, r.Name, StringComparison.Ordinal));
            var isCase = false;
            if (item is null)
            {
                var paren = r.Name.IndexOf('(');
                if (paren > 0)
                {
                    var baseName = r.Name[..paren];
                    item = tests.FirstOrDefault(t => string.Equals(t.FilterExpression, baseName, StringComparison.Ordinal));
                    isCase = item is not null;
                }
            }
            if (item is null) { item = new TestItemViewModel(r.Name); tests.Add(item); }

            if (isCase) item.ApplyCaseResult(r.Status, r.Message, r.SourcePath, r.Line);
            else item.Update(r.Status, r.Message, r.SourcePath, r.Line);
        }
    }
}

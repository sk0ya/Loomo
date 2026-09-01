using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.CSharp.Testing;

/// <summary>C#テストの実行結果。TRXの解釈とUIへの反映はホスト側が担当し、
/// testコマンドと成果物の管理はこのC#機能DLLが担当する。</summary>
public sealed record CSharpTestExecutionResult(
    CommandResult? Command,
    string? TrxPath,
    string? PreparationError = null,
    string? ResultsDirectory = null);

/// <summary>C#カバレッジ実行の結果。collectorが結果を生成しない場合でも、
/// 実行結果と保存先を失わずにホストへ返す。</summary>
public sealed record CSharpCoverageExecutionResult(
    CommandResult? Command,
    string ResultsDirectory,
    string? PreparationError = null);

/// <summary><c>dotnet test</c>とcoverletのC#固有実行経路。
/// Appは出力表示・Problems・TRXのViewModel反映だけを行う。</summary>
public static class CSharpTestExecutionService
{
    private static readonly string ResultsRootDirectory =
        Path.Combine(Path.GetTempPath(), "Loomo", "test-results");
    private static readonly string CoverageRootDirectory =
        Path.Combine(Path.GetTempPath(), "Loomo", "coverage-results");

    private const string BuildRedirect = "/p:BaseOutputPath=artifacts/loomo-test/";

    /// <summary>テストメソッドの完全名から、dotnet testのORフィルターを作る。</summary>
    public static string BuildFullyQualifiedNameFilter(IEnumerable<string> fullyQualifiedNames)
        => string.Join("|", (fullyQualifiedNames ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Select(name => $"FullyQualifiedName={name}"));

    /// <summary>テスト実行のコマンドを返す。PowerShellの単一引用符でパスとfilterを保護する。</summary>
    public static string BuildTestCommand(
        string target, string? filterExpression, string configuration, string resultsDirectory,
        string? targetFramework = null)
    {
        var filter = filterExpression is null
            ? ""
            : $" --filter {PowerShellQuote(filterExpression)}";
        var framework = string.IsNullOrWhiteSpace(targetFramework)
            ? ""
            : " -f " + PowerShellQuote(targetFramework);
        return "$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test " + PowerShellQuote(target) +
            " -c " + PowerShellQuote(configuration) + framework + filter + " --nologo " + BuildRedirect +
            " --logger \"trx;LogFileName=loomo.trx\" --results-directory " +
            PowerShellQuote(resultsDirectory);
    }

    /// <summary>カバレッジ実行のコマンドを返す。</summary>
    public static string BuildCoverageCommand(
        string target, string configuration, string resultsDirectory,
        string? targetFramework = null)
    {
        var framework = string.IsNullOrWhiteSpace(targetFramework)
            ? ""
            : " -f " + PowerShellQuote(targetFramework);
        return "$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test " + PowerShellQuote(target) +
           " -c " + PowerShellQuote(configuration) + framework +
           " --collect:\"XPlat Code Coverage\" --nologo " + BuildRedirect +
           " --results-directory " + PowerShellQuote(resultsDirectory);
    }

    /// <summary>公式test adapterで実テスト名を列挙するコマンドを返す。</summary>
    public static string BuildListTestsCommand(
        string target, string configuration, string? targetFramework = null)
    {
        var framework = string.IsNullOrWhiteSpace(targetFramework)
            ? ""
            : " -f " + PowerShellQuote(targetFramework);
        return "$env:DOTNET_CLI_UI_LANGUAGE='en'; dotnet test " + PowerShellQuote(target) +
           " -c " + PowerShellQuote(configuration) + framework +
           " --list-tests --nologo " + BuildRedirect;
    }

    /// <summary>公式test adapterの列挙を実行する。結果の解析とUI反映はApp側が担当する。</summary>
    public static Task<CommandResult> RunListTestsAsync(
        ITerminalService terminal, string target, string configuration = "Debug",
        CancellationToken cancellationToken = default,
        string? targetFramework = null)
        => terminal.RunCommandInVisibleTerminalAsync(
            BuildListTestsCommand(target, configuration, targetFramework), cancellationToken);

    /// <summary><c>dotnet test</c>をTRXロガー付きで実行する。準備失敗は例外にせず結果へ載せる。</summary>
    public static async Task<CSharpTestExecutionResult> RunAsync(
        ITerminalService terminal,
        string target,
        string? filterExpression,
        string configuration = "Debug",
        CancellationToken cancellationToken = default,
        string? targetFramework = null)
    {
        // 実行ごとに分離する。固定 loomo.trx だと、並行実行や直前の終了通知の遅延で
        // 別の実行結果を拾い、再実行の状態を巻き戻す可能性がある。
        var resultsDirectory = Path.Combine(
            ResultsRootDirectory, Guid.NewGuid().ToString("N"));
        var trxPath = Path.Combine(resultsDirectory, "loomo.trx");
        try
        {
            Directory.CreateDirectory(resultsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, null, $"テスト結果フォルダを準備できません: {ex.Message}", resultsDirectory);
        }

        var command = BuildTestCommand(
            target, filterExpression, configuration, resultsDirectory, targetFramework);
        try
        {
            var result = await terminal.RunCommandInVisibleTerminalAsync(command, cancellationToken);
            return new(result, File.Exists(trxPath) ? trxPath : null,
                ResultsDirectory: resultsDirectory);
        }
        catch
        {
            // 呼び出し元へexecutionを返せないキャンセル／Terminal例外でも、ここで所有する
            // 一時成果物を回収して次回実行へ漏らさない。
            TryDeleteResultsDirectory(resultsDirectory);
            throw;
        }
    }

    /// <summary>このサービスが作成した1回分のTRX成果物を後始末する。失敗してもテスト結果の
    /// UI反映を妨げない。実行ごとのディレクトリだけを対象にするため、別実行の結果を削除しない。</summary>
    public static void CleanupResults(CSharpTestExecutionResult execution)
    {
        if (string.IsNullOrWhiteSpace(execution.ResultsDirectory)) return;
        string directory, root;
        try
        {
            directory = Path.GetFullPath(execution.ResultsDirectory);
            root = Path.GetFullPath(ResultsRootDirectory);
        }
        catch (ArgumentException) { return; }
        if (!string.Equals(Path.GetDirectoryName(directory), root,
                StringComparison.OrdinalIgnoreCase)) return;
        TryDeleteResultsDirectory(directory);
    }

    private static void TryDeleteResultsDirectory(string resultsDirectory)
    {
        try { Directory.Delete(resultsDirectory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>coverletのXPlat Code Coverage collector付きでC#テストを実行する。</summary>
    public static async Task<CSharpCoverageExecutionResult> RunCoverageAsync(
        ITerminalService terminal,
        string target,
        string configuration = "Debug",
        CancellationToken cancellationToken = default,
        string? targetFramework = null)
    {
        var resultsDirectory = Path.Combine(
            Path.GetTempPath(), "Loomo", "coverage-results", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(resultsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, resultsDirectory, $"カバレッジ結果フォルダを準備できません: {ex.Message}");
        }

        var command = BuildCoverageCommand(target, configuration, resultsDirectory, targetFramework);
        try
        {
            var result = await terminal.RunCommandInVisibleTerminalAsync(command, cancellationToken);
            return new(result, resultsDirectory);
        }
        catch
        {
            TryDeleteCoverageDirectory(resultsDirectory);
            throw;
        }
    }

    /// <summary>このサービスが作成した1回分のカバレッジ成果物を後始末する。
    /// 所有ルート直下の実行ディレクトリだけを対象にする。</summary>
    public static void CleanupCoverageResults(string? resultsDirectory)
    {
        if (string.IsNullOrWhiteSpace(resultsDirectory)) return;
        string directory, root;
        try
        {
            directory = Path.GetFullPath(resultsDirectory);
            root = Path.GetFullPath(CoverageRootDirectory);
        }
        catch (ArgumentException) { return; }
        if (!string.Equals(Path.GetDirectoryName(directory), root,
                StringComparison.OrdinalIgnoreCase)) return;
        TryDeleteCoverageDirectory(directory);
    }

    private static void TryDeleteCoverageDirectory(string resultsDirectory)
    {
        try { Directory.Delete(resultsDirectory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string PowerShellQuote(string value)
        => "'" + (value ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";
}

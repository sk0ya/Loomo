using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「問題」一覧の1件の重要度（MSBuild の error/warning に対応）。</summary>
public enum ProblemSeverity
{
    Error,
    Warning,
}

/// <summary>「問題」一覧の1件。<c>dotnet build</c> / <c>dotnet test</c> 出力の MSBuild 診断行
/// （<c>path(line,col): error CS1002: …</c>）をパースしたもの。</summary>
public sealed class ProblemItemViewModel
{
    public ProblemItemViewModel(string filePath, int line1, int column1, ProblemSeverity severity,
        string code, string message)
    {
        FilePath = filePath;
        Line1 = line1;
        Column1 = column1;
        Severity = severity;
        Code = code;
        Message = message;
    }

    public string FilePath { get; }
    /// <summary>1始まりの行/列（MSBuild 出力のまま）。</summary>
    public int Line1 { get; }
    public int Column1 { get; }
    public ProblemSeverity Severity { get; }
    /// <summary>診断コード（CS1002 / MSB3027 / MC3000 など）。</summary>
    public string Code { get; }
    public string Message { get; }

    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string Location => $"{Code} · {FileName}:{Line1}:{Column1}";
    public string SeverityGlyph => Severity switch
    {
        ProblemSeverity.Error => "✕",
        _ => "▲",
    };
}

/// <summary>IDE（デバッグ）ペインの「問題」タブ。ビルド系コマンド（<c>dotnet build</c> / <c>dotnet test</c>）の
/// 出力からエラー/警告を抽出して一覧表示する（エディタの LSP 診断は波線で見えるので扱わない——ここは
/// ワークスペース全体の「本物の」ビルド結果）。デバッグセッションには依存しない（全セッション共有のサブ VM）。
/// 流し込みは各ビルド実行箇所が <see cref="IDebugSession.ReportBuildOutput"/> 経由で行う。</summary>
public sealed partial class ProblemsViewModel : ObservableObject
{
    /// <summary>MSBuild の診断行：<c>path(line,col): error|warning CODE: message [proj.csproj]</c>。
    /// 末尾のプロジェクト表記は落とす。サマリ節の再掲は重複除去で吸収する。</summary>
    private static readonly Regex DiagnosticLine = new(
        @"^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(\s*\[[^\[\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>全診断のフラット一覧（エラー→警告、次いでファイル名→行の順）。</summary>
    public ObservableCollection<ProblemItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

    /// <summary>行クリックでその位置へジャンプする要求。ShellWindow が購読する。</summary>
    public event Action<ProblemItemViewModel>? OpenRequested;

    [RelayCommand]
    private void Open(ProblemItemViewModel? item)
    {
        if (item is not null) OpenRequested?.Invoke(item);
    }

    /// <summary>ビルド系コマンドの出力全文からエラー/警告を抽出して一覧を丸ごと差し替える。
    /// 診断行が 1 つも無ければ空になる（＝ビルドがきれいという正しい状態）。</summary>
    public void SetFromBuildOutput(string output) => ReplaceItems(ParseBuildOutput(output));

    /// <summary>MSBuild 診断行のパース（テスト用に分離）。同一診断の再掲（サマリ節・マルチターゲット）は除く。</summary>
    internal static List<ProblemItemViewModel> ParseBuildOutput(string output)
    {
        var items = new List<ProblemItemViewModel>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var m = DiagnosticLine.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;

            var file = m.Groups["file"].Value.Trim();
            var line = int.Parse(m.Groups["line"].Value);
            var col = int.Parse(m.Groups["col"].Value);
            var sev = m.Groups["sev"].Value.StartsWith("e", System.StringComparison.OrdinalIgnoreCase)
                ? ProblemSeverity.Error : ProblemSeverity.Warning;
            var code = m.Groups["code"].Value;
            var msg = m.Groups["msg"].Value;

            if (!seen.Add($"{file}|{line}|{col}|{sev}|{code}|{msg}")) continue;
            items.Add(new ProblemItemViewModel(file, line, col, sev, code, msg));
        }
        return items;
    }

    private void ReplaceItems(IReadOnlyList<ProblemItemViewModel> items)
    {
        var ordered = items
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.FileName, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Line1)
            .ToList();

        Items.Clear();
        foreach (var i in ordered) Items.Add(i);

        HasItems = Items.Count > 0;
        ErrorCount = Items.Count(i => i.Severity == ProblemSeverity.Error);
        WarningCount = Items.Count(i => i.Severity == ProblemSeverity.Warning);
    }
}

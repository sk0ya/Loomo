using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;

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

    public string FileName => Path.GetFileName(FilePath);
    public string LineColumn => $"{Line1}:{Column1}";
    /// <summary>行のツールチップ（メッセージ全文＋コード＋位置）。</summary>
    public string ToolTipText => $"{Message}\n{Code} · {FileName}:{Line1}:{Column1}";
    public string SeverityGlyph => Severity switch
    {
        ProblemSeverity.Error => "✕",
        _ => "▲",
    };
}

/// <summary>「問題」ツリーのファイル見出し（SEARCH ペインの結果ツリーと同じファイル別グルーピング）。
/// 配下にそのファイルの診断行を持ち、開閉状態は更新をまたいでパスで引き継がれる。</summary>
public sealed partial class ProblemFileGroup : ObservableObject
{
    public ProblemFileGroup(string filePath, string relativeDir, IReadOnlyList<ProblemItemViewModel> items)
    {
        FilePath = filePath;
        RelativeDir = relativeDir;
        Items = items;
        ErrorCount = items.Count(i => i.Severity == ProblemSeverity.Error);
        WarningCount = items.Count - ErrorCount;
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    /// <summary>ワークスペース相対のディレクトリ（表示用。ルート直下は空、マルチルート時はフォルダー名前置）。</summary>
    public string RelativeDir { get; }
    public IReadOnlyList<ProblemItemViewModel> Items { get; }
    public int ErrorCount { get; }
    public int WarningCount { get; }
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;

    [ObservableProperty] private bool _isExpanded = true;
}

/// <summary>IDE（デバッグ）ペインの「問題」タブ。ビルド系コマンド（<c>dotnet build</c> / <c>dotnet test</c>）の
/// 出力からエラー/警告を抽出し、ファイル別ツリー（<see cref="Groups"/>）で表示する（エディタの LSP 診断は
/// 波線で見えるので扱わない——ここはワークスペース全体の「本物の」ビルド結果）。デバッグセッションには
/// 依存しない（全セッション共有のサブ VM）。流し込みは各ビルド実行箇所が
/// <see cref="IDebugSession.ReportBuildOutput"/> 経由で行う。</summary>
public sealed partial class ProblemsViewModel : ObservableObject
{
    /// <summary>MSBuild の診断行：<c>path(line,col): error|warning CODE: message [proj.csproj]</c>。
    /// 末尾のプロジェクト表記は落とす。サマリ節の再掲は重複除去で吸収する。</summary>
    private static readonly Regex DiagnosticLine = new(
        @"^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(\s*\[[^\[\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IWorkspaceService? _workspace;

    public ProblemsViewModel(IWorkspaceService? workspace = null) => _workspace = workspace;

    /// <summary>ファイル別のツリー（エラーを含むファイルが先、次いでファイル名順。配下は行順）。</summary>
    public ObservableCollection<ProblemFileGroup> Groups { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

    /// <summary>行クリック（または Enter）でその位置へジャンプする要求。ShellWindow が購読する。</summary>
    public event Action<ProblemItemViewModel>? OpenRequested;

    [RelayCommand]
    private void Open(ProblemItemViewModel? item)
    {
        if (item is not null) OpenRequested?.Invoke(item);
    }

    /// <summary>ビルド系コマンドの出力全文からエラー/警告を抽出してツリーを丸ごと作り直す
    /// （診断行が 1 つも無ければ空＝ビルドがきれいという正しい状態）。ファイルの開閉状態はパスで引き継ぐ。</summary>
    public void SetFromBuildOutput(string output)
    {
        var expanded = Groups.ToDictionary(g => g.FilePath, g => g.IsExpanded, System.StringComparer.OrdinalIgnoreCase);
        var items = ParseBuildOutput(output);

        Groups.Clear();
        var groups = items
            .GroupBy(i => i.FilePath, System.StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProblemFileGroup(g.Key, ToRelativeDir(g.Key),
                g.OrderBy(i => i.Line1).ThenBy(i => i.Column1).ToList()))
            .OrderByDescending(g => g.HasErrors)
            .ThenBy(g => g.FileName, System.StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            if (expanded.TryGetValue(g.FilePath, out var e)) g.IsExpanded = e;
            Groups.Add(g);
        }

        HasItems = Groups.Count > 0;
        ErrorCount = Groups.Sum(g => g.ErrorCount);
        WarningCount = Groups.Sum(g => g.WarningCount);
    }

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

    /// <summary>見出しに添えるワークスペース相対ディレクトリ。ルート直下は空、マルチルート時は
    /// 「フォルダー名/相対パス」（SEARCH の結果ツリーと同じ表記）。ワークスペース外はフルパスのまま。</summary>
    private string ToRelativeDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        var folders = _workspace?.Folders;
        if (folders is null || folders.Count == 0) return dir;
        foreach (var folder in folders)
        {
            if (!dir.StartsWith(folder, System.StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(folder, dir);
            if (rel == ".") rel = "";
            if (folders.Count > 1)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                rel = rel.Length == 0 ? name : $"{name}{Path.DirectorySeparatorChar}{rel}";
            }
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        return dir;
    }
}

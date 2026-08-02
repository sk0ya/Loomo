using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「問題」一覧の1件の重要度（MSBuild の error/warning に対応）。</summary>
public enum ProblemSeverity
{
    Error,
    Warning,
}

public enum ProblemSource { Build, Lsp }

public enum ProblemScope { Workspace, CurrentFile }

/// <summary>「問題」一覧の1件。<c>dotnet build</c> / <c>dotnet test</c> 出力の MSBuild 診断行
/// （<c>path(line,col): error CS1002: …</c>）をパースしたもの。</summary>
public sealed class ProblemItemViewModel
{
    public ProblemItemViewModel(string filePath, int line1, int column1, ProblemSeverity severity,
        string code, string message, ProblemSource source = ProblemSource.Build,
        int? endLine1 = null, int? endColumn1 = null)
    {
        FilePath = filePath;
        Line1 = line1;
        Column1 = column1;
        Severity = severity;
        Code = code;
        Message = message;
        Source = source;
        EndLine1 = endLine1 ?? line1;
        EndColumn1 = endColumn1 ?? column1;
    }

    public string FilePath { get; }
    /// <summary>1始まりの行/列（MSBuild 出力のまま）。</summary>
    public int Line1 { get; }
    public int Column1 { get; }
    public int EndLine1 { get; }
    public int EndColumn1 { get; }
    public ProblemSeverity Severity { get; }
    /// <summary>診断コード（CS1002 / MSB3027 / MC3000 など）。</summary>
    public string Code { get; }
    public string Message { get; }
    public ProblemSource Source { get; }
    public string SourceLabel => Source == ProblemSource.Lsp ? "LSP" : "Build";

    public string FileName => Path.GetFileName(FilePath);
    public string LineColumn => $"{Line1}:{Column1}";
    /// <summary>行のツールチップ（メッセージ全文＋コード＋位置）。</summary>
    public string ToolTipText => $"{Message}\n{SourceLabel} · {Code} · {FileName}:{Line1}:{Column1}";
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
    private IReadOnlyList<ProblemItemViewModel> _buildItems = [];
    private readonly Dictionary<string, IReadOnlyList<ProblemItemViewModel>> _lspItems =
        new(System.StringComparer.OrdinalIgnoreCase);
    private string? _navigationKey;

    public ProblemsViewModel(IWorkspaceService? workspace = null) => _workspace = workspace;

    /// <summary>ファイル別のツリー（エラーを含むファイルが先、次いでファイル名順。配下は行順）。
    /// 更新時はコレクションを一括交換し、1000件でもUIへAdd通知を1000回送らない。</summary>
    private ObservableCollection<ProblemFileGroup> _groups = new();
    public ObservableCollection<ProblemFileGroup> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private bool _showErrors = true;
    [ObservableProperty] private bool _showWarnings = true;
    [ObservableProperty] private bool _showBuild = true;
    [ObservableProperty] private bool _showLsp = true;
    [ObservableProperty] private ProblemScope _scope;
    [ObservableProperty] private string? _currentFilePath;

    partial void OnShowErrorsChanged(bool value) => Rebuild();
    partial void OnShowWarningsChanged(bool value) => Rebuild();
    partial void OnShowBuildChanged(bool value) => Rebuild();
    partial void OnShowLspChanged(bool value) => Rebuild();
    partial void OnScopeChanged(ProblemScope value) => Rebuild();
    partial void OnCurrentFilePathChanged(string? value)
    {
        if (Scope == ProblemScope.CurrentFile) Rebuild();
    }

    /// <summary>行クリック（または Enter）でその位置へジャンプする要求。ShellWindow が購読する。</summary>
    public event Action<ProblemItemViewModel>? OpenRequested;
    /// <summary>該当位置を開き、Editor所有のCode Action候補UIを表示する要求。</summary>
    public event Action<ProblemItemViewModel>? QuickFixRequested;

    [RelayCommand]
    private void Open(ProblemItemViewModel? item)
    {
        if (item is null) return;
        _navigationKey = NavigationKey(item);
        OpenRequested?.Invoke(item);
    }

    [RelayCommand]
    private void QuickFix(ProblemItemViewModel? item)
    {
        if (item is null) return;
        _navigationKey = NavigationKey(item);
        QuickFixRequested?.Invoke(item);
    }

    [RelayCommand]
    private void Next() => Move(1);

    [RelayCommand]
    private void Previous() => Move(-1);

    private void Move(int delta)
    {
        var items = Groups.SelectMany(g => g.Items).ToList();
        if (items.Count == 0) return;
        var current = _navigationKey is null ? -1 : items.FindIndex(i => NavigationKey(i) == _navigationKey);
        var next = current < 0
            ? (delta > 0 ? 0 : items.Count - 1)
            : (current + delta + items.Count) % items.Count;
        Open(items[next]);
    }

    private static string NavigationKey(ProblemItemViewModel item) =>
        $"{item.FilePath}|{item.Line1}|{item.Column1}|{item.EndLine1}|{item.EndColumn1}|{item.Severity}|{item.Code}|{item.Message}";

    /// <summary>ビルド系コマンドの出力全文からエラー/警告を抽出してツリーを丸ごと作り直す
    /// （診断行が 1 つも無ければ空＝ビルドがきれいという正しい状態）。ファイルの開閉状態はパスで引き継ぐ。
    /// <paramref name="baseDir"/> は相対パス診断の絶対化基準（MSBuild は絶対パスを出すので null で可、
    /// tsc は cwd 相対を出すので実行ディレクトリを渡す）。</summary>
    public void SetFromBuildOutput(string output, string? baseDir = null)
    {
        _buildItems = ParseBuildOutput(output, baseDir);
        Rebuild();
    }

    public void SetLspDiagnostics(string uri, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        if (!TryGetFilePath(uri, out var filePath)) return;
        var items = diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => new ProblemItemViewModel(filePath, d.Range.Start.Line + 1, d.Range.Start.Character + 1,
                d.Severity == DiagnosticSeverity.Error ? ProblemSeverity.Error : ProblemSeverity.Warning,
                string.IsNullOrWhiteSpace(d.Code) ? (string.IsNullOrWhiteSpace(d.Source) ? "LSP" : d.Source!) : d.Code!,
                d.Message, ProblemSource.Lsp, d.Range.End.Line + 1, d.Range.End.Character + 1))
            .ToList();
        if (items.Count == 0) _lspItems.Remove(filePath); else _lspItems[filePath] = items;
        Rebuild();
    }

    public void ClearLspDiagnostics()
    {
        if (_lspItems.Count == 0) return;
        _lspItems.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        var expanded = Groups.ToDictionary(g => g.FilePath, g => g.IsExpanded, System.StringComparer.OrdinalIgnoreCase);
        var items = _buildItems.Concat(_lspItems.Values.SelectMany(x => x))
            // 発生源フィルターを重複排除より先に適用する。同じ診断が Build/LSP の双方にあるとき、
            // Build を隠しただけで代表に選ばれた Build 項目と一緒に LSP 項目まで消してはならない。
            .Where(IsVisible)
            .GroupBy(i => $"{i.FilePath}|{i.Line1}|{i.Column1}|{i.EndLine1}|{i.EndColumn1}|{i.Severity}|{i.Code}|{i.Message}",
                System.StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => i.Source).First())
            .ToList();

        var groups = items
            .GroupBy(i => i.FilePath, System.StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProblemFileGroup(g.Key, ToRelativeDir(g.Key),
                g.OrderBy(i => i.Line1).ThenBy(i => i.Column1).ToList()))
            .OrderByDescending(g => g.HasErrors)
            .ThenBy(g => g.FileName, System.StringComparer.OrdinalIgnoreCase);
        var replacement = new ObservableCollection<ProblemFileGroup>();
        foreach (var g in groups)
        {
            if (expanded.TryGetValue(g.FilePath, out var e)) g.IsExpanded = e;
            replacement.Add(g);
        }
        Groups = replacement;

        HasItems = Groups.Count > 0;
        ErrorCount = Groups.Sum(g => g.ErrorCount);
        WarningCount = Groups.Sum(g => g.WarningCount);
    }

    private bool IsVisible(ProblemItemViewModel item)
    {
        if (item.Severity == ProblemSeverity.Error ? !ShowErrors : !ShowWarnings) return false;
        if (item.Source == ProblemSource.Build ? !ShowBuild : !ShowLsp) return false;
        return Scope != ProblemScope.CurrentFile ||
            (!string.IsNullOrWhiteSpace(CurrentFilePath) &&
             string.Equals(Path.GetFullPath(item.FilePath), Path.GetFullPath(CurrentFilePath),
                 System.StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetFilePath(string uri, out string filePath)
    {
        filePath = "";
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return false;
        filePath = Path.GetFullPath(parsed.LocalPath);
        return true;
    }

    /// <summary>MSBuild/tsc 診断行のパース（テスト用に分離）。同一診断の再掲（サマリ節・マルチターゲット）は除く。
    /// tsc（<c>--pretty false</c>）の <c>src/x.ts(7,5): error TS2322: msg</c> も同じ形なのでそのまま拾える。</summary>
    internal static List<ProblemItemViewModel> ParseBuildOutput(string output, string? baseDir = null)
    {
        var items = new List<ProblemItemViewModel>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var m = DiagnosticLine.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;

            var file = m.Groups["file"].Value.Trim();
            if (baseDir is not null && !Path.IsPathRooted(file))
                file = Path.GetFullPath(Path.Combine(baseDir, file));
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

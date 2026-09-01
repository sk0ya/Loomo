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
    Information,
    Hint,
}

public enum ProblemSource { Build, Compiler, Lsp, StyleCop }

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
    public string SourceLabel => Source switch
    {
        ProblemSource.Compiler => "Compiler",
        ProblemSource.Lsp => "LSP",
        ProblemSource.StyleCop => "StyleCop",
        _ => "Build",
    };

    public string FileName => Path.GetFileName(FilePath);
    public string LineColumn => $"{Line1}:{Column1}";
    /// <summary>行のツールチップ（メッセージ全文＋コード＋位置）。</summary>
    public string ToolTipText => $"{Message}\n{SourceLabel} · {Code} · {FileName}:{Line1}:{Column1}";
    public string SeverityGlyph => Severity switch
    {
        ProblemSeverity.Error => "✕",
        ProblemSeverity.Warning => "▲",
        ProblemSeverity.Information => "●",
        _ => "·",
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
        WarningCount = items.Count(i => i.Severity == ProblemSeverity.Warning);
        InformationCount = items.Count(i => i.Severity == ProblemSeverity.Information);
        HintCount = items.Count(i => i.Severity == ProblemSeverity.Hint);
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    /// <summary>ワークスペース相対のディレクトリ（表示用。ルート直下は空、マルチルート時はフォルダー名前置）。</summary>
    public string RelativeDir { get; }
    public IReadOnlyList<ProblemItemViewModel> Items { get; }
    public int ErrorCount { get; }
    public int WarningCount { get; }
    public int InformationCount { get; }
    public int HintCount { get; }
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;
    public bool HasInformation => InformationCount > 0;
    public bool HasHints => HintCount > 0;

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
    private readonly Dictionary<string, IReadOnlyList<ProblemItemViewModel>> _compilerItems =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ProblemItemViewModel>> _styleCopItems =
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
    [ObservableProperty] private bool _showInformation = true;
    [ObservableProperty] private bool _showHints = true;
    [ObservableProperty] private bool _showBuild = true;
    [ObservableProperty] private bool _showCompiler = true;
    [ObservableProperty] private bool _showLsp = true;
    [ObservableProperty] private bool _showStyleCop = true;
    [ObservableProperty] private ProblemScope _scope;
    [ObservableProperty] private string? _currentFilePath;

    partial void OnShowErrorsChanged(bool value) => Rebuild();
    partial void OnShowWarningsChanged(bool value) => Rebuild();
    partial void OnShowInformationChanged(bool value) => Rebuild();
    partial void OnShowHintsChanged(bool value) => Rebuild();
    partial void OnShowBuildChanged(bool value) => Rebuild();
    partial void OnShowCompilerChanged(bool value) => Rebuild();
    partial void OnShowLspChanged(bool value) => Rebuild();
    partial void OnShowStyleCopChanged(bool value) => Rebuild();
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
            .Select(d => new ProblemItemViewModel(filePath, d.Range.Start.Line + 1, d.Range.Start.Character + 1,
                ToProblemSeverity(d.Severity),
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

    /// <summary>Roslyn Language Serverの代わりにLoomo.CSharpが返したcompiler診断を保持する。</summary>
    public void SetCompilerDiagnostics(string filePath, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        var fullPath = Path.GetFullPath(filePath);
        var items = diagnostics
            .Where(d => d.Source?.Equals("Compiler", StringComparison.OrdinalIgnoreCase) == true)
            .Select(d => new ProblemItemViewModel(fullPath, d.Range.Start.Line + 1, d.Range.Start.Character + 1,
                ToProblemSeverity(d.Severity), d.Code ?? "Compiler", d.Message, ProblemSource.Compiler,
                d.Range.End.Line + 1, d.Range.End.Character + 1))
            .ToList();
        if (items.Count == 0) _compilerItems.Remove(fullPath); else _compilerItems[fullPath] = items;
        Rebuild();
    }

    public void ClearCompilerDiagnostics(string filePath)
    {
        if (_compilerItems.Remove(Path.GetFullPath(filePath))) Rebuild();
    }

    public void ClearAllCompilerDiagnostics()
    {
        if (_compilerItems.Count == 0) return;
        _compilerItems.Clear();
        Rebuild();
    }

    /// <summary>LSPがStyleCopを返さない環境で、Loomo.CSharpの公式Analyzerフォールバックを反映する。</summary>
    public void SetStyleCopDiagnostics(string filePath, IReadOnlyList<LspDiagnostic> diagnostics)
    {
        var fullPath = Path.GetFullPath(filePath);
        var items = diagnostics
            .Where(d => d.Code?.StartsWith("SA", StringComparison.OrdinalIgnoreCase) == true)
            .Select(d => new ProblemItemViewModel(fullPath, d.Range.Start.Line + 1, d.Range.Start.Character + 1,
                ToProblemSeverity(d.Severity), d.Code!, d.Message, ProblemSource.StyleCop,
                d.Range.End.Line + 1, d.Range.End.Character + 1))
            .ToList();
        if (items.Count == 0) _styleCopItems.Remove(fullPath); else _styleCopItems[fullPath] = items;
        Rebuild();
    }

    public void ClearStyleCopDiagnostics(string filePath)
    {
        if (_styleCopItems.Remove(Path.GetFullPath(filePath))) Rebuild();
    }

    public void ClearAllStyleCopDiagnostics()
    {
        if (_styleCopItems.Count == 0) return;
        _styleCopItems.Clear();
        Rebuild();
    }

    private void Rebuild()
    {
        var expanded = Groups.ToDictionary(g => g.FilePath, g => g.IsExpanded, System.StringComparer.OrdinalIgnoreCase);
        var items = _buildItems.Concat(_compilerItems.Values.SelectMany(x => x))
            .Concat(_lspItems.Values.SelectMany(x => x))
            .Concat(_styleCopItems.Values.SelectMany(x => x))
            // 発生源フィルターを重複排除より先に適用する。同じ診断が Build/LSP の双方にあるとき、
            // Build を隠しただけで代表に選ばれた Build 項目と一緒に LSP 項目まで消してはならない。
            .Where(IsVisible)
            // severity／message は除外する。Analyzer の設定やローカライズが一時的に異なっても、
            // 同じ ID・位置の診断を二重表示せず、SourcePriorityの正本を残す。
            // Build出力は終端rangeを持たず開始位置だけなので、終端位置はキーに含めない。
            .GroupBy(i => $"{i.FilePath}|{i.Line1}|{i.Column1}|{i.Code}",
                System.StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(SourcePriority).First())
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
        InformationCount = Groups.Sum(g => g.InformationCount);
        HintCount = Groups.Sum(g => g.HintCount);
    }

    private bool IsVisible(ProblemItemViewModel item)
    {
        if (item.Severity == ProblemSeverity.Error && !ShowErrors) return false;
        if (item.Severity == ProblemSeverity.Warning && !ShowWarnings) return false;
        if (item.Severity == ProblemSeverity.Information && !ShowInformation) return false;
        if (item.Severity == ProblemSeverity.Hint && !ShowHints) return false;
        if (item.Source == ProblemSource.Build && !ShowBuild) return false;
        if (item.Source == ProblemSource.Compiler && !ShowCompiler) return false;
        if (item.Source == ProblemSource.Lsp && !ShowLsp) return false;
        if (item.Source == ProblemSource.StyleCop && !ShowStyleCop) return false;
        return Scope != ProblemScope.CurrentFile ||
            (!string.IsNullOrWhiteSpace(CurrentFilePath) &&
             string.Equals(Path.GetFullPath(item.FilePath), Path.GetFullPath(CurrentFilePath),
                 System.StringComparison.OrdinalIgnoreCase));
    }

    private static int SourcePriority(ProblemItemViewModel item) => item.Source switch
    {
        // Build is the persisted command result and remains the canonical copy when
        // its location/message is also reported by an editor source.
        ProblemSource.Build => 0,
        ProblemSource.Lsp => 1,
        // Compiler is a fallback and must not hide a later LSP copy during a race.
        ProblemSource.Compiler => 2,
        _ => 3,
    };

    [ObservableProperty] private int _informationCount;
    [ObservableProperty] private int _hintCount;

    private static ProblemSeverity ToProblemSeverity(DiagnosticSeverity severity)
        => severity switch
        {
            DiagnosticSeverity.Error => ProblemSeverity.Error,
            DiagnosticSeverity.Warning => ProblemSeverity.Warning,
            DiagnosticSeverity.Information => ProblemSeverity.Information,
            _ => ProblemSeverity.Hint,
        };

    private static bool TryGetFilePath(string uri, out string filePath)
    {
        filePath = "";
        // Uri.LocalPath 直読みは不可（tsserver 系の "file:///c%3A/…" が "/c:/…" になる）。
        var local = LspUri.TryToLocalPath(uri);
        if (local is null) return false;
        filePath = Path.GetFullPath(local);
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
        // 表記の規則は WorkspacePaths が正本（SEARCH の結果ツリーと同じ）。以前ここは
        // 区切り文字を付けずに前方一致していたので、C:\work\app2 を C:\work\app 配下と誤認していた。
        return _workspace is null ? dir : _workspace.ToDisplayPath(dir);
    }
}

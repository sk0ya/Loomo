using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Editor.Controls.HostIntegration;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「問題」一覧の1件。開いているエディタタブの <c>EffectiveDiagnostics</c>（LSPの
/// publishDiagnostics＋ホスト診断を合成したもの）をそのまま表示用に写したもの。</summary>
public sealed class ProblemItemViewModel
{
    public ProblemItemViewModel(string filePath, EditorDiagnostic diagnostic)
    {
        FilePath = filePath;
        Diagnostic = diagnostic;
    }

    public string FilePath { get; }
    public EditorDiagnostic Diagnostic { get; }

    public string FileName => System.IO.Path.GetFileName(FilePath);
    /// <summary>1始まりの表示用の行/列（Diagnostic.Range は0始まり）。</summary>
    public int Line1 => Diagnostic.Range.Start.Line + 1;
    public int Column1 => Diagnostic.Range.Start.Column + 1;
    public string Message => Diagnostic.Message;
    public EditorDiagnosticSeverity Severity => Diagnostic.Severity;
    public string? Source => Diagnostic.Source;
    public string Location => $"{FileName}:{Line1}:{Column1}";
    public string SeverityGlyph => Severity switch
    {
        EditorDiagnosticSeverity.Error => "✕",
        EditorDiagnosticSeverity.Warning => "▲",
        EditorDiagnosticSeverity.Information => "ⓘ",
        _ => "·",
    };
}

/// <summary>IDE（デバッグ）ペインの「問題」タブ。開いている全エディタタブの診断を集約して一覧表示する。
/// デバッグセッションには依存しない（全セッション共有のサブ VM）。集約自体（LSPマネージャの購読・
/// EffectiveDiagnosticsの読み出し）は <c>ShellWindow.Problems.cs</c>（View層。VimEditorControl/
/// Editor.Controls.Lsp に触れるのはそちら）が行い、ここは表示用データだけを持つ。</summary>
public sealed partial class ProblemsViewModel : ObservableObject
{
    /// <summary>全診断のフラット一覧（ファイル名→行の順）。</summary>
    public ObservableCollection<ProblemItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

    /// <summary>行クリック（またはダブルクリック）でその位置へジャンプする要求。ShellWindow が購読する。</summary>
    public event Action<ProblemItemViewModel>? OpenRequested;

    [RelayCommand]
    private void Open(ProblemItemViewModel? item)
    {
        if (item is not null) OpenRequested?.Invoke(item);
    }

    /// <summary>集約結果を丸ごと差し替える（ShellWindowが診断変化のたびに呼ぶ）。</summary>
    internal void ReplaceItems(IReadOnlyList<ProblemItemViewModel> items)
    {
        var ordered = items
            .OrderBy(i => i.FileName, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Line1)
            .ToList();

        Items.Clear();
        foreach (var i in ordered) Items.Add(i);

        HasItems = Items.Count > 0;
        ErrorCount = Items.Count(i => i.Severity == EditorDiagnosticSeverity.Error);
        WarningCount = Items.Count(i => i.Severity == EditorDiagnosticSeverity.Warning);
    }
}

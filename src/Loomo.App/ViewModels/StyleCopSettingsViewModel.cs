using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 設定オーバーレイのStyleCopセクション。設定の解決と書き込みはLoomo.CSharpへ委譲し、
/// ここはプロジェクト一覧と明示操作の入力状態だけを保持する。
/// </summary>
public sealed partial class StyleCopSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISolutionModelService? _solution;
    private readonly StyleCopConfigurationService _configuration;
    private readonly StyleCopSeverityService _severity;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ObservableCollection<StyleCopProjectRowViewModel> Projects { get; } = new();
    public IReadOnlyList<string> SeverityOptions { get; } =
        ["none", "silent", "suggestion", "warning", "error"];

    [ObservableProperty] private string? _selectedProjectPath;
    [ObservableProperty] private string _ruleId = "SA1101";
    [ObservableProperty] private string _selectedSeverity = "warning";
    [ObservableProperty] private string _status = "";

    public StyleCopProjectRowViewModel? SelectedProject
        => Projects.FirstOrDefault(row => string.Equals(
            row.FullPath, SelectedProjectPath, StringComparison.OrdinalIgnoreCase));

    public string TargetEditorConfigPath => SelectedProject?.EditorConfigPath
        ?? "プロジェクトを選択してください";

    public StyleCopSettingsViewModel(
        ISolutionModelService? solution = null,
        StyleCopConfigurationService? configuration = null,
        StyleCopSeverityService? severity = null)
    {
        _solution = solution;
        _configuration = configuration ?? new StyleCopConfigurationService();
        _severity = severity ?? new StyleCopSeverityService();
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (_solution is not null)
        {
            _solution.Changed += OnSolutionChanged;
            Refresh();
        }
    }

    partial void OnSelectedProjectPathChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(TargetEditorConfigPath));
    }

    public void Refresh()
    {
        if (_disposed) return;
        Projects.Clear();
        foreach (var project in _solution?.Current.Projects ?? [])
            Projects.Add(new StyleCopProjectRowViewModel(project, _configuration.Resolve(project)));

        if (SelectedProject is null)
            SelectedProjectPath = Projects.FirstOrDefault()?.FullPath;
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(TargetEditorConfigPath));
    }

    private void OnSolutionChanged(object? sender, SolutionModel model)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) Refresh();
        else _dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.DataBind);
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        Refresh();
        Status = "StyleCop設定を更新しました。";
    }

    [RelayCommand]
    private void ApplySeverity()
    {
        var project = SelectedProject?.Project;
        if (project is null)
        {
            Status = "対象プロジェクトを選択してください。";
            return;
        }

        var result = _severity.SetSeverity(project, RuleId, SelectedSeverity);
        if (!result.Succeeded)
        {
            Status = result.Error ?? "StyleCop severityを変更できませんでした。";
            return;
        }

        Refresh();
        Status = $"{result.FilePath} の {RuleId.Trim().ToUpperInvariant()} を {SelectedSeverity} に変更しました。";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_solution is not null) _solution.Changed -= OnSolutionChanged;
    }
}

/// <summary>StyleCop設定画面に表示するプロジェクト1行分の表示アダプター。</summary>
public sealed class StyleCopProjectRowViewModel
{
    public ProjectModel Project { get; }
    public StyleCopConfiguration Configuration { get; }
    public string FullPath => Project.FullPath;
    public string ProjectName => Project.Name;
    public string StateText => Project.State switch
    {
        ProjectLoadState.Ready => "ready",
        ProjectLoadState.Loading => "解析中",
        ProjectLoadState.Failed => "失敗",
        ProjectLoadState.NotInProject => "対象外",
        _ => "未構成",
    };
    public string AnalyzerText => Configuration.State == StyleCopConfigurationState.AnalyzerNotLoaded
        ? "Analyzer未読込"
        : Configuration.AnalyzerPaths.Count == 0
            ? "Analyzerなし"
        : $"Analyzer {Configuration.AnalyzerPaths.Count}件";
    public string ConfigurationText => Configuration.ConfigurationFiles.Count == 0
        ? "stylecop.jsonなし"
        : string.Join(", ", Configuration.ConfigurationFiles.Select(Path.GetFileName));
    public string RulesetText => Configuration.RulesetFiles.Count == 0
        ? "rulesetなし"
        : string.Join(", ", Configuration.RulesetFiles.Select(Path.GetFileName));
    public string SeverityText => Configuration.RuleSettings.Count == 0
        ? "severity設定なし"
        : string.Join(" / ", Configuration.RuleSettings.Select(rule =>
            $"{rule.RuleId}={rule.Severity}"));
    public string EditorConfigPath => Path.Combine(Project.Directory, ".editorconfig");
    public string ErrorText => Configuration.Error ?? "";

    public StyleCopProjectRowViewModel(ProjectModel project, StyleCopConfiguration configuration)
    {
        Project = project;
        Configuration = configuration;
    }
}

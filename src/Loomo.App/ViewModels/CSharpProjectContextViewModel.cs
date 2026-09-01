using System;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>アクティブなC#ファイルと評価済みプロジェクトモデルを結ぶ表示用アダプター。
/// プロジェクトの発見・MSBuild評価は <c>Loomo.CSharp</c> 側に置き、ここはUIスレッドへの反映だけを担う。</summary>
public sealed partial class CSharpProjectContextViewModel : ObservableObject, IDisposable
{
    private readonly ISolutionModelService? _solution;
    private readonly CSharpEditorConfigService? _editorConfigService;
    private readonly StyleCopConfigurationService? _styleCopService;
    private readonly Dispatcher _dispatcher;
    private string? _filePath;
    private bool _disposed;
    private bool _suppressTargetFrameworkSelection;
    private bool _suppressConfigurationSelection;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusToolTip = "";
    [ObservableProperty] private ProjectLoadState _state = ProjectLoadState.NotConfigured;
    [ObservableProperty] private ProjectModel? _project;
    [ObservableProperty] private CSharpEditorConfig? _editorConfig;
    [ObservableProperty] private StyleCopConfiguration? _styleCop;
    [ObservableProperty] private string? _selectedTargetFramework;
    [ObservableProperty] private string? _selectedConfiguration;

    public string EditorConfigSummary => EditorConfig?.SourceFiles.Count > 0
        ? $".editorconfig ×{EditorConfig.SourceFiles.Count}"
        : ".editorconfigなし";

    public IReadOnlyList<string> TargetFrameworkOptions
        => Project?.TargetFrameworks.Select(t => t.Name).ToList() ?? [];

    public bool HasMultipleTargetFrameworks => TargetFrameworkOptions.Count > 1;

    public IReadOnlyList<string> ConfigurationOptions
        => _solution?.Current.ConfigurationOptions ?? ["Debug", "Release"];

    public bool HasMultipleConfigurations => Project is not null && ConfigurationOptions.Count > 1;

    public string ProjectStructureSummary => Project is { } project
        ? $"参照 {project.ProjectReferences.Count} · Analyzer {(project.SelectedTargetFrameworkModel?.Analyzers.Count ?? 0)}"
        : "";

    public string StyleCopSummary => StyleCop?.StatusText ?? "StyleCop 未導入";
    public string StyleCopToolTip => StyleCop switch
    {
        { State: StyleCopConfigurationState.InvalidConfiguration, Error: { } error } =>
            $"StyleCop設定が不正です。{Environment.NewLine}{error}",
        { State: StyleCopConfigurationState.AnalyzerNotLoaded } config =>
            $"StyleCopはプロジェクト設定にありますが、Analyzer DLLを解決できません。{Environment.NewLine}" +
            $"評価済みパス: {config.AnalyzerPaths.Count}",
        { IsInstalled: true } config =>
            $"Analyzer: {config.AnalyzerPaths.Count}{Environment.NewLine}" +
            $"設定: {config.ConfigurationFiles.Count}{Environment.NewLine}" +
            $"Ruleset: {config.RulesetFiles.Count}{Environment.NewLine}" +
            $"severity設定: {config.RuleSettings.Count}",
        _ => "StyleCop.Analyzers がプロジェクトへ導入されていません。",
    };

    public CSharpProjectContextViewModel(
        ISolutionModelService? solution = null,
        CSharpEditorConfigService? editorConfigService = null,
        StyleCopConfigurationService? styleCopService = null)
    {
        _solution = solution;
        _editorConfigService = editorConfigService;
        _styleCopService = styleCopService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (_solution is not null)
        {
            _solution.Changed += OnSolutionChanged;
            Apply(_solution.Current);
        }
    }

    partial void OnSelectedTargetFrameworkChanged(string? value)
    {
        if (_suppressTargetFrameworkSelection || _disposed || _solution is null ||
            Project is null || string.IsNullOrWhiteSpace(value) ||
            string.Equals(Project.SelectedTargetFramework, value, StringComparison.OrdinalIgnoreCase)) return;
        _ = SelectTargetFrameworkAsync(Project.FullPath, value);
    }

    partial void OnSelectedConfigurationChanged(string? value)
    {
        if (_suppressConfigurationSelection || _disposed || _solution is null ||
            Project is null || string.IsNullOrWhiteSpace(value) ||
            string.Equals(_solution.Current.EffectiveConfiguration, value, StringComparison.OrdinalIgnoreCase)) return;
        _ = SelectConfigurationAsync(value);
    }

    private async Task SelectTargetFrameworkAsync(string projectPath, string targetFramework)
    {
        try
        {
            if (!await _solution!.SelectTargetFrameworkAsync(projectPath, targetFramework))
                Apply(_solution.Current);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // 選択に失敗した場合はサービスの正本へ戻す。UIだけが未適用TFMを表示し続けない。
            Apply(_solution!.Current);
        }
    }

    private async Task SelectConfigurationAsync(string configuration)
    {
        try
        {
            if (!await _solution!.SelectConfigurationAsync(configuration))
                Apply(_solution.Current);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // 選択に失敗した場合はサービスの正本へ戻す。UIだけが未適用構成を表示し続けない。
            Apply(_solution!.Current);
        }
    }

    /// <summary>アクティブエディタのファイルを更新する。C#以外のファイルでは表示を隠す。</summary>
    public void SetCurrentFile(string? filePath)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => SetCurrentFile(filePath)), DispatcherPriority.DataBind);
            return;
        }

        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        Apply(_solution?.Current);
    }

    private void OnSolutionChanged(object? sender, SolutionModel model)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) Apply(model);
        else _dispatcher.BeginInvoke(new Action(() => Apply(model)), DispatcherPriority.DataBind);
    }

    private void Apply(SolutionModel? model)
    {
        var isCSharp = _filePath is not null &&
            (string.Equals(Path.GetExtension(_filePath), ".cs", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Path.GetExtension(_filePath), ".csx", StringComparison.OrdinalIgnoreCase));
        if (!isCSharp || model is null)
        {
            Project = null;
            _suppressTargetFrameworkSelection = true;
            SelectedTargetFramework = null;
            _suppressTargetFrameworkSelection = false;
            _suppressConfigurationSelection = true;
            SelectedConfiguration = null;
            _suppressConfigurationSelection = false;
            EditorConfig = null;
            StyleCop = null;
            OnPropertyChanged(nameof(EditorConfigSummary));
            OnPropertyChanged(nameof(TargetFrameworkOptions));
            OnPropertyChanged(nameof(HasMultipleTargetFrameworks));
            OnPropertyChanged(nameof(ConfigurationOptions));
            OnPropertyChanged(nameof(HasMultipleConfigurations));
            OnPropertyChanged(nameof(ProjectStructureSummary));
            OnPropertyChanged(nameof(StyleCopSummary));
            OnPropertyChanged(nameof(StyleCopToolTip));
            State = ProjectLoadState.NotConfigured;
            IsVisible = false;
            StatusText = "";
            StatusToolTip = "";
            return;
        }

        var state = model.ResolveFileState(_filePath!);
        State = state;
        // 別TFMのファイルは意味解析対象外のまま、TFM選択UIだけは表示して切替可能にする。
        Project = state == ProjectLoadState.NotInSelectedTargetFramework
            ? model.ProjectForFileInAnyTargetFramework(_filePath!)
            : model.ProjectForFile(_filePath!);
        _suppressTargetFrameworkSelection = true;
        SelectedTargetFramework = Project?.SelectedTargetFramework;
        _suppressTargetFrameworkSelection = false;
        _suppressConfigurationSelection = true;
        SelectedConfiguration = model.EffectiveConfiguration;
        _suppressConfigurationSelection = false;
        EditorConfig = _editorConfigService?.Resolve(_filePath!);
        StyleCop = _styleCopService?.Resolve(Project);
        OnPropertyChanged(nameof(EditorConfigSummary));
        OnPropertyChanged(nameof(TargetFrameworkOptions));
        OnPropertyChanged(nameof(HasMultipleTargetFrameworks));
        OnPropertyChanged(nameof(ConfigurationOptions));
        OnPropertyChanged(nameof(HasMultipleConfigurations));
        OnPropertyChanged(nameof(ProjectStructureSummary));
        OnPropertyChanged(nameof(StyleCopSummary));
        OnPropertyChanged(nameof(StyleCopToolTip));
        var tfm = Project?.SelectedTargetFrameworkModel?.Name;

        (StatusText, StatusToolTip) = state switch
        {
            ProjectLoadState.Loading =>
                ("C#プロジェクトを解析中…", "プロジェクトとMSBuild評価結果を読み込んでいます。"),
            ProjectLoadState.Failed =>
                ("C#プロジェクト解析に失敗", BuildToolTip(model.Error ?? "MSBuild評価に失敗しました。")),
            ProjectLoadState.NotConfigured =>
                ("C#プロジェクト未設定", "このワークスペースには .sln / .csproj がありません。"),
            ProjectLoadState.NotInProject =>
                ("プロジェクト外 · 基本C#機能", "このファイルは評価済みプロジェクトのCompile項目に含まれていません。"),
            ProjectLoadState.NotInSelectedTargetFramework =>
                ("別TFMのCompile対象 · 基本C#機能",
                 "このファイルはプロジェクトには含まれていますが、現在選択中のTargetFrameworkのCompile項目ではありません。"),
            _ when Project is not null =>
                ($"{Project.Name} · {tfm ?? "TFM不明"}",
                 $"{Project.FullPath}\nTargetFramework: {tfm ?? "(不明)"}\n" +
                 $"ProjectReference: {Project.ProjectReferences.Count}\n" +
                 $"Analyzer: {string.Join(", ", Project.SelectedTargetFrameworkModel?.Analyzers.Select(a => a.Include) ?? [])}\n" +
                 $"Configuration: {ConfigurationDescription(model)}\n" +
                 $"{EditorConfigDescription()}"),
            _ => ("C#プロジェクトを特定できません", "ファイルの所属プロジェクトを特定できません。"),
        };
        IsVisible = true;
    }

    private string BuildToolTip(string message)
        => $"{message}\n{EditorConfigDescription()}";

    private string ConfigurationDescription(SolutionModel model)
        => string.Equals(model.EffectiveConfiguration, Project?.Configuration,
                StringComparison.OrdinalIgnoreCase)
            ? model.EffectiveConfiguration
            : $"{model.EffectiveConfiguration} → {Project?.Configuration ?? model.EffectiveConfiguration}";

    private string EditorConfigDescription()
        => EditorConfig?.SourceFiles.Count > 0
            ? $"EditorConfig: {string.Join("; ", EditorConfig.SourceFiles)}"
            : "EditorConfig: (なし)";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_solution is not null) _solution.Changed -= OnSolutionChanged;
    }
}

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

public enum CSharpSolutionAction
{
    Build,
    Test,
    Run,
    Debug,
    DebugTests,
    FixAllProject,
    FixAllSolution,
}

public sealed record CSharpSolutionActionEventArgs(
    CSharpSolutionNodeViewModel Node,
    CSharpSolutionAction Action);

/// <summary>ファイル一覧とは別のC# Solution Explorer表示用VM。
/// 階層の構築は <c>Loomo.CSharp</c>、ここはUIスレッドとファイル開要求だけを担う。</summary>
public sealed partial class CSharpSolutionExplorerViewModel : ObservableObject, IDisposable
{
    private readonly ISolutionModelService _solution;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private bool _suppressConfigurationSelection;

    public ObservableCollection<CSharpSolutionNodeViewModel> Nodes { get; } = [];

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _executionStatusText = "";
    [ObservableProperty] private CSharpSolutionNodeViewModel? _selectedNode;
    [ObservableProperty] private string? _selectedConfiguration;

    public IReadOnlyList<string> ConfigurationOptions => _solution.Current.ConfigurationOptions;
    public bool HasMultipleConfigurations => IsVisible && ConfigurationOptions.Count > 1;

    public event EventHandler<string>? FileOpenRequested;
    public event EventHandler<CSharpSolutionActionEventArgs>? ActionRequested;

    public CSharpSolutionExplorerViewModel(ISolutionModelService solution)
    {
        _solution = solution;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _solution.Changed += OnSolutionChanged;
        Apply(_solution.Current);
    }

    public void Open(CSharpSolutionNodeViewModel? node)
    {
        if (node?.Kind != CSharpSolutionNodeKind.File || string.IsNullOrWhiteSpace(node.FullPath)) return;
        FileOpenRequested?.Invoke(this, node.FullPath);
    }

    public void RequestAction(CSharpSolutionNodeViewModel? node, CSharpSolutionAction action)
    {
        if (node is null || node.Kind is not (CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project) ||
            string.IsNullOrWhiteSpace(node.FullPath)) return;
        if (action is CSharpSolutionAction.Test or CSharpSolutionAction.DebugTests && !node.CanRunTests) return;
        if (action is CSharpSolutionAction.Run or CSharpSolutionAction.Debug
            && node.Kind != CSharpSolutionNodeKind.Project) return;
        if (action == CSharpSolutionAction.FixAllProject &&
            node.Kind != CSharpSolutionNodeKind.Project) return;
        if (action == CSharpSolutionAction.FixAllSolution &&
            node.Kind != CSharpSolutionNodeKind.Solution) return;
        ActionRequested?.Invoke(this, new CSharpSolutionActionEventArgs(node, action));
    }

    partial void OnSelectedConfigurationChanged(string? value)
    {
        if (_suppressConfigurationSelection || _disposed || string.IsNullOrWhiteSpace(value) ||
            string.Equals(_solution.Current.EffectiveConfiguration, value, StringComparison.OrdinalIgnoreCase)) return;
        _ = SelectConfigurationAsync(value);
    }

    private async Task SelectConfigurationAsync(string configuration)
    {
        try
        {
            if (!await _solution.SelectConfigurationAsync(configuration))
                Apply(_solution.Current);
        }
        catch (OperationCanceledException) { }
        catch { Apply(_solution.Current); }
    }

    private void OnSolutionChanged(object? sender, SolutionModel model)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) Apply(model);
        else _dispatcher.BeginInvoke(new Action(() => Apply(model)), DispatcherPriority.DataBind);
    }

    private void Apply(SolutionModel model)
    {
        if (_disposed) return;
        Nodes.Clear();
        SelectedNode = null;
        var hasProjects = model.Projects.Count > 0;
        IsVisible = hasProjects || model.State is ProjectLoadState.Loading or ProjectLoadState.Failed;
        StatusText = model.State switch
        {
            ProjectLoadState.Loading => "C#プロジェクトを解析中…",
            ProjectLoadState.Failed => model.Error is { Length: > 0 }
                ? $"C#解析失敗: {model.Error}" : "C#プロジェクト解析に失敗",
            ProjectLoadState.NotConfigured => "C#プロジェクトなし",
            _ => "",
        };
        _suppressConfigurationSelection = true;
        SelectedConfiguration = model.EffectiveConfiguration;
        _suppressConfigurationSelection = false;
        OnPropertyChanged(nameof(ConfigurationOptions));
        OnPropertyChanged(nameof(HasMultipleConfigurations));
        if (hasProjects)
            Nodes.Add(CSharpSolutionNodeViewModel.From(CSharpSolutionTreeBuilder.Build(model)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _solution.Changed -= OnSolutionChanged;
    }
}

public sealed class CSharpSolutionNodeViewModel
{
    public string Name { get; }
    public CSharpSolutionNodeKind Kind { get; }
    public string? FullPath { get; }
    public bool IsSelected { get; }
    public bool CanRunTests { get; }
    public bool IsExpanded { get; set; } = true;
    public string Glyph => Kind switch
    {
        CSharpSolutionNodeKind.Solution => "◈",
        CSharpSolutionNodeKind.Project => "▣",
        CSharpSolutionNodeKind.TargetFramework => IsSelected ? "●" : "○",
        CSharpSolutionNodeKind.ProjectReference => "↗",
        CSharpSolutionNodeKind.Analyzer => "◆",
        CSharpSolutionNodeKind.AdditionalFile => "≡",
        CSharpSolutionNodeKind.Folder => "▸",
        _ => "·",
    };
    public ObservableCollection<CSharpSolutionNodeViewModel> Children { get; } = [];

    private CSharpSolutionNodeViewModel(CSharpSolutionNode node)
    {
        Name = node.Name;
        Kind = node.Kind;
        FullPath = node.FullPath;
        IsSelected = node.IsSelected;
        CanRunTests = node.CanRunTests;
        foreach (var child in node.Children) Children.Add(From(child));
    }

    public static CSharpSolutionNodeViewModel From(CSharpSolutionNode node) => new(node);
}

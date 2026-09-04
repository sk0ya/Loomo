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
        // 開閉は利用者の状態なので、作り直しても引き継ぐ。ノード VM ごと捨てているため、
        // 引き継がないと構成切替や .csproj の保存のたびにツリーが畳まれて手元が飛ぶ。
        var expanded = CollectExpanded(Nodes);
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
        if (!hasProjects) return;
        var root = CSharpSolutionNodeViewModel.From(CSharpSolutionTreeBuilder.Build(model));
        if (expanded.Count > 0) RestoreExpanded(new[] { root }, expanded);
        Nodes.Add(root);
    }

    /// <summary>開いているノードの識別子（パス、無ければ種類＋名前）を集める。</summary>
    private static HashSet<string> CollectExpanded(IEnumerable<CSharpSolutionNodeViewModel> nodes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(nodes);
        return result;

        void Walk(IEnumerable<CSharpSolutionNodeViewModel> current)
        {
            foreach (var node in current)
            {
                if (node.IsExpanded) result.Add(ExpansionKey(node));
                Walk(node.Children);
            }
        }
    }

    private static void RestoreExpanded(
        IEnumerable<CSharpSolutionNodeViewModel> nodes, HashSet<string> expanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = expanded.Contains(ExpansionKey(node));
            RestoreExpanded(node.Children, expanded);
        }
    }

    private static string ExpansionKey(CSharpSolutionNodeViewModel node) =>
        node.FullPath is { Length: > 0 } path ? path : $"{node.Kind}:{node.Name}";

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
    /// <summary>
    /// 初期状態で開いておくか。<b>ソリューションとプロジェクトだけ</b>を開き、フォルダー以下は畳む。
    /// 全段を開いた状態で作ると、ソリューション全ファイルぶん（この repo で13,684ノード）の
    /// TreeViewItem が実体化され、WPF のバインディングと視覚要素だけで gen2 が 900MB に達する。
    /// その大きさになるとブロッキング GC が10秒級になり、ドロップダウンを開いた程度の
    /// アロケーションで UI が固まる（実測19.5秒／Windows が AppHang でアプリを落とす）。
    /// </summary>
    public bool IsExpanded { get; set; }
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
        IsExpanded = node.Kind is CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project;
        foreach (var child in node.Children) Children.Add(From(child));
    }

    public static CSharpSolutionNodeViewModel From(CSharpSolutionNode node) => new(node);
}

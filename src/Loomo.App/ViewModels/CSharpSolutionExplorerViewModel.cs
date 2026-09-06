using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// <summary>絞り込み前の完全なツリー。絞り込みは表示側でこれを刈り込んで作る。</summary>
    private CSharpSolutionNode? _fullTree;
    /// <summary>利用者が開いた状態の正本。絞り込み中は結果を全開にするので、
    /// そのときの開閉をここへ書き戻すと解除後に全部開いたままになる（＝書き戻さない）。</summary>
    private HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<CSharpSolutionNodeViewModel> Nodes { get; } = [];

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _executionStatusText = "";
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _filterStatus = "";
    [ObservableProperty] private CSharpSolutionNodeViewModel? _selectedNode;
    [ObservableProperty] private string? _selectedConfiguration;

    public IReadOnlyList<string> ConfigurationOptions => _solution.Current.ConfigurationOptions;
    public bool HasMultipleConfigurations => IsVisible && ConfigurationOptions.Count > 1;

    /// <summary>見出しのビルド／テストが実際に叩く対象。選択がファイルやフォルダーでも、
    /// それを含むプロジェクト（無ければソリューション）まで遡って解決する。
    /// 「選んだ行では実行できない」ではなく「選んだ行の持ち主を実行する」を既定にする。</summary>
    public CSharpSolutionNodeViewModel? ActionTarget
    {
        get
        {
            for (var node = SelectedNode; node is not null; node = node.Parent)
                if (node.Kind is CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project)
                    return node;
            return Nodes.FirstOrDefault();
        }
    }

    public string ActionTargetLabel => ActionTarget?.Name ?? "";
    public string? ActionTargetPath => ActionTarget?.FullPath;
    public bool CanTestTarget => ActionTarget?.CanRunTests == true;
    public bool IsFiltering => FilterText.Trim().Length > 0;

    public event EventHandler<string>? FileOpenRequested;
    public event EventHandler<CSharpSolutionActionEventArgs>? ActionRequested;

    public CSharpSolutionExplorerViewModel(ISolutionModelService solution)
    {
        _solution = solution;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _solution.Changed += OnSolutionChanged;
        // テーマの明暗でアイコンの配色が入れ替わる。ツリーはそのとき作り直さないので引き直させる。
        FileIcons.PaletteChanged += OnIconPaletteChanged;
        Apply(_solution.Current);
    }

    private void OnIconPaletteChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) RefreshIcons(Nodes);
        else _dispatcher.BeginInvoke(new Action(() => RefreshIcons(Nodes)), DispatcherPriority.DataBind);
    }

    private static void RefreshIcons(IEnumerable<CSharpSolutionNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.RefreshIcon();
            RefreshIcons(node.Children);
        }
    }

    /// <summary>ファイルとして開ける行か（ファイル・追加ファイル・その他・参照先csproj・csproj・sln）。
    /// グループ見出しやフォルダーには <c>FullPath</c> が無いか、開いても意味がない。</summary>
    public static bool CanOpen(CSharpSolutionNodeViewModel? node)
        => node is { FullPath.Length: > 0 } &&
           node.Kind is not (CSharpSolutionNodeKind.Folder or CSharpSolutionNodeKind.TargetFramework) &&
           node.Children.Count == 0;

    public void Open(CSharpSolutionNodeViewModel? node)
    {
        if (!CanOpen(node)) return;
        FileOpenRequested?.Invoke(this, node!.FullPath!);
    }

    /// <summary>パス直指定で開く。右クリックの「プロジェクトファイルを開く」など、
    /// ダブルクリックでは開けない行（開閉が優先されるsln／csproj）のための入口。</summary>
    public void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        FileOpenRequested?.Invoke(this, path);
    }

    public void RequestAction(CSharpSolutionNodeViewModel? node, CSharpSolutionAction action)
    {
        // 対象はソリューション／プロジェクト。ファイルやフォルダーを渡されたら持ち主まで遡る
        // （見出しのボタンも右クリックも、ここ一本で同じ解決をする）。
        for (; node is not null; node = node.Parent)
            if (node.Kind is CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project) break;
        if (node is null || string.IsNullOrWhiteSpace(node.FullPath)) return;
        if (action is CSharpSolutionAction.Test or CSharpSolutionAction.DebugTests && !node.CanRunTests) return;
        if (action is CSharpSolutionAction.Run or CSharpSolutionAction.Debug
            && node.Kind != CSharpSolutionNodeKind.Project) return;
        if (action == CSharpSolutionAction.FixAllProject &&
            node.Kind != CSharpSolutionNodeKind.Project) return;
        if (action == CSharpSolutionAction.FixAllSolution &&
            node.Kind != CSharpSolutionNodeKind.Solution) return;
        ActionRequested?.Invoke(this, new CSharpSolutionActionEventArgs(node, action));
    }

    /// <summary>見出しのビルド／テスト。対象は <see cref="ActionTarget"/>。</summary>
    public void RequestTargetAction(CSharpSolutionAction action) => RequestAction(ActionTarget, action);

    /// <summary>ソリューションとプロジェクトの段だけ残して畳む（＝初期状態へ戻す）。
    /// 深く潜ったあと元の見通しへ戻す動作が無いと、狭い左列では迷子になる。</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        _expanded.Clear();
        // 絞り込みを解除するときは FilterText の setter が Rebuild するので、ここでは呼ばない。
        // 呼ぶと1万ノード級のVMツリーを1回の折りたたみで2度作り直すことになる。
        if (FilterText.Length > 0) FilterText = "";
        else Rebuild();
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = "";

    partial void OnSelectedNodeChanged(CSharpSolutionNodeViewModel? value) => NotifyActionTargetChanged();

    /// <summary><see cref="ActionTarget"/> から算出する見出しの表示を作り直す。選択が変わったときだけでなく
    /// <see cref="Rebuild"/> の後にも要る——無選択のとき対象は <see cref="Nodes"/> の先頭に落ちるので、
    /// 木が入れ替われば選択が動かなくても対象は変わっているため。</summary>
    private void NotifyActionTargetChanged()
    {
        OnPropertyChanged(nameof(ActionTarget));
        OnPropertyChanged(nameof(ActionTargetLabel));
        OnPropertyChanged(nameof(ActionTargetPath));
        OnPropertyChanged(nameof(CanTestTarget));
    }

    partial void OnFilterTextChanged(string? oldValue, string newValue)
    {
        // 絞り込みへ入る直前の開閉を控えておく（結果は全開にするため、ここでしか拾えない）。
        if (string.IsNullOrWhiteSpace(oldValue) && !string.IsNullOrWhiteSpace(newValue))
            _expanded = CollectExpanded(Nodes);
        OnPropertyChanged(nameof(IsFiltering));
        Rebuild();
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
        if (!IsFiltering) _expanded = CollectExpanded(Nodes);
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
        _fullTree = hasProjects ? CSharpSolutionTreeBuilder.Build(model) : null;
        Rebuild();
    }

    /// <summary>絞り込みと開閉状態を反映して <see cref="Nodes"/> を作り直す。</summary>
    private void Rebuild()
    {
        RebuildNodes();
        // 木を差し替えた後に通知する。RebuildNodes の中の SelectedNode=null は Nodes が空の時点で
        // 起きるうえ、元から無選択なら等値判定で短絡して通知そのものが上がらない。
        NotifyActionTargetChanged();
    }

    private void RebuildNodes()
    {
        Nodes.Clear();
        SelectedNode = null;
        if (_fullTree is null)
        {
            FilterStatus = "";
            return;
        }

        var query = FilterText.Trim();
        if (query.Length == 0)
        {
            FilterStatus = "";
            var root = CSharpSolutionNodeViewModel.From(_fullTree);
            if (_expanded.Count > 0) RestoreExpanded([root], _expanded);
            Nodes.Add(root);
            return;
        }

        var result = CSharpSolutionTreeFilter.Apply(_fullTree, query);
        FilterStatus = result.Matched == 0
            ? "一致なし"
            : result.Truncated
                ? $"{result.Matched}件（上限）"
                : $"{result.Matched}件";
        if (result.Root is null) return;
        var filtered = CSharpSolutionNodeViewModel.From(result.Root);
        ExpandAll(filtered);
        Nodes.Add(filtered);
    }

    private static void ExpandAll(CSharpSolutionNodeViewModel node)
    {
        node.IsExpanded = true;
        foreach (var child in node.Children) ExpandAll(child);
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
        FileIcons.PaletteChanged -= OnIconPaletteChanged;
    }
}

public sealed partial class CSharpSolutionNodeViewModel : ObservableObject
{
    public string Name { get; }
    public CSharpSolutionNodeKind Kind { get; }
    public string? FullPath { get; }
    /// <summary>名前の後ろに淡色で添える補助情報（単一TFM名・テスト印・件数）。</summary>
    public string? Detail { get; }
    public bool HasDetail => Detail is { Length: > 0 };
    public bool IsSelected { get; }
    public bool CanRunTests { get; }
    /// <summary>親ノード。ファイルを選んだままビルドしたときに持ち主のプロジェクトへ
    /// 遡るために持つ（ツリーは丸ごと作り直すので、循環参照は残らない）。</summary>
    public CSharpSolutionNodeViewModel? Parent { get; private set; }
    /// <summary>
    /// 初期状態で開いておくか。<b>ソリューションとプロジェクトだけ</b>を開き、フォルダー以下は畳む。
    /// 全段を開いた状態で作ると、ソリューション全ファイルぶん（この repo で13,684ノード）の
    /// TreeViewItem が実体化され、WPF のバインディングと視覚要素だけで gen2 が 900MB に達する。
    /// その大きさになるとブロッキング GC が10秒級になり、ドロップダウンを開いた程度の
    /// アロケーションで UI が固まる（実測19.5秒／Windows が AppHang でアプリを落とす）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconImage))]
    private bool _isExpanded;

    /// <summary>行頭のアイコン。フォルダーツリー（FolderTreeView）と同じ <see cref="FileIcons"/> の
    /// 線画を使う——記号だけでは sln／csproj／.cs／.json の区別が付かず、行が何なのか読めない。
    /// ファイルは拡張子（sln・csproj も専用の絵を持つ）、フォルダーとグループ見出し（参照・
    /// アナライザー・追加ファイル）は開閉に追随するフォルダーの絵。TFM の段だけは対応する
    /// ファイルが無いので記号（<see cref="Glyph"/>）に任せる。
    /// アイコンの実体は種別ごとの共有インスタンスなので、都度引いても配列参照ぶんしかかからない。</summary>
    public ImageSource? IconImage => Kind switch
    {
        CSharpSolutionNodeKind.TargetFramework => null,
        CSharpSolutionNodeKind.Folder => FileIcons.FolderImage(IsExpanded),
        _ when FullPath is not { Length: > 0 } => FileIcons.FolderImage(IsExpanded),
        _ => FileIcons.ImageFor(FileIcons.IndexFor(FullPath!, isDirectory: false)),
    };

    /// <summary>アイコンの有無。<see cref="IconImage"/> から直に引く——種類の条件を書き写すと、
    /// 種類が増えたときに「絵は無いのに16px空けるだけの列」が残る。</summary>
    public bool HasIcon => IconImage is not null;

    /// <summary>アイコンで表せない行の記号。TFM の段だけ（選択中＝●／それ以外＝○）。</summary>
    public string Glyph => Kind == CSharpSolutionNodeKind.TargetFramework
        ? IsSelected ? "●" : "○"
        : "";
    public bool HasGlyph => Glyph.Length > 0;

    /// <summary>テーマの明暗が変わってアイコンの配色が入れ替わったとき、引き直させる。</summary>
    public void RefreshIcon() => OnPropertyChanged(nameof(IconImage));

    public ObservableCollection<CSharpSolutionNodeViewModel> Children { get; } = [];

    private CSharpSolutionNodeViewModel(CSharpSolutionNode node)
    {
        Name = node.Name;
        Kind = node.Kind;
        FullPath = node.FullPath;
        Detail = node.Detail;
        IsSelected = node.IsSelected;
        CanRunTests = node.CanRunTests;
        IsExpanded = node.Kind is CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project;
        foreach (var child in node.Children)
        {
            var view = From(child);
            view.Parent = this;
            Children.Add(view);
        }
    }

    public static CSharpSolutionNodeViewModel From(CSharpSolutionNode node) => new(node);
}

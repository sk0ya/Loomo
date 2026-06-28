using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>コールスタックの 1 フレーム（停止時の呼び出し履歴）。選択でそのフレームの変数を表示する。</summary>
public sealed class DebugFrameViewModel
{
    public DebugFrameViewModel(DebugStackFrame frame)
    {
        Id = frame.Id;
        Name = frame.Name;
        SourcePath = frame.SourcePath;
        Line = frame.Line;
        Location = frame.SourcePath is { } p ? $"{Path.GetFileName(p)}:{frame.Line}" : "";
    }

    public int Id { get; }
    public string Name { get; }

    /// <summary>ソースの絶対パス（取得できなければ null）。プレビュー/ジャンプの対象。</summary>
    public string? SourcePath { get; }

    /// <summary>ソース行（1 始まり、DAP 準拠）。</summary>
    public int Line { get; }

    public string Location { get; }

    /// <summary>ソース位置を持ち、エディタへプレビュー/ジャンプできるか。</summary>
    public bool HasSource => !string.IsNullOrEmpty(SourcePath) && Line > 0;
}

/// <summary>変数ツリーの 1 ノード。<see cref="VariablesReference"/> が正なら子を遅延展開する。</summary>
public sealed partial class DebugVariableViewModel : ObservableObject
{
    private readonly Func<int, Task<IReadOnlyList<DebugVariable>>> _loadChildren;
    private bool _loaded;

    public DebugVariableViewModel(DebugVariable v, Func<int, Task<IReadOnlyList<DebugVariable>>> loadChildren)
    {
        _loadChildren = loadChildren;
        Name = v.Name;
        Value = v.Value;
        Type = v.Type;
        VariablesReference = v.VariablesReference;
        // 展開可能なら、矢印を出すためのプレースホルダ子を先に置く（展開時に実体へ差し替える）。
        if (HasChildren) Children.Add(Placeholder);
    }

    public string Name { get; }
    public string Value { get; }
    public string? Type { get; }
    public int VariablesReference { get; }
    public bool HasChildren => VariablesReference > 0;

    public ObservableCollection<DebugVariableViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = EnsureChildrenAsync();
    }

    private async Task EnsureChildrenAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var children = await _loadChildren(VariablesReference);
        Children.Clear();
        foreach (var c in children)
            Children.Add(new DebugVariableViewModel(c, _loadChildren));
    }

    private DebugVariableViewModel Placeholder
        => new(new DebugVariable("…", "", null, 0), _loadChildren);
}

/// <summary>ウォッチ式 1 件（式とその評価結果）。</summary>
public sealed partial class WatchItemViewModel : ObservableObject
{
    public WatchItemViewModel(string expression) => Expression = expression;

    public string Expression { get; }
    [ObservableProperty] private string _value = "";
}

/// <summary>失敗したテスト 1 件（<c>dotnet test</c> 出力から抽出）。<see cref="Line"/> はスタックトレースから
/// 拾ったソース行（1 始まり、不明なら 0）。<see cref="HasSource"/> ならダブルクリックでその位置へジャンプする。</summary>
public sealed class TestResultViewModel
{
    public TestResultViewModel(string name, string? message, string? sourcePath, int line)
    {
        Name = name;
        Message = message;
        SourcePath = sourcePath;
        Line = line;
    }

    public string Name { get; }

    /// <summary>失敗メッセージの先頭行（取得できなければ null）。</summary>
    public string? Message { get; }

    /// <summary>失敗箇所のソース絶対パス（スタックトレースから抽出、無ければ null）。</summary>
    public string? SourcePath { get; }

    /// <summary>失敗箇所のソース行（1 始まり、不明なら 0）。</summary>
    public int Line { get; }

    public bool HasSource => !string.IsNullOrEmpty(SourcePath) && Line > 0;

    public string Location => HasSource ? $"{Path.GetFileName(SourcePath)}:{Line}" : "";
}

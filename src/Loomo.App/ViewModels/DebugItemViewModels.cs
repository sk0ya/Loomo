using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

/// <summary>変数ツリーの 1 ノード。<see cref="VariablesReference"/> が正なら子を遅延展開する。
/// アダプタが <c>setVariable</c> 対応なら（＝この変数を含むスコープ/親の参照 <see cref="_containerRef"/> が分かれば）
/// 値をインラインで書き換えられる。</summary>
public sealed partial class DebugVariableViewModel : ObservableObject
{
    private readonly Func<int, Task<IReadOnlyList<DebugVariable>>> _loadChildren;
    /// <summary>値書き換え（containerRef, name, value → 新値 or null）。未対応なら null。</summary>
    private readonly Func<int, string, string, Task<string?>>? _setVariable;
    /// <summary>この変数を保持する側（スコープ or 親変数）の variablesReference。setVariable に渡す。</summary>
    private readonly int _containerRef;
    private bool _loaded;

    public DebugVariableViewModel(DebugVariable v, int containerRef,
        Func<int, Task<IReadOnlyList<DebugVariable>>> loadChildren,
        Func<int, string, string, Task<string?>>? setVariable)
    {
        _loadChildren = loadChildren;
        _setVariable = setVariable;
        _containerRef = containerRef;
        Name = v.Name;
        _value = v.Value;
        Type = v.Type;
        VariablesReference = v.VariablesReference;
        // 展開可能なら、矢印を出すためのプレースホルダ子を先に置く（展開時に実体へ差し替える）。
        if (HasChildren) Children.Add(Placeholder);
    }

    public string Name { get; }
    [ObservableProperty] private string _value;
    public string? Type { get; }
    public int VariablesReference { get; }
    public bool HasChildren => VariablesReference > 0;

    /// <summary>値を書き換えられるか（アダプタ対応＋保持側参照が分かる＝スコープ直下/親持ちの実変数）。</summary>
    public bool CanEdit => _setVariable is not null && _containerRef > 0;

    /// <summary>インライン編集中か（TextBox の表示切替）。</summary>
    [ObservableProperty] private bool _isEditing;

    /// <summary>編集中の入力値。</summary>
    [ObservableProperty] private string _editValue = "";

    public ObservableCollection<DebugVariableViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = EnsureChildrenAsync();
    }

    /// <summary>インライン編集を開始する（現在値を入力欄へ）。</summary>
    public void BeginEdit()
    {
        if (!CanEdit) return;
        EditValue = Value;
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    /// <summary>入力値で値を書き換える（成功すれば表示値を更新）。</summary>
    public async Task CommitEditAsync()
    {
        if (!IsEditing) return;
        IsEditing = false;
        if (_setVariable is null) return;
        var nv = await _setVariable(_containerRef, Name, EditValue);
        if (nv is not null) Value = nv;
    }

    private async Task EnsureChildrenAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var children = await _loadChildren(VariablesReference);
        Children.Clear();
        // 子の「保持側参照」はこのノードの VariablesReference（子はこの変数の中身）。
        foreach (var c in children)
            Children.Add(new DebugVariableViewModel(c, VariablesReference, _loadChildren, _setVariable));
    }

    private DebugVariableViewModel Placeholder
        => new(new DebugVariable("…", "", null, 0), 0, _loadChildren, _setVariable);
}

/// <summary>ブレークポイント 1 件（管理パネルの行）。行は表示用に 1 始まり。条件/ヒット数/ログメッセージと
/// 有効フラグを持ち、変更は <see cref="Changed"/> でデバッグサービスへ再送される。</summary>
public sealed partial class BreakpointViewModel : ObservableObject
{
    public BreakpointViewModel(string path, int line0)
    {
        Path = path;
        Line0 = line0;
        FileName = System.IO.Path.GetFileName(path);
    }

    /// <summary>ソースの絶対パス。</summary>
    public string Path { get; }

    /// <summary>0 始まりの行（エディタのバッファ行と一致）。</summary>
    public int Line0 { get; }

    public string FileName { get; }

    /// <summary>1 始まりの表示行。</summary>
    public int DisplayLine => Line0 + 1;

    public string Location => $"{FileName}:{DisplayLine}";

    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _condition = "";
    [ObservableProperty] private string _hitCondition = "";
    [ObservableProperty] private string _logMessage = "";

    /// <summary>条件パネルを開いているか（行ごとの詳細編集の開閉）。</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>有効/条件/ログのいずれかが変わったときに発火（VM がアダプタへ再送する）。</summary>
    public event Action<BreakpointViewModel>? Changed;

    partial void OnEnabledChanged(bool value) => Changed?.Invoke(this);
    partial void OnConditionChanged(string value) => Changed?.Invoke(this);
    partial void OnHitConditionChanged(string value) => Changed?.Invoke(this);
    partial void OnLogMessageChanged(string value) => Changed?.Invoke(this);

    public DebugBreakpoint ToModel() => new(DisplayLine,
        string.IsNullOrWhiteSpace(Condition) ? null : Condition.Trim(),
        string.IsNullOrWhiteSpace(HitCondition) ? null : HitCondition.Trim(),
        string.IsNullOrWhiteSpace(LogMessage) ? null : LogMessage.Trim(),
        Enabled);
}

/// <summary>エディタのガターでブレークポイントを描き分けるためのメタ（UI ライブラリ非依存）。
/// <paramref name="Line0"/> は 0 始まりのバッファ行。ShellWindow が Editor の EditorBreakpoint に写像する。</summary>
public readonly record struct BreakpointGlyphInfo(int Line0, bool HasCondition, bool IsLogpoint, bool Enabled);

/// <summary>ウォッチ式 1 件（式とその評価結果）。</summary>
public sealed partial class WatchItemViewModel : ObservableObject
{
    public WatchItemViewModel(string expression) => Expression = expression;

    public string Expression { get; }
    [ObservableProperty] private string _value = "";
}

/// <summary>イミディエイト（REPL）の 1 行（入力式とその評価結果）。コピー表示用に <c>&gt; 式</c> / 結果を持つ。</summary>
public sealed class ImmediateEntryViewModel
{
    public ImmediateEntryViewModel(string expression, string result)
    {
        Expression = expression;
        Result = result;
    }

    public string Expression { get; }
    public string Result { get; }

    /// <summary>入力行の表示（VS のイミディエイト風に <c>&gt;</c> を前置）。</summary>
    public string Prompt => "> " + Expression;
}

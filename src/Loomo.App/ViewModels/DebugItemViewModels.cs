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

/// <summary>テスト 1 件の実行状態。</summary>
public enum TestStatus
{
    /// <summary>未実行（探索しただけ）。</summary>
    NotRun,
    /// <summary>実行中。</summary>
    Running,
    /// <summary>成功。</summary>
    Passed,
    /// <summary>失敗。</summary>
    Failed,
    /// <summary>スキップ（NotExecuted）。</summary>
    Skipped,
}

/// <summary>テストエクスプローラの 1 行。ソース走査の自動収集で全テストを並べ、
/// 実行結果（TRX）で <see cref="Status"/>/<see cref="Message"/>/位置を更新する。失敗時は
/// <see cref="HasSource"/> ならダブルクリックでその位置へジャンプ、▶ で個別実行する。</summary>
public sealed partial class TestItemViewModel : ObservableObject
{
    public TestItemViewModel(string fullyQualifiedName)
    {
        FullyQualifiedName = fullyQualifiedName;
        FilterExpression = StripArgs(fullyQualifiedName);
        DisplayName = ShortName(fullyQualifiedName);

        // FilterExpression = Namespace.Class.Method（引数なし）。最後の '.' でクラスとメソッドに割る。
        var lastDot = FilterExpression.LastIndexOf('.');
        ClassName = lastDot >= 0 ? FilterExpression[..lastDot] : "";
        var method = lastDot >= 0 ? FilterExpression[(lastDot + 1)..] : FilterExpression;
        var paren = fullyQualifiedName.IndexOf('(');
        MethodName = method + (paren >= 0 ? fullyQualifiedName[paren..] : "");
    }

    /// <summary>完全名（テオリはケースごとに引数付き）。TRX の testName と一致させて結果を突き合わせる。</summary>
    public string FullyQualifiedName { get; }

    /// <summary>一覧表示用の短縮名（末尾の クラス.メソッド ＋ 引数）。コピー/ステータス文言用。</summary>
    public string DisplayName { get; }

    /// <summary>個別実行の <c>--filter</c> 用（テオリの引数を落とした完全名。同メソッドの全ケースが対象）。</summary>
    public string FilterExpression { get; }

    /// <summary>所属クラスの完全名（<c>Namespace.Class</c>）。ツリーのグループキー。</summary>
    public string ClassName { get; }

    /// <summary>メソッド名（＋テオリ引数）。ツリーの葉表示用（クラス名の重複を避ける）。</summary>
    public string MethodName { get; }

    /// <summary>TreeView の <c>ItemContainerStyle</c> 用（葉は展開しないが、共通スタイルのバインド先として持つ）。</summary>
    public bool IsExpanded { get; set; }

    /// <summary><c>[Theory]</c> 等の複数ケースを持つメソッドか。実行結果（TRX）の各ケース（<c>Method(args)</c>）を
    /// このメソッド単位の行へ集約するために使う（<see cref="ApplyCaseResult"/>）。</summary>
    public bool IsParameterized { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private TestStatus _status = TestStatus.NotRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Location))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string? _sourcePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Location))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private int _line;

    /// <summary>状態を表すグリフ（色は XAML の <c>Status</c> トリガで付ける）。</summary>
    public string Glyph => Status switch
    {
        TestStatus.Passed => "✓",
        TestStatus.Failed => "✗",
        TestStatus.Skipped => "⊘",
        TestStatus.Running => "…",
        _ => "·",
    };

    public bool HasSource => !string.IsNullOrEmpty(SourcePath) && Line > 0;

    public string Location => HasSource ? $"{Path.GetFileName(SourcePath)}:{Line}" : "";

    /// <summary>失敗していて表示すべきメッセージがあるか（一覧の 2 行目の出し分け）。</summary>
    public bool HasMessage => Status == TestStatus.Failed && !string.IsNullOrEmpty(Message);

    /// <summary>実行結果（TRX）を反映する。位置はスタックトレースから取れたときだけ更新する。</summary>
    public void Update(TestStatus status, string? message, string? sourcePath, int line)
    {
        Status = status;
        Message = message;
        if (!string.IsNullOrEmpty(sourcePath)) { SourcePath = sourcePath; Line = line; }
    }

    /// <summary>テオリ等のケース 1 件分の結果を、このメソッド行へ集約する（1 ケースでも失敗なら失敗、
    /// 全成功なら成功、それ以外はスキップ）。失敗位置・メッセージは最初の失敗ケースのものを残す。</summary>
    public void ApplyCaseResult(TestStatus status, string? message, string? sourcePath, int line)
    {
        if (Status != TestStatus.Failed)  // 既に失敗確定なら降格させない
        {
            if (status == TestStatus.Failed) { Status = TestStatus.Failed; Message = message; }
            else if (status == TestStatus.Passed) Status = TestStatus.Passed;
            else if (Status is TestStatus.NotRun or TestStatus.Running) Status = TestStatus.Skipped;
        }
        if (!string.IsNullOrEmpty(sourcePath) && string.IsNullOrEmpty(SourcePath)) { SourcePath = sourcePath; Line = line; }
    }

    public void SetRunning() => Status = TestStatus.Running;
    public void ResetStatus() => Status = TestStatus.NotRun;

    /// <summary>名前空間を落として クラス.メソッド（＋テオリ引数）を残す。</summary>
    private static string ShortName(string fqn)
    {
        var paren = fqn.IndexOf('(');
        var head = paren >= 0 ? fqn[..paren] : fqn;
        var args = paren >= 0 ? fqn[paren..] : "";
        var parts = head.Split('.');
        var shortHead = parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : head;
        return shortHead + args;
    }

    /// <summary>テオリの引数部（最初の <c>(</c> 以降）を落とす。</summary>
    private static string StripArgs(string fqn)
    {
        var paren = fqn.IndexOf('(');
        return paren >= 0 ? fqn[..paren] : fqn;
    }
}

/// <summary>テストツリーのグループ（クラス単位）。子の <see cref="Tests"/> の状態から集計グリフ・件数を作る。
/// ▶ でグループ内をまとめて実行できる（<see cref="Key"/> に前方一致するテストが対象）。</summary>
public sealed partial class TestGroupViewModel : ObservableObject
{
    public TestGroupViewModel(string key, string name)
    {
        Key = key;
        Name = name;
    }

    /// <summary>クラスの完全名（<c>Namespace.Class</c>）。グループ実行フィルタ・展開状態の保持キー。</summary>
    public string Key { get; }

    /// <summary>表示名（短いクラス名。名前空間が無ければ完全名）。</summary>
    public string Name { get; }

    /// <summary>このクラスのテスト（葉）。</summary>
    public ObservableCollection<TestItemViewModel> Tests { get; } = new();

    /// <summary>TreeView の展開状態（既定は折りたたみ）。行クリックで開閉、再構築時に <see cref="Key"/> で引き継ぐ。</summary>
    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private TestStatus _status = TestStatus.NotRun;

    /// <summary>集計を表すグリフ（色は XAML の <c>Status</c> トリガで付ける）。</summary>
    public string Glyph => Status switch
    {
        TestStatus.Passed => "✓",
        TestStatus.Failed => "✗",
        TestStatus.Skipped => "⊘",
        TestStatus.Running => "…",
        _ => "·",
    };

    /// <summary>「成功数/合計（＋失敗数）」の簡易カウンタ。</summary>
    public string CountText { get; private set; } = "";

    /// <summary>子の状態から集計（グリフ・件数）を作り直す。</summary>
    public void RecomputeAggregate()
    {
        var failed = Tests.Count(t => t.Status == TestStatus.Failed);
        var passed = Tests.Count(t => t.Status == TestStatus.Passed);
        var running = Tests.Any(t => t.Status == TestStatus.Running);
        var anyRun = Tests.Any(t => t.Status is TestStatus.Passed or TestStatus.Failed or TestStatus.Skipped);

        Status = failed > 0 ? TestStatus.Failed
            : running ? TestStatus.Running
            : !anyRun ? TestStatus.NotRun
            : passed > 0 ? TestStatus.Passed
            : TestStatus.Skipped;
        CountText = failed > 0 ? $"{passed}/{Tests.Count}  失敗 {failed}" : $"{passed}/{Tests.Count}";
        OnPropertyChanged(nameof(CountText));
    }
}

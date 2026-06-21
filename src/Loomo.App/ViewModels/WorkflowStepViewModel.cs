using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークフローステップの実行状態。</summary>
public enum WorkflowStepStatus { Idle, Running, Done, Error }

/// <summary>ワークフロー1ステップのViewModel（編集用フィールド＋実行時の状態・ログ）。</summary>
public sealed partial class WorkflowStepViewModel : ObservableObject
{
    // ===== 編集用（永続化対象） =====
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private bool _useTools;

    /// <summary>テキストのみステップか（種別セグメントの「テキスト」側の選択状態）。UseTools の反転。</summary>
    public bool IsTextMode => !UseTools;
    partial void OnUseToolsChanged(bool value) => OnPropertyChanged(nameof(IsTextMode));

    /// <summary>種別セグメント（テキスト/エージェント）の選択。XAML から "text"/"agent" を渡す。</summary>
    [RelayCommand]
    private void SelectMode(string mode) => UseTools = string.Equals(mode, "agent", System.StringComparison.Ordinal);

    // ===== 表示・実行時 =====

    /// <summary>1始まりの並び順。親コレクションの変更時に振り直される。</summary>
    [ObservableProperty] private int _index;

    /// <summary>パイプライン表示の接続線を出し分けるための先頭/末尾フラグ（親が振り直す）。</summary>
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;

    [ObservableProperty] private WorkflowStepStatus _status = WorkflowStepStatus.Idle;

    /// <summary>状態の1行表示（待機／実行中…／完了 (2.1秒)／失敗）。</summary>
    [ObservableProperty] private string _statusText = "待機";

    /// <summary>このステップの最終出力（後続ステップへ渡される素のテキスト）。</summary>
    [ObservableProperty] private string _output = "";

    /// <summary>実行ログ（思考・ツールカード・承認カード・結果）。折りたたみ表示。</summary>
    public ObservableCollection<TranscriptEntry> Log { get; } = new();

    /// <summary>実行ログ/出力を折りたたんでいるか。</summary>
    [ObservableProperty] private bool _isLogCollapsed = true;
    public string LogGlyph => IsLogCollapsed ? "▶" : "▼";
    partial void OnIsLogCollapsedChanged(bool value) => OnPropertyChanged(nameof(LogGlyph));

    /// <summary>クリックで指示文へ挿入できる前段参照トークン（{{1}}…{{N-1}}・{{prev}}・{{all}}）。</summary>
    public ObservableCollection<string> RefTokens { get; } = new();

    public WorkflowStepViewModel() { }

    public WorkflowStepViewModel(WorkflowStep step)
    {
        _title = step.Title;
        _prompt = step.Prompt;
        _useTools = step.UseTools;
    }

    /// <summary>編集中の内容を永続化用モデルへ写す。</summary>
    public WorkflowStep ToModel() => new() { Title = Title, Prompt = Prompt, UseTools = UseTools };

    partial void OnIndexChanged(int value) => RebuildRefTokens();

    /// <summary>自分より前のステップを参照できるトークン一覧を作り直す。</summary>
    private void RebuildRefTokens()
    {
        RefTokens.Clear();
        for (var i = 1; i < Index; i++)
            RefTokens.Add($"{{{{{i}}}}}");
        if (Index > 1)
        {
            RefTokens.Add("{{prev}}");
            RefTokens.Add("{{all}}");
        }
    }

    /// <summary>参照トークンを指示文の末尾へ挿入する（クリックで呼ばれる）。</summary>
    [RelayCommand]
    private void InsertRef(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        Prompt = string.IsNullOrEmpty(Prompt) ? token : Prompt + " " + token;
    }

    [RelayCommand]
    private void ToggleLog() => IsLogCollapsed = !IsLogCollapsed;

    /// <summary>実行開始前に前回実行の痕跡を消す。</summary>
    public void ResetRun()
    {
        Status = WorkflowStepStatus.Idle;
        StatusText = "待機";
        Output = "";
        Log.Clear();
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークフローステップの実行状態。</summary>
public enum WorkflowStepStatus { Idle, Running, Done, Error }

/// <summary>種別セレクタ（ComboBox）の1項目（種別＋表示ラベル）。</summary>
public sealed record StepKindOption(WorkflowStepKind Kind, string Label);

/// <summary>ワークフロー1ステップのViewModel（編集用フィールド＋実行時の状態・ログ）。</summary>
public sealed partial class WorkflowStepViewModel : ObservableObject
{
    private static readonly Regex RefTokenRegex = new(@"\{\{\s*(\d+|prev|all)\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ===== 編集用（永続化対象） =====
    [ObservableProperty] private string _title = "";

    /// <summary>ステップ種別。AI=LLM呼び出し / それ以外=決定論的ツール実行。</summary>
    [ObservableProperty] private WorkflowStepKind _kind = WorkflowStepKind.Ai;

    /// <summary>主テキスト（AI=指示文 / Command=コマンド / ReadFile・WriteFile=パス / Transform=入力）。</summary>
    [ObservableProperty] private string _prompt = "";

    /// <summary>WriteFile=書き込む内容テンプレート / Transform=置換後文字列。</summary>
    [ObservableProperty] private string _content = "";

    /// <summary>Transform=検索パターン。</summary>
    [ObservableProperty] private string _pattern = "";

    /// <summary>Transform=正規表現として扱うか。</summary>
    [ObservableProperty] private bool _isRegex;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    partial void OnKindChanged(WorkflowStepKind value)
    {
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(KindGlyph));
        OnPropertyChanged(nameof(IsAiStep));
        OnPropertyChanged(nameof(IsCommandStep));
        OnPropertyChanged(nameof(IsReadFileStep));
        OnPropertyChanged(nameof(IsWriteFileStep));
        OnPropertyChanged(nameof(IsTransformStep));
        OnPropertyChanged(nameof(PrimaryPlaceholder));
        OnPropertyChanged(nameof(PrimaryIsMonospace));
        OnPropertyChanged(nameof(PromptNotice));
        OnPropertyChanged(nameof(CanAppendPrevious));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(PromptNotice));
    partial void OnPatternChanged(string value) => OnPropertyChanged(nameof(PromptNotice));

    // ===== 種別の表示ヘルパ =====

    /// <summary>種別の短いラベル（バッジ表示用）。</summary>
    public string KindLabel => Kind switch
    {
        WorkflowStepKind.Command => "コマンド",
        WorkflowStepKind.ReadFile => "読込",
        WorkflowStepKind.WriteFile => "書込",
        WorkflowStepKind.Transform => "変換",
        _ => "AI",
    };

    /// <summary>種別のアイコン（バッジ表示用）。</summary>
    public string KindGlyph => Kind switch
    {
        WorkflowStepKind.Command => "💻",
        WorkflowStepKind.ReadFile => "📄",
        WorkflowStepKind.WriteFile => "💾",
        WorkflowStepKind.Transform => "🔁",
        _ => "🤖",
    };

    public bool IsAiStep => Kind == WorkflowStepKind.Ai;
    public bool IsCommandStep => Kind == WorkflowStepKind.Command;
    public bool IsReadFileStep => Kind == WorkflowStepKind.ReadFile;
    public bool IsWriteFileStep => Kind == WorkflowStepKind.WriteFile;
    public bool IsTransformStep => Kind == WorkflowStepKind.Transform;

    /// <summary>主テキスト欄のプレースホルダ（種別で文言を変える）。</summary>
    public string PrimaryPlaceholder => Kind switch
    {
        WorkflowStepKind.Command => "PowerShell コマンド…（例: git status --short）",
        WorkflowStepKind.ReadFile => "読み込むファイルパス…（ワークスペース相対可）",
        WorkflowStepKind.WriteFile => "書き込み先のファイルパス…（ワークスペース相対可）",
        WorkflowStepKind.Transform => "変換する入力…（通常は {{prev}}）",
        _ => "AIへの指示を入力…   {{1}} などで前段の出力を差し込めます",
    };

    /// <summary>主テキスト欄を等幅にするか（コマンド/パス/入力テキスト系）。</summary>
    public bool PrimaryIsMonospace => Kind != WorkflowStepKind.Ai;

    /// <summary>種別セレクタ（ComboBox）の選択肢。</summary>
    public IReadOnlyList<StepKindOption> KindOptions => StepKindOptions;

    private static readonly IReadOnlyList<StepKindOption> StepKindOptions = new[]
    {
        new StepKindOption(WorkflowStepKind.Ai, "🤖 AI"),
        new StepKindOption(WorkflowStepKind.Command, "💻 コマンド"),
        new StepKindOption(WorkflowStepKind.ReadFile, "📄 ファイル読込"),
        new StepKindOption(WorkflowStepKind.WriteFile, "💾 ファイル書込"),
        new StepKindOption(WorkflowStepKind.Transform, "🔁 テキスト変換"),
    };

    /// <summary>主テキストがテキスト/コマンドを消費する種別か（前段参照の注意やチップを出す対象）。</summary>
    private bool IsTextConsuming => Kind is WorkflowStepKind.Ai or WorkflowStepKind.Command or WorkflowStepKind.Transform;

    // ===== 表示・実行時 =====

    /// <summary>1始まりの並び順。親コレクションの変更時に振り直される。</summary>
    [ObservableProperty] private int _index;

    /// <summary>パイプライン表示の接続線を出し分けるための先頭/末尾フラグ（親が振り直す）。</summary>
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;

    [ObservableProperty] private WorkflowStepStatus _status = WorkflowStepStatus.Idle;

    /// <summary>状態の1行表示（待機／実行中…／完了 (2.1秒)／失敗）。</summary>
    [ObservableProperty] private string _statusText = "待機";

    /// <summary>パイプライン上の見出し（タイトル未設定なら「ステップN」）。実行ログのステップ見出しにも使う。</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"ステップ{Index}" : Title;

    /// <summary>クリックで指示文へ挿入できる前段参照トークン（{{1}}…{{N-1}}・{{prev}}・{{all}}）。</summary>
    public ObservableCollection<string> RefTokens { get; } = new();

    /// <summary>入力内容から分かる実行前の軽い注意。空なら表示しない。</summary>
    public string PromptNotice
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Prompt))
                return Kind switch
                {
                    WorkflowStepKind.Command => "コマンドが空です。実行時はスキップされます。",
                    WorkflowStepKind.ReadFile => "読み込むファイルパスを入力してください。",
                    WorkflowStepKind.WriteFile => "書き込み先のファイルパスを入力してください。",
                    WorkflowStepKind.Transform => "変換する入力（通常は {{prev}}）を入力してください。",
                    _ => "空ステップです。実行時はスキップされます。",
                };

            // 種別固有の追加チェック。
            if (Kind == WorkflowStepKind.Transform && string.IsNullOrEmpty(Pattern))
                return "検索パターンが空です。このままでは変換されません。";
            if (Kind == WorkflowStepKind.WriteFile && string.IsNullOrEmpty(Content))
                return "書き込む内容が空です。{{prev}} や {{input}} を内容に指定できます。";

            // AI ステップは前段出力が自動では渡らないことを案内する。
            if (Kind == WorkflowStepKind.Ai && Index > 1 && !RefTokenRegex.IsMatch(Prompt))
                return "前段の出力は自動では渡りません。必要なら {{prev}} か {{all}} を追加してください。";
            return "";
        }
    }

    public bool CanAppendPrevious => IsTextConsuming && Index > 1 && !string.IsNullOrWhiteSpace(Prompt);

    public WorkflowStepViewModel() { }

    public WorkflowStepViewModel(WorkflowStep step)
    {
        _title = step.Title;
        _kind = step.Kind;
        _prompt = step.Prompt;
        _content = step.Content;
        _pattern = step.Pattern;
        _isRegex = step.IsRegex;
    }

    /// <summary>編集中の内容を永続化用モデルへ写す。</summary>
    public WorkflowStep ToModel() => new()
    {
        Title = Title,
        Kind = Kind,
        Prompt = Prompt,
        Content = Content,
        Pattern = Pattern,
        IsRegex = IsRegex,
    };

    partial void OnIndexChanged(int value)
    {
        RebuildRefTokens();
        OnPropertyChanged(nameof(PromptNotice));
        OnPropertyChanged(nameof(CanAppendPrevious));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    partial void OnPromptChanged(string value)
    {
        OnPropertyChanged(nameof(PromptNotice));
        OnPropertyChanged(nameof(CanAppendPrevious));
    }

    /// <summary>指示文へ挿入できる参照トークン一覧を作り直す。<c>{{input}}</c> は全ステップで使える。</summary>
    private void RebuildRefTokens()
    {
        RefTokens.Clear();
        RefTokens.Add("{{input}}");
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
    private void AppendPrevious()
    {
        InsertRef("{{prev}}");
    }

    /// <summary>実行開始前に前回実行の痕跡を消す。</summary>
    public void ResetRun()
    {
        Status = WorkflowStepStatus.Idle;
        StatusText = "待機";
    }
}

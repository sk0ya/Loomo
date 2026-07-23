using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>停止中の検査（コールスタック・変数・自動変数・ウォッチ・スレッド・イミディエイト・モジュール）を扱うサブ ViewModel。
/// 停止時にコールスタック等を読み、フレーム選択でその変数を読み直す。値の評価はすべて <see cref="IDebugService"/> 経由。</summary>
public sealed partial class DebugInspectionViewModel : ObservableObject
{
    private readonly IDebugService _debug;
    private readonly IDebugSession _session;

    internal DebugInspectionViewModel(IDebugService debug, IDebugSession session)
    {
        _debug = debug;
        _session = session;
        _session.SessionStateChanged += () =>
        {
            OnPropertyChanged(nameof(IsStopped));
            SubmitImmediateCommand.NotifyCanExecuteChanged();
            RefreshModulesCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>停止中か（各タブの「停止すると…表示されます」案内のバインド先）。状態の真実はセッション側。</summary>
    public bool IsStopped => _session.IsStopped;

    // --- コールスタック / フレーム ---

    /// <summary>停止中のコールスタック。</summary>
    public ObservableCollection<DebugFrameViewModel> CallStack { get; } = new();

    /// <summary>選択中フレーム（変更で変数を読み直す）。</summary>
    [ObservableProperty] private DebugFrameViewModel? _selectedFrame;

    /// <summary>停止時：コールスタックを取得し、先頭フレームを選ぶ（→変数読込）。</summary>
    public async Task LoadStackAsync()
    {
        var frames = await _debug.GetStackTraceAsync();
        CallStack.Clear();
        foreach (var f in frames) CallStack.Add(new DebugFrameViewModel(f));
        SelectedFrame = CallStack.Count > 0 ? CallStack[0] : null;  // → OnSelectedFrameChanged が変数を読む
    }

    /// <summary>停止時：実行中スレッド・コールスタック・モジュールをまとめて読み込む（ファサードの停止ハンドラから）。</summary>
    public async Task OnStoppedAsync()
    {
        await LoadThreadsAsync();
        await LoadStackAsync();
        await LoadModulesAsync();
    }

    partial void OnSelectedFrameChanged(DebugFrameViewModel? value)
    {
        _ = LoadFrameInspectionAsync(value);
        // フレームを選んだら、そのソース位置をエディタにプレビュー表示する（フォーカスは奪わない）。
        if (value is { HasSource: true, SourcePath: { } p })
            _session.RaiseFramePreview(p, value.Line - 1);  // DAP 1始まり → エディタ 0始まり
    }

    /// <summary>コールスタックのフレームのダブルクリック：ソースへジャンプする（通常タブ＋フォーカス）。</summary>
    public void ActivateFrame(DebugFrameViewModel? frame)
    {
        if (frame is { HasSource: true, SourcePath: { } p })
            _session.RaiseFrameActivated(p, frame.Line - 1);  // DAP 1始まり → エディタ 0始まり
    }

    /// <summary>選択フレームのスコープ（Locals 等）をトップ階層に並べ、ウォッチも評価し直す。</summary>
    private async Task LoadFrameInspectionAsync(DebugFrameViewModel? frame)
    {
        Variables.Clear();
        if (frame is null) return;

        var scopes = await _debug.GetScopesAsync(frame.Id);
        Func<int, string, string, Task<string?>>? setVar =
            _debug.SupportsSetVariable ? (cr, n, v) => _debug.SetVariableAsync(cr, n, v) : null;
        foreach (var s in scopes)
        {
            // スコープを「展開可能な変数ノード」として扱い、展開時に GetVariablesAsync で中身を読む。
            // スコープ自身は書き換え対象でないので containerRef=0（直下の子は scope の VR を保持側に持つ）。
            var node = new DebugVariableViewModel(
                new DebugVariable(s.Name, "", null, s.VariablesReference), 0,
                vr => _debug.GetVariablesAsync(vr), setVar);
            Variables.Add(node);
        }
        if (Variables.Count > 0) Variables[0].IsExpanded = true;  // Locals を既定で開く

        await LoadAutosAsync(frame);
        await RefreshWatchesAsync(frame.Id);
    }

    // --- 変数 ---

    /// <summary>変数ツリー（トップ階層はスコープ＝Locals 等、展開で変数→子フィールド）。</summary>
    public ObservableCollection<DebugVariableViewModel> Variables { get; } = new();

    // --- 自動変数（Autos） ---

    /// <summary>自動変数（Autos）。停止行とその直前行で使われている変数だけを近似表示する（VS の「自動」）。</summary>
    public ObservableCollection<WatchItemViewModel> Autos { get; } = new();

    /// <summary>自動変数が 1 件でもあるか（空案内の出し分け）。</summary>
    [ObservableProperty] private bool _hasAutos;

    /// <summary>自動変数を読み直す。停止行とその直前行のソースから識別子を拾い、フレーム文脈で評価し、
    /// 値が取れたものだけ並べる（VS の「自動」をアダプタ非依存に近似）。</summary>
    private async Task LoadAutosAsync(DebugFrameViewModel? frame)
    {
        Autos.Clear();
        HasAutos = false;
        if (frame is not { HasSource: true, SourcePath: { } path }) return;

        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path); }
        catch { return; }  // ソースが読めない（生成コード等）なら自動変数は出さない

        var idx = frame.Line - 1;  // DAP 1始まり → 配列 0始まり
        if (idx < 0 || idx >= lines.Length) return;
        var prev = idx > 0 ? lines[idx - 1] : null;

        foreach (var expr in AutosExtractor.ExtractCandidates(lines[idx], prev,
                     AutosExtractor.LanguageForPath(frame.SourcePath)))
        {
            var value = await _debug.EvaluateAsync(expr, frame.Id);
            if (AutosExtractor.LooksLikeValue(value))
                Autos.Add(new WatchItemViewModel(expr) { Value = value });
        }
        HasAutos = Autos.Count > 0;
    }

    // --- ウォッチ ---

    /// <summary>ウォッチ式。</summary>
    public ObservableCollection<WatchItemViewModel> Watches { get; } = new();

    /// <summary>ウォッチ追加欄。</summary>
    [ObservableProperty] private string _watchExpression = "";

    private async Task RefreshWatchesAsync(int frameId)
    {
        foreach (var w in Watches)
            w.Value = await _debug.EvaluateAsync(w.Expression, frameId);
    }

    private bool CanAddWatch() => !string.IsNullOrWhiteSpace(WatchExpression);

    [RelayCommand(CanExecute = nameof(CanAddWatch))]
    private async Task AddWatch()
    {
        var item = new WatchItemViewModel(WatchExpression.Trim());
        Watches.Add(item);
        WatchExpression = "";
        if (_session.IsStopped && SelectedFrame is { } f)
            item.Value = await _debug.EvaluateAsync(item.Expression, f.Id);
    }

    [RelayCommand]
    private void RemoveWatch(WatchItemViewModel? item)
    {
        if (item is not null) Watches.Remove(item);
    }

    partial void OnWatchExpressionChanged(string value) => AddWatchCommand.NotifyCanExecuteChanged();

    /// <summary>DataTip 用：停止中の現在フレームで式を評価し、表示値を返す。停止していない／値として意味のない結果なら null。</summary>
    public async Task<string?> EvaluateDataTipAsync(string expression)
    {
        if (!_session.IsStopped || SelectedFrame is not { } f || string.IsNullOrWhiteSpace(expression)) return null;
        var value = await _debug.EvaluateAsync(expression, f.Id);
        return AutosExtractor.LooksLikeValue(value) ? value : null;
    }

    // --- スレッド ---

    /// <summary>実行中スレッド一覧（停止時に取得）。選択でアクティブスレッドを切り替える。</summary>
    public ObservableCollection<DebugThread> Threads { get; } = new();

    /// <summary>選択中スレッド（切替で stackTrace/step の対象を変える）。</summary>
    [ObservableProperty] private DebugThread? _selectedThread;

    /// <summary>スレッド一覧をプログラムから差し替え中（選択変更の再入で再読込しないためのガード）。</summary>
    private bool _settingThread;

    /// <summary>停止時：実行中スレッドを取得し、アクティブスレッドを選択状態にする。</summary>
    private async Task LoadThreadsAsync()
    {
        var threads = await _debug.GetThreadsAsync();
        _settingThread = true;
        try
        {
            Threads.Clear();
            foreach (var t in threads) Threads.Add(t);
            SelectedThread = Threads.FirstOrDefault(t => t.Id == _debug.ActiveThreadId) ?? Threads.FirstOrDefault();
        }
        finally { _settingThread = false; }
    }

    partial void OnSelectedThreadChanged(DebugThread? value)
    {
        if (_settingThread || value is null || !_session.IsStopped) return;
        _debug.SetActiveThread(value.Id);
        _ = LoadStackAsync();  // 切替先スレッドのコールスタック→変数を読み直す
    }

    // --- イミディエイト（REPL） ---

    /// <summary>イミディエイト（REPL）の入出力履歴（式とその評価結果）。</summary>
    public ObservableCollection<ImmediateEntryViewModel> ImmediateLog { get; } = new();

    /// <summary>イミディエイトの入力欄。</summary>
    [ObservableProperty] private string _immediateInput = "";

    private bool CanSubmitImmediate() => _session.IsStopped && !string.IsNullOrWhiteSpace(ImmediateInput);

    /// <summary>イミディエイト：入力式を REPL コンテキストで評価する（副作用のある式も実行できる）。
    /// 状態を変える可能性があるので、評価後に変数/ウォッチを読み直す。</summary>
    [RelayCommand(CanExecute = nameof(CanSubmitImmediate))]
    private async Task SubmitImmediate()
    {
        var expr = ImmediateInput.Trim();
        ImmediateInput = "";
        var result = await _debug.EvaluateReplAsync(expr, SelectedFrame?.Id);
        ImmediateLog.Add(new ImmediateEntryViewModel(expr, result));
        const int max = 500;
        if (ImmediateLog.Count > max) ImmediateLog.RemoveAt(0);
        // 副作用で変数が変わり得るので検査ペインを更新する。
        if (SelectedFrame is { } f) await LoadFrameInspectionAsync(f);
    }

    [RelayCommand]
    private void ClearImmediate() => ImmediateLog.Clear();

    partial void OnImmediateInputChanged(string value) => SubmitImmediateCommand.NotifyCanExecuteChanged();

    // --- モジュール ---

    /// <summary>ロード済みモジュール（アセンブリ）一覧。停止時に取得し、更新ボタンで読み直せる。</summary>
    public ObservableCollection<DebugModule> Modules { get; } = new();

    /// <summary>モジュールが 1 件でも取得できているか（空案内の出し分け）。</summary>
    [ObservableProperty] private bool _hasModules;

    private bool CanRefreshModules() => _session.IsStopped;

    [RelayCommand(CanExecute = nameof(CanRefreshModules))]
    private async Task RefreshModules() => await LoadModulesAsync();

    /// <summary>モジュール一覧を取得して並べ替える（パス有りを後ろ、名前順）。</summary>
    private async Task LoadModulesAsync()
    {
        var mods = await _debug.GetModulesAsync();
        Modules.Clear();
        foreach (var m in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            Modules.Add(m);
        HasModules = Modules.Count > 0;
    }

    // --- クリア ---

    /// <summary>続行・終了時に検査内容を片付ける。</summary>
    public void Clear()
    {
        CallStack.Clear();
        Variables.Clear();
        Autos.Clear();
        HasAutos = false;
        _settingThread = true;
        try { Threads.Clear(); SelectedThread = null; }
        finally { _settingThread = false; }
        foreach (var w in Watches) w.Value = "";
        Modules.Clear();
        HasModules = false;
    }
}

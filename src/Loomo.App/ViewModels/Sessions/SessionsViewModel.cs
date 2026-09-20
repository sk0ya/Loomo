using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Observability;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>AIセッション履歴パネルの ViewModel。保存済み会話の一覧・新規・復元・削除。
/// AIペインのチャット横に開閉できるサイドバーとして表示される（<see cref="IsOpen"/>）。</summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ConversationStore _store;
    private readonly AiBarViewModel _aiBar;
    private readonly TraceReader _traces;

    public ObservableCollection<SessionSummary> Sessions { get; } = new();

    /// <summary>サイドバー（AIペイン内の一覧）を開いているか。</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>初回の一覧読込を済ませたか。サイドバーを開くまで遅延する。</summary>
    private bool _loaded;

    public SessionsViewModel(ConversationStore store, AiBarViewModel aiBar, TraceReader traces)
    {
        _store = store;
        _aiBar = aiBar;
        _traces = traces;
        // 一覧の読込は List() が全セッション JSON をフル読込・デシリアライズするため重い。
        // Sessions パネルは既定で非表示なので、初回表示時（EnsureLoaded）まで遅延し起動を軽くする。
        _store.Changed += OnStoreChanged;
    }

    /// <summary>サイドバーが初めて開かれたときに一覧を読み込む（以降は Changed で追従）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    /// <summary>AIペインヘッダーの開閉ボタン。開くときに一覧を遅延読込する。</summary>
    [RelayCommand]
    private void ToggleOpen()
    {
        IsOpen = !IsOpen;
        if (IsOpen) EnsureLoaded();
    }

    private void OnStoreChanged()
    {
        // まだ一度も開いていなければ、次に開いたとき EnsureLoaded が最新を読むので何もしない。
        if (!_loaded) return;
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) Refresh();
        else app.Dispatcher.Invoke(Refresh);
    }

    [RelayCommand]
    public void Refresh()
    {
        _loaded = true;
        Sessions.Clear();
        foreach (var s in _store.List()) Sessions.Add(s);
    }

    [RelayCommand]
    private void New() => _aiBar.StartNewSession();

    [RelayCommand]
    private void Load(SessionSummary? summary)
    {
        if (summary is null) return;
        var session = _store.Load(summary.Id);
        if (session is not null) _aiBar.RestoreSession(session);
    }

    [RelayCommand]
    private void Delete(SessionSummary? summary)
    {
        if (summary is null) return;
        // 会話とトレースは sessionId を共有する。分析一覧に残らないよう先にトレースを消す
        // （_store.Delete が発火する Changed で分析パネルが再列挙する前に消す必要がある）。
        _traces.Delete(summary.Id);
        _store.Delete(summary.Id);
    }
}

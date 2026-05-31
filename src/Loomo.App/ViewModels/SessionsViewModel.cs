using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>AIセッション履歴パネルの ViewModel。保存済み会話の一覧・新規・復元・削除。</summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ConversationStore _store;
    private readonly AiBarViewModel _aiBar;

    public ObservableCollection<SessionSummary> Sessions { get; } = new();

    public SessionsViewModel(ConversationStore store, AiBarViewModel aiBar)
    {
        _store = store;
        _aiBar = aiBar;
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    private void OnStoreChanged()
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) Refresh();
        else app.Dispatcher.Invoke(Refresh);
    }

    [RelayCommand]
    public void Refresh()
    {
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
        if (summary is not null) _store.Delete(summary.Id);
    }
}

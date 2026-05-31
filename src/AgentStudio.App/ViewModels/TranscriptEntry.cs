using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.App.ViewModels;

public enum EntryKind { User, Assistant, Tool, Approval, Error }

/// <summary>AIバーの会話トランスクリプト1項目。</summary>
public sealed partial class TranscriptEntry : ObservableObject
{
    public EntryKind Kind { get; init; }

    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _text = "";

    // 承認カード用
    [ObservableProperty] private bool _isPending;
    private TaskCompletionSource<bool>? _completion;

    public void AppendText(string chunk) => Text += chunk;

    public void BindApproval(TaskCompletionSource<bool> completion)
    {
        _completion = completion;
        IsPending = true;
    }

    [RelayCommand]
    private void Approve()
    {
        IsPending = false;
        _completion?.TrySetResult(true);
        Header = "✅ 承認済み: " + Header;
    }

    [RelayCommand]
    private void Reject()
    {
        IsPending = false;
        _completion?.TrySetResult(false);
        Header = "⛔ 拒否: " + Header;
    }
}

using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 未保存の編集があるタブのファイルが外で書き換わったとき、エディタ上端に出すバー（§24.57）。
/// 黙って読み直すと人の編集が消え、黙って放っておくと次の保存で外の変更が消える——どちらも人が選ぶ。
/// 出す／隠すの判断は Shell（アクティブタブが競合を抱えているか）で、ここは表示と操作の橋渡しだけ。
/// </summary>
public sealed partial class ExternalFileChangeBarViewModel : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _message = "";

    /// <summary>「ディスクの内容で読み直す」。未保存の編集は捨てる。</summary>
    public event Action? ReloadRequested;

    /// <summary>「ディスクとの差分を見る」。</summary>
    public event Action? CompareRequested;

    /// <summary>「このまま編集を続ける」（次の保存で外の変更を上書きする）。</summary>
    public event Action? KeepRequested;

    public void Show(string filePath)
    {
        Message = $"{Path.GetFileName(filePath)} がエディタの外で変更されました（未保存の編集あり。このまま保存すると外の変更を上書きします）。";
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    [RelayCommand] private void Reload() => ReloadRequested?.Invoke();
    [RelayCommand] private void Compare() => CompareRequested?.Invoke();
    [RelayCommand] private void Keep() => KeepRequested?.Invoke();
}

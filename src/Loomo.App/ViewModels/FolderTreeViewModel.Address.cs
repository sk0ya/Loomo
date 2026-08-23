using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>FolderTree の編集可能なアドレス欄。表示ルートの切替と入力履歴をまとめる。</summary>
public sealed partial class FolderTreeViewModel
{
    private readonly FolderTreeAddressHistory _addressHistory = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddressError))]
    private string _addressError = string.Empty;

    [ObservableProperty]
    private string _addressText = string.Empty;

    public ObservableCollection<string> AddressSuggestions { get; } = new();

    public IReadOnlyList<string> AddressHistory => _addressHistory.Entries;

    public bool HasAddressError => !string.IsNullOrEmpty(AddressError);

    /// <summary>現在のワークスペース外へ移動するとき、ShellWindow の既存切替経路へ渡す。</summary>
    public event EventHandler<string>? AddressNavigationRequested;

    partial void OnAddressTextChanged(string value)
    {
        AddressError = string.Empty;
        RefreshAddressSuggestions(value);
    }

    /// <summary>
    /// アドレス欄の入力を検証して移動する。ワークスペース内なら表示ルートだけを切り替え、
    /// 外ならワークスペース切替を要求する。成功時は履歴へ追加する。
    /// </summary>
    public bool NavigateAddress(string? input)
    {
        var basePath = _currentRoot ?? _workspaceRoot;
        if (!FolderTreeAddressHistory.TryNormalizePath(input, basePath, out var fullPath))
        {
            AddressError = "パスを解釈できません";
            return false;
        }

        if (!_query.DirectoryExists(fullPath))
        {
            AddressError = $"フォルダーが存在しません: {fullPath}";
            return false;
        }

        AddressError = string.Empty;
        _addressHistory.Add(fullPath);
        AddressText = fullPath;

        // マルチルートの各見出しは同時に表示されるため、1つのアドレスへ寄せる操作は
        // 既存の「フォルダーを開く」経路へ渡して単一ルートへ戻す。
        if (_multiRootStates.Count != 0 || !_workspace.Contains(fullPath))
        {
            AddressNavigationRequested?.Invoke(this, fullPath);
            return true;
        }

        var option = RootOptions.FirstOrDefault(o => PathsEqual(o.FullPath, fullPath));
        if (option is not null)
        {
            SelectRootOption(option);
        }
        else
        {
            // ピン留めされていない子フォルダーもアドレス欄から直接開ける。
            // RootOptions はピン留め候補のままにし、TreeRootOverride で保存する。
            _suppressRootSelection = true;
            try { SelectedRootOption = null; }
            finally { _suppressRootSelection = false; }
            SetDisplayRoot(fullPath);
        }

        RootStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RefreshAddressSuggestions(string input)
    {
        var basePath = _currentRoot ?? _workspaceRoot;
        var suggestions = _addressHistory.Suggest(input, basePath);
        AddressSuggestions.Clear();
        foreach (var suggestion in suggestions)
            AddressSuggestions.Add(suggestion);
    }

}

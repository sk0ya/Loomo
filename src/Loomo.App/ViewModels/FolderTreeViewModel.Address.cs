using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>FolderTree の編集可能なアドレス欄。表示ルートの切替と入力履歴をまとめる。</summary>
public sealed partial class FolderTreeViewModel
{
    private readonly FolderTreeAddressHistory _addressHistory = new();
    private readonly Stack<string> _backPaths = new();
    private readonly Stack<string> _forwardPaths = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddressError))]
    private string _addressError = string.Empty;

    [ObservableProperty]
    private string _addressText = string.Empty;

    public ObservableCollection<string> AddressSuggestions { get; } = new();

    public IReadOnlyList<string> AddressHistory => _addressHistory.Entries;

    public bool HasAddressError => !string.IsNullOrEmpty(AddressError);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoForward;

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
            AddressError = FolderTreeShellNamespaces.IsShellPath(fullPath)
                ? "Windows Shell 名前空間を利用できません"
                : $"フォルダーが存在しません: {fullPath}";
            return false;
        }

        AddressError = string.Empty;
        if (_currentRoot is not null && !PathsEqual(_currentRoot, fullPath))
        {
            _backPaths.Push(_currentRoot);
            _forwardPaths.Clear();
            UpdateNavigationState();
        }
        _addressHistory.Add(fullPath);
        AddressText = fullPath;

        // Shell 名前空間はワークスペースの物理パスではないため、既存のワークスペース
        // 切替経路へ渡さず FolderTree 内で表示ルートだけを切り替える。ライブラリ内の
        // 子項目も同じ経路に通す。
        if (FolderTreeShellNamespaces.IsShellPath(fullPath))
        {
            _suppressRootSelection = true;
            try { SelectedRootOption = RootOptions.FirstOrDefault(o => PathsEqual(o.FullPath, fullPath)); }
            finally { _suppressRootSelection = false; }
            SetDisplayRoot(fullPath);
            RootStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

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

    /// <summary>FolderTree 内の表示ルート履歴を戻る。外部ワークスペースへの移動は、
    /// アプリ全体のワークスペース切替を壊さないため履歴へ戻す要求を出す。</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_backPaths.Count == 0 || _currentRoot is null)
            return;
        var target = _backPaths.Pop();
        _forwardPaths.Push(_currentRoot);
        NavigateHistoryTarget(target);
        UpdateNavigationState();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_forwardPaths.Count == 0 || _currentRoot is null)
            return;
        var target = _forwardPaths.Pop();
        _backPaths.Push(_currentRoot);
        NavigateHistoryTarget(target);
        UpdateNavigationState();
    }

    private void NavigateHistoryTarget(string target)
    {
        AddressError = string.Empty;
        AddressText = target;
        if (FolderTreeShellNamespaces.IsShellPath(target) || _workspace.Contains(target))
        {
            var option = RootOptions.FirstOrDefault(o => PathsEqual(o.FullPath, target));
            if (option is not null)
                SelectRootOption(option);
            else
            {
                _suppressRootSelection = true;
                try { SelectedRootOption = null; }
                finally { _suppressRootSelection = false; }
                SetDisplayRoot(target);
            }
            return;
        }

        AddressNavigationRequested?.Invoke(this, target);
    }

    private void UpdateNavigationState()
    {
        CanGoBack = _backPaths.Count > 0;
        CanGoForward = _forwardPaths.Count > 0;
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

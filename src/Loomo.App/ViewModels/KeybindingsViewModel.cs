using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定オーバーレイ「キーボード」カテゴリの ViewModel。
/// <see cref="KeybindingService"/> の現在状態を行の一覧として公開し、再割り当て・リセットを仲介する。
/// バインドが変わると（<see cref="KeybindingService.Changed"/>）一覧を組み直し、実効ジェスチャと
/// 競合表示を更新する。XAML 側は <see cref="Rows"/> をカテゴリでグループ化して表示する。</summary>
public sealed partial class KeybindingsViewModel : ObservableObject
{
    private readonly KeybindingService _service;

    public ObservableCollection<KeybindingRowViewModel> Rows { get; } = new();

    /// <summary>カテゴリでグループ化した表示用ビュー（XAML の GroupStyle ヘッダに使う）。</summary>
    public ICollectionView RowsView { get; }

    public KeybindingsViewModel(KeybindingService service)
    {
        _service = service;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(KeybindingRowViewModel.Category)));
        _service.Changed += Reload;
        Reload();
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var row in _service.Rows())
            Rows.Add(new KeybindingRowViewModel(_service, row));
    }

    /// <summary>すべてのショートカットを既定へ戻す（確認のうえ）。</summary>
    [RelayCommand]
    private void ResetAll()
    {
        if (Confirm("すべてのショートカットを既定に戻します。よろしいですか？"))
            _service.ResetAll();
    }

    /// <summary>破壊的・確認が要る操作の前にユーザーへ尋ねる。アプリ未起動（テスト等）では true。</summary>
    internal static bool Confirm(string message)
    {
        if (Application.Current is null) return true;
        return MessageBox.Show(message, "Loomo", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK;
    }
}

/// <summary>キーボード設定の 1 行（1 コマンド）。表示と、再割り当て／リセット／未割当化の操作を持つ。</summary>
public sealed partial class KeybindingRowViewModel : ObservableObject
{
    private readonly KeybindingService _service;
    private readonly KeybindingRow _row;

    public KeybindingRowViewModel(KeybindingService service, KeybindingRow row)
    {
        _service = service;
        _row = row;
    }

    public string Id => _row.Descriptor.Id;
    public string Category => _row.Descriptor.Category;
    public string Title => _row.Descriptor.Title;

    /// <summary>実効ジェスチャの表示（未割当なら「未割当」）。</summary>
    public string GestureText => _row.Effective?.Format() ?? "未割当";

    /// <summary>ユーザーが既定から変更しているか（「既定に戻す」を出す）。</summary>
    public bool IsCustom => _row.IsCustom;

    /// <summary>同じジェスチャを持つ別コマンドがあるか。</summary>
    public bool HasConflict => _row.ConflictId is not null;

    public string ConflictText => _row.ConflictId is { } id
        ? $"⚠ 「{CommandCatalog.Find(id)?.Title ?? id}」と重複"
        : "";

    /// <summary>キャプチャ待機中か（KeyCaptureBox の表示切替に使う）。</summary>
    [ObservableProperty] private bool _isCapturing;

    [RelayCommand] private void BeginCapture() => IsCapturing = true;

    [RelayCommand] private void CancelCapture() => IsCapturing = false;

    /// <summary>このコマンドを既定の割り当てへ戻す。</summary>
    [RelayCommand] private void Reset() => _service.Reset(Id);

    /// <summary>このコマンドを未割当にする。</summary>
    [RelayCommand] private void Clear() => _service.Rebind(Id, null);

    /// <summary>キャプチャした新ジェスチャを適用する。競合があれば確認する。</summary>
    public void ApplyCapture(KeySequence? sequence)
    {
        IsCapturing = false;
        if (sequence is null) return;

        if (_service.CommandAt(sequence, Id) is { } other)
        {
            var name = CommandCatalog.Find(other)?.Title ?? other;
            if (!KeybindingsViewModel.Confirm(
                    $"このジェスチャは「{name}」に割り当て済みです。重複して割り当てますか？"))
                return;
        }

        _service.Rebind(Id, sequence);
    }
}

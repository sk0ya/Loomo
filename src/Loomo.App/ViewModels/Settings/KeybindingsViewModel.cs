using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ショートカット一覧の絞り込み観点（設定画面のチップで切り替える）。</summary>
public enum KeybindingScope
{
    /// <summary>すべてのコマンド。</summary>
    All,
    /// <summary>既定から変更したコマンドだけ。</summary>
    Customized,
    /// <summary>キーが割り当たっていないコマンドだけ。</summary>
    Unassigned,
    /// <summary>同じキーが重複している／連鎖に隠れているコマンドだけ（＝要対処）。</summary>
    Problem,
}

/// <summary>キーボードショートカット画面（設定とは別の独立ウィンドウ）の ViewModel。
/// <see cref="KeybindingService"/> の現在状態を行の一覧として公開し、再割り当て・リセットを仲介する。
/// バインドが変わると（<see cref="KeybindingService.Changed"/>）一覧を組み直し、実効ジェスチャと
/// 競合表示を更新する。コマンドは 100 件近くあるので、素の一覧では目的の 1 行に辿り着けない——
/// まず大分類（<see cref="SelectedArea"/>：エディタ・ターミナル…）で分け、その中を小分類で区切って見せる。
/// さらに <see cref="Query"/>（名前・Id・キー表記の AND 検索）と <see cref="Scope"/>（変更済み／未割当／要対処）
/// で絞り込める。大分類ごとの件数は絞り込み後の数なので、検索のヒットがどの分類にあるかが左ナビで見える。</summary>
public sealed partial class KeybindingsViewModel : ObservableObject
{
    private readonly KeybindingService _service;

    public ObservableCollection<KeybindingRowViewModel> Rows { get; } = new();

    /// <summary>小分類でグループ化し、大分類・<see cref="Query"/>／<see cref="Scope"/> で絞り込んだ表示用ビュー。</summary>
    public ICollectionView RowsView { get; }

    /// <summary>左ナビの大分類。先頭は「すべて」。</summary>
    public IReadOnlyList<KeybindingAreaViewModel> Areas { get; }

    public KeybindingsViewModel(KeybindingService service)
    {
        _service = service;
        Areas = new[] { new KeybindingAreaViewModel(null) }
            .Concat(CommandCatalog.Areas.Select(area => new KeybindingAreaViewModel(area)))
            .ToArray();
        _selectedArea = Areas[0];
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = item => item is KeybindingRowViewModel row && Matches(row);
        ApplyGrouping();
        _service.Changed += Reload;
        Reload();
    }

    /// <summary>左ナビで選んでいる大分類（「すべて」は <see cref="KeybindingAreaViewModel.Area"/> が null）。</summary>
    [ObservableProperty] private KeybindingAreaViewModel _selectedArea;

    partial void OnSelectedAreaChanged(KeybindingAreaViewModel value)
    {
        ApplyGrouping();
        RefreshView();
    }

    /// <summary>「すべて」では大分類をまたいで並ぶので見出しに大分類も添え、分類を選んでいるときは小分類だけにする。</summary>
    private void ApplyGrouping()
    {
        RowsView.GroupDescriptions.Clear();
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(SelectedArea.Area is null
            ? nameof(KeybindingRowViewModel.AreaAndCategory)
            : nameof(KeybindingRowViewModel.Category)));
    }

    /// <summary>絞り込み文字列。カテゴリ・表示名・コマンド Id・現在のキー表記に対する空白区切りの
    /// AND 検索。「Ctrl+W」と打てばそのキーの行だけが残る＝キーからの逆引きも兼ねる。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    private string _query = "";

    /// <summary>絞り込み観点（チップ）。</summary>
    [ObservableProperty] private KeybindingScope _scope = KeybindingScope.All;

    partial void OnQueryChanged(string value) => RefreshView();

    partial void OnScopeChanged(KeybindingScope value) => RefreshView();

    public bool HasQuery => Query.Length > 0;

    // ===== 件数（見出し右の表示とチップのバッジ） =====

    public int TotalCount => Rows.Count;
    public int MatchCount => Rows.Count(Matches);
    public int CustomizedCount => Rows.Count(r => r.IsCustom);
    public int UnassignedCount => Rows.Count(r => r.IsUnassigned);
    public int ProblemCount => Rows.Count(r => r.HasProblem);

    /// <summary>見出し右の件数（絞り込み中は「41 件中 7 件」）。</summary>
    public string CountText => MatchCount == TotalCount ? $"{TotalCount} 件" : $"{TotalCount} 件中 {MatchCount} 件";

    /// <summary>絞り込み結果が 0 件か（空状態の案内を出す）。</summary>
    public bool HasNoMatch => MatchCount == 0;

    public string AllChipText => $"すべて {TotalCount}";
    public string CustomizedChipText => $"変更済み {CustomizedCount}";
    public string UnassignedChipText => $"未割当 {UnassignedCount}";
    public string ProblemChipText => $"要対処 {ProblemCount}";

    /// <summary>絞り込みをすべて解除する（空状態からの復帰）。</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        Query = "";
        Scope = KeybindingScope.All;
    }

    /// <summary>検索文字列だけを消す（検索ボックスの ✕）。</summary>
    [RelayCommand]
    private void ClearQuery() => Query = "";

    /// <summary>すべてのショートカットを既定へ戻す（確認のうえ）。変更が無ければ実行できない。</summary>
    [RelayCommand(CanExecute = nameof(CanResetAll))]
    private void ResetAll()
    {
        if (Confirm($"変更した {CustomizedCount} 件のショートカットをすべて既定に戻します。よろしいですか？"))
            _service.ResetAll();
    }

    private bool CanResetAll() => CustomizedCount > 0;

    /// <summary>そのジェスチャを持つ行だけを一覧に残す。競合警告から呼び、当事者どちらの行も
    /// 同時に見える状態にして直せるようにする。</summary>
    internal void RevealGesture(string gestureText)
    {
        // 当事者は別の大分類にいることが多い（例: ペイン操作とエディタ）ので「すべて」へ戻す。
        SelectedArea = Areas[0];
        Scope = KeybindingScope.All;
        Query = gestureText;
    }

    private void Reload()
    {
        Rows.Clear();
        // 大分類 → 小分類の表示順で並べる（同じ小分類の中はカタログ順のまま＝OrderBy は安定）。
        foreach (var row in _service.Rows().OrderBy(r => DisplayOrder(r.Descriptor)))
            Rows.Add(new KeybindingRowViewModel(this, _service, row));
        MarkShadowedRows();

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CustomizedCount));
        OnPropertyChanged(nameof(UnassignedCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(AllChipText));
        OnPropertyChanged(nameof(CustomizedChipText));
        OnPropertyChanged(nameof(UnassignedChipText));
        OnPropertyChanged(nameof(ProblemChipText));
        ResetAllCommand.NotifyCanExecuteChanged();
        RefreshView();
    }

    /// <summary>連鎖の 1 打目（プレフィックス）と同じ単独ジェスチャに印を付ける。
    /// <see cref="KeyboardResolver"/> はプレフィックスを先に見るので、そのコマンドは**決して発火しない**——
    /// 競合表に出ない静かな死にバインドなので、ここで見つけて行に警告を出す。</summary>
    private void MarkShadowedRows()
    {
        var prefixes = new HashSet<KeyChord>(
            Rows.Where(r => r.Sequence is { Count: 2 }).Select(r => r.Sequence!.First));
        if (prefixes.Count == 0) return;

        foreach (var row in Rows)
            if (row.Sequence is { Count: 1 } single && prefixes.Contains(single.First))
                row.MarkShadowed();
    }

    /// <summary>大分類と小分類の並び（<see cref="CommandCatalog.Areas"/> の順）を 1 つの整数にする。</summary>
    private static int DisplayOrder(CommandDescriptor descriptor)
    {
        var area = descriptor.Area;
        var areaIndex = CommandCatalog.Areas.ToList().IndexOf(area);
        if (areaIndex < 0) areaIndex = CommandCatalog.Areas.Count;
        var categoryIndex = area.Categories.ToList().IndexOf(descriptor.Category);
        return areaIndex * 1000 + Math.Max(categoryIndex, 0);
    }

    private void RefreshView()
    {
        foreach (var area in Areas)
            area.Count = Rows.Count(row => MatchesFilters(row) && area.Contains(row));
        RowsView.Refresh();
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasNoMatch));
    }

    /// <summary>その行を現在の絞り込み条件で表示すべきか。</summary>
    private bool Matches(KeybindingRowViewModel row)
        => SelectedArea.Contains(row) && MatchesFilters(row);

    /// <summary>大分類を除いた絞り込み条件（観点チップと検索）に合うか。左ナビの件数はこちらで数える。</summary>
    private bool MatchesFilters(KeybindingRowViewModel row)
    {
        if (!ScopeMatches(row)) return false;
        if (Query.Length == 0) return true;

        foreach (var term in Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!row.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private bool ScopeMatches(KeybindingRowViewModel row) => Scope switch
    {
        KeybindingScope.Customized => row.IsCustom,
        KeybindingScope.Unassigned => row.IsUnassigned,
        KeybindingScope.Problem => row.HasProblem,
        _ => true,
    };

    /// <summary>破壊的・確認が要る操作の前にユーザーへ尋ねる。アプリ未起動（テスト等）では true。</summary>
    internal static bool Confirm(string message)
    {
        if (Application.Current is null) return true;
        return MessageBox.Show(message, "Loomo", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK;
    }
}

/// <summary>左ナビの大分類 1 項目。件数は絞り込み（観点チップ・検索）を通した数。</summary>
public sealed partial class KeybindingAreaViewModel : ObservableObject
{
    public KeybindingAreaViewModel(CommandArea? area) => Area = area;

    /// <summary>大分類（null は「すべて」）。</summary>
    public CommandArea? Area { get; }

    public string Name => Area?.Name ?? "すべて";
    public string Icon => Area?.Icon ?? "⌨";

    [ObservableProperty] private int _count;

    internal bool Contains(KeybindingRowViewModel row) => Area is null || row.Area == Area.Name;
}

/// <summary>キーボード設定の 1 行（1 コマンド）。表示と、再割り当て／リセット／未割当化の操作を持つ。</summary>
public sealed partial class KeybindingRowViewModel : ObservableObject
{
    private readonly KeybindingsViewModel _owner;
    private readonly KeybindingService _service;
    private readonly KeybindingRow _row;

    public KeybindingRowViewModel(KeybindingsViewModel owner, KeybindingService service, KeybindingRow row)
    {
        _owner = owner;
        _service = service;
        _row = row;
        SearchText = $"{Area} {Category} {Title} {Id} {GestureText}";
    }

    /// <summary>絞り込み照合用に連結した検索対象（カテゴリ・表示名・Id・現在のキー表記）。</summary>
    internal string SearchText { get; }

    /// <summary>実効ジェスチャ（未割当なら null）。連鎖の影判定に使う。</summary>
    internal KeySequence? Sequence => _row.Effective;

    public string Id => _row.Descriptor.Id;
    public string Category => _row.Descriptor.Category;

    /// <summary>大分類の名前。</summary>
    public string Area => _row.Descriptor.Area.Name;

    /// <summary>「すべて」表示での見出し（大分類 › 小分類）。</summary>
    public string AreaAndCategory => $"{Area} › {Category}";
    public string Title => _row.Descriptor.Title;

    /// <summary>実効ジェスチャの表示（未割当なら「未割当」）。</summary>
    public string GestureText => _row.Effective?.Format() ?? "未割当";

    /// <summary>キーが割り当たっていないか（表示を淡くし、「未割当にする」を無効化する）。</summary>
    public bool IsUnassigned => _row.Effective is null;

    /// <summary>ユーザーが既定から変更しているか（「既定に戻す」を出す）。</summary>
    public bool IsCustom => _row.IsCustom;

    /// <summary>同じジェスチャを持つ別コマンドがあるか。</summary>
    public bool HasConflict => _row.ConflictId is not null;

    /// <summary>そのままでは意図どおり動かない行（重複、または連鎖プレフィックスに隠れた単独キー）。</summary>
    public bool HasProblem => HasConflict || IsShadowed;

    public string ConflictText => _row.ConflictId is { } id
        ? $"⚠ 「{CommandCatalog.Find(id)?.Title ?? id}」と重複"
        : "";

    /// <summary>連鎖の 1 打目に隠れて発火しない単独ジェスチャか（<see cref="KeybindingsViewModel"/> が判定）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyPropertyChangedFor(nameof(WarningText))]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private bool _isShadowed;

    internal void MarkShadowed() => IsShadowed = true;

    /// <summary>行に出す 1 行警告（重複 → 影、の優先順）。無ければ空。</summary>
    public string WarningText => HasConflict
        ? ConflictText
        : IsShadowed ? $"⚠ {GestureText} は連鎖の 1 打目なので、単独では実行されません" : "";

    public bool HasWarning => WarningText.Length > 0;

    /// <summary>既定と違うときだけ出す補足（何に戻るのかが見えるように）。</summary>
    public string DefaultHintText => IsCustom ? $"既定: {DefaultText}" : "";

    /// <summary>ツールチップ：コマンド Id と既定キー。設定ファイルを直接いじるときの手掛かりにもなる。</summary>
    public string DetailText => $"{Id}\n既定: {DefaultText}";

    private string DefaultText => KeySequence.TryParse(_row.Descriptor.DefaultBinding)?.Format() ?? "未割当";

    /// <summary>キャプチャ待機中か（KeyCaptureBox の表示切替に使う）。</summary>
    [ObservableProperty] private bool _isCapturing;

    [RelayCommand] private void BeginCapture() => IsCapturing = true;

    [RelayCommand] private void CancelCapture() => IsCapturing = false;

    /// <summary>このコマンドを既定の割り当てへ戻す。</summary>
    [RelayCommand] private void Reset() => _service.Reset(Id);

    /// <summary>このコマンドを未割当にする。</summary>
    [RelayCommand] private void Clear() => _service.Rebind(Id, null);

    /// <summary>競合しているジェスチャで一覧を絞り込み、当事者を並べて見せる。</summary>
    [RelayCommand] private void RevealConflict() => _owner.RevealGesture(GestureText);

    /// <summary>キャプチャした新ジェスチャを適用する。競合があれば確認する。</summary>
    public void ApplyCapture(KeySequence? sequence)
    {
        IsCapturing = false;
        if (sequence is null) return;
        if (sequence.Equals(_row.Effective)) return;    // 同じキーの押し直し：何もしない

        if (_service.CommandAt(sequence, Id) is { } other)
        {
            var name = CommandCatalog.Find(other)?.Title ?? other;
            if (!KeybindingsViewModel.Confirm(
                    $"{sequence.Format()} は「{name}」に割り当て済みです。重複して割り当てますか？"
                    + "\n（重複したままだと、どちらか一方しか実行されません）"))
                return;
        }

        _service.Rebind(Id, sequence);
    }
}

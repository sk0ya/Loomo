using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「自分のテーマ」編集画面。上の切り替えで種類（アプリの画面・エディタ・ターミナル・Markdown プレビュー）を
/// 選び、その種類の自分のテーマを作る・直す。左に保存済みのテーマ、中央にその種類の色の欄、右にその種類の見本。
/// 編集は下書き（<see cref="CustomThemeDraft"/>）に溜め、保存したときだけファイルへ書く（保存すると
/// <see cref="UserThemes"/> の見張りで、そのテーマを使っている所も塗り直る）。</summary>
public sealed partial class UserThemeEditorViewModel : ObservableObject, IDisposable
{
    private readonly AppearanceViewModel _appearance;
    private readonly Action _onCatalogChanged;
    private CustomThemeDraft _draft;
    private bool _loading;

    public sealed record KindTab(CustomThemeKind Kind, string Title);

    /// <summary>上の切り替え（外観パネルの 4 つの選択欄と同じ並び）。</summary>
    public IReadOnlyList<KindTab> Kinds { get; } =
        ThemeSchemas.All.Select(s => new KindTab(s.Kind, s.Title)).ToArray();

    public ObservableCollection<AppearanceViewModel.PresetSwatch> Items { get; } = new();
    public ObservableCollection<ColorSlotViewModel> Slots { get; } = new();

    [ObservableProperty] private KindTab _selectedKind;
    [ObservableProperty] private IReadOnlyList<AppearanceViewModel.PresetSwatch> _baseChoices = [];
    [ObservableProperty] private AppearanceViewModel.PresetSwatch? _selectedItem;
    [ObservableProperty] private AppearanceViewModel.PresetSwatch? _selectedBase;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasTheme;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _readabilitySummary = "";
    [ObservableProperty] private bool _hasReadabilityIssues;

    /// <summary>いまの下書きから決めた色（見本が塗る色）。</summary>
    [ObservableProperty] private CustomTheme? _preview;

    public CustomThemeKind Kind => SelectedKind.Kind;
    public string KindTitle => ThemeSchemas.For(Kind).Title;
    public string EmptyMessage =>
        $"まだ{KindTitle}の自分のテーマがありません。左下で「もとにするテーマ」を選んで「新しく作る」を押すと、その色を写したところから始められます。";

    public UserThemeEditorViewModel(AppearanceViewModel appearance, CustomThemeKind kind)
    {
        _appearance = appearance;
        _selectedKind = Kinds.First(k => k.Kind == kind);
        _draft = new CustomThemeDraft(kind);
        _onCatalogChanged = () => Application.Current?.Dispatcher.BeginInvoke(OnCatalogChanged);
        UserThemes.Changed += _onCatalogChanged;
        EnterKind();
    }

    public void Dispose() => UserThemes.Changed -= _onCatalogChanged;

    /// <summary>種類を切り替えた（または開いた）とき：欄・一覧・もとにする候補を入れ替え、その欄でいま使っている
    /// 自分のテーマがあればそれを、無ければ先頭を開く。</summary>
    private void EnterKind()
    {
        var schema = ThemeSchemas.For(Kind);
        foreach (var slot in Slots) slot.Edited -= OnSlotEdited;
        Slots.Clear();
        foreach (var info in schema.Slots)
        {
            var slot = new ColorSlotViewModel(info, schema.GroupDescriptions.GetValueOrDefault(info.Group, ""));
            slot.Edited += OnSlotEdited;
            Slots.Add(slot);
        }

        BaseChoices = _appearance.BuiltInChoices(Kind);
        var selectedKey = _appearance.SelectedKey(Kind);
        SelectedBase = BaseChoices.FirstOrDefault(c => c.Key == selectedKey) ?? BaseChoices.FirstOrDefault();

        _draft = new CustomThemeDraft(Kind);
        IsDirty = false;
        RefreshItems();
        var current = UserThemes.IsUserKey(selectedKey) ? selectedKey![CustomTheme.KeyPrefix.Length..] : null;
        _loading = true;
        SelectedItem = Items.FirstOrDefault(i => string.Equals(i.Key, current, StringComparison.OrdinalIgnoreCase)) ?? Items.FirstOrDefault();
        _loading = false;
        HasTheme = SelectedItem is not null;
        if (SelectedItem is { } item) LoadFromFile(item.Key);
        else Recompute();
        OnPropertyChanged(nameof(KindTitle));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnSelectedKindChanged(KindTab? oldValue, KindTab newValue)
    {
        if (_loading || oldValue is null || oldValue.Kind == newValue.Kind) return;
        if (IsDirty && !ConfirmLeave())
        {
            Application.Current?.Dispatcher.BeginInvoke(() => { _loading = true; SelectedKind = oldValue; _loading = false; });
            return;
        }
        EnterKind();
    }

    private void RefreshItems()
    {
        var keep = SelectedItem?.Key;
        var wasLoading = _loading;
        _loading = true;
        Items.Clear();
        foreach (var t in UserThemes.All(Kind).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new AppearanceViewModel.PresetSwatch(t.Id, t.Name,
                AppearanceViewModel.Chip(t).Bg, AppearanceViewModel.Chip(t).Accent, AppearanceViewModel.Chip(t).Fg));
        SelectedItem = Items.FirstOrDefault(i => i.Key == keep);
        _loading = wasLoading;
        HasTheme = SelectedItem is not null;
    }

    /// <summary>フォルダーが変わったとき（自分の保存も含む）。一覧を並べ直し、手を入れていなければ開いている
    /// テーマもファイルから読み直す（JSON を直接書き換えた場合に画面を追従させる）。</summary>
    private void OnCatalogChanged()
    {
        RefreshItems();
        if (SelectedItem is { } item && !IsDirty) LoadFromFile(item.Key);
    }

    partial void OnSelectedItemChanged(AppearanceViewModel.PresetSwatch? oldValue, AppearanceViewModel.PresetSwatch? newValue)
    {
        if (_loading) return;
        if (IsDirty && oldValue is not null && !ConfirmLeave())
        {
            Application.Current?.Dispatcher.BeginInvoke(() => { _loading = true; SelectedItem = oldValue; _loading = false; });
            return;
        }
        HasTheme = newValue is not null;
        if (newValue is not null) LoadFromFile(newValue.Key);
    }

    /// <summary>保存していない変更があるとき、離れてよいか聞く。はい＝保存して進む、いいえ＝捨てて進む。</summary>
    public bool ConfirmLeave()
    {
        if (!IsDirty) return true;
        var answer = MessageBox.Show($"「{Name}」の変更を保存しますか？", "自分のテーマ",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveCore();
        IsDirty = false;
        return true;
    }

    private string? CurrentId => SelectedItem?.Key;
    private string PathOf(string id) => Path.Combine(UserThemes.FolderFor(Kind), id + ".json");

    private void LoadFromFile(string id)
    {
        try
        {
            LoadDraft(CustomThemeDraft.FromJson(Kind, File.ReadAllText(PathOf(id))));
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            Status = $"読めませんでした: {ex.Message}";
        }
    }

    private void LoadDraft(CustomThemeDraft draft)
    {
        _loading = true;
        _draft = draft;
        Name = draft.Name;
        foreach (var slot in Slots) slot.Load(draft.Values.GetValueOrDefault(slot.Key));
        _loading = false;
        IsDirty = false;
        Recompute();
    }

    partial void OnNameChanged(string value)
    {
        if (_loading) return;
        _draft.Name = string.IsNullOrWhiteSpace(value) ? "自分のテーマ" : value.Trim();
        IsDirty = true;
    }

    private void OnSlotEdited(ColorSlotViewModel slot)
    {
        if (_loading) return;
        _draft.Values[slot.Key] = slot.IsAuto ? null : slot.Color;
        IsDirty = true;
        Recompute();
    }

    /// <summary>下書きから色を決め直して、自動の欄の見本・見本の塗り・注意を更新する。</summary>
    private void Recompute()
    {
        CustomTheme theme;
        try { theme = _draft.Derive(); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { Status = ex.Message; return; }

        var issues = Check(theme).ToDictionary(i => i.Key, i => i.Message);
        foreach (var slot in Slots)
        {
            slot.ShowEffective(theme[slot.Key]);
            slot.Warning = issues.GetValueOrDefault(slot.Key);
        }
        Preview = theme;

        HasReadabilityIssues = issues.Count > 0;
        ReadabilitySummary = issues.Count == 0
            ? "読みやすさ：気になるところはありません"
            : $"読みやすさ：{issues.Count} 色が背景に埋もれ気味（⚠ にカーソルで詳細）";
    }

    /// <summary>読みやすさの注意（欄の鍵と理由）。各欄の <see cref="ThemeSlot.MinContrast"/>（背景に対する WCAG の
    /// コントラスト比の目安：本文 4.5・補足 3.0・色分けや印 2.0）を切った欄だけ言う。</summary>
    internal static IEnumerable<(string Key, string Message)> Check(CustomTheme t)
    {
        var bg = t["background"];
        foreach (var slot in t.Schema.Slots)
        {
            if (slot.MinContrast is not { } min) continue;
            var c = ThemeManager.Contrast(bg, t[slot.Key]);
            if (c < min) yield return (slot.Key, $"「{slot.Label}」が背景に埋もれ気味（コントラスト {c:0.0}・目安 {min:0.0} 以上）");
        }
    }

    /// <summary>ピッカーの「テーマの色から」に並べる色（その欄以外）。</summary>
    public IEnumerable<Views.ColorPicker.PaletteChip> PaletteFor(ColorSlotViewModel except) =>
        Slots.Where(s => s != except).Select(s => new Views.ColorPicker.PaletteChip(s.Label, s.Color))
            .Concat(new[]
            {
                new Views.ColorPicker.PaletteChip("白", Colors.White),
                new Views.ColorPicker.PaletteChip("黒", Colors.Black),
            });

    // ===== 操作 =====

    [RelayCommand]
    private void NewTheme()
    {
        if (!ConfirmLeave()) return;
        var baseChoice = SelectedBase ?? BaseChoices.First();
        var draft = new CustomThemeDraft(Kind) { Name = $"{baseChoice.Name} をもとに" };
        foreach (var (k, v) in AppearanceViewModel.SeedFromBuiltIn(Kind, baseChoice.Key)) draft.Values[k] = v;
        CreateFile(draft, $"「{draft.Name}」を作りました");
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (CurrentId is null || !ConfirmLeave()) return;
        var copy = _draft.Clone();
        copy.Name = $"{_draft.Name} のコピー";
        CreateFile(copy, $"「{copy.Name}」を作りました");
    }

    private void CreateFile(CustomThemeDraft draft, string message)
    {
        try
        {
            var folder = UserThemes.FolderFor(Kind);
            Directory.CreateDirectory(folder);
            var id = "my-theme";
            for (var i = 2; File.Exists(Path.Combine(folder, id + ".json")); i++) id = $"my-theme-{i}";
            File.WriteAllText(PathOf(id), draft.ToJson(), new UTF8Encoding(false));
            UserThemes.Reload();
            IsDirty = false;
            RefreshItems();
            SelectedItem = Items.FirstOrDefault(i => i.Key == id);
            Status = message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"作れませんでした: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Save() => SaveCore();

    private bool SaveCore()
    {
        if (CurrentId is not { } id) return false;
        try
        {
            File.WriteAllText(PathOf(id), _draft.ToJson(), new UTF8Encoding(false));
            IsDirty = false;
            UserThemes.Reload();
            RefreshItems();
            Status = "保存しました";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
            return false;
        }
    }

    /// <summary>保存して、外観パネルのこの種類の欄でこのテーマを選ぶ。</summary>
    [RelayCommand]
    private void UseInRoom()
    {
        if (CurrentId is not { } id || IsDirty && !SaveCore()) return;
        _appearance.UseUserTheme(Kind, id);
        Status = $"「{Name}」を{KindTitle}に使いました";
    }

    [RelayCommand]
    private void Revert()
    {
        if (CurrentId is { } id) LoadFromFile(id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (CurrentId is not { } id) return;
        if (MessageBox.Show($"「{Name}」を削除しますか？", "自分のテーマ",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            File.Delete(PathOf(id));
            IsDirty = false;
            UserThemes.Reload();
            RefreshItems();
            SelectedItem = Items.FirstOrDefault();
            HasTheme = SelectedItem is not null;
            Status = "削除しました";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"削除できませんでした: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenFolder() => _appearance.OpenUserThemesFolderCommand.Execute(null);

    /// <summary>JSON を直接開く。</summary>
    [RelayCommand]
    private void OpenJson()
    {
        if (CurrentId is { } id && (!IsDirty || SaveCore())) _appearance.RequestOpenFile(PathOf(id));
    }
}

/// <summary>色の欄 1 つ。<see cref="IsAuto"/> の間は決まった色（<see cref="ShowEffective"/>）を見本に出す。</summary>
public sealed partial class ColorSlotViewModel : ObservableObject
{
    private bool _quiet;

    public ColorSlotViewModel(ThemeSlot info, string groupDescription)
    {
        Info = info;
        GroupDescription = groupDescription;
    }

    public ThemeSlot Info { get; }
    public string Key => Info.Key;
    public string Label => Info.Label;
    public string Group => Info.Group;
    public string Usage => Info.Usage;
    public string Hint => Info.Hint;
    public string GroupDescription { get; }
    public bool CanBeAuto => !Info.Required;

    /// <summary>書き換えられた（ピッカー・16 進・自動の切替）。</summary>
    public event Action<ColorSlotViewModel>? Edited;

    [ObservableProperty] private Color _color = Colors.Gray;
    [ObservableProperty] private bool _isAuto;
    [ObservableProperty] private string _hex = "";

    /// <summary>読みやすさの注意（無ければ null）。色見本の横に ⚠ で出し、理由はツールチップ。</summary>
    [ObservableProperty] private string? _warning;

    public Brush Brush => new SolidColorBrush(Color);

    /// <summary>自動に戻す（ピッカーの「自動に戻す」）。</summary>
    public void ResetToAuto()
    {
        if (CanBeAuto) IsAuto = true;
    }

    internal void Load(Color? value)
    {
        _quiet = true;
        IsAuto = value is null && CanBeAuto;
        if (value is { } c) Color = c;
        _quiet = false;
    }

    /// <summary>決まった色を見本に出す（自動の欄だけ。手で決めた欄は自分の色のまま）。</summary>
    internal void ShowEffective(Color effective)
    {
        if (!IsAuto && Hex.Length > 0) return;
        _quiet = true;
        Color = effective;
        _quiet = false;
    }

    partial void OnColorChanged(Color value)
    {
        OnPropertyChanged(nameof(Brush));
        var hex = CustomThemeFormat.Hex(value);
        if (Hex != hex) { var q = _quiet; _quiet = true; Hex = hex; _quiet = q; }
        if (_quiet) return;
        _quiet = true;
        IsAuto = false;   // 色を触ったら自動を外す
        _quiet = false;
        Edited?.Invoke(this);
    }

    partial void OnHexChanged(string value)
    {
        if (_quiet) return;
        if (CustomThemeFormat.TryParse(value, out var c) && c != Color) Color = c;
    }

    partial void OnIsAutoChanged(bool value)
    {
        if (_quiet) return;
        if (value && !CanBeAuto) { IsAuto = false; return; }
        Edited?.Invoke(this);
    }
}

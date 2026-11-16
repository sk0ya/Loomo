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
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「自分のテーマ」編集画面。左に保存済みのテーマ、中央に基準色の欄（色見本・16 進・自動）、
/// 右にその場で塗り直すプレビューと読みやすさの注意。編集は下書き（<see cref="UserThemeDraft"/>）に溜め、
/// 保存したときだけファイルへ書く（保存すると <see cref="UserThemes"/> の見張りで部屋も塗り直る）。</summary>
public sealed partial class UserThemeEditorViewModel : ObservableObject, IDisposable
{
    private readonly AppearanceViewModel _appearance;
    private readonly Action _onCatalogChanged;
    private UserThemeDraft _draft = new();
    private bool _loading;

    public ObservableCollection<AppearanceViewModel.PresetSwatch> Items { get; } = new();
    public ObservableCollection<ColorSlotViewModel> Slots { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>「新しく作る」の元にできる同梱テーマ。</summary>
    public IReadOnlyList<AppearanceViewModel.PresetSwatch> BaseChoices { get; }

    [ObservableProperty] private AppearanceViewModel.PresetSwatch? _selectedItem;
    [ObservableProperty] private AppearanceViewModel.PresetSwatch? _selectedBase;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasTheme;
    [ObservableProperty] private bool _hasOverrides;
    [ObservableProperty] private string _status = "";

    /// <summary>いまの下書きから派生させたテーマ（プレビューが塗る色）。</summary>
    [ObservableProperty] private UserTheme? _preview;

    public UserThemeEditorViewModel(AppearanceViewModel appearance)
    {
        _appearance = appearance;
        BaseChoices = appearance.BuiltInThemeChoices;
        _selectedBase = BaseChoices.FirstOrDefault(c => c.Key == appearance.SelectedTheme?.Key) ?? BaseChoices.FirstOrDefault();

        foreach (var info in UserThemeDraft.SlotInfos)
        {
            var slot = new ColorSlotViewModel(info);
            slot.Edited += OnSlotEdited;
            Slots.Add(slot);
        }

        _onCatalogChanged = () => Application.Current?.Dispatcher.BeginInvoke(OnCatalogChanged);
        UserThemes.Changed += _onCatalogChanged;

        RefreshItems();
        // いま使っている自分のテーマがあればそれを、無ければ先頭を開く。
        var current = appearance.SelectedTheme?.Key is { } key && UserThemes.IsUserKey(key) ? key[UserTheme.KeyPrefix.Length..] : null;
        SelectedItem = Items.FirstOrDefault(i => string.Equals(i.Key, current, StringComparison.OrdinalIgnoreCase)) ?? Items.FirstOrDefault();
        if (SelectedItem is null) Recompute();
    }

    public void Dispose() => UserThemes.Changed -= _onCatalogChanged;

    private void RefreshItems()
    {
        var keep = SelectedItem?.Key;
        _loading = true;
        Items.Clear();
        foreach (var t in UserThemes.All)
            Items.Add(new AppearanceViewModel.PresetSwatch(t.Id, t.Name, UserTheme.Hex(t.Bg), UserTheme.Hex(t.Accent), UserTheme.Hex(t.Fg)));
        SelectedItem = Items.FirstOrDefault(i => i.Key == keep);
        _loading = false;
        HasTheme = SelectedItem is not null;
    }

    /// <summary>フォルダーが変わったとき（自分の保存も含む）。一覧を並べ直し、手を入れていなければ開いている
    /// テーマもファイルから読み直す（JSON を直接書き換えた場合に画面を追従させる）。</summary>
    private void OnCatalogChanged()
    {
        RefreshItems();
        if (SelectedItem is { } item && !IsDirty) LoadFromFile(item.Key);
        else if (SelectedItem is null && !IsDirty) { HasTheme = false; }
    }

    partial void OnSelectedItemChanged(AppearanceViewModel.PresetSwatch? oldValue, AppearanceViewModel.PresetSwatch? newValue)
    {
        if (_loading) return;
        if (IsDirty && oldValue is not null && !ConfirmLeave())
        {
            // 選択を元へ戻す（コンボの選択変更の最中に戻すと効かないので後で）。
            Application.Current?.Dispatcher.BeginInvoke(() => { _loading = true; SelectedItem = oldValue; _loading = false; });
            return;
        }
        HasTheme = newValue is not null;
        if (newValue is not null) LoadFromFile(newValue.Key);
    }

    /// <summary>保存していない変更があるとき、離れてよいか聞く。保存＝保存して進む、破棄＝捨てて進む。</summary>
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
    private static string PathOf(string id) => Path.Combine(UserThemes.Folder, id + ".json");

    private void LoadFromFile(string id)
    {
        try
        {
            LoadDraft(UserThemeDraft.FromJson(File.ReadAllText(PathOf(id))));
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status = $"読めませんでした: {ex.Message}";
        }
    }

    private void LoadDraft(UserThemeDraft draft)
    {
        _loading = true;
        _draft = draft;
        Name = draft.Name;
        foreach (var slot in Slots) slot.Load(draft.Slots[slot.Key]);
        HasOverrides = draft.HasOverrides;
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
        _draft.Slots[slot.Key] = slot.IsAuto ? null : slot.Color;
        IsDirty = true;
        Recompute();
    }

    /// <summary>下書きから派生し直して、自動の欄の見本・プレビュー・注意を更新する。</summary>
    private void Recompute()
    {
        UserTheme theme;
        try { theme = _draft.Derive(); }
        catch (FormatException ex) { Status = ex.Message; return; }

        foreach (var slot in Slots) slot.ShowEffective(UserThemeDraft.Effective(theme, slot.Key));
        Preview = theme;

        Warnings.Clear();
        foreach (var w in Check(theme)) Warnings.Add(w);
    }

    /// <summary>読みやすさの注意。数字は WCAG のコントラスト比（本文は 4.5、補足や色の印は 3.0 が目安）。</summary>
    internal static IEnumerable<string> Check(UserTheme t)
    {
        static double C(Color a, Color b) => ThemeManager.Contrast(a, b);
        var body = C(t.Bg, t.Fg);
        if (body < 4.5) yield return $"文字が背景に対して読みにくい（{body:0.0}・目安 4.5 以上）";
        var dim = C(t.Bg, t.Dim);
        if (dim < 3.0) yield return $"控えめな文字が背景に埋もれる（{dim:0.0}・目安 3.0 以上）";
        if (C(t.Bg, t.Accent) < 1.6) yield return $"アクセントが背景と見分けにくい（{C(t.Bg, t.Accent):0.0}）";
        foreach (var (label, color) in new[]
                 {
                     ("キーワード", t.Keyword), ("文字列", t.String), ("数値", t.Number), ("型", t.Type), ("関数", t.Function),
                     ("赤", t.Red), ("黄", t.Yellow), ("緑", t.Green),
                 })
        {
            var c = C(t.Bg, color);
            if (c < 3.0) yield return $"「{label}」が背景に埋もれる（{c:0.0}・目安 3.0 以上）";
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
        var name = $"{baseChoice.Name} をもとに";
        string json;
        if (Enum.TryParse<AppTheme>(baseChoice.Key, out var builtIn))
            json = AppearanceViewModel.SeedFromBuiltIn(builtIn, name);
        else
            json = new UserThemeDraft { Name = name }.ToJson();
        var draft = UserThemeDraft.FromJson(json);
        draft.Name = name;
        CreateFile(draft, $"「{name}」を作りました");
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (CurrentId is null || !ConfirmLeave()) return;
        var copy = _draft.Clone();
        copy.Name = $"{_draft.Name} のコピー";
        CreateFile(copy, $"「{copy.Name}」を作りました");
    }

    private void CreateFile(UserThemeDraft draft, string message)
    {
        try
        {
            Directory.CreateDirectory(UserThemes.Folder);
            var id = UserThemeDraft.NextFreeId(UserThemes.Folder);
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

    /// <summary>保存して、アプリ全体・エディタ・ターミナル・プレビューのすべてでこのテーマを選ぶ。</summary>
    [RelayCommand]
    private void UseInRoom()
    {
        if (CurrentId is not { } id || IsDirty && !SaveCore()) return;
        _appearance.UseUserTheme(id);
        Status = $"「{Name}」を部屋全体に当てました";
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
        if (MessageBox.Show($"「{Name}」を削除しますか？（{id}.json を消します）", "自分のテーマ",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            File.Delete(PathOf(id));
            IsDirty = false;
            UserThemes.Reload();
            RefreshItems();
            SelectedItem = Items.FirstOrDefault();
            if (SelectedItem is null) HasTheme = false;
            Status = "削除しました";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"削除できませんでした: {ex.Message}";
        }
    }

    /// <summary>JSON を直接開く（ui / ansi の直接指定など、画面に出していない項目を書きたいとき）。</summary>
    [RelayCommand]
    private void OpenJson()
    {
        if (CurrentId is { } id && (!IsDirty || SaveCore())) _appearance.RequestOpenFile(PathOf(id));
    }
}

/// <summary>基準色の欄 1 つ。<see cref="IsAuto"/> の間は派生した色（<see cref="ShowEffective"/>）を見本に出す。</summary>
public sealed partial class ColorSlotViewModel : ObservableObject
{
    private bool _quiet;

    public ColorSlotViewModel(UserThemeDraft.SlotInfo info) => Info = info;

    public UserThemeDraft.SlotInfo Info { get; }
    public string Key => Info.Key;
    public string Label => Info.Label;
    public string Group => Info.Group;
    public string Hint => Info.Hint;
    public bool CanBeAuto => !Info.Required;

    /// <summary>書き換えられた（ピッカー・16 進・自動の切替）。</summary>
    public event Action<ColorSlotViewModel>? Edited;

    [ObservableProperty] private Color _color = Colors.Gray;
    [ObservableProperty] private bool _isAuto;
    [ObservableProperty] private string _hex = "";

    public Brush Brush => new SolidColorBrush(Color);

    internal void Load(Color? value)
    {
        _quiet = true;
        IsAuto = value is null && CanBeAuto;
        if (value is { } c) Color = c;
        _quiet = false;
    }

    /// <summary>派生後の実際の色を見本に出す（自動の欄だけ。手で決めた欄は自分の色のまま）。</summary>
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
        var hex = UserTheme.Hex(value);
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
        if (UserThemeParser.TryParse(value, out var c) && c != Color) Color = c;
    }

    partial void OnIsAutoChanged(bool value)
    {
        if (_quiet) return;
        if (value && !CanBeAuto) { IsAuto = false; return; }
        Edited?.Invoke(this);
    }
}

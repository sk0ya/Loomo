using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>自分のテーマ（<see cref="UserThemes"/>）を外観パネルの 4 つの選択欄へ並べる部分。テーマは種類ごと
/// （アプリの画面・エディタ・ターミナル・Markdown プレビュー）に別々に作り、作った種類の欄にだけ「★ 名前」で並ぶ。
/// themes フォルダーへ保存し直すたびに並べ直して当て直す。</summary>
public sealed partial class AppearanceViewModel
{
    private IReadOnlyList<PresetSwatch> _builtInThemes = [];
    private IReadOnlyList<PresetSwatch> _builtInEditorThemes = [];
    private IReadOnlyList<PresetSwatch> _builtInPreviewThemes = [];
    private IReadOnlyList<PresetSwatch> _builtInTerminalThemes = [];

    /// <summary>ファイルをエディタで開いてほしいとき（編集画面の「JSON を開く」）。</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>読めなかったテーマファイルと理由（無ければ空）。</summary>
    [ObservableProperty] private string _userThemeErrors = "";

    public string UserThemesFolder => UserThemes.Folder;

    private void InitUserThemes()
    {
        _builtInThemes = Themes;
        _builtInEditorThemes = EditorThemes;
        _builtInPreviewThemes = PreviewThemes;
        _builtInTerminalThemes = TerminalThemes;
        ComposeChoices();
        UserThemes.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RefreshUserThemes);
    }

    /// <summary>同梱の選択肢（自分のテーマを含まない）。編集画面の「もとにするテーマ」に使う。</summary>
    public IReadOnlyList<PresetSwatch> BuiltInChoices(CustomThemeKind kind) => kind switch
    {
        CustomThemeKind.App => _builtInThemes,
        CustomThemeKind.Editor => _builtInEditorThemes,
        CustomThemeKind.Terminal => _builtInTerminalThemes,
        _ => _builtInPreviewThemes,
    };

    /// <summary>自分のテーマの色見本（背景・アクセント相当・文字）。</summary>
    internal static PresetSwatch Chip(CustomTheme t) => new(t.Key, $"★ {t.Name}",
        t.Hex("background"), CustomThemeFormat.Hex(SwatchAccent(t)), t.Hex("foreground"));

    private static Color SwatchAccent(CustomTheme t) => t.Kind switch
    {
        CustomThemeKind.App => t["accent"],
        CustomThemeKind.Editor => t["keyword"],
        CustomThemeKind.Terminal => t["blue"],
        _ => t["link"],
    };

    /// <summary>同梱の選択肢＋その種類の自分のテーマで 4 つの一覧を組み直す。同梱の項目は同じインスタンスを
    /// 使い回すので、選択中の同梱テーマはコンボボックスの選択が外れない。</summary>
    private void ComposeChoices()
    {
        PresetSwatch[] Chips(CustomThemeKind kind) => UserThemes.All(kind).Select(Chip).ToArray();
        Themes = _builtInThemes.Concat(Chips(CustomThemeKind.App)).ToArray();
        EditorThemes = _builtInEditorThemes.Concat(Chips(CustomThemeKind.Editor)).ToArray();
        TerminalThemes = _builtInTerminalThemes.Concat(Chips(CustomThemeKind.Terminal)).ToArray();
        PreviewThemes = _builtInPreviewThemes.Concat(Chips(CustomThemeKind.Preview)).ToArray();
        var problems = UserThemes.Errors.Concat(MissingUserThemes()).ToArray();
        UserThemeErrors = problems.Length == 0 ? "" : "読めなかったテーマ：\n" + string.Join("\n", problems);
    }

    /// <summary>欄で選んでいる自分のテーマが見つからないとき（消した・名前を変えた・前の形のまま）、その欄は既定の
    /// 配色（一覧の先頭／アプリ全体は同梱のテーマ）で塗られる。黙って色が変わらないよう理由を並べる。</summary>
    private IEnumerable<string> MissingUserThemes()
    {
        var ap = _settings.Appearance;
        var saved = new (CustomThemeKind Kind, string? Key)[]
        {
            (CustomThemeKind.App, _settings.UserTheme is { } id ? CustomTheme.KeyPrefix + id : null),
            (CustomThemeKind.Editor, ap.EditorTheme),
            (CustomThemeKind.Terminal, ap.TerminalTheme),
            (CustomThemeKind.Preview, ap.MarkdownPreviewTheme),
        };
        foreach (var (kind, key) in saved)
        {
            if (!UserThemes.IsUserKey(key) || UserThemes.TryGet(kind, key) is not null) continue;
            var schema = ThemeSchemas.For(kind);
            var name = key!.Trim()[CustomTheme.KeyPrefix.Length..];
            yield return $"{schema.Title}で選んでいた「{name}」が {schema.Folder}/ に見つかりません（いまは既定の配色です）";
        }
    }

    /// <summary>いまのアプリ全体テーマに当たる選択肢。自分のテーマが消えていたら同梱の <see cref="LoomoSettings.Theme"/>。</summary>
    private PresetSwatch CurrentThemeChoice() =>
        (_settings.UserTheme is { } id
            ? Themes.FirstOrDefault(c => string.Equals(c.Key, CustomTheme.KeyPrefix + id, StringComparison.OrdinalIgnoreCase))
            : null)
        ?? Match(Themes, _settings.Theme.ToString(), 0);

    /// <summary>テーマファイルが保存し直された・増えた・消えたとき：一覧を並べ直し、使っている自分のテーマを
    /// 当て直し、開いているエディタ・ターミナル・プレビューも塗り直す。</summary>
    private void RefreshUserThemes()
    {
        ComposeChoices();
        OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(EditorThemes));
        OnPropertyChanged(nameof(PreviewThemes));
        OnPropertyChanged(nameof(TerminalThemes));

        // 選択は背面フィールドへ直接戻す（setter を通すと保存・再適用が走る）。一覧の差し替えで
        // コンボボックスが選択を null にしてくることがあるので、保存値から引き直す。
        var ap = _settings.Appearance;
        _selectedTheme = CurrentThemeChoice();
        _selectedEditorTheme = Match(EditorThemes, ap.EditorTheme, 0);
        _selectedPreviewTheme = Match(PreviewThemes, ap.MarkdownPreviewTheme, 0);
        _selectedTerminalTheme = Match(TerminalThemes, ap.TerminalTheme, 0);
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(SelectedEditorTheme));
        OnPropertyChanged(nameof(SelectedPreviewTheme));
        OnPropertyChanged(nameof(SelectedTerminalTheme));

        if (_settings.UserTheme is { } id)
        {
            if (UserThemes.TryGet(CustomThemeKind.App, CustomTheme.KeyPrefix + id) is { } user) _themeManager.ApplyUserTheme(user);
            else _themeManager.ApplyTheme(_settings.Theme);   // 消した・壊したときは直前の同梱テーマで見せる（設定は残す）
        }
        Status = UserThemes.Errors.Count == 0 ? "テーマを読み直しました" : "読めなかったテーマがあります";
        AppearanceChanged?.Invoke();
    }

    /// <summary>自分のテーマをその種類の欄で選ぶ（編集画面の「保存して使う」）。</summary>
    public void UseUserTheme(CustomThemeKind kind, string id)
    {
        UserThemes.Reload();
        RefreshUserThemes();
        var key = CustomTheme.KeyPrefix + id;
        PresetSwatch? Find(IReadOnlyList<PresetSwatch> list) =>
            list.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        switch (kind)
        {
            case CustomThemeKind.App when Find(Themes) is { } c: SelectedTheme = c; break;
            case CustomThemeKind.Editor when Find(EditorThemes) is { } c: SelectedEditorTheme = c; break;
            case CustomThemeKind.Terminal when Find(TerminalThemes) is { } c: SelectedTerminalTheme = c; break;
            case CustomThemeKind.Preview when Find(PreviewThemes) is { } c: SelectedPreviewTheme = c; break;
        }
    }

    /// <summary>いまその種類の欄で選んでいる選択肢の鍵（編集画面を開いたとき、どれを開くか・何をもとにするかに使う）。</summary>
    public string? SelectedKey(CustomThemeKind kind) => kind switch
    {
        CustomThemeKind.App => SelectedTheme?.Key,
        CustomThemeKind.Editor => SelectedEditorTheme?.Key,
        CustomThemeKind.Terminal => SelectedTerminalTheme?.Key,
        _ => SelectedPreviewTheme?.Key,
    };

    /// <summary>ファイルを部屋のエディタで開く（編集画面の「JSON を開く」）。</summary>
    public void RequestOpenFile(string path) => OpenFileRequested?.Invoke(path);

    [RelayCommand]
    private void OpenUserThemesFolder()
    {
        try
        {
            foreach (var schema in ThemeSchemas.All) Directory.CreateDirectory(UserThemes.FolderFor(schema.Kind));
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{UserThemes.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = $"フォルダーを開けませんでした: {ex.Message}";
        }
    }

    /// <summary>同梱の配色を写した下書きの色（「〜をもとに新しく作る」）。種類ごとに、その欄が実際に使っている配色
    /// から取る（エディタなら同名のエディタ配色、ターミナルなら同名のターミナル配色…）。</summary>
    internal static Dictionary<string, Color> SeedFromBuiltIn(CustomThemeKind kind, string key)
    {
        static Color Of(Brush brush) => brush is SolidColorBrush b ? b.Color : Colors.Magenta;
        static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);
        switch (kind)
        {
            case CustomThemeKind.App:
            {
                var theme = Enum.TryParse<AppTheme>(key, out var t) ? t : AppTheme.Dark;
                // 絶対 pack URI で引く（Application の無いテストからも同じ関数を呼べるように）。
                var palette = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/sk0ya.Loomo.App;component/Themes/Palette.{theme}.xaml", UriKind.Absolute)
                };
                Color Ui(string k) => palette[k] is SolidColorBrush b ? Opaque(b.Color) : Colors.Magenta;
                return new()
                {
                    ["background"] = Ui("Bg"), ["foreground"] = Ui("Fg"), ["dim"] = Ui("FgDim"),
                    ["surface"] = Ui("BgAlt"), ["activityBar"] = Ui("ActivityBarBg"), ["border"] = Ui("Border"),
                    ["accent"] = Ui("Accent"), ["selection"] = Ui("SelectionBg"), ["searchHighlight"] = Ui("SearchHighlight"),
                    ["success"] = Ui("Success"), ["error"] = Ui("Error"),
                    ["symType"] = Ui("SymType"), ["symMethod"] = Ui("SymMethod"), ["symProperty"] = Ui("SymProperty"),
                    ["symField"] = Ui("SymField"), ["symConstant"] = Ui("SymConstant"), ["symInterface"] = Ui("SymInterface"),
                    ["symEnum"] = Ui("SymEnum"), ["symNamespace"] = Ui("SymNamespace"),
                };
            }
            case CustomThemeKind.Editor:
            {
                var e = ShellAppearanceCoordinator.ResolveEditorTheme(key);
                return new()
                {
                    ["background"] = Of(e.Background), ["foreground"] = Of(e.Foreground), ["comment"] = Of(e.TokenComment),
                    ["accent"] = Of(e.InsertCursor), ["keyword"] = Of(e.TokenKeyword), ["string"] = Of(e.TokenString),
                    ["number"] = Of(e.TokenNumber), ["type"] = Of(e.TokenType), ["function"] = Of(e.TokenFunction),
                    ["attribute"] = Of(e.TokenAttribute), ["error"] = Of(e.DiagnosticError),
                    ["warning"] = Of(e.DiagnosticWarning), ["added"] = Of(e.GitAdded),
                };
            }
            case CustomThemeKind.Terminal:
            {
                var t = ShellAppearanceCoordinator.BuildTerminalColorTheme(key);
                var ansi = ThemeSchemas.AnsiKeys;
                var values = new Dictionary<string, Color>
                {
                    ["background"] = t.Background, ["foreground"] = t.Foreground, ["cursor"] = t.Cursor,
                    ["selection"] = Opaque(t.SelectionBackground),
                };
                for (var i = 0; i < ansi.Count; i++) values[ansi[i]] = t.AnsiPalette[i];
                return values;
            }
            default:
            {
                var c = MarkdownPage.BuiltInPreviewColors(key).Select(CustomThemeFormat.Parse).ToArray();
                return new()
                {
                    ["background"] = c[0], ["foreground"] = c[1], ["codeBackground"] = c[2], ["border"] = c[3],
                    ["link"] = c[4], ["heading"] = c[5], ["strong"] = c[6], ["dim"] = c[7], ["code"] = c[8],
                };
            }
        }
    }
}

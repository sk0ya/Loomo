using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>自分のテーマ（<see cref="UserThemes"/>）を外観パネルの 4 つの選択肢へ並べる部分。
/// 同梱の選択肢の後ろに「★ 名前」で足し、themes フォルダーへ保存し直すたびに並べ直して当て直す。</summary>
public sealed partial class AppearanceViewModel
{
    private IReadOnlyList<PresetSwatch> _builtInThemes = [];
    private IReadOnlyList<PresetSwatch> _builtInEditorThemes = [];
    private IReadOnlyList<PresetSwatch> _builtInPreviewThemes = [];
    private IReadOnlyList<PresetSwatch> _builtInTerminalThemes = [];

    /// <summary>ファイルをエディタで開いてほしいとき（作ったテーマをその場で書き換えられるように）。</summary>
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

    /// <summary>同梱の選択肢＋自分のテーマで 4 つの一覧を組み直す。同梱の項目は同じインスタンスを使い回すので、
    /// 選択中の同梱テーマはコンボボックスの選択が外れない。</summary>
    private void ComposeChoices()
    {
        var chips = UserThemes.All
            .Select(t => new PresetSwatch(t.Key, $"★ {t.Name}", UserTheme.Hex(t.Bg), UserTheme.Hex(t.Accent), UserTheme.Hex(t.Fg)))
            .ToArray();
        Themes = _builtInThemes.Concat(chips).ToArray();
        EditorThemes = _builtInEditorThemes.Concat(chips).ToArray();
        PreviewThemes = _builtInPreviewThemes.Concat(chips).ToArray();
        TerminalThemes = _builtInTerminalThemes.Concat(chips).ToArray();
        UserThemeErrors = UserThemes.Errors.Count == 0 ? "" : "読めなかったテーマ：\n" + string.Join("\n", UserThemes.Errors);
    }

    /// <summary>いまのアプリ全体テーマに当たる選択肢。自分のテーマが消えていたら同梱の <see cref="LoomoSettings.Theme"/>。</summary>
    private PresetSwatch CurrentThemeChoice() =>
        (_settings.UserTheme is { } id
            ? Themes.FirstOrDefault(c => string.Equals(c.Key, UserTheme.KeyPrefix + id, StringComparison.OrdinalIgnoreCase))
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
            if (UserThemes.TryGet(UserTheme.KeyPrefix + id) is { } user) _themeManager.ApplyUserTheme(user);
            else _themeManager.ApplyTheme(_settings.Theme);   // 消した・壊したときは直前の同梱テーマで見せる（設定は残す）
        }
        Status = UserThemes.Errors.Count == 0 ? "テーマを読み直しました" : "読めなかったテーマがあります";
        AppearanceChanged?.Invoke();
    }

    /// <summary>いまの配色をもとに自分のテーマを 1 つ作り、4 つの選択肢すべてで選んで、エディタで開く。
    /// 以後はそのファイルを書き換えて保存するだけで色が変わる。</summary>
    [RelayCommand]
    private void CreateUserTheme()
    {
        try
        {
            Directory.CreateDirectory(UserThemes.Folder);
            var id = "my-theme";
            for (var i = 2; File.Exists(Path.Combine(UserThemes.Folder, id + ".json")); i++) id = $"my-theme-{i}";
            var path = Path.Combine(UserThemes.Folder, id + ".json");

            var current = _settings.UserTheme is { } userId ? UserThemes.TryGet(UserTheme.KeyPrefix + userId) : null;
            var baseName = current?.Name ?? SelectedTheme?.Name ?? _settings.Theme.ToString();
            var name = $"{baseName} をもとに";
            var json = current is not null
                ? Regex.Replace(File.ReadAllText(current.Path), "\"name\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"",
                    "\"name\": " + Quote(name).Replace("$", "$$"))
                : SeedFromBuiltIn(_settings.Theme, name);
            File.WriteAllText(path, json, new UTF8Encoding(false));

            UserThemes.Reload();
            RefreshUserThemes();
            var key = UserTheme.KeyPrefix + id;
            if (Themes.FirstOrDefault(c => c.Key == key) is { } chip)
            {
                SelectedTheme = chip;
                SelectedEditorTheme = EditorThemes.First(c => c.Key == key);
                SelectedTerminalTheme = TerminalThemes.First(c => c.Key == key);
                SelectedPreviewTheme = PreviewThemes.First(c => c.Key == key);
            }
            OpenFileRequested?.Invoke(path);
            Status = $"{Path.GetFileName(path)} を作りました。書き換えて保存すると、その場で色が変わります";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"テーマを作れませんでした: {ex.Message}";
        }
    }

    /// <summary>JSON の文字列にする（日本語をエスケープしない＝書き換える人が読めるように）。</summary>
    private static string Quote(string text) => JsonSerializer.Serialize(text,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [RelayCommand]
    private void OpenUserThemesFolder()
    {
        try
        {
            Directory.CreateDirectory(UserThemes.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{UserThemes.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = $"フォルダーを開けませんでした: {ex.Message}";
        }
    }

    /// <summary>同梱テーマの色から、書き換えやすい形のテーマファイルを作る。地と文字・アクセントは UI の
    /// パレット、6 色は同じ名前のターミナル配色、構文の色は同じ名前のエディタ配色から取る（どれも同じ名前で
    /// 揃っているので、選んでいたテーマの見た目に近いところから始まる）。</summary>
    internal static string SeedFromBuiltIn(AppTheme theme, string name)
    {
        // 絶対 pack URI で引く（Application の無いテストからも同じ関数を呼べるように）。
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/sk0ya.Loomo.App;component/Themes/Palette.{theme}.xaml", UriKind.Absolute)
        };
        Color Ui(string key) => palette[key] is SolidColorBrush b ? b.Color : Colors.Magenta;
        var editor = ShellAppearanceCoordinator.ResolveEditorTheme(theme.ToString());
        static Color Of(Brush brush) => brush is SolidColorBrush b ? b.Color : Colors.Magenta;
        var ansi = ShellAppearanceCoordinator.BuildTerminalColorTheme(theme.ToString()).AnsiPalette;
        string H(Color c) => "\"" + UserTheme.Hex(c) + "\"";

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // 自分のテーマ。保存すると、その場で Loomo の色が変わります。");
        sb.AppendLine("  // 必須は background と foreground だけ。書かない色は明暗に合った既定で埋まります。");
        sb.AppendLine("  // ここから UI・エディタ・ターミナル・プレビューの配色をまとめて作ります。");
        sb.AppendLine($"  \"name\": {Quote(name)},");
        sb.AppendLine();
        sb.AppendLine($"  \"background\": {H(Ui("Bg"))},");
        sb.AppendLine($"  \"foreground\": {H(Ui("Fg"))},");
        sb.AppendLine($"  \"dim\": {H(Ui("FgDim"))},          // 補足の文字・コメント・行番号");
        sb.AppendLine($"  \"accent\": {H(Ui("Accent"))},       // 選択中の印・ボタン・カーソル");
        sb.AppendLine();
        sb.AppendLine("  // 6 色：エラー・警告・成功などの印と、ターミナルの 16 色の元");
        sb.AppendLine($"  \"red\": {H(ansi[1])},");
        sb.AppendLine($"  \"yellow\": {H(ansi[3])},");
        sb.AppendLine($"  \"green\": {H(ansi[2])},");
        sb.AppendLine($"  \"cyan\": {H(ansi[6])},");
        sb.AppendLine($"  \"blue\": {H(ansi[4])},");
        sb.AppendLine($"  \"magenta\": {H(ansi[5])},");
        sb.AppendLine();
        sb.AppendLine("  // 構文の色（書かなければ magenta / green / yellow / cyan / blue から取ります）");
        sb.AppendLine($"  \"keyword\": {H(Of(editor.TokenKeyword))},");
        sb.AppendLine($"  \"string\": {H(Of(editor.TokenString))},");
        sb.AppendLine($"  \"number\": {H(Of(editor.TokenNumber))},");
        sb.AppendLine($"  \"type\": {H(Of(editor.TokenType))},");
        sb.AppendLine($"  \"function\": {H(Of(editor.TokenFunction))},");
        sb.AppendLine();
        sb.AppendLine("  // さらに詰めたいとき：アプリ UI のパレットを直接上書きします（行頭の // を外す）。");
        sb.AppendLine("  // 書けるキー：" + string.Join(" ", UserThemeParser.PaletteKeys));
        sb.AppendLine("  \"ui\": {");
        foreach (var key in new[] { "BgAlt", "ActivityBarBg", "Border", "SelectionBg" })
            sb.AppendLine($"    // \"{key}\": {H(Ui(key))},");
        sb.AppendLine("  },");
        sb.AppendLine();
        sb.AppendLine("  // ターミナルの 16 色を直接決めたいとき（黒 赤 緑 黄 青 紫 水 白 → 明るい側の 8 色）");
        sb.AppendLine("  // \"ansi\": [" + string.Join(", ", ansi.Select(H)) + "],");
        sb.AppendLine("}");
        return sb.ToString();
    }
}

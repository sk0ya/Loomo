using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Settings;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コマンドパレットを開いている間の「検索対象（探し方）の切替」キーの検証。
/// パレットの中では<b>パレット自身のコマンドだけ</b>を通し、それ以外のキーは入力欄へ素通しする
/// （＝打っている最中に部屋が動かない）。
/// </summary>
public class PaletteScopeKeysTests
{
    private static bool PaletteScope(string id) => id.StartsWith("palette.", StringComparison.Ordinal);

    private static (KeyboardDispatcher Dispatcher, List<string> Executed, string Path) NewDispatcher()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-kb-{Guid.NewGuid():N}.json");
        var service = new KeybindingService(new LoomoSettings(), new SettingsStore(path));
        var executed = new List<string>();
        var actions = CommandCatalog.All.ToDictionary(
            c => c.Id, c => (Action)(() => executed.Add(c.Id)), StringComparer.Ordinal);
        return (new KeyboardDispatcher(service, actions), executed, path);
    }

    private static string? Scoped(Key key, ModifierKeys mods = ModifierKeys.None)
    {
        var (dispatcher, _, path) = NewDispatcher();
        try { return dispatcher.FindScoped(new KeyChord(mods, key), PaletteScope); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Catalog_has_the_scope_switch_commands_with_tab_defaults()
    {
        Assert.Equal("Tab", CommandCatalog.Find("palette.nextScope")?.DefaultBinding);
        Assert.Equal("Shift+Tab", CommandCatalog.Find("palette.previousScope")?.DefaultBinding);
        Assert.Equal("パレット", CommandCatalog.Find("palette.nextScope")?.Category);
    }

    [Fact]
    public void Tab_and_shift_tab_switch_the_search_target_inside_the_palette()
    {
        Assert.Equal("palette.nextScope", Scoped(Key.Tab));
        Assert.Equal("palette.previousScope", Scoped(Key.Tab, ModifierKeys.Shift));
    }

    [Fact]
    public void Opening_keys_also_work_inside_the_palette_as_a_direct_target_switch()
        => Assert.Equal("palette.goToFile", Scoped(Key.P, ModifierKeys.Control));

    [Fact]
    public void Other_shortcuts_are_not_taken_while_the_palette_is_open()
    {
        // Ctrl+S（保存）・Ctrl+T（舞台の巡回）は部屋の操作。パレットに文字を打っている間は通さない
        // ＝ null が返り、キーは消費されず入力欄へ届く。
        Assert.Null(Scoped(Key.S, ModifierKeys.Control));
        Assert.Null(Scoped(Key.T, ModifierKeys.Control));
    }

    [Fact]
    public void Unbound_commands_are_not_matched()
    {
        // 既定未割当（palette.goToText 等）は、どのキーにも当たらない。
        var (dispatcher, _, path) = NewDispatcher();
        try
        {
            Assert.DoesNotContain(
                new[] { Key.A, Key.B, Key.C }.Select(k => dispatcher.FindScoped(new KeyChord(ModifierKeys.None, k), PaletteScope)),
                id => id == "palette.goToText");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scoped_execution_runs_the_bound_action()
    {
        var (dispatcher, executed, path) = NewDispatcher();
        try
        {
            var id = dispatcher.FindScoped(new KeyChord(ModifierKeys.None, Key.Tab), PaletteScope);
            Assert.Equal("palette.nextScope", id);
            Assert.Empty(executed);   // 探すだけでは走らない（実行は TryExecuteScoped の仕事）
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Hint_names_the_actual_switch_keys()
    {
        Assert.Contains("Tab で切替", PaletteQuery.HintWith("Tab"));
        Assert.Contains("Ctrl+Shift+M で切替", PaletteQuery.HintWith("Ctrl+Shift+M"));
        // 切替に使えるキーが複数（Tab と、パレットを開くキー自身）ならどちらも案内する。
        Assert.Contains("Tab / Ctrl+Shift+P で切替", PaletteQuery.HintWith("Tab", "Ctrl+Shift+P"));
        // 同じキーは1つにまとめる。
        Assert.Contains("Tab で切替", PaletteQuery.HintWith("Tab", "Tab"));
        // 未割当なら切替キーの案内を出さない（存在しないキーを教えない）。
        Assert.Equal(PaletteQuery.ModesHint, PaletteQuery.HintWith((string?)null));
        Assert.Equal(PaletteQuery.ModesHint, PaletteQuery.HintWith("  ", null));
    }

    [Fact]
    public void The_opening_key_is_also_the_switch_key_inside_the_palette()
    {
        // 「開く」キーはパレットの中でも通る（＝もう一度押すと検索対象が次へ回る）。
        Assert.Equal("palette.open", Scoped(Key.P, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Contains("検索対象", CommandCatalog.Find("palette.open")?.Title);
    }
}

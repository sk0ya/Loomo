using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>キーボード設定画面（<see cref="KeybindingsViewModel"/>）の絞り込み・件数・警告の検証。
/// コマンドは 40 件超あるので、この画面の価値はほぼ「探せること」——検索とチップが実装の要。</summary>
public class KeybindingsViewModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"loomo-kbvm-{Guid.NewGuid():N}.json");
    private readonly KeybindingService _service;
    private readonly KeybindingsViewModel _sut;

    public KeybindingsViewModelTests()
    {
        _service = new KeybindingService(new LoomoSettings(), new SettingsStore(_path));
        _sut = new KeybindingsViewModel(_service);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private KeybindingRowViewModel Row(string id) => _sut.Rows.Single(r => r.Id == id);

    /// <summary>絞り込み後に表示される行（ビューの実体を通す）。</summary>
    private string[] VisibleIds() => _sut.RowsView.Cast<KeybindingRowViewModel>().Select(r => r.Id).ToArray();

    // ===== 検索 =====

    [Fact]
    public void Query_matches_title()
    {
        _sut.Query = "ズーム";
        Assert.Equal(new[] { "pane.zoom" }, VisibleIds());
    }

    [Fact]
    public void Query_matches_command_id()
    {
        _sut.Query = "sidebar.git";
        Assert.Equal(new[] { "sidebar.git" }, VisibleIds());
    }

    /// <summary>キー表記でも引ける＝「このキー、何に割り当たってる？」の逆引き。</summary>
    [Fact]
    public void Query_matches_gesture_text()
    {
        _sut.Query = "F11";
        Assert.Equal(new[] { "pane.fullscreen" }, VisibleIds());
    }

    [Fact]
    public void Query_terms_are_anded()
    {
        _sut.Query = "ペイン 分割";
        var ids = VisibleIds();
        Assert.Contains("pane.split.vertical", ids);
        Assert.DoesNotContain("pane.zoom", ids);
    }

    [Fact]
    public void Query_is_case_insensitive()
    {
        _sut.Query = "ctrl+shift+p";
        Assert.Equal(new[] { "palette.open" }, VisibleIds());
    }

    [Fact]
    public void No_match_reports_empty_state_and_clearing_restores()
    {
        _sut.Query = "存在しないコマンド";
        Assert.True(_sut.HasNoMatch);
        Assert.Empty(VisibleIds());

        _sut.ClearFiltersCommand.Execute(null);
        Assert.False(_sut.HasNoMatch);
        Assert.Equal(_sut.TotalCount, _sut.MatchCount);
    }

    // ===== 観点チップ =====

    [Fact]
    public void Scope_unassigned_shows_only_unbound_commands()
    {
        _sut.Scope = KeybindingScope.Unassigned;
        Assert.NotEmpty(VisibleIds());
        Assert.All(_sut.RowsView.Cast<KeybindingRowViewModel>(), r => Assert.True(r.IsUnassigned));
    }

    [Fact]
    public void Scope_customized_follows_rebinding()
    {
        _sut.Scope = KeybindingScope.Customized;
        Assert.Empty(VisibleIds());

        _service.Rebind("tab.newTerminal", KeySequence.TryParse("Ctrl+Alt+N"));
        Assert.Equal(new[] { "tab.newTerminal" }, VisibleIds());
        Assert.Equal(1, _sut.CustomizedCount);
    }

    [Fact]
    public void Scope_problem_shows_conflicts()
    {
        _service.Rebind("pane.focus.right", KeySequence.TryParse("Ctrl+W H")); // pane.focus.left と重複
        _sut.Scope = KeybindingScope.Problem;

        var ids = VisibleIds();
        Assert.Contains("pane.focus.left", ids);
        Assert.Contains("pane.focus.right", ids);
        Assert.Equal(2, _sut.ProblemCount);
    }

    // ===== 連鎖に隠れた単独キー（競合表には出ない死にバインド） =====

    [Fact]
    public void Single_gesture_shadowed_by_chord_prefix_is_flagged()
    {
        _service.Rebind("sidebar.explorer", KeySequence.TryParse("Ctrl+W")); // Ctrl+W は連鎖の 1 打目

        var row = Row("sidebar.explorer");
        Assert.True(row.IsShadowed);
        Assert.True(row.HasProblem);
        Assert.False(row.HasConflict);      // 同じジェスチャの相手はいない
        Assert.Contains("単独では実行されません", row.WarningText);
    }

    [Fact]
    public void Ordinary_single_gesture_is_not_shadowed()
        => Assert.False(Row("pane.fullscreen").IsShadowed);   // F11 は誰のプレフィックスでもない

    // ===== 警告からの導線 =====

    [Fact]
    public void Reveal_conflict_filters_to_that_gesture()
    {
        _service.Rebind("pane.focus.right", KeySequence.TryParse("Ctrl+W H"));
        _sut.Scope = KeybindingScope.Unassigned;

        Row("pane.focus.right").RevealConflictCommand.Execute(null);

        Assert.Equal(KeybindingScope.All, _sut.Scope);        // 観点も戻さないと相手が見えない
        var ids = VisibleIds();
        Assert.Contains("pane.focus.left", ids);
        Assert.Contains("pane.focus.right", ids);
    }

    // ===== 件数表示・行の操作 =====

    [Fact]
    public void CountText_shows_filtered_ratio()
    {
        Assert.Equal($"{_sut.TotalCount} 件", _sut.CountText);
        _sut.Query = "ズーム";
        Assert.Equal($"{_sut.TotalCount} 件中 1 件", _sut.CountText);
    }

    [Fact]
    public void ResetAll_is_disabled_until_something_changed()
    {
        Assert.False(_sut.ResetAllCommand.CanExecute(null));
        _service.Rebind("pane.zoom", KeySequence.TryParse("Ctrl+Alt+Z"));
        Assert.True(_sut.ResetAllCommand.CanExecute(null));
    }

    [Fact]
    public void Clear_makes_row_unassigned()
    {
        Row("pane.zoom").ClearCommand.Execute(null);

        var row = Row("pane.zoom");
        Assert.True(row.IsUnassigned);
        Assert.Equal("未割当", row.GestureText);
        Assert.Equal("既定: Ctrl+W Z", row.DefaultHintText);   // 何に戻るのかが行に見える
    }

    [Fact]
    public void Recapturing_the_same_gesture_does_not_mark_it_custom()
    {
        Row("pane.zoom").ApplyCapture(KeySequence.TryParse("Ctrl+W Z"));
        Assert.False(Row("pane.zoom").IsCustom);
    }
}

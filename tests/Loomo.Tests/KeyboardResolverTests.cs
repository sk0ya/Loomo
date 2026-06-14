using System.Collections.Generic;
using System.Windows.Input;
using sk0ya.Loomo.App.Input;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>キーバインド状態機械（KeyboardResolver）の遷移検証。既定バインドで組み立てる。</summary>
public class KeyboardResolverTests
{
    private static KeyboardResolver NewResolver()
    {
        var effective = new Dictionary<string, KeySequence>();
        foreach (var d in CommandCatalog.All)
            if (KeySequence.TryParse(d.DefaultBinding) is { } seq)
                effective[d.Id] = seq;
        var resolver = new KeyboardResolver();
        resolver.SetBindings(effective);
        return resolver;
    }

    private static KeyChord C(Key key, ModifierKeys mods = ModifierKeys.None) => new(mods, key);

    [Fact]
    public void Single_gesture_executes()
    {
        var r = NewResolver();
        var res = r.Resolve(C(Key.P, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.True(res.Handled);
        Assert.Equal("palette.open", res.Execute);
    }

    [Fact]
    public void Prefix_then_direction_focuses()
    {
        var r = NewResolver();
        var first = r.Resolve(C(Key.W, ModifierKeys.Control));
        Assert.True(first.Handled);
        Assert.Null(first.Execute);
        Assert.NotNull(r.Pending);

        var second = r.Resolve(C(Key.H));
        Assert.Equal("pane.focus.left", second.Execute);
        Assert.Null(r.Pending);
    }

    [Fact]
    public void Prefix_tolerates_held_control_on_second_chord()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        var second = r.Resolve(C(Key.L, ModifierKeys.Control)); // Ctrl 押しっぱなし
        Assert.Equal("pane.focus.right", second.Execute);
    }

    [Fact]
    public void Prefix_then_shift_direction_enters_resize_mode()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        var res = r.Resolve(C(Key.H, ModifierKeys.Shift));
        Assert.Equal("pane.resize.left", res.Execute);
        Assert.Equal(CommandCatalog.ResizeMode, r.Mode);
    }

    [Fact]
    public void Resize_mode_repeats_on_bare_keys()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        r.Resolve(C(Key.H, ModifierKeys.Shift)); // resize モードへ

        var repeat = r.Resolve(C(Key.J)); // bare キーで反復
        Assert.True(repeat.Handled);
        Assert.Equal("pane.resize.down", repeat.Execute);
        Assert.Equal(CommandCatalog.ResizeMode, r.Mode); // モード維持
    }

    [Fact]
    public void Resize_mode_exits_on_escape()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        r.Resolve(C(Key.H, ModifierKeys.Shift));

        var esc = r.Resolve(C(Key.Escape));
        Assert.True(esc.Handled);
        Assert.Null(esc.Execute);
        Assert.Null(r.Mode);
    }

    [Fact]
    public void Resize_mode_passes_through_unrelated_key()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        r.Resolve(C(Key.H, ModifierKeys.Shift));

        var other = r.Resolve(C(Key.A));
        Assert.False(other.Handled);   // 素通し
        Assert.Null(r.Mode);           // モードは抜ける
    }

    [Fact]
    public void Prefix_then_unmatched_key_passes_through()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        var res = r.Resolve(C(Key.A));
        Assert.False(res.Handled);
        Assert.Null(res.Execute);
        Assert.Null(r.Pending);
    }

    [Fact]
    public void Prefix_re_arms_on_repeat()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        var again = r.Resolve(C(Key.W, ModifierKeys.Control));
        Assert.True(again.Handled);
        Assert.NotNull(r.Pending);
    }

    [Fact]
    public void Prefix_two_stroke_split_executes()
    {
        var r = NewResolver();
        r.Resolve(C(Key.W, ModifierKeys.Control));
        var res = r.Resolve(C(Key.V));
        Assert.Equal("pane.split.vertical", res.Execute);
    }

    [Fact]
    public void Rebinding_changes_dispatch()
    {
        var effective = new Dictionary<string, KeySequence>
        {
            ["pane.zoom"] = KeySequence.TryParse("Ctrl+Alt+Z")!,
        };
        var r = new KeyboardResolver();
        r.SetBindings(effective);

        var res = r.Resolve(C(Key.Z, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.Equal("pane.zoom", res.Execute);
    }
}

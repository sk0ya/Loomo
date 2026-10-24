using System;
using System.Windows.Input;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>「直前のペインへ」（§24.58）の戻り先の覚え方と既定キー。</summary>
public class PaneFocusHistoryTests
{
    private sealed class Element;

    [Fact]
    public void Previous_is_the_place_focus_came_from()
    {
        var history = new PaneFocusHistory<Element>();
        var editor = new Element();
        history.Record(PaneFocusTarget.Of(PaneKind.Editor), editor);
        history.Record(PaneFocusTarget.Of(PaneKind.Terminal), new Element());

        Assert.Equal(PaneFocusTarget.Of(PaneKind.Editor), history.Previous!.Value.Target);
        Assert.True(history.Previous.Value.Element.TryGetTarget(out var back));
        Assert.Same(editor, back);
    }

    [Fact]
    public void Moving_inside_the_same_place_keeps_the_previous_place()
    {
        var history = new PaneFocusHistory<Element>();
        history.Record(PaneFocusTarget.Of(PaneKind.Editor), new Element());
        history.Record(PaneFocusTarget.Sidebar, new Element());
        history.Record(PaneFocusTarget.Sidebar, new Element());   // ツリーの行どうし

        Assert.Equal(PaneFocusTarget.Of(PaneKind.Editor), history.Previous!.Value.Target);
    }

    [Fact]
    public void Going_back_and_pressing_again_returns_to_the_start()
    {
        var history = new PaneFocusHistory<Element>();
        history.Record(PaneFocusTarget.Of(PaneKind.Editor), new Element());
        history.Record(PaneFocusTarget.Of(PaneKind.Browser), new Element());
        history.Record(PaneFocusTarget.Of(PaneKind.Editor), new Element());   // 戻った

        Assert.Equal(PaneFocusTarget.Of(PaneKind.Browser), history.Previous!.Value.Target);
    }

    [Fact]
    public void Split_viewports_count_as_separate_places()
    {
        var history = new PaneFocusHistory<Element>();
        var left = Guid.NewGuid();
        history.Record(PaneFocusTarget.Viewport(PaneKind.Editor, left), new Element());
        history.Record(PaneFocusTarget.Viewport(PaneKind.Editor, Guid.NewGuid()), new Element());

        Assert.Equal(PaneFocusTarget.Viewport(PaneKind.Editor, left), history.Previous!.Value.Target);
    }

    [Fact]
    public void Nothing_to_return_to_before_a_second_place()
    {
        var history = new PaneFocusHistory<Element>();
        history.Record(PaneFocusTarget.Of(PaneKind.Editor), new Element());
        Assert.Null(history.Previous);
    }

    [Fact]
    public void Default_key_is_ctrl_w_then_w()
    {
        var effective = new System.Collections.Generic.Dictionary<string, KeySequence>();
        foreach (var d in CommandCatalog.All)
            if (KeySequence.TryParse(d.DefaultBinding) is { } seq)
                effective[d.Id] = seq;
        var resolver = new KeyboardResolver();
        resolver.SetBindings(effective);

        resolver.Resolve(new KeyChord(ModifierKeys.Control, Key.W));
        // Ctrl+W の打ち直しは「待ち続け」なので Ctrl+W Ctrl+W にはしない（KeyboardResolver の仕様）。
        Assert.Equal("pane.focus.previous", resolver.Resolve(new KeyChord(ModifierKeys.None, Key.W)).Execute);
    }
}

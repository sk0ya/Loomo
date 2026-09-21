namespace sk0ya.Loomo.App.Services;

/// <summary>アプリのブレークポイント状態をエディタのガター表示へ変換する。</summary>
internal static class DebugBreakpointMapper
{
    public static EditorBreakpoint ToEditorBreakpoint(BreakpointGlyphInfo info)
    {
        var glyph = info.IsLogpoint ? BreakpointGlyphKind.Logpoint
            : info.HasCondition ? BreakpointGlyphKind.Conditional
            : BreakpointGlyphKind.Normal;
        return new EditorBreakpoint(info.Line0, glyph, info.Enabled);
    }
}

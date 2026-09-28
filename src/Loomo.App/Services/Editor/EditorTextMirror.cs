using Editor.Controls;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 2つのエディタの本文を双方向に揃え続ける。WPF のコントロールは親を1つしか持てないので、同じ文書を
/// 2か所に出すときは「2つ目のエディタ＋本文の同期」になる（切り離し窓の複製・Diff の右側）。
/// キャレットとスクロールはそれぞれのまま——揃えるのは本文だけ。
///
/// <para>受け側へは<b>編集として</b>入れる（<see cref="ApplyAsEdit"/>）。<c>SetText</c> は読み込みの扱いで、
/// <c>BufferChanged</c> も LSP への変更通知も出さず Undo にも残らない——受け側のタブの未保存マーク・
/// LSP・プレビューが取り残され、差分の取り直しも走らない。</para>
///
/// <para>編集として入れるので受け側も「未保存」になる。片方を保存したら、本文が同じもう片方も保存済みに
/// 揃える（<see cref="EditorDocumentEvents.Saved"/>）——揃えないと、保存した後も複製側に未保存の印が残り、
/// 帯へ戻すと変わっていないファイルで「保存しますか」と聞く。</para>
/// </summary>
internal sealed class EditorTextMirror : IDisposable
{
    private readonly VimEditorControl _source;
    private readonly VimEditorControl _mirror;
    private readonly EditorDocumentEvents? _events;
    private bool _syncing;

    public EditorTextMirror(VimEditorControl source, VimEditorControl mirror, EditorDocumentEvents? events)
    {
        _source = source;
        _mirror = mirror;
        _events = events;
        _source.BufferChanged += OnSourceBufferChanged;
        _mirror.BufferChanged += OnMirrorBufferChanged;
        if (_events is not null) _events.Saved += OnSaved;
    }

    public VimEditorControl Source => _source;

    public void Dispose()
    {
        _source.BufferChanged -= OnSourceBufferChanged;
        _mirror.BufferChanged -= OnMirrorBufferChanged;
        if (_events is not null) _events.Saved -= OnSaved;
    }

    /// <summary>片方が保存された：本文が同じなら、もう片方も保存済みにする。</summary>
    public void OnSaved(VimEditorControl saved)
    {
        var other = ReferenceEquals(saved, _source) ? _mirror
            : ReferenceEquals(saved, _mirror) ? _source
            : null;
        if (other is { IsModified: true } && !saved.IsModified
            && string.Equals(other.Text, saved.Text, StringComparison.Ordinal))
            other.MarkSaved();
    }

    /// <summary>
    /// <paramref name="target"/> の本文を <paramref name="text"/> にする。変わった範囲（前後の一致を除いた
    /// 部分）だけを1回の編集として入れるので、Undo は1手で戻り、キャレットも変わっていない所に残る。
    /// </summary>
    public static void ApplyAsEdit(VimEditorControl target, string text)
    {
        var current = target.Text;
        if (string.Equals(current, text, StringComparison.Ordinal))
            return;
        var prefix = 0;
        var max = Math.Min(current.Length, text.Length);
        while (prefix < max && current[prefix] == text[prefix]) prefix++;
        var suffix = 0;
        while (suffix < max - prefix
               && current[current.Length - 1 - suffix] == text[text.Length - 1 - suffix]) suffix++;
        var edit = new LspTextEdit(
            new LspRange(PositionAt(current, prefix), PositionAt(current, current.Length - suffix)),
            text.Substring(prefix, text.Length - prefix - suffix));
        if (!target.TryApplyLspTextEdits([edit], expectedVersion: null, out _))
            target.SetText(text);   // 編集として入れられなかったときも、本文だけは揃える
    }

    private static LspPosition PositionAt(string text, int offset)
    {
        int line = 0, lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] != '\n') continue;
            line++;
            lineStart = i + 1;
        }
        return new LspPosition(line, offset - lineStart);
    }

    private void OnSourceBufferChanged(object? sender, EventArgs e) => Sync(_source, _mirror);
    private void OnMirrorBufferChanged(object? sender, EventArgs e) => Sync(_mirror, _source);

    private void Sync(VimEditorControl from, VimEditorControl to)
    {
        if (_syncing || string.Equals(to.Text, from.Text, StringComparison.Ordinal))
            return;
        _syncing = true;
        try { ApplyAsEdit(to, from.Text); }
        finally { _syncing = false; }
    }
}

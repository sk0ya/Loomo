using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Emmet（§36.7）。「略記を展開」はキャレットの手前の略記をタグへ、「選択を略記で囲む」は
/// 選択を入力した略記の一番内側へ入れる。展開そのものは <see cref="Emmet"/>（純粋）で、ここは
/// 範囲の置き換え（1 回の取り消しで戻る）とキャレットの移動だけ。
/// <para>補完の一覧には出さない——略記は <c>*</c> <c>&gt;</c> の打鍵で一覧が絞り込み直されるだけで
/// 引き直されないので、一覧の候補が「打ちかけの略記」の展開のまま残り、確定すると違う形に化ける。</para>
/// </summary>
public partial class ShellWindow
{
    private void ExpandEmmetAbbreviation()
    {
        if (EmmetTarget() is not { } target) return;
        var (control, syntax) = target;
        var caret = control.Caret;
        var lines = control.Text.Split('\n');
        if (caret.Line >= lines.Length) return;
        var line = lines[caret.Line].TrimEnd('\r');

        if (Emmet.Extract(line, caret.Column) is not { } span ||
            Emmet.Expand(span.Abbreviation, syntax, LeadingWhitespace(line), IndentUnit(lines)) is not { } expansion)
        {
            ReportEmmet("Emmet: キャレットの手前に展開できる略記がありません");
            return;
        }

        var range = new LspRange(new LspPosition(caret.Line, span.Start), new LspPosition(caret.Line, caret.Column));
        ApplyEmmet(control, range, expansion);
    }

    private void WrapWithEmmetAbbreviation()
    {
        if (EmmetTarget() is not { } target) return;
        var (control, syntax) = target;
        if (!control.HasSelection || control.SelectionAsLspRange() is not { } range)
        {
            ReportEmmet("Emmet: 囲む範囲を選択してください");
            return;
        }
        var selected = control.SelectedText;
        var abbreviation = InputDialog.Prompt(this, "Emmet: 略記で囲む",
            "略記（例: div.note、ul>li*  ※数の無い * は行ごとに繰り返す）:");
        if (string.IsNullOrWhiteSpace(abbreviation)) return;

        var lines = control.Text.Split('\n');
        var startLine = range.Start.Line < lines.Length ? lines[range.Start.Line].TrimEnd('\r') : "";
        if (Emmet.Expand(abbreviation, syntax, LeadingWhitespace(startLine), IndentUnit(lines), selected) is not { } expansion)
        {
            ReportEmmet($"Emmet: 「{abbreviation}」は略記として読めません");
            return;
        }
        // 行選択（次の行の頭で終わる）の改行は残す——囲んだ後ろの行が引っ付かないように。
        if (selected.EndsWith('\n'))
            expansion = expansion with { Text = expansion.Text + "\n" };
        ApplyEmmet(control, range, expansion);
    }

    /// <summary>対象のエディタと出力の流儀。Emmet の効かないファイルなら理由を出して null。</summary>
    private (VimEditorControl Control, EmmetSyntax Syntax)? EmmetTarget()
    {
        var control = FocusedEditorControl() ?? (_activeEditorTab is { IsRealized: true } tab ? tab.Control : null);
        if (control is null) return null;
        if (Emmet.SyntaxFor(control.FilePath) is not { } syntax)
        {
            ReportEmmet("Emmet: HTML 系（.html .vue .cshtml …）と .jsx／.tsx のファイルで使えます");
            return null;
        }
        return (control, syntax);
    }

    private void ApplyEmmet(VimEditorControl control, LspRange range, EmmetExpansion expansion)
    {
        if (!control.TryApplyLspTextEdits([new LspTextEdit(range, expansion.Text)], null, out var error))
        {
            ReportEmmet($"Emmet: 書き換えられませんでした（{error}）");
            return;
        }
        var line = range.Start.Line;
        var column = range.Start.Character;
        for (var i = 0; i < expansion.CaretOffset && i < expansion.Text.Length; i++)
        {
            if (expansion.Text[i] == '\n') { line++; column = 0; }
            else column++;
        }
        control.NavigateTo(line, column);
        control.Focus();
    }

    /// <summary>右クリックメニューへ1項目だけ足す：選択があれば「囲む」、無ければキャレットの手前に
    /// 略記があるときだけ「展開」。押しても何も起きない項目は出さない。</summary>
    private void AddEmmetMenuItem(ContextMenu menu, VimEditorControl? control)
    {
        if (control is null || Emmet.SyntaxFor(control.FilePath) is not { } syntax) return;
        if (control.HasSelection)
        {
            var wrap = new MenuItem { Header = "Emmet: 選択を略記で囲む…", InputGestureText = DescribeBinding("editor.emmet.wrap") };
            wrap.Click += (_, _) => WrapWithEmmetAbbreviation();
            menu.Items.Add(wrap);
            return;
        }
        var caret = control.Caret;
        var lines = control.Text.Split('\n');
        if (caret.Line >= lines.Length ||
            Emmet.Extract(lines[caret.Line].TrimEnd('\r'), caret.Column) is not { } span ||
            Emmet.Expand(span.Abbreviation, syntax, "", "  ") is null)
            return;
        var expand = new MenuItem { Header = $"Emmet: 「{span.Abbreviation}」を展開", InputGestureText = DescribeBinding("editor.emmet.expand") };
        expand.Click += (_, _) => ExpandEmmetAbbreviation();
        menu.Items.Add(expand);
    }

    private void ReportEmmet(string message) => EditorSharedStatusBar?.UpdateStatus(message);

    private static string LeadingWhitespace(string line) => line[..(line.Length - line.TrimStart().Length)];

    /// <summary>字下げ 1 段。文書の中で最初に見つかった字下げに合わせる（タブならタブ、空白ならその桁数）。
    /// 字下げが1つも無い文書では 2 桁（HTML の整形ツールの既定）。</summary>
    internal static string IndentUnit(IReadOnlyList<string> lines)
    {
        var smallest = int.MaxValue;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line[0] == '\t') return "\t";
            var spaces = line.Length - line.TrimStart(' ').Length;
            if (spaces > 0 && spaces < smallest) smallest = spaces;
        }
        return smallest is > 0 and <= 8 ? new string(' ', smallest) : "  ";
    }
}

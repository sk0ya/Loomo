using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Buffer;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 抜粋タブの文書（設計書 §35.3）。実際の <see cref="TextBuffer"/> を「抜粋タブ」と「元ファイル」の2つ用意し、
/// 抜粋タブでの編集を差分のまま元へ写したとき、元ファイルの該当行と抜粋の本文が一致し続けることを確かめる。
/// </summary>
public sealed class ExcerptDocumentTests
{
    private const string PathA = @"C:\w\A.cs";
    private const string PathB = @"C:\w\B.cs";

    private static readonly string[] SourceA = Enumerable.Range(0, 20).Select(i => $"a{i}").ToArray();
    private static readonly string[] SourceB = Enumerable.Range(0, 10).Select(i => $"b{i}").ToArray();

    private static string Header(string path) => "// ── " + System.IO.Path.GetFileName(path);

    /// <summary>抜粋タブ・元ファイル2つ・モデルを結び付けた小さな部屋。</summary>
    private sealed class Room
    {
        public TextBuffer View { get; }
        public Dictionary<string, TextBuffer> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ExcerptDocument Document { get; }
        private bool _forwarding;

        public Room(params ExcerptSpan[] spans)
        {
            Sources[PathA] = new TextBuffer(string.Join("\n", SourceA));
            Sources[PathB] = new TextBuffer(string.Join("\n", SourceB));
            Document = new ExcerptDocument(spans, p => Sources[p].GetText().Split('\n'), Header);
            View = new TextBuffer(Document.Text);
            View.Changed += change =>
            {
                if (_forwarding) return;
                var edit = Document.TranslateEdit(change);
                Assert.NotNull(edit);
                _forwarding = true;
                try { Apply(Sources[edit!.Path], edit.Edit); }
                finally { _forwarding = false; }
                Document.Commit(edit);
            };
        }

        /// <summary>元ファイルを抜粋タブ以外から編集する（元のタブで打鍵した相当）。</summary>
        public void EditSource(string path, Action<TextBuffer> edit)
        {
            var source = Sources[path];
            void OnChanged(TextBufferChange change)
            {
                foreach (var viewEdit in Document.SourceChanged(path, change, () => source.GetText().Split('\n')))
                {
                    _forwarding = true;
                    try { Apply(View, viewEdit.Edit); }
                    finally { _forwarding = false; }
                }
            }
            source.Changed += OnChanged;
            try { edit(source); }
            finally { source.Changed -= OnChanged; }
        }

        public void AssertConsistent()
        {
            Assert.Equal(Document.Text, View.GetText());
            foreach (var excerpt in Document.Excerpts)
            {
                var lines = Sources[excerpt.Path].GetText().Split('\n');
                var slice = lines.Skip(excerpt.Source.Start.Line)
                    .Take(excerpt.Source.End.Line - excerpt.Source.Start.Line).ToList();
                Assert.Equal(slice, excerpt.Lines);
            }
        }
    }

    private static void Apply(TextBuffer buffer, LspTextEdit edit)
    {
        var text = buffer.GetText();
        var lines = text.Split('\n').ToList();
        var head = lines[edit.Range.Start.Line][..edit.Range.Start.Character];
        var endLine = Math.Min(edit.Range.End.Line, lines.Count - 1);
        var tail = edit.Range.End.Line >= lines.Count ? "" : lines[endLine][edit.Range.End.Character..];
        lines.RemoveRange(edit.Range.Start.Line, endLine - edit.Range.Start.Line + 1);
        lines.InsertRange(edit.Range.Start.Line, (head + edit.NewText + tail).Split('\n'));
        buffer.ReplaceAll(string.Join("\n", lines));
    }

    [Fact]
    public void 要求は前後の行で広げ重なりと隣接はまとめる()
    {
        var spans = ExcerptPlanner.Plan(
            [new(PathA, 5), new(PathB, 0), new(PathA, 8), new(PathA, 15), new(PathA, 19)],
            path => path == PathA ? 20 : 10);

        Assert.Equal(
            [new ExcerptSpan(PathA, 3, 10), new ExcerptSpan(PathA, 13, 19), new ExcerptSpan(PathB, 0, 2)],
            spans);
    }

    /// <summary>元ファイルへ入れられなかった編集はモデルに残さない（確定しなければ本文もアンカーも動かない）。</summary>
    [Fact]
    public void 確定しなかった編集はモデルを変えない()
    {
        var document = new ExcerptDocument(
            [new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathA, 10, 11)],
            _ => SourceA, Header);
        var before = document.Text;

        var edit = document.TranslateEdit(new TextBufferChange(2, 2, 2, 2, "\nnew", 1));

        Assert.NotNull(edit);
        Assert.Equal(before, document.Text);
        Assert.Equal(10, document.Excerpts[1].Source.Start.Line);
    }

    /// <summary>文書の末尾の抜粋の本文を全部 dd すると、エディタは「見出しの行末から」消したと報告する。</summary>
    [Fact]
    public void 末尾の抜粋の本文を全部消すと元ファイルからも行ごと消える()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathB, 2, 3));

        room.View.DeleteLines(4, 5);   // B の本文（b2, b3）を全部
        room.AssertConsistent();

        Assert.Empty(room.Document.Excerpts[1].Lines);
        Assert.Equal(["b0", "b1", "b4"], room.Sources[PathB].GetText().Split('\n').Take(3));
    }

    [Fact]
    public void 見出しと本文を並べガターには元の行番号を出す()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathB, 0, 1));

        Assert.Equal("// ── A.cs\na3\na4\n// ── B.cs\nb0\nb1", room.Document.Text);
        Assert.Null(room.Document.LineLabel(0));
        Assert.Equal("4", room.Document.LineLabel(1));
        Assert.Null(room.Document.LineLabel(3));
        Assert.Equal("1", room.Document.LineLabel(4));
    }

    [Fact]
    public void 抜粋で打った文字は元ファイルの同じ行へ入る()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathB, 0, 1));

        room.View.InsertText(2, 2, "X");     // a4 → a4X
        room.View.InsertText(4, 0, "Y");     // b0 → Yb0
        room.AssertConsistent();

        Assert.Equal("a4X", room.Sources[PathA].GetLine(4));
        Assert.Equal("Yb0", room.Sources[PathB].GetLine(0));
    }

    [Fact]
    public void 抜粋の末尾に足した行は抜粋に残り後ろの抜粋は元ファイルの行に付いて動く()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathA, 10, 11));

        room.View.BreakLine(2, 2);            // a4 の行末で Enter
        room.View.InsertText(3, 0, "new");
        room.AssertConsistent();

        Assert.Equal(["a3", "a4", "new"], room.Document.Excerpts[0].Lines);
        Assert.Equal(11, room.Document.Excerpts[1].Source.Start.Line);
        Assert.Equal(["a10", "a11"], room.Document.Excerpts[1].Lines);
    }

    [Fact]
    public void 見出しの上に足した行は前の抜粋の末尾になる()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathB, 0, 1));

        room.View.InsertLineAbove(3, "tail");   // B の見出しの上（O 相当）
        room.AssertConsistent();

        Assert.Equal(["a3", "a4", "tail"], room.Document.Excerpts[0].Lines);
        Assert.Equal("tail", room.Sources[PathA].GetLine(5));
    }

    [Fact]
    public void 抜粋の最終行を消すと元ファイルからも行ごと消える()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 5), new ExcerptSpan(PathB, 0, 1));

        room.View.DeleteLines(3, 3);   // a5
        room.AssertConsistent();

        Assert.Equal(["a3", "a4"], room.Document.Excerpts[0].Lines);
        Assert.Equal("a6", room.Sources[PathA].GetLine(5));
    }

    [Fact]
    public void 見出しに触れる編集と二つの抜粋にまたがる編集は断る()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathB, 0, 1));
        var before = room.Document.Text.Split('\n');

        string? Guard(Func<List<string>, List<string>> edit) => room.Document.Guard(before, edit([.. before]));

        Assert.NotNull(Guard(l => { l[0] = "// changed"; return l; }));
        Assert.NotNull(Guard(l => { l.RemoveAt(3); return l; }));
        Assert.NotNull(Guard(l => { l[2] = "x"; l[4] = "y"; return l; }));
        Assert.NotNull(Guard(l => { l.Insert(0, "above everything"); return l; }));
        Assert.Null(Guard(l => { l[1] = "a3!"; return l; }));
        Assert.Null(Guard(l => { l.Insert(3, "appended to A"); return l; }));
    }

    [Fact]
    public void 元ファイルで抜粋の外を編集しても抜粋は付いて動き本文は変わらない()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathA, 10, 11));

        room.EditSource(PathA, s => s.InsertLines(0, ["top1", "top2"]));
        room.EditSource(PathA, s => s.InsertText(8, 0, "between\n"));
        room.AssertConsistent();

        Assert.Equal(5, room.Document.Excerpts[0].Source.Start.Line);
        Assert.Equal(["a3", "a4"], room.Document.Excerpts[0].Lines);
        Assert.Equal(["a10", "a11"], room.Document.Excerpts[1].Lines);
    }

    [Fact]
    public void 元ファイルで抜粋の中を編集すると抜粋タブへ写る()
    {
        var room = new Room(new ExcerptSpan(PathA, 3, 4), new ExcerptSpan(PathB, 0, 1));

        room.EditSource(PathA, s => s.InsertText(4, 2, "!"));
        room.EditSource(PathA, s => s.BreakLine(3, 1));
        room.AssertConsistent();

        Assert.Equal(["a", "3", "a4!"], room.Document.Excerpts[0].Lines);
        Assert.Equal("// ── A.cs\na\n3\na4!\n// ── B.cs\nb0\nb1", room.View.GetText());
    }

    [Fact]
    public void ランダムな編集で抜粋タブと元ファイルがずれない()
    {
        var random = new Random(3503);
        var room = new Room(new ExcerptSpan(PathA, 2, 4), new ExcerptSpan(PathA, 9, 12), new ExcerptSpan(PathB, 3, 5));
        for (var i = 0; i < 400; i++)
        {
            if (random.Next(3) == 0)
            {
                var path = random.Next(2) == 0 ? PathA : PathB;
                room.EditSource(path, s =>
                {
                    var line = random.Next(s.LineCount);
                    switch (random.Next(4))
                    {
                        case 0: s.InsertText(line, random.Next(s.GetLineLength(line) + 1), "s"); break;
                        case 1: s.InsertLines(line, ["ins" + i]); break;
                        case 2: if (s.LineCount > 8) s.DeleteLines(line, line); break;
                        case 3: s.BreakLine(line, random.Next(s.GetLineLength(line) + 1)); break;
                    }
                });
            }
            else
            {
                // 抜粋タブの本文の行だけを編集する（見出しはガードが守る。ここはモデルの写し替えを見る）。
                var contentLines = Enumerable.Range(0, room.View.LineCount)
                    .Where(l => room.Document.Describe(l).Kind == ExcerptLineKind.Content).ToList();
                if (contentLines.Count == 0) break;
                var line = contentLines[random.Next(contentLines.Count)];
                switch (random.Next(3))
                {
                    case 0: room.View.InsertText(line, random.Next(room.View.GetLineLength(line) + 1), "v"); break;
                    case 1: room.View.BreakLine(line, random.Next(room.View.GetLineLength(line) + 1)); break;
                    case 2: room.View.DeleteChar(line, random.Next(Math.Max(1, room.View.GetLineLength(line)))); break;
                }
            }
            room.AssertConsistent();
        }
    }
}

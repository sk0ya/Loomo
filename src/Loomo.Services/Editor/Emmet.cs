using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Services;

/// <summary>Emmet の出力の流儀。JSX は <c>class</c>→<c>className</c>・空要素を <c>&lt;br /&gt;</c> にする。</summary>
public enum EmmetSyntax { Html, Jsx }

/// <summary>展開結果。<see cref="CaretOffset"/> は <see cref="Text"/> の中でキャレットを置く位置
/// （最初の空の属性値か空の中身。どちらも無ければ末尾）。</summary>
public sealed record EmmetExpansion(string Text, int CaretOffset);

/// <summary>行の中の略記の位置（<see cref="Start"/> 列から キャレットまで）。</summary>
public sealed record EmmetAbbreviationSpan(int Start, string Abbreviation);

/// <summary>
/// Emmet（HTML の略記 <c>ul&gt;li.item*3</c> → 入れ子のタグ）の展開。VS Code / Zed が内蔵しているものの
/// 必要な部分だけを持つ：子 <c>&gt;</c>・兄弟 <c>+</c>・上る <c>^</c>・繰り返し <c>*N</c>・番号 <c>$</c>
/// （<c>$$</c> で桁埋め・<c>@-</c> で逆順・<c>@N</c> で開始番号）・<c>#id</c>・<c>.class</c>・<c>[属性]</c>・
/// <c>{本文}</c>・<c>( )</c> のまとまり・親から決まる暗黙のタグ（<c>ul&gt;.x</c> は <c>li</c>）・
/// よく使う既定属性（<c>a</c> の <c>href</c> 等）・<c>!</c> の HTML5 雛形。CSS の略記は持たない。
/// <para>純粋な関数で、エディタには触らない（呼び出し側が範囲の置き換えとキャレット移動をする）。</para>
/// </summary>
public static class Emmet
{
    private const char Slot = '';

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".vue", ".svelte", ".cshtml", ".razor", ".php", ".astro",
    };

    private static readonly HashSet<string> JsxExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jsx", ".tsx" };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "param",
    };

    private static readonly HashSet<string> InlineElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "bdi", "bdo", "br", "cite", "code", "data", "dfn", "em", "i", "img", "input", "kbd", "label",
        "mark", "q", "s", "samp", "small", "span", "strong", "sub", "sup", "time", "u", "var", "wbr", "button",
        "select", "textarea",
    };

    /// <summary>単独で書いたときに展開してよい名前（素の語は普通の文章と区別がつかないので、既知のタグだけ）。</summary>
    private static readonly HashSet<string> KnownTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "area", "article", "aside", "audio", "b", "base", "blockquote", "body", "br",
        "button", "canvas", "caption", "cite", "code", "col", "colgroup", "data", "datalist", "dd", "del", "details",
        "dfn", "dialog", "div", "dl", "dt", "em", "embed", "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hgroup", "hr", "html", "i", "iframe", "img", "input",
        "ins", "kbd", "label", "legend", "li", "link", "main", "map", "mark", "menu", "meta", "meter", "nav",
        "noscript", "object", "ol", "optgroup", "option", "output", "p", "param", "picture", "pre", "progress",
        "q", "s", "samp", "script", "section", "select", "slot", "small", "source", "span", "strong", "style",
        "sub", "summary", "sup", "svg", "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead",
        "time", "title", "tr", "track", "u", "ul", "var", "video", "wbr", "btn", "bq",
    };

    /// <summary>名前の言い換え（Emmet の省略名）。</summary>
    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["btn"] = "button", ["bq"] = "blockquote", ["fig"] = "figure", ["figc"] = "figcaption",
        ["sect"] = "section", ["art"] = "article", ["hdr"] = "header", ["ftr"] = "footer", ["str"] = "strong",
        ["leg"] = "legend", ["opt"] = "option", ["tarea"] = "textarea", ["obj"] = "object", ["prog"] = "progress",
    };

    /// <summary>既定で付ける属性（略記に同名の属性があればそちらが勝つ）。</summary>
    private static readonly Dictionary<string, (string Name, string Value)[]> DefaultAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = [("href", "")],
            ["img"] = [("src", ""), ("alt", "")],
            ["input"] = [("type", "text")],
            ["link"] = [("rel", "stylesheet"), ("href", "")],
            ["form"] = [("action", "")],
            ["label"] = [("for", "")],
            ["iframe"] = [("src", "")],
            ["script"] = [],
            ["area"] = [("shape", ""), ("coords", ""), ("href", ""), ("alt", "")],
            ["source"] = [("src", ""), ("type", "")],
            ["object"] = [("data", ""), ("type", "")],
            ["embed"] = [("src", ""), ("type", "")],
        };

    /// <summary>コロン付きの名前（<c>input:checkbox</c>・<c>a:link</c> …）。</summary>
    private static readonly Dictionary<string, (string Tag, (string Name, string Value)[] Attributes)> ColonAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a:link"] = ("a", [("href", "http://")]),
            ["a:mail"] = ("a", [("href", "mailto:")]),
            ["a:tel"] = ("a", [("href", "tel:+")]),
            ["link:css"] = ("link", [("rel", "stylesheet"), ("href", "style.css")]),
            ["link:favicon"] = ("link", [("rel", "shortcut icon"), ("type", "image/x-icon"), ("href", "favicon.ico")]),
            ["script:src"] = ("script", [("src", "")]),
            ["meta:utf"] = ("meta", [("http-equiv", "Content-Type"), ("content", "text/html;charset=UTF-8")]),
            ["meta:vp"] = ("meta", [("name", "viewport"), ("content", "width=device-width, initial-scale=1.0")]),
            ["form:get"] = ("form", [("action", ""), ("method", "get")]),
            ["form:post"] = ("form", [("action", ""), ("method", "post")]),
            ["btn:s"] = ("button", [("type", "submit")]),
            ["btn:r"] = ("button", [("type", "reset")]),
            ["btn:b"] = ("button", [("type", "button")]),
            ["img:s"] = ("img", [("srcset", ""), ("alt", "")]),
        };

    /// <summary>このファイルで Emmet が意味を持つか（HTML 系・JSX）。それ以外は null。</summary>
    public static EmmetSyntax? SyntaxFor(string? filePath)
    {
        var extension = Path.GetExtension(filePath ?? "");
        if (HtmlExtensions.Contains(extension)) return EmmetSyntax.Html;
        if (JsxExtensions.Contains(extension)) return EmmetSyntax.Jsx;
        return null;
    }

    /// <summary>
    /// キャレットの手前の略記を切り出す。タグの中（<c>&lt;div cl|</c>）なら null。直前のタグの閉じ
    /// （<c>&lt;div&gt;ul&gt;li|</c>）は略記に含めない。<c>[ ]</c> <c>{ }</c> の中の空白は略記の一部として読む。
    /// </summary>
    public static EmmetAbbreviationSpan? Extract(string line, int caret)
    {
        caret = Math.Clamp(caret, 0, line.Length);
        var textStart = 0;
        var lastOpen = line.LastIndexOf('<', Math.Max(0, caret - 1));
        if (caret > 0 && lastOpen >= 0 && lastOpen < caret)
        {
            var close = IndexOfTagClose(line, lastOpen, caret);
            if (close < 0)
            {
                // `<` の直後がタグとして読めない（`a<b` の比較など）なら、タグの中ではない。
                if (lastOpen + 1 < caret && IsTagStart(line[lastOpen + 1])) return null;
                textStart = lastOpen + 1;
            }
            else
            {
                textStart = close + 1;
            }
        }

        var start = caret;
        var depth = 0;
        while (start > textStart)
        {
            var c = line[start - 1];
            if (c is ']' or '}' or ')') depth++;
            else if (c is '[' or '{' or '(')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (depth == 0 && (char.IsWhiteSpace(c) || c is '<' or ';' or '`' or '|' or '&'))
                break;
            start--;
        }
        if (start == caret) return null;
        var abbreviation = line[start..caret];
        return abbreviation.Length == 0 ? null : new EmmetAbbreviationSpan(start, abbreviation);
    }

    private static bool IsTagStart(char c) => char.IsLetter(c) || c is '/' or '!';

    private static int IndexOfTagClose(string line, int open, int end)
    {
        char? quote = null;
        for (var i = open + 1; i < end; i++)
        {
            var c = line[i];
            if (quote is { } q)
            {
                if (c == q) quote = null;
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    /// <summary>
    /// 略記を展開する。展開できない（文法の誤り・ただの語）なら null。
    /// <paramref name="indent"/> は略記がある行の字下げ（2 行目以降の頭に付ける）、<paramref name="unit"/> は字下げ 1 段。
    /// <paramref name="wrapped"/> を渡すと「選択を略記で囲む」：一番内側の最後の要素の中身になる。
    /// その要素が数の無い <c>*</c> なら、行ごとに繰り返す（<c>ul&gt;li*</c> で行を箇条書きに）。
    /// </summary>
    public static EmmetExpansion? Expand(
        string abbreviation, EmmetSyntax syntax, string indent, string unit, string? wrapped = null)
    {
        abbreviation = abbreviation.Trim();
        if (abbreviation.Length == 0) return null;
        if (abbreviation is "!" or "html:5" or "doc")
            return Finish(Boilerplate(syntax, unit), indent);

        List<Node> roots;
        try
        {
            var parser = new Parser(abbreviation);
            roots = parser.ParseAll();
        }
        catch (FormatException)
        {
            return null;
        }
        if (roots.Count == 0) return null;
        if (wrapped is null && !LooksLikeAbbreviation(abbreviation, roots)) return null;

        if (wrapped is not null)
        {
            var leaf = DeepestLast(roots);
            if (leaf is null) return null;
            var lines = SplitLines(wrapped);
            if (leaf.RepeatImplicit)
            {
                leaf.WrappedLines = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                leaf.Repeat = Math.Max(1, leaf.WrappedLines.Count);
            }
            else
            {
                leaf.WrappedContent = lines;
            }
        }

        var builder = new StringBuilder();
        var renderer = new Renderer(syntax, unit, builder);
        renderer.RenderList(roots, null, 0, inherited: null, inline: false);
        return Finish(builder.ToString(), indent);
    }

    private static EmmetExpansion Finish(string text, string indent)
    {
        text = text.Replace("\n", "\n" + indent);
        var caret = text.IndexOf(Slot);
        var clean = text.Replace(Slot.ToString(), "");
        return new EmmetExpansion(clean, caret < 0 ? clean.Length : caret);
    }

    /// <summary>素の語（<c>hello</c>）は文章と区別がつかないので展開しない。演算子・記号を含むか、
    /// 既知のタグ名・コロン付きの名前だけを略記とみなす。</summary>
    private static bool LooksLikeAbbreviation(string abbreviation, List<Node> roots)
    {
        if (abbreviation.IndexOfAny(['>', '+', '^', '*', '.', '#', '[', '{', '(']) >= 0) return true;
        return roots.Count == 1 && roots[0].Name is { } name &&
               (KnownTags.Contains(name) || ColonAliases.ContainsKey(name) ||
                (name.Contains(':') && name.Split(':')[0].Equals("input", StringComparison.OrdinalIgnoreCase)));
    }

    private static Node? DeepestLast(List<Node> nodes)
    {
        if (nodes.Count == 0) return null;
        var last = nodes[^1];
        if (last.IsGroup) return DeepestLast(last.Children) ?? last;
        return last.Children.Count > 0 ? DeepestLast(last.Children) ?? last : last;
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n').ToList();

    private static string Boilerplate(EmmetSyntax syntax, string unit)
    {
        var close = syntax == EmmetSyntax.Jsx ? " />" : ">";
        return string.Join('\n',
            "<!DOCTYPE html>",
            "<html lang=\"en\">",
            "<head>",
            $"{unit}<meta charset=\"UTF-8\"{close}",
            $"{unit}<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"{close}",
            $"{unit}<title>Document</title>",
            "</head>",
            "<body>",
            $"{unit}{Slot}",
            "</body>",
            "</html>");
    }

    // ── 構文木 ────────────────────────────────────────────────────────────

    private sealed class Node
    {
        public string? Name;
        public string? Id;
        public readonly List<string> Classes = [];
        public readonly List<(string Name, string? Value)> Attributes = [];
        public string? Text;
        public int Repeat = 1;
        /// <summary>数の無い <c>*</c>（囲むときに行の数だけ繰り返す）。</summary>
        public bool RepeatImplicit;
        public bool IsGroup;
        public readonly List<Node> Children = [];
        public List<string>? WrappedLines;
        public List<string>? WrappedContent;

        public bool IsTextOnly => !IsGroup && Name is null && Id is null && Classes.Count == 0 &&
                                  Attributes.Count == 0 && Text is not null;
    }

    private sealed class Parser(string source)
    {
        private int _pos;

        public List<Node> ParseAll()
        {
            var result = ParseSequence();
            if (_pos != source.Length) throw new FormatException();
            return result;
        }

        /// <summary><c>a&gt;b+c^d</c> を、根の並びとして読む。</summary>
        private List<Node> ParseSequence()
        {
            var roots = new List<Node>();
            // 親の積み上げ。null は根（roots）を表す。
            var parents = new List<Node?> { null };
            Node? last = null;

            void Add(Node node)
            {
                var parent = parents[^1];
                (parent is null ? roots : parent.Children).Add(node);
                last = node;
            }

            Add(ParseItem());
            while (_pos < source.Length && source[_pos] != ')')
            {
                var op = source[_pos];
                if (op == '>')
                {
                    _pos++;
                    if (last is null || last.IsGroup || last.IsTextOnly) throw new FormatException();
                    parents.Add(last);
                    Add(ParseItem());
                }
                else if (op == '+')
                {
                    _pos++;
                    Add(ParseItem());
                }
                else if (op == '^')
                {
                    while (_pos < source.Length && source[_pos] == '^')
                    {
                        _pos++;
                        if (parents.Count > 1) parents.RemoveAt(parents.Count - 1);
                    }
                    Add(ParseItem());
                }
                else
                {
                    throw new FormatException();
                }
            }
            return roots;
        }

        private Node ParseItem()
        {
            if (_pos >= source.Length) throw new FormatException();
            if (source[_pos] == '(')
            {
                _pos++;
                var group = new Node { IsGroup = true };
                group.Children.AddRange(ParseSequence());
                Expect(')');
                ParseMultiplier(group);
                return group;
            }

            var node = new Node();
            var name = ReadWhile(c => char.IsLetterOrDigit(c) || c is '-' or ':' or '_' or '$' or '@' or '!');
            if (name.Length > 0) node.Name = name;
            var any = name.Length > 0;
            while (_pos < source.Length)
            {
                var c = source[_pos];
                if (c == '#')
                {
                    _pos++;
                    node.Id = ReadName();
                }
                else if (c == '.')
                {
                    _pos++;
                    node.Classes.Add(ReadName());
                }
                else if (c == '[')
                {
                    _pos++;
                    ParseAttributes(node);
                }
                else if (c == '{')
                {
                    _pos++;
                    node.Text = (node.Text ?? "") + ReadUntil('}');
                }
                else break;
                any = true;
            }
            if (!any) throw new FormatException();
            ParseMultiplier(node);
            return node;
        }

        private void ParseMultiplier(Node node)
        {
            if (_pos >= source.Length || source[_pos] != '*') return;
            _pos++;
            var digits = ReadWhile(char.IsDigit);
            if (digits.Length == 0)
            {
                node.RepeatImplicit = true;
                node.Repeat = 1;
            }
            else
            {
                node.Repeat = Math.Clamp(int.Parse(digits), 1, 1000);
            }
        }

        private void ParseAttributes(Node node)
        {
            while (true)
            {
                SkipSpaces();
                if (_pos >= source.Length) throw new FormatException();
                if (source[_pos] == ']')
                {
                    _pos++;
                    return;
                }
                var name = ReadWhile(c => !char.IsWhiteSpace(c) && c is not '=' and not ']');
                if (name.Length == 0) throw new FormatException();
                string? value = null;
                if (_pos < source.Length && source[_pos] == '=')
                {
                    _pos++;
                    if (_pos < source.Length && source[_pos] is '"' or '\'')
                    {
                        var quote = source[_pos++];
                        value = ReadUntil(quote);
                    }
                    else
                    {
                        value = ReadWhile(c => !char.IsWhiteSpace(c) && c != ']');
                    }
                }
                node.Attributes.Add((name, value));
            }
        }

        private string ReadName()
        {
            var name = ReadWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '$' or '@' or ':');
            if (name.Length == 0) throw new FormatException();
            return name;
        }

        private string ReadUntil(char end)
        {
            var close = source.IndexOf(end, _pos);
            if (close < 0) throw new FormatException();
            var text = source[_pos..close];
            _pos = close + 1;
            return text;
        }

        private string ReadWhile(Func<char, bool> predicate)
        {
            var start = _pos;
            while (_pos < source.Length && predicate(source[_pos])) _pos++;
            return source[start.._pos];
        }

        private void SkipSpaces()
        {
            while (_pos < source.Length && char.IsWhiteSpace(source[_pos])) _pos++;
        }

        private void Expect(char c)
        {
            if (_pos >= source.Length || source[_pos] != c) throw new FormatException();
            _pos++;
        }
    }

    // ── 出力 ──────────────────────────────────────────────────────────────

    /// <summary>繰り返しの中の位置（<see cref="N"/> は 0 始まり）。</summary>
    internal readonly record struct Repetition(int N, int Count);

    private sealed class Renderer(EmmetSyntax syntax, string unit, StringBuilder output)
    {
        /// <summary>並びを出す。<paramref name="inline"/> なら改行せず 1 行に続ける。</summary>
        public void RenderList(List<Node> nodes, string? parentTag, int level, Repetition? inherited, bool inline)
        {
            var expanded = Flatten(nodes, parentTag, inherited);
            for (var i = 0; i < expanded.Count; i++)
            {
                if (i > 0 && !inline) output.Append('\n').Append(Indent(level));
                RenderElement(expanded[i], level);
            }
        }

        private sealed record Instance(Node Node, string? Tag, Repetition? Index, string? Wrapped);

        /// <summary>繰り返しとまとまりを開いた、実際に出す要素の並び。</summary>
        private List<Instance> Flatten(List<Node> nodes, string? parentTag, Repetition? inherited)
        {
            var result = new List<Instance>();
            foreach (var node in nodes)
            {
                for (var n = 0; n < node.Repeat; n++)
                {
                    // 番号は一番内側の繰り返しから取る（繰り返さない子は親の番号を引き継ぐ）。
                    var index = node.Repeat > 1 || node.RepeatImplicit ? new Repetition(n, node.Repeat) : inherited;
                    if (node.IsGroup)
                    {
                        result.AddRange(Flatten(node.Children, parentTag, index));
                        continue;
                    }
                    var wrapped = node.WrappedLines is { } lines && n < lines.Count ? lines[n] : null;
                    result.Add(new Instance(node, ResolveTag(node, parentTag), index, wrapped));
                }
            }
            return result;
        }

        private void RenderElement(Instance instance, int level)
        {
            var node = instance.Node;
            var index = instance.Index;
            if (instance.Tag is null)
            {
                output.Append(Number(node.Text ?? "", index));
                return;
            }

            var tag = instance.Tag;
            output.Append('<').Append(tag);
            foreach (var (name, value) in Attributes(node, tag, index))
            {
                output.Append(' ').Append(name);
                if (value is null && syntax == EmmetSyntax.Html && IsBooleanAttribute(name)) continue;
                output.Append("=\"").Append(value is null or "" ? Slot.ToString() : value).Append('"');
            }

            if (VoidElements.Contains(tag))
            {
                output.Append(syntax == EmmetSyntax.Jsx ? " />" : ">");
                return;
            }
            output.Append('>');

            var text = node.Text is null ? "" : Number(node.Text, index);
            var children = node.Children;
            if (instance.Wrapped is { } line)
            {
                output.Append(text).Append(line);
            }
            else if (node.WrappedContent is { } content)
            {
                output.Append(text);
                if (content.Count == 1 && children.Count == 0)
                {
                    output.Append(content[0].Trim());
                }
                else
                {
                    var common = CommonIndent(content);
                    foreach (var contentLine in content)
                        output.Append('\n').Append(Indent(level + 1))
                            .Append(contentLine.Length >= common ? contentLine[common..] : contentLine.TrimStart());
                    output.Append('\n').Append(Indent(level));
                }
            }
            else if (children.Count == 0)
            {
                output.Append(text.Length == 0 ? Slot.ToString() : text);
            }
            else
            {
                output.Append(text);
                var flat = Flatten(children, tag, index);
                var inline = flat.Count < 3 && flat.All(c => c.Tag is null || (InlineElements.Contains(c.Tag) && c.Node.Children.Count == 0));
                if (inline)
                {
                    RenderList(children, tag, level + 1, index, inline: true);
                }
                else
                {
                    output.Append('\n').Append(Indent(level + 1));
                    RenderList(children, tag, level + 1, index, inline: false);
                    output.Append('\n').Append(Indent(level));
                }
            }
            output.Append("</").Append(tag).Append('>');
        }

        private IEnumerable<(string Name, string? Value)> Attributes(Node node, string tag, Repetition? index)
        {
            var list = new List<(string Name, string? Value)>();
            void Set(string name, string? value)
            {
                if (syntax == EmmetSyntax.Jsx) name = JsxName(name);
                var at = list.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) list[at] = (list[at].Name, value);
                else list.Add((name, value));
            }

            if (node.Name is { } raw && ColonAliases.TryGetValue(raw, out var alias))
                foreach (var (name, value) in alias.Attributes) Set(name, value);
            else if (node.Name is { } rawName && rawName.Contains(':') &&
                     rawName.Split(':', 2) is [var head, var type] &&
                     head.Equals("input", StringComparison.OrdinalIgnoreCase) && type.Length > 0)
            {
                Set("type", type);
                Set("name", "");
                Set("id", "");
            }
            else if (DefaultAttributes.TryGetValue(tag, out var defaults))
                foreach (var (name, value) in defaults) Set(name, value);

            if (node.Id is { } id) Set("id", Number(id, index));
            if (node.Classes.Count > 0)
                Set("class", string.Join(' ', node.Classes.Select(c => Number(c, index))));
            foreach (var (name, value) in node.Attributes)
                Set(name, value is null ? null : Number(value, index));
            return list;
        }

        private static string JsxName(string name) => name switch
        {
            "class" => "className",
            "for" => "htmlFor",
            "tabindex" => "tabIndex",
            _ => name,
        };

        private static bool IsBooleanAttribute(string name) =>
            name is "disabled" or "checked" or "selected" or "readonly" or "required" or "hidden" or "multiple"
                or "autofocus" or "async" or "defer" or "novalidate" or "controls" or "autoplay" or "loop" or "muted";

        private static string? ResolveTag(Node node, string? parentTag)
        {
            if (node.IsTextOnly) return null;
            if (node.Name is { } name)
            {
                if (ColonAliases.TryGetValue(name, out var alias)) return alias.Tag;
                var head = name.Contains(':') ? name.Split(':')[0] : name;
                return NameAliases.TryGetValue(head, out var real) ? real : head;
            }
            return parentTag?.ToLowerInvariant() switch
            {
                "ul" or "ol" or "menu" => "li",
                "table" or "tbody" or "thead" or "tfoot" => "tr",
                "tr" => "td",
                "select" or "optgroup" => "option",
                "colgroup" => "col",
                "audio" or "video" or "picture" => "source",
                "map" => "area",
                "dl" => "dt",
                { } p when InlineElements.Contains(p) => "span",
                _ => "div",
            };
        }

        /// <summary><c>$</c> の並びを番号に（桁数はその並びの長さで 0 埋め）。直後の <c>@-</c> で逆順、
        /// <c>@N</c> で N から数える。繰り返しの外では開始番号そのもの。</summary>
        internal static string Number(string text, Repetition? index)
        {
            if (!text.Contains('$')) return text;
            var builder = new StringBuilder();
            var i = 0;
            while (i < text.Length)
            {
                if (text[i] != '$')
                {
                    builder.Append(text[i++]);
                    continue;
                }
                var run = 0;
                while (i < text.Length && text[i] == '$') { run++; i++; }
                var reverse = false;
                var start = 1;
                if (i < text.Length && text[i] == '@')
                {
                    i++;
                    if (i < text.Length && text[i] == '-') { reverse = true; i++; }
                    var digits = i;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                    if (i > digits) start = int.Parse(text[digits..i]);
                }
                var value = index is { } r ? start + (reverse ? r.Count - 1 - r.N : r.N) : start;
                builder.Append(value.ToString().PadLeft(run, '0'));
            }
            return builder.ToString();
        }

        private string Indent(int level) => string.Concat(Enumerable.Repeat(unit, level));

        private static int CommonIndent(List<string> lines)
        {
            var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
            if (nonEmpty.Count == 0) return 0;
            return nonEmpty.Min(l => l.Length - l.TrimStart().Length);
        }
    }
}

using System.IO;
using Editor.Core.Buffer;
using Editor.Core.Editing;
using Editor.Core.Engine;
using Editor.Core.Syntax;
using Editor.Core.Lsp;
using Editor.Core.Models;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpEditorIntegrationTests
{
    [Fact]
    public void Loomo_registration_replaces_the_generic_CSharp_syntax_for_CSharp_files()
    {
        var services = VimEngineServices.CreateIsolated();
        CSharpEditorIntegration.Configure(services);
        var syntax = new SyntaxEngine(services.SyntaxLanguages);
        syntax.DetectLanguage("Feature.cs");

        var tokens = syntax.Tokenize(["  #if DEBUG", "[Fact]", "var text = $\"value\";", "var raw = \"\"\"value\"\"\";"]);

        Assert.Contains(tokens[0].Tokens, t => t.Kind == TokenKind.Preprocessor);
        Assert.Contains(tokens[1].Tokens, t => t.Kind == TokenKind.Attribute);
        Assert.Contains(tokens[2].Tokens, t => t.Kind == TokenKind.String && t.StartColumn == 11);
        Assert.Contains(tokens[3].Tokens, t => t.Kind == TokenKind.String && t.StartColumn == 10);
    }

    [Fact]
    public void Interpolated_strings_keep_expression_identifiers_out_of_the_string_token()
    {
        var line = "var text = $\"Hello {name} — {amount.ToString()} {{literal}}\";";

        var tokens = new CSharpSyntaxLanguage().Tokenize([line])[0].Tokens;

        Assert.Contains(tokens, token => token.Kind == TokenKind.Identifier
            && line[token.StartColumn..(token.StartColumn + token.Length)] == "name");
        Assert.Contains(tokens, token => token.Kind == TokenKind.Identifier
            && line[token.StartColumn..(token.StartColumn + token.Length)] == "amount");
        Assert.Contains(tokens, token => token.Kind == TokenKind.Function
            && line[token.StartColumn..(token.StartColumn + token.Length)] == "ToString");
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Identifier
            && line[token.StartColumn..(token.StartColumn + token.Length)] == "literal");
    }

    [Fact]
    public void Raw_and_verbatim_interpolated_strings_color_their_expressions()
    {
        var lines = new[]
        {
            "var raw = $\"\"\"value: {count}\"\"\";",
            "var verbatim = $@\"value: {count}\";",
        };

        var tokens = new CSharpSyntaxLanguage().Tokenize(lines);

        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Identifier
            && lines[0][token.StartColumn..(token.StartColumn + token.Length)] == "count");
        Assert.Contains(tokens[1].Tokens, token => token.Kind == TokenKind.Identifier
            && lines[1][token.StartColumn..(token.StartColumn + token.Length)] == "count");
    }

    [Fact]
    public void Multi_dollar_raw_interpolation_requires_the_matching_brace_count()
    {
        const string line = "var text = $$\"\"\"value: {{count}}; literal {brace}\"\"\";";

        var tokens = new CSharpSyntaxLanguage().Tokenize([line])[0].Tokens;

        Assert.Contains(tokens, token => token.Kind == TokenKind.Identifier
            && line[token.StartColumn..(token.StartColumn + token.Length)] == "count");
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Identifier
            && line[token.StartColumn..(token.StartColumn + token.Length)] == "brace");
    }

    [Fact]
    public void Multiline_interpolated_strings_color_expression_tokens_across_lines()
    {
        var lines = new[]
        {
            "var raw = $\"\"\"value:",
            "    {count + 1} items\"\"\";",
            "var verbatim = $@\"value:",
            "    {name}\";",
        };

        var tokens = new CSharpSyntaxLanguage().Tokenize(lines);

        Assert.Contains(tokens[1].Tokens, token => token.Kind == TokenKind.Identifier &&
            lines[1][token.StartColumn..(token.StartColumn + token.Length)] == "count");
        Assert.Contains(tokens[1].Tokens, token => token.Kind == TokenKind.Number &&
            lines[1][token.StartColumn..(token.StartColumn + token.Length)] == "1");
        Assert.Contains(tokens[3].Tokens, token => token.Kind == TokenKind.Identifier &&
            lines[3][token.StartColumn..(token.StartColumn + token.Length)] == "name");
        Assert.DoesNotContain(tokens[1].Tokens, token => token.Kind == TokenKind.String &&
            lines[1][token.StartColumn..(token.StartColumn + token.Length)].Contains("count", StringComparison.Ordinal));
    }

    [Fact]
    public void Multiline_raw_and_verbatim_strings_close_when_the_terminator_starts_at_column_zero()
    {
        var lines = new[]
        {
            "var raw = \"\"\"",
            "payload",
            "\"\"\"; var afterRaw",
            "var verbatim = @\"",
            "payload",
            "\"; var afterVerbatim",
            "var value = 1;",
        };

        var tokens = new CSharpSyntaxLanguage().Tokenize(lines);

        Assert.Contains(tokens[2].Tokens, token =>
            token.Kind == TokenKind.String && token.StartColumn == 0 && token.Length == 3);
        Assert.Contains(tokens[2].Tokens, token => token.Kind == TokenKind.Keyword && token.StartColumn == 5);
        Assert.Contains(tokens[5].Tokens, token =>
            token.Kind == TokenKind.String && token.StartColumn == 0 && token.Length == 1);
        Assert.Contains(tokens[5].Tokens, token => token.Kind == TokenKind.Keyword && token.StartColumn == 3);
        Assert.Contains(tokens[6].Tokens, token => token.Kind == TokenKind.Keyword && token.StartColumn == 0);
    }

    [Fact]
    public void Escaped_keyword_identifiers_are_not_colored_as_keywords()
    {
        var tokens = new CSharpSyntaxLanguage().Tokenize([
            "var @class = 1;",
            "@class.ToString();",
            "@class();",
        ]);

        Assert.Contains(tokens[0].Tokens, token =>
            token.StartColumn == 4 && token.Length == 6 && token.Kind == TokenKind.Identifier);
        Assert.Contains(tokens[1].Tokens, token =>
            token.StartColumn == 0 && token.Length == 6 && token.Kind == TokenKind.Identifier);
        Assert.Contains(tokens[2].Tokens, token =>
            token.StartColumn == 0 && token.Length == 6 && token.Kind == TokenKind.Function);
    }

    [Fact]
    public void Modern_contextual_keywords_are_available_to_the_fallback_lexer()
    {
        var lines = new[]
        {
            "partial class Sample { }",
            "event Action Changed { add { } remove { } }",
            "T M<T>() where T : allows ref struct => default;",
            "extension static void Map(this int value) { }",
            "public int Value => field;",
        };

        var tokens = new CSharpSyntaxLanguage().Tokenize(lines);

        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Keyword &&
            lines[0].Substring(token.StartColumn, token.Length) == "partial");
        Assert.Contains(tokens[1].Tokens, token => token.Kind == TokenKind.Keyword &&
            lines[1].Substring(token.StartColumn, token.Length) == "add");
        Assert.Contains(tokens[1].Tokens, token => token.Kind == TokenKind.Keyword &&
            lines[1].Substring(token.StartColumn, token.Length) == "remove");
        Assert.Contains(tokens[2].Tokens, token => token.Kind == TokenKind.Keyword &&
            lines[2].Substring(token.StartColumn, token.Length) == "allows");
        Assert.Contains(tokens[3].Tokens, token => token.Kind == TokenKind.Keyword &&
            lines[3].Substring(token.StartColumn, token.Length) == "extension");
        Assert.Contains(tokens[4].Tokens, token => token.Kind == TokenKind.Keyword &&
            lines[4].Substring(token.StartColumn, token.Length) == "field");
    }

    [Fact]
    public void Xml_documentation_tags_are_colored_without_parsing_the_comment_body_as_code()
    {
        var lines = new[]
        {
            "/// <summary>Returns <see cref=\"T\" />.</summary>",
            "var value = from item in items where item is int number and number > 0 select number;",
        };

        var tokens = new CSharpSyntaxLanguage().Tokenize(lines);

        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Comment &&
            token.StartColumn == 0 && token.Length == "/// ".Length);
        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute &&
            lines[0].Substring(token.StartColumn, token.Length) == "<summary>");
        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute &&
            lines[0].Substring(token.StartColumn, token.Length) == "<see cref=\"T\" />");
        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute &&
            lines[0].Substring(token.StartColumn, token.Length) == "</summary>");
        Assert.DoesNotContain(tokens[0].Tokens, token => token.Kind == TokenKind.Keyword);

        foreach (var keyword in new[] { "from", "in", "where", "is", "and", "select" })
            Assert.Contains(tokens[1].Tokens, token => token.Kind == TokenKind.Keyword &&
                lines[1].Substring(token.StartColumn, token.Length) == keyword);
    }

    [Fact]
    public void Xml_documentation_tag_scanning_respects_quoted_greater_than_and_incomplete_tags()
    {
        var lines = new[]
        {
            "/// <see cref=\"T > U\" /> and <paramref name=\"value\" />",
            "/// text <see cref=\"T >",
        };

        var tokens = new CSharpSyntaxLanguage().Tokenize(lines);

        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute &&
            lines[0].Substring(token.StartColumn, token.Length) == "<see cref=\"T > U\" />");
        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute &&
            lines[0].Substring(token.StartColumn, token.Length) == "<paramref name=\"value\" />");
        Assert.DoesNotContain(tokens[1].Tokens, token => token.Kind == TokenKind.Attribute);
        Assert.Contains(tokens[1].Tokens, token => token.Kind == TokenKind.Comment &&
            lines[1][token.StartColumn..(token.StartColumn + token.Length)]
                .Contains("<see", StringComparison.Ordinal));
    }

    [Fact]
    public void Multiline_attributes_keep_the_attribute_token_until_the_closing_bracket()
    {
        var tokens = new CSharpSyntaxLanguage().Tokenize([
            "    [InlineData(new[] { \"[not-a-token\" },",
            "        1)]",
            "    public void Adds(int value) { }",
        ]);

        var firstAttribute = Assert.Single(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute);
        Assert.Equal(4, firstAttribute.StartColumn);
        Assert.Equal("    [InlineData(new[] { \"[not-a-token\" },".Length - 4, firstAttribute.Length);
        var secondAttribute = Assert.Single(tokens[1].Tokens, token => token.Kind == TokenKind.Attribute);
        Assert.Equal(0, secondAttribute.StartColumn);
        Assert.Equal("        1)]".Length, secondAttribute.Length);
        Assert.Contains(tokens[2].Tokens, token => token.Kind == TokenKind.Keyword
            && token.StartColumn == 4);
    }

    [Fact]
    public void Attribute_scanning_handles_nested_brackets_and_returns_to_code()
    {
        var tokens = new CSharpSyntaxLanguage().Tokenize([
            "[Uses(values[0])] public class Sample { }",
        ]);

        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute
            && token.StartColumn == 0 && token.Length == "[Uses(values[0])]".Length);
        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Keyword
            && token.StartColumn == "[Uses(values[0])] ".Length);
    }

    [Fact]
    public void Collection_expression_at_line_start_is_not_mistaken_for_an_attribute()
    {
        var tokens = new CSharpSyntaxLanguage().Tokenize(["[1, 2]"]);

        Assert.DoesNotContain(tokens[0].Tokens, token => token.Kind == TokenKind.Attribute);
        Assert.Contains(tokens[0].Tokens, token => token.Kind == TokenKind.Number);
    }

    [Fact]
    public void Enter_between_CSharp_braces_creates_an_indented_body_line()
    {
        var services = VimEngineServices.CreateIsolated();
        CSharpEditorIntegration.Configure(services);
        var buffer = new TextBuffer("if (true) {}");
        var cursor = new Editor.Core.Models.CursorPosition(0, buffer.GetLine(0).IndexOf('{') + 1);

        var result = services.EditAssists.OnEnter(new EditContext(buffer, cursor, "Feature.cs", 4, true));

        Assert.True(result.Handled);
        Assert.Equal("if (true) {\n    \n}", buffer.GetText());
        Assert.Equal(new Editor.Core.Models.CursorPosition(1, 4), result.Cursor);
    }

    [Fact]
    public void Enter_before_a_closing_brace_inserts_a_body_line_and_dedents_the_brace()
    {
        var assist = new CSharpEditAssist();
        var buffer = new TextBuffer("class C\n{\n    void M()\n    {\n        Run();\n    }\n}");
        var cursor = new Editor.Core.Models.CursorPosition(5, 4);

        var result = assist.OnEnter(new EditContext(buffer, cursor, "Feature.cs", 4, true));

        Assert.True(result.Handled);
        Assert.Equal(
            "class C\n{\n    void M()\n    {\n        Run();\n        \n    }\n}",
            buffer.GetText());
        Assert.Equal(new Editor.Core.Models.CursorPosition(5, 8), result.Cursor);
    }

    [Fact]
    public void Enter_after_a_switch_label_indents_the_implicit_case_body()
    {
        var assist = new CSharpEditAssist();
        var buffer = new TextBuffer("switch (value)\n{\n    case 1:\n    default:\n}");
        var cursor = new Editor.Core.Models.CursorPosition(2, buffer.GetLine(2).Length);

        var result = assist.OnEnter(new EditContext(buffer, cursor, "Feature.cs", 4, true));

        Assert.True(result.Handled);
        Assert.Equal("switch (value)\n{\n    case 1:\n        \n    default:\n}", buffer.GetText());
        Assert.Equal(new Editor.Core.Models.CursorPosition(3, 8), result.Cursor);
    }

    [Fact]
    public void Open_line_prefix_uses_CSharp_brace_depth_but_ignores_string_braces()
    {
        var assist = new CSharpEditAssist();
        var nested = new TextBuffer("class C\n{\n    void M()\n    {");
        var nestedPrefix = assist.OpenLinePrefix(
            new EditContext(nested, new Editor.Core.Models.CursorPosition(3, nested.GetLine(3).Length),
                "Feature.cs", 4, true), above: false);

        Assert.Equal("        ", nestedPrefix);

        var stringBuffer = new TextBuffer("var text = \"{ not a block }\";");
        var stringPrefix = assist.OpenLinePrefix(
            new EditContext(stringBuffer, new Editor.Core.Models.CursorPosition(0, stringBuffer.GetLine(0).Length),
                "Feature.cs", 4, true), above: false);

        Assert.Equal("", stringPrefix);
    }

    [Theory]
    [InlineData("Run()", "Run();", 6)]
    [InlineData("var value = 1 // keep", "var value = 1; // keep", 22)]
    [InlineData("return value", "return value;", 13)]
    public void Complete_statement_adds_a_semicolon_to_safe_CSharp_statements(
        string source, string expected, int expectedColumn)
    {
        var assist = new CSharpEditAssist();
        var buffer = new TextBuffer(source);
        var cursor = new Editor.Core.Models.CursorPosition(0, source.Length);

        var result = assist.OnCompleteStatement(new EditContext(buffer, cursor, "Feature.cs", 4, true));

        Assert.True(result.Handled);
        Assert.Equal(expected, buffer.GetText());
        Assert.Equal(new Editor.Core.Models.CursorPosition(0, expectedColumn), result.Cursor);
    }

    [Theory]
    [InlineData("if (ready)")]
    [InlineData("public void Run()")]
    [InlineData("var text = \"not a statement ;\";")]
    [InlineData("// Run()")]
    public void Complete_statement_does_not_change_blocks_declarations_or_protected_text(string source)
    {
        var assist = new CSharpEditAssist();
        var buffer = new TextBuffer(source);

        var result = assist.OnCompleteStatement(new EditContext(buffer,
            new Editor.Core.Models.CursorPosition(0, source.Length), "Feature.cs", 4, true));

        Assert.False(result.Handled);
        Assert.Equal(source, buffer.GetText());
    }

    [Fact]
    public void Complete_statement_keeps_semicolons_inside_strings_untouched()
    {
        const string source = "var text = \"not a statement ;\"";
        var assist = new CSharpEditAssist();
        var buffer = new TextBuffer(source);

        var result = assist.OnCompleteStatement(new EditContext(buffer,
            new Editor.Core.Models.CursorPosition(0, source.Length), "Feature.cs", 4, true));

        Assert.True(result.Handled);
        Assert.Equal(source + ";", buffer.GetText());
        Assert.Equal(new Editor.Core.Models.CursorPosition(0, source.Length + 1), result.Cursor);
    }

    [Fact]
    public void Complete_statement_hook_uses_the_editor_transaction_and_undo()
    {
        var services = VimEngineServices.CreateIsolated();
        CSharpEditorIntegration.Configure(services);
        var engine = new VimEngine(engineServices: services);
        engine.RebaseFilePath("Feature.cs");
        engine.SetText("Run()");
        engine.SetVimEnabled(false);
        engine.SetCursorPosition(new Editor.Core.Models.CursorPosition(0, 5));

        var events = engine.CompleteStatement();

        Assert.Contains(events, item => item.Type == VimEventType.TextChanged);
        Assert.Equal("Run();", engine.CurrentBuffer.Text.GetText());
        engine.SetVimEnabled(true);
        engine.ProcessKey("u");
        Assert.Equal("Run()", engine.CurrentBuffer.Text.GetText());
    }

    [Theory]
    [InlineData('{', "if (true) {}", 11)]
    [InlineData('(', "var value = ()", 13)]
    [InlineData('[', "var value = []", 13)]
    [InlineData('"', "var value = \"\"", 13)]
    public void CSharp_code_positions_auto_close_pairs(char typed, string expected, int expectedColumn)
    {
        var assist = new CSharpEditAssist();
        var buffer = new TextBuffer(typed == '{' ? "if (true) " : typed == '"' ? "var value = " : "var value = ");
        var cursor = new Editor.Core.Models.CursorPosition(0, buffer.GetLine(0).Length);

        var result = assist.OnChar(new EditContext(buffer, cursor, "Feature.cs", 4, true), typed);

        Assert.True(result.Handled);
        Assert.Equal(expected, buffer.GetText());
        Assert.Equal(new Editor.Core.Models.CursorPosition(0, expectedColumn), result.Cursor);
    }

    [Fact]
    public void CSharp_pair_assist_does_not_touch_comments_or_strings_and_overtype_skips_close()
    {
        var assist = new CSharpEditAssist();

        var comment = new TextBuffer("// ");
        var commentResult = assist.OnChar(new EditContext(comment,
            new Editor.Core.Models.CursorPosition(0, 3), "Feature.cs", 4, true), '{');
        Assert.False(commentResult.Handled);
        Assert.Equal("// ", comment.GetText());

        var text = new TextBuffer("var value = \"text\"");
        var stringResult = assist.OnChar(new EditContext(text,
            new Editor.Core.Models.CursorPosition(0, 14), "Feature.cs", 4, true), '{');
        Assert.False(stringResult.Handled);
        Assert.Equal("var value = \"text\"", text.GetText());

        var existing = new TextBuffer("if (true) {}");
        var overtype = assist.OnChar(new EditContext(existing,
            new Editor.Core.Models.CursorPosition(0, 11), "Feature.cs", 4, true), '}');
        Assert.True(overtype.Handled);
        Assert.Equal("if (true) {}", existing.GetText());
        Assert.Equal(new Editor.Core.Models.CursorPosition(0, 12), overtype.Cursor);
    }

    [Fact]
    public void Semantic_tokens_are_compatible_with_fallback_lexical_ranges()
    {
        var lines = new[]
        {
            "public class Sample",
            "    // comment",
            "    string text = \"x\";",
        };
        var tokens = new SemanticToken[]
        {
            new(0, 0, 6, "keyword", []),
            new(0, 13, 6, "class", ["declaration"]),
            new(1, 4, 10, "comment", []),
            new(2, 4, 6, "keyword", []),
            new(2, 18, 3, "string", []),
        };

        var result = CSharpSemanticTokenVerifier.Compare(lines, tokens);

        Assert.True(result.IsCompatible);
        Assert.Equal(tokens.Length, result.ComparedTokens);
    }

    [Fact]
    public void Semantic_tokens_cannot_override_a_string_with_an_incompatible_type()
    {
        var result = CSharpSemanticTokenVerifier.Compare(
            ["var text = \"x\";"],
            [new SemanticToken(11, 0, 0, "class", [])]);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Mismatches, mismatch => mismatch.Message.Contains("範囲外"));

        result = CSharpSemanticTokenVerifier.Compare(
            ["var text = \"x\";"],
            [new SemanticToken(0, 11, 3, "class", [])]);
        Assert.False(result.IsCompatible);
        Assert.Contains(result.Mismatches, mismatch => mismatch.Message.Contains("文字列"));
    }

    [Fact]
    public void Organizes_top_level_usings_and_removes_exact_duplicates()
    {
        var path = Path.Combine(Path.GetTempPath(), "LoomoUsingOrganizer.cs");
        var source = "using Zed;\nusing System.Text;\nusing System;\nusing System;\n\nclass Sample {}\n";

        var result = CSharpUsingOrganizer.Organize(path, source);

        Assert.Null(result.Error);
        var edit = Assert.IsType<Editor.Core.Lsp.LspWorkspaceEdit>(result.Edit);
        var replacement = Assert.Single(Assert.Single(edit.Changes).Value).NewText;
        Assert.Equal("using System;\nusing System.Text;\nusing Zed;\n", replacement);
        Assert.Contains("3件", result.Summary);
    }

    [Fact]
    public void Refuses_using_reordering_when_a_comment_is_attached()
    {
        var path = Path.Combine(Path.GetTempPath(), "LoomoUsingComment.cs");
        var result = CSharpUsingOrganizer.Organize(
            path, "// keep this header\nusing Zed;\nusing System;\nclass Sample {}\n");

        Assert.Null(result.Edit);
        Assert.Contains("コメント", result.Error);
    }
}

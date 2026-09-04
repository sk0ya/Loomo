using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>コンストラクターのパラメーターから private readonly フィールドと代入を生成する。</summary>
internal static class CSharpFieldFromParameterGenerator
{
    /// <summary>コンストラクターのパラメーターからprivate readonlyフィールドと代入を生成する。
    /// constructor bodyが複数行で、パラメーターが直接解決できる場合だけ対象にし、既存の
    /// フィールド名との衝突や、ref/outパラメーターからの不正なフィールド生成を拒否する。</summary>
    internal static CSharpCodeGenerationResult Generate(
        string filePath, SourceText source, TypeDeclarationSyntax type, int position,
        CSharpGenerationOptions options)
    {
        if (type is not ClassDeclarationSyntax)
            return CSharpCodeGenerationResult.Failed("フィールド生成はクラスでのみ実行できます。");

        var constructor = type.Members.OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.ParameterList.Parameters.Any(parameter =>
                position >= parameter.SpanStart && position <= parameter.Span.End));
        if (constructor?.Body is not { } body)
            return CSharpCodeGenerationResult.Failed("フィールドを生成するコンストラクターのパラメーターにcaretを置いてください。");

        var parameter = constructor.ParameterList.Parameters.First(candidate =>
            position >= candidate.SpanStart && position <= candidate.Span.End);
        if (parameter.Type is null || parameter.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.RefKeyword) ||
                modifier.IsKind(SyntaxKind.OutKeyword) ||
                modifier.IsKind(SyntaxKind.ThisKeyword)))
            return CSharpCodeGenerationResult.Failed("ref／out／拡張パラメーターからフィールドは生成できません。");

        var fieldName = GenerationNames.ToFieldName(parameter.Identifier.ValueText, options.FieldNaming);
        if (type.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.Declaration.Variables)
            .Any(variable => string.Equals(variable.Identifier.ValueText, fieldName, StringComparison.Ordinal)))
            return CSharpCodeGenerationResult.Failed($"フィールド「{fieldName}」が既にあります。");

        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var fieldTarget = FindFieldInsertion(source, type);
        if (fieldTarget is null)
            return CSharpCodeGenerationResult.Failed("フィールドを安全に挿入できる行を見つけられません。");

        var assignmentTarget = FindConstructorAssignmentInsertion(source, constructor, body);
        if (assignmentTarget is null)
            return CSharpCodeGenerationResult.Failed("コンストラクター本文が複数行でないため、安全に代入を挿入できません。");

        var fieldText = $"{fieldTarget.Value.Indent}private readonly {parameter.Type} {fieldName};{newline}";
        // ValueTextは予約語の先頭に付く「@」を含まない。生成した代入側でも
        // 元のパラメーターを識別子として再構成するため、構文上のTextではなく
        // 共通のエスケープを通す（例: `string @class` → `this._class = @class`）。
        var parameterReference = GenerationNames.EscapeIdentifier(parameter.Identifier.ValueText);
        var assignmentText = $"{assignmentTarget.Value.Indent}this.{fieldName} = {parameterReference};{newline}";
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [uri] =
            [
                new LspTextEdit(
                    new LspRange(fieldTarget.Value.Position, fieldTarget.Value.Position), fieldText),
                new LspTextEdit(
                    new LspRange(assignmentTarget.Value.Position, assignmentTarget.Value.Position),
                    assignmentText),
            ],
        };
        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(changes),
            $"パラメーター「{parameter.Identifier.ValueText}」からフィールド「{fieldName}」を生成");
    }

    private static (LspPosition Position, string Indent)? FindFieldInsertion(
        SourceText source, TypeDeclarationSyntax type)
    {
        var member = type.Members.FirstOrDefault();
        var line = member is null
            ? source.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart)
            : source.Lines.GetLineFromPosition(member.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start,
            member is null ? type.CloseBraceToken.SpanStart : member.SpanStart));
        if (prefix.Any(character => !char.IsWhiteSpace(character))) return null;
        var indent = member is null ? prefix + "    " : prefix;
        return (new LspPosition(line.LineNumber, 0), indent);
    }

    private static (LspPosition Position, string Indent)? FindConstructorAssignmentInsertion(
        SourceText source, ConstructorDeclarationSyntax constructor, BlockSyntax body)
    {
        var firstStatement = body.Statements.FirstOrDefault();
        var line = firstStatement is null
            ? source.Lines.GetLineFromPosition(body.CloseBraceToken.SpanStart)
            : source.Lines.GetLineFromPosition(firstStatement.SpanStart);
        var prefixEnd = firstStatement is null ? body.CloseBraceToken.SpanStart : firstStatement.SpanStart;
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, prefixEnd));
        if (prefix.Any(character => !char.IsWhiteSpace(character))) return null;

        var indent = firstStatement is not null
            ? prefix
            : new string(source.ToString(TextSpan.FromBounds(
                source.Lines.GetLineFromPosition(constructor.SpanStart).Start,
                constructor.SpanStart)).TakeWhile(char.IsWhiteSpace).ToArray()) + "    ";
        return (new LspPosition(line.LineNumber, 0), indent);
    }
}

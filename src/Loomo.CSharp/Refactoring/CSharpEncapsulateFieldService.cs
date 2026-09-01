using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>非公開フィールドを公開プロパティから読み取れる形へカプセル化する。
/// 意味モデルなしで既存コードの評価順序を変えないため、フィールドとプロパティの追加だけを行い、
/// 既存の参照や代入は書き換えない。</summary>
public static class CSharpEncapsulateFieldService
{
    public static CSharpCodeGenerationResult Encapsulate(
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName)
        => EncapsulateCore(filePath, sourceText, selection, propertyName,
            semanticCompilation: null);

    internal static CSharpCodeGenerationResult Encapsulate(
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName,
        CSharpCompilation semanticCompilation)
        => EncapsulateCore(filePath, sourceText, selection, propertyName,
            semanticCompilation);

    private static CSharpCodeGenerationResult EncapsulateCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string propertyName,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみフィールドのカプセル化を実行できます。");
        if (!SyntaxFacts.IsValidIdentifier(propertyName)
            || SyntaxFacts.GetKeywordKind(propertyName) != SyntaxKind.None)
            return Failed("プロパティ名がC#の識別子として正しくありません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var field = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(candidate => candidate.Declaration.Variables.Count == 1)
            .FirstOrDefault(candidate => candidate.Declaration.Variables[0].Identifier.Span == selectedSpan);
        if (field is null)
            return Failed("フィールド名全体を選択してください。");
        if (field.Parent is not TypeDeclarationSyntax type || type.CloseBraceToken.IsMissing)
            return Failed("型の中にあるフィールドを選択してください。");
        if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword)
                || modifier.IsKind(SyntaxKind.InternalKeyword)))
            return Failed("既に公開されているフィールドはカプセル化の対象外です。");
        if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ConstKeyword)))
            return Failed("constフィールドはプロパティへカプセル化できません。");

        var variable = field.Declaration.Variables[0];
        var fieldName = variable.Identifier.ValueText;
        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        if (semanticCompilation is not null && semanticModel is null)
            return Failed("対象ファイルをC#の意味モデルから解決できません。");
        var semanticField = semanticModel is not null &&
            FindEquivalent(variable, semanticModel) is { } semanticVariable
            ? semanticModel.GetDeclaredSymbol(semanticVariable) as IFieldSymbol
            : null;
        if (semanticCompilation is not null && semanticField is null)
            return Failed("選択フィールドをC#の意味モデルから解決できません。");
        if (semanticField is not null)
        {
            fieldName = semanticField.Name;
            if (semanticField.Type is IErrorTypeSymbol)
                return Failed("フィールドの型をC#の意味モデルから解決できません。");
            if (semanticField.ContainingType.GetMembers(propertyName).Any(member =>
                    !SymbolEqualityComparer.Default.Equals(member, semanticField)))
                return Failed("同名のプロパティまたはメンバーが既にあります。");
        }
        if (type.Members.Any(member => HasDeclaredName(member, propertyName)))
            return Failed("同名のプロパティまたはメンバーが既にあります。");

        var line = source.Lines.GetLineFromPosition(field.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, field.SpanStart));
        var suffix = source.ToString(TextSpan.FromBounds(field.Span.End, line.End));
        if (prefix.Any(character => !char.IsWhiteSpace(character)) || suffix.Trim().Length > 0)
            return Failed("フィールド宣言は単独行に置かれている必要があります。");

        var typeName = semanticField?.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? field.Declaration.Type.ToString();
        if (string.Equals(typeName, "var", StringComparison.Ordinal))
            return Failed("var型のフィールドはカプセル化できません。");

        var isStatic = semanticField?.IsStatic == true ||
            field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword));
        var isReadOnly = semanticField?.IsReadOnly == true ||
            field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword));
        var access = isStatic
            ? "public static"
            : "public";
        var property = isReadOnly
            ? $"{access} {typeName} {propertyName} => {fieldName};"
            : $"{access} {typeName} {propertyName} {{ get => {fieldName}; set => {fieldName} = value; }}";
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indentation = prefix;
        var insertion = $"{indentation}{property}{newline}";
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] =
                [
                    new LspTextEdit(
                        new LspRange(
                            new LspPosition(line.LineNumber + 1, 0),
                            new LspPosition(line.LineNumber + 1, 0)),
                        insertion),
                ],
            });
        return new CSharpCodeGenerationResult(
            edit, $"フィールド「{fieldName}」をプロパティ「{propertyName}」へカプセル化");
    }

    public static string DefaultPropertyName(string fieldName)
    {
        var name = fieldName.TrimStart('_');
        if (name.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
            name = name[2..];
        if (name.Length == 0) name = "Value";
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static bool HasDeclaredName(MemberDeclarationSyntax member, string name)
        => member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables.Any(variable =>
                string.Equals(variable.Identifier.ValueText, name, StringComparison.Ordinal)),
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.Any(variable =>
                string.Equals(variable.Identifier.ValueText, name, StringComparison.Ordinal)),
            PropertyDeclarationSyntax property => string.Equals(
                property.Identifier.ValueText, name, StringComparison.Ordinal),
            MethodDeclarationSyntax method => string.Equals(
                method.Identifier.ValueText, name, StringComparison.Ordinal),
            ConstructorDeclarationSyntax constructor => string.Equals(
                constructor.Identifier.ValueText, name, StringComparison.Ordinal),
            DestructorDeclarationSyntax destructor => string.Equals(
                destructor.Identifier.ValueText, name, StringComparison.Ordinal),
            EventDeclarationSyntax @event => string.Equals(
                @event.Identifier.ValueText, name, StringComparison.Ordinal),
            TypeDeclarationSyntax nestedType => string.Equals(
                nestedType.Identifier.ValueText, name, StringComparison.Ordinal),
            EnumDeclarationSyntax nestedEnum => string.Equals(
                nestedEnum.Identifier.ValueText, name, StringComparison.Ordinal),
            DelegateDeclarationSyntax nestedDelegate => string.Equals(
                nestedDelegate.Identifier.ValueText, name, StringComparison.Ordinal),
            _ => false,
        };

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static bool TryGetSelectionSpan(SourceText source, LspRange range, out TextSpan span)
    {
        span = default;
        if (range.Start.Line < 0 || range.End.Line < 0
            || range.Start.Line >= source.Lines.Count || range.End.Line >= source.Lines.Count)
            return false;
        var start = Position(source, range.Start);
        var end = Position(source, range.End);
        if (start > end) (start, end) = (end, start);
        if (start == end) return false;
        span = TextSpan.FromBounds(start, end);
        return true;
    }

    private static int Position(SourceText source, LspPosition position)
    {
        var line = source.Lines[position.Line];
        return line.Start + Math.Clamp(position.Character, 0, line.Span.Length);
    }

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}

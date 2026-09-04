using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>未定義のローカル／this メソッド呼び出しから、呼び出し先の private メソッドを生成する。</summary>
internal static class CSharpMethodFromUsageGenerator
{
    /// <summary>未定義のローカル／thisメソッド呼び出しから、呼び出し先のprivateメソッドを生成する。
    /// 意味モデルを持たないため、対象は現在の型に属する呼び出しだけに限定し、引数は構文から安全に推測できる型へ落とす。</summary>
    internal static (string? Text, string? Summary, string? Error) Generate(
        TypeDeclarationSyntax type, SyntaxNode root, int position, CSharpGenerationOptions options,
        SemanticModel? semanticModel)
    {
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(candidate => position >= candidate.SpanStart && position <= candidate.Span.End)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (invocation is null || !invocation.Ancestors().Contains(type))
            return (null, null, "未定義メソッド呼び出しの中にキャレットを置いてください。");

        var methodName = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax
                => member.Name.Identifier.ValueText,
            _ => "",
        };
        if (methodName.Length == 0 || !SyntaxFacts.IsValidIdentifier(methodName))
            return (null, null, "現在の型に生成できるローカル／thisメソッド呼び出しではありません。");

        var genericArity = invocation.Expression switch
        {
            GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Count,
            MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax &&
                member.Name is GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Count,
            _ => 0,
        };
        if (type.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                string.Equals(method.Identifier.ValueText, methodName, StringComparison.Ordinal)
                && method.ParameterList.Parameters.Count == invocation.ArgumentList.Arguments.Count
                && (method.TypeParameterList?.Parameters.Count ?? 0) == genericArity))
            return (null, null, "同じ名前と引数数のメソッドが既にあります。");

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<string>();
        foreach (var (argument, index) in invocation.ArgumentList.Arguments.Select((value, index) => (value, index)))
        {
            var requestedName = argument.NameColon?.Name.Identifier.ValueText;
            var name = GenerationNames.MakeUniqueParameterName(
                string.IsNullOrWhiteSpace(requestedName) ? $"arg{index + 1}" : requestedName,
                usedNames, options.ParameterNaming);
            var modifier = argument.RefKindKeyword.Kind() switch
            {
                SyntaxKind.RefKeyword => "ref ",
                SyntaxKind.OutKeyword => "out ",
                SyntaxKind.InKeyword => "in ",
                _ => "",
            };
            parameters.Add($"{modifier}{InferUsageArgumentType(
                argument.Expression, options.NullableEnabled, semanticModel)} {name}");
        }

        var returnType = InferUsageReturnType(invocation, options.NullableEnabled);
        var body = "    throw new global::System.NotImplementedException();";
        var typeParameters = genericArity == 0
            ? ""
            : "<" + string.Join(", ", Enumerable.Range(1, genericArity).Select(index => $"T{index}")) + ">";
        var generated = $"private {returnType} {methodName}{typeParameters}({string.Join(", ", parameters)})\n{{\n{body}\n}}";
        return (generated, "使用箇所からメソッドを生成", null);
    }

    private static string InferUsageReturnType(InvocationExpressionSyntax invocation, bool nullableEnabled)
    {
        var returnStatement = invocation.Ancestors().OfType<ReturnStatementSyntax>().FirstOrDefault();
        if (returnStatement is not null)
        {
            var declared = returnStatement.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (declared is not null) return declared.ReturnType.ToString();
            var local = returnStatement.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();
            if (local is not null) return local.ReturnType.ToString();
            return nullableEnabled ? "object?" : "object";
        }

        var variable = invocation.Ancestors().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Initializer?.Value, invocation));
        if (variable?.Parent?.Parent is VariableDeclarationSyntax declaration
            && !declaration.Type.IsVar)
            return declaration.Type.ToString();

        return invocation.Ancestors().OfType<ExpressionStatementSyntax>().Any()
            ? "void"
            : nullableEnabled ? "object?" : "object";
    }

    private static string InferUsageArgumentType(
        ExpressionSyntax expression, bool nullableEnabled, SemanticModel? semanticModel)
    {
        if (semanticModel is not null)
        {
            try
            {
                var semanticExpression = semanticModel.SyntaxTree.GetRoot().DescendantNodes()
                    .OfType<ExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.SpanStart == expression.SpanStart &&
                        candidate.Span.Length == expression.Span.Length &&
                        candidate.RawKind == expression.RawKind);
                if (semanticExpression is null) return InferUsageArgumentType(expression, nullableEnabled, null);
                var typeInfo = semanticModel.GetTypeInfo(semanticExpression);
                var semanticType = typeInfo.ConvertedType ?? typeInfo.Type;
                if (semanticType is { TypeKind: not TypeKind.Error })
                {
                    if (!nullableEnabled && semanticType.IsReferenceType)
                        semanticType = semanticType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                    return MemberFormat.DisplayGeneratedType(semanticType);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        return expression switch
        {
            LiteralExpressionSyntax literal => literal.Kind() switch
            {
                SyntaxKind.StringLiteralExpression or SyntaxKind.InterpolatedStringExpression => "string",
                SyntaxKind.CharacterLiteralExpression => "char",
                SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression => "bool",
                SyntaxKind.NumericLiteralExpression => InferNumericType(literal.Token.Text),
                SyntaxKind.NullLiteralExpression => nullableEnabled ? "object?" : "object",
                _ => nullableEnabled ? "object?" : "object",
            },
            InterpolatedStringExpressionSyntax => "string",
            ObjectCreationExpressionSyntax creation => creation.Type.ToString(),
            ArrayCreationExpressionSyntax creation => creation.Type.ToString(),
            ImplicitArrayCreationExpressionSyntax => "global::System.Array",
            CastExpressionSyntax cast => cast.Type.ToString(),
            DefaultExpressionSyntax @default => @default.Type.ToString(),
            TypeOfExpressionSyntax => "global::System.Type",
            AnonymousObjectCreationExpressionSyntax => "object",
            SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax => "global::System.Delegate",
            _ => nullableEnabled ? "object?" : "object",
        };
    }

    private static string InferNumericType(string token)
    {
        var value = token.Trim();
        if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase)) return "decimal";
        if (value.EndsWith("f", StringComparison.OrdinalIgnoreCase)) return "float";
        if (value.EndsWith("d", StringComparison.OrdinalIgnoreCase)
            || value.Contains('.', StringComparison.Ordinal)
            || value.Contains('e', StringComparison.OrdinalIgnoreCase)) return "double";
        if (value.EndsWith("ul", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("lu", StringComparison.OrdinalIgnoreCase)) return "ulong";
        if (value.EndsWith("u", StringComparison.OrdinalIgnoreCase)) return "uint";
        if (value.EndsWith("l", StringComparison.OrdinalIgnoreCase)) return "long";
        return "int";
    }
}

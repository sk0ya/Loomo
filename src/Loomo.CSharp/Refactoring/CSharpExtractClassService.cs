using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>型の状態と、それだけを使う単純なメンバーを別クラスへ抽出する。
/// 意味モデルを持たないため、field／auto-property／単純なinstance methodに限定し、
/// 元クラスには委譲ラッパーを残して既存の呼び出し側を壊さない。</summary>
public static class CSharpExtractClassService
{
    public static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string extractedClassName,
        string destinationFilePath,
        CSharpCompilation? semanticCompilation = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみクラス抽出を実行できます。");
        if (!string.Equals(Path.GetExtension(destinationFilePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("抽出先は .cs ファイルにしてください。");
        if (!SyntaxFacts.IsValidIdentifier(extractedClassName.Trim()))
            return Failed("抽出先クラス名がC#の識別子ではありません。");

        var sourcePath = Path.GetFullPath(filePath);
        var destinationPath = Path.GetFullPath(destinationFilePath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            return Failed("抽出元と抽出先が同じファイルです。");
        if (File.Exists(destinationPath))
            return Failed("抽出先ファイルが既に存在します。");
        if (Path.GetDirectoryName(destinationPath) is not { } destinationDirectory ||
            !Directory.Exists(destinationDirectory))
            return Failed("抽出先フォルダーが存在しません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(IsSupportedContainingType)
            .FirstOrDefault(candidate => candidate.Span.Contains(selectedSpan));
        if (type is null)
            return Failed("クラスまたは構造体のメンバーを選択してください。");
        if (type is not ClassDeclarationSyntax)
            return Failed("クラス抽出はclassに対してのみ実行できます。");
        if (type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)) &&
            semanticCompilation is null)
            return Failed("partialクラスは全パーツを確認してから抽出してください。");
        if (type.TypeParameterList is not null && semanticCompilation is null)
            return Failed("ジェネリッククラスの抽出は意味モデルが必要です。");

        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, sourcePath)
            : null;
        var semanticType = semanticModel is null
            ? null
            : FindEquivalentType(type, semanticModel);
        var semanticTypeSymbol = semanticModel is not null && semanticType is not null
            ? semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
            : null;
        if (type.TypeParameterList is not null && semanticTypeSymbol is null)
            return Failed("ジェネリッククラスの意味モデルを解決できません。");
        if (type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)) &&
            semanticTypeSymbol is null)
            return Failed("partialクラスの全パーツを意味モデルから解決できません。");

        var selected = type.Members
            .Where(member => selectedSpan.Contains(member.Span))
            .ToArray();
        if (selected.Length == 0)
            return Failed("メンバー全体を選択してください。");

        var firstIndex = type.Members.IndexOf(selected[0]);
        var lastIndex = type.Members.IndexOf(selected[^1]);
        if (firstIndex < 0 || lastIndex < firstIndex ||
            type.Members.Skip(firstIndex).Take(lastIndex - firstIndex + 1).Count() != selected.Length)
            return Failed("抽出するメンバーは連続した範囲で選択してください。");

        var names = selected.Select(MemberName).Where(name => name is not null).Select(name => name!).ToHashSet(StringComparer.Ordinal);
        if (names.Count != selected.Length ||
            string.Equals(type.Identifier.ValueText, extractedClassName.Trim(), StringComparison.Ordinal))
            return Failed("抽出対象のメンバー名を解決できないか、クラス名が衝突しています。");

        var allMemberNames = type.Members.Select(MemberName).Where(name => name is not null).Select(name => name!).ToHashSet(StringComparer.Ordinal);
        if (semanticTypeSymbol is not null)
        {
            foreach (var member in semanticTypeSymbol.GetMembers()
                         .Where(member => !member.IsImplicitlyDeclared))
                allMemberNames.Add(member.Name);
        }
        foreach (var member in selected)
        {
            if (!CanExtract(member, names, allMemberNames, semanticModel, semanticTypeSymbol, out var error))
                return Failed(error!);
            if (member is FieldDeclarationSyntax field && field.Declaration.Variables.Count == 1)
            {
                var fieldName = field.Declaration.Variables[0].Identifier.ValueText;
                if (type.DescendantNodes().OfType<ArgumentSyntax>().Any(argument =>
                    argument.RefKindKeyword.Kind() is (SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword) &&
                    argument.Expression is IdentifierNameSyntax identifier &&
                    string.Equals(identifier.Identifier.ValueText, fieldName, StringComparison.Ordinal)))
                    return Failed("ref／out／inで渡すfieldはpropertyへ安全に変換できないため抽出できません。");
            }
        }

        var componentName = MakeComponentName(extractedClassName.Trim(), allMemberNames);
        var indent = MemberIndent(source, selected[0]);
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var extractedMembers = string.Join(newline + newline,
            selected.Select(member => IndentMember(ExtractedMemberText(member), "    ", newline)));
        var namespaceName = string.Join(".", type.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(namespaceNode => namespaceNode.Name.ToString()));
        var typeParameters = type.TypeParameterList?.ToString() ?? "";
        var extractedTypeName = extractedClassName.Trim() + typeParameters;
        var destinationText = BuildDestinationText(root, namespaceName, extractedClassName.Trim(),
            type.TypeParameterList, type.ConstraintClauses, extractedMembers, newline);
        var component = $"private readonly {extractedTypeName} {componentName} = new();";

        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase);
        var sourceUri = LspUri.FromPath(sourcePath);
        var edits = new List<LspTextEdit>();
        foreach (var member in selected)
        {
            var wrapper = BuildWrapper(member, componentName, newline);
            if (member == selected[0]) wrapper = component + newline + newline + wrapper;
            edits.Add(new LspTextEdit(ToLspRange(source, member.Span),
                IndentGenerated(wrapper, indent, newline)));
        }
        changes[sourceUri] = edits;
        var destinationUri = LspUri.FromPath(destinationPath);
        changes[destinationUri] = [new LspTextEdit(
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)), destinationText)];

        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(changes,
                FileOperations: [new LspFileOperation(LspFileOperationKind.Create, destinationUri)]),
            $"メンバー{selected.Length}件をクラス「{extractedClassName.Trim()}」へ抽出");
    }

    private static bool CanExtract(
        MemberDeclarationSyntax member,
        IReadOnlySet<string> selectedNames,
        IReadOnlySet<string> allMemberNames,
        SemanticModel? semanticModel,
        INamedTypeSymbol? semanticTypeSymbol,
        out string? error)
    {
        error = null;
        if (member.AttributeLists.Count > 0)
        {
            error = "属性付きメンバーは抽出先との意味差分を確認できないため対象外です。";
            return false;
        }

        switch (member)
        {
            case FieldDeclarationSyntax field:
                if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword) ||
                        modifier.IsKind(SyntaxKind.ProtectedKeyword)))
                {
                    error = "public／protected fieldは抽出先でpropertyへ変わるとAPIが変わるため対象外です。";
                    return false;
                }
                if (field.Declaration.Variables.Count != 1 ||
                    field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword) ||
                        modifier.IsKind(SyntaxKind.ReadOnlyKeyword) ||
                        modifier.IsKind(SyntaxKind.ConstKeyword) ||
                        modifier.IsKind(SyntaxKind.VolatileKeyword)) ||
                    field.Declaration.Variables[0].Initializer is not null)
                {
                    error = "抽出できるfieldは、初期値のない単一のinstance fieldだけです。";
                    return false;
                }
                return true;

            case PropertyDeclarationSyntax property:
                if (property.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword) ||
                        modifier.IsKind(SyntaxKind.AbstractKeyword) ||
                        modifier.IsKind(SyntaxKind.RequiredKeyword)) ||
                    property.AccessorList is null ||
                    property.AccessorList.Accessors.Any(accessor => accessor.Body is not null ||
                        accessor.ExpressionBody is not null) ||
                    property.Initializer is not null)
                {
                    error = "抽出できるpropertyは、初期値と本文のないinstance auto-propertyだけです。";
                    return false;
                }
                return true;

            case MethodDeclarationSyntax method:
                if (method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword) ||
                        modifier.IsKind(SyntaxKind.AbstractKeyword) ||
                        modifier.IsKind(SyntaxKind.OverrideKeyword) ||
                        modifier.IsKind(SyntaxKind.AsyncKeyword) ||
                        modifier.IsKind(SyntaxKind.ExternKeyword) ||
                        modifier.IsKind(SyntaxKind.PartialKeyword)) ||
                    method.Body is null || method.ExpressionBody is not null ||
                    method.TypeParameterList is not null || method.ReturnType is RefTypeSyntax ||
                    method.ParameterList.Parameters.Any(parameter => parameter.Type is null))
                {
                    error = "抽出できるmethodは、単純なinstance method（本文・型引数なし）だけです。";
                    return false;
                }
                if (method.DescendantNodes().OfType<ThisExpressionSyntax>().Any() ||
                    method.DescendantNodes().OfType<BaseExpressionSyntax>().Any())
                {
                    error = "this／baseを使うmethodは、抽出先での意味を安全に解決できないため対象外です。";
                    return false;
                }
                var unselectedNames = allMemberNames.Except(selectedNames, StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);
                if (ReferencesUnselectedMember(method, unselectedNames, semanticModel, semanticTypeSymbol))
                {
                    error = "選択範囲外のメンバーを参照するmethodは抽出できません。";
                    return false;
                }
                return true;

            default:
                error = "抽出対象はfield、auto-property、単純なinstance methodに限定しています。";
                return false;
        }
    }

    private static bool ReferencesUnselectedMember(
        MethodDeclarationSyntax method,
        IReadOnlySet<string> unselectedNames,
        SemanticModel? semanticModel,
        INamedTypeSymbol? semanticTypeSymbol)
    {
        foreach (var identifier in method.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!unselectedNames.Contains(identifier.Identifier.ValueText)) continue;

            if (semanticModel is null || semanticTypeSymbol is null)
                return true;

            var semanticIdentifier = semanticModel.SyntaxTree.GetRoot().DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(candidate => candidate.SpanStart == identifier.SpanStart &&
                    string.Equals(candidate.Identifier.ValueText, identifier.Identifier.ValueText,
                        StringComparison.Ordinal));
            var symbol = semanticIdentifier is null
                ? null
                : semanticModel.GetSymbolInfo(semanticIdentifier).Symbol;
            if (symbol is null)
                return true;

            if ((symbol is IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol) &&
                SymbolEqualityComparer.Default.Equals(symbol.ContainingType, semanticTypeSymbol))
                return true;
        }

        return false;
    }

    private static string BuildWrapper(MemberDeclarationSyntax member, string componentName, string newline)
        => member switch
        {
            FieldDeclarationSyntax field => BuildFieldWrapper(field, componentName),
            PropertyDeclarationSyntax property => BuildPropertyWrapper(property, componentName),
            MethodDeclarationSyntax method => BuildMethodWrapper(method, componentName, newline),
            _ => throw new InvalidOperationException("未対応のメンバーです。"),
        };

    private static string BuildFieldWrapper(FieldDeclarationSyntax field, string componentName)
    {
        var variable = field.Declaration.Variables[0];
        var modifiers = field.Modifiers.ToFullString().Trim();
        if (modifiers.Length == 0) modifiers = "private";
        return $"{modifiers} {field.Declaration.Type} {variable.Identifier.ValueText} {{ get => {componentName}.{variable.Identifier.ValueText}; set => {componentName}.{variable.Identifier.ValueText} = value; }}";
    }

    private static string BuildPropertyWrapper(PropertyDeclarationSyntax property, string componentName)
    {
        var modifiers = property.Modifiers.ToFullString().Trim();
        var accessors = property.AccessorList!.Accessors.Select(accessor =>
        {
            var accessorModifiers = accessor.Modifiers.ToFullString().Trim();
            var prefix = accessorModifiers.Length == 0 ? "" : accessorModifiers + " ";
            var name = accessor.Keyword.ValueText;
            return $"{prefix}{name} => {componentName}.{property.Identifier.ValueText}" +
                (name is "set" or "init" ? " = value" : "") + ";";
        });
        var modifierPrefix = modifiers.Length == 0 ? "private" : modifiers;
        return $"{modifierPrefix} {property.Type} {property.Identifier.ValueText} {{ {string.Join(" ", accessors)} }}";
    }

    private static string BuildMethodWrapper(MethodDeclarationSyntax method, string componentName, string newline)
    {
        var modifiers = method.Modifiers.ToFullString().Trim();
        if (modifiers.Length == 0) modifiers = "private";
        var parameterDeclarations = string.Join(", ", method.ParameterList.Parameters
            .Select(parameter => parameter.ToFullString().Trim()));
        var arguments = string.Join(", ", method.ParameterList.Parameters.Select(parameter =>
        {
            var modifier = parameter.Modifiers.ToFullString().Trim();
            return (modifier.Length == 0 ? "" : modifier + " ") + parameter.Identifier.ValueText;
        }));
        var invocation = $"{componentName}.{method.Identifier.ValueText}({arguments})";
        var body = method.ReturnType is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword)
            ? $"{method.ReturnType} {method.Identifier.ValueText}{method.TypeParameterList?.ToFullString() ?? ""}({parameterDeclarations})\n{{\n    {invocation};\n}}"
            : $"{method.ReturnType} {method.Identifier.ValueText}{method.TypeParameterList?.ToFullString() ?? ""}({parameterDeclarations})\n{{\n    return {invocation};\n}}";
        return modifiers + " " + body.Replace("\n", newline, StringComparison.Ordinal);
    }

    private static string ExtractedMemberText(MemberDeclarationSyntax member)
    {
        var text = member.ToFullString().Trim();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var contentIndent = lines.Where(line => line.Trim().Length > 0)
            .Select(line => line.TakeWhile(char.IsWhiteSpace).Count()).DefaultIfEmpty(0).Min();
        if (contentIndent > 0) text = string.Join("\n", lines.Select(line =>
            line.Length >= contentIndent ? line[contentIndent..] : line.TrimStart()));

        var first = text.TrimStart();
        var accessibility = first.StartsWith("public ", StringComparison.Ordinal) ? "public " :
            first.StartsWith("protected ", StringComparison.Ordinal) ? "protected " :
            first.StartsWith("internal ", StringComparison.Ordinal) ? "internal " :
            first.StartsWith("private ", StringComparison.Ordinal) ? "private " : "";
        if (accessibility.Length > 0)
            text = first[accessibility.Length..].TrimStart();
        return "internal " + text;
    }

    private static string BuildDestinationText(
        CompilationUnitSyntax root, string namespaceName, string className,
        TypeParameterListSyntax? typeParameters,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraints,
        string members, string newline)
    {
        var parts = new List<string>();
        var usings = root.Usings.ToFullString().Trim();
        if (usings.Length > 0) parts.Add(usings);
        if (namespaceName.Length > 0) parts.Add("namespace " + namespaceName + ";");
        var suffix = typeParameters?.ToString() ?? "";
        if (constraints.Count > 0)
            suffix += " " + string.Join(" ", constraints.Select(constraint => constraint.ToString().Trim()));
        parts.Add("internal sealed class " + className + suffix + newline + "{" + newline + members + newline + "}");
        return string.Join(newline + newline, parts) + newline;
    }

    private static string IndentMember(string text, string indent, string newline)
        => string.Join(newline, text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').Select(line => indent + line));

    private static string IndentGenerated(string text, string indent, string newline)
        => string.Join(newline, text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').Select(line => indent + line));

    private static string MemberIndent(SourceText source, MemberDeclarationSyntax member)
    {
        var line = source.Lines.GetLineFromPosition(member.SpanStart);
        return source.ToString(TextSpan.FromBounds(line.Start, member.SpanStart));
    }

    private static string MakeComponentName(string className, IReadOnlySet<string> names)
    {
        var baseName = "_" + char.ToLowerInvariant(className[0]) + className[1..];
        var name = baseName;
        for (var i = 2; names.Contains(name); i++) name = baseName + i;
        return name;
    }

    private static string? MemberName(MemberDeclarationSyntax member)
        => member switch
        {
            FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1
                => field.Declaration.Variables[0].Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            _ => null,
        };

    private static bool IsSupportedContainingType(TypeDeclarationSyntax type)
        => type is ClassDeclarationSyntax &&
           (type.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax);

    private static TypeDeclarationSyntax? FindEquivalentType(
        TypeDeclarationSyntax target, SemanticModel semanticModel)
        => semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == target.SpanStart &&
                candidate.RawKind == target.RawKind &&
                string.Equals(candidate.Identifier.ValueText, target.Identifier.ValueText,
                    StringComparison.Ordinal));

    private static bool TryGetSelectionSpan(SourceText source, LspRange range, out TextSpan span)
    {
        span = default;
        if (range.Start.Line < 0 || range.End.Line < 0 ||
            range.Start.Line >= source.Lines.Count || range.End.Line >= source.Lines.Count)
            return false;
        var start = Position(source, range.Start);
        var end = Position(source, range.End);
        if (start > end) (start, end) = (end, start);
        if (start == end) return false;
        span = TextSpan.FromBounds(start, end);
        return true;
    }

    private static int Position(SourceText source, LspPosition position)
        => source.Lines[position.Line].Start + Math.Clamp(position.Character, 0, source.Lines[position.Line].Span.Length);

    private static LspRange ToLspRange(SourceText source, TextSpan span)
    {
        var start = source.Lines.GetLinePosition(span.Start);
        var end = source.Lines.GetLinePosition(span.End);
        return new LspRange(new LspPosition(start.Line, start.Character),
            new LspPosition(end.Line, end.Character));
    }

    private static CSharpCodeGenerationResult Failed(string error) => new(null, "", error);

    private static class SyntaxFacts
    {
        public static bool IsValidIdentifier(string value)
            => value.Length > 0 && Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(value) &&
               Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None;
    }
}

using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>既存メソッドへ新しいパラメーターを追加し、ワークスペース内で構文上確実に
/// 対応付けられる呼び出しへ同じ引数を追加する。型解決できない呼び出しやメソッドグループを
/// 見つけた場合は、宣言だけを変更せず計画全体を中止する。</summary>
public static class CSharpIntroduceParameterService
{
    public static CSharpCodeGenerationResult Introduce(
        string filePath,
        string sourceText,
        LspRange selection,
        string parameterName,
        string parameterType,
        string callSiteArgument,
        IReadOnlyDictionary<string, string>? workspaceTexts = null,
        string? defaultValue = null,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions = null)
        => IntroduceCore(filePath, sourceText, selection, parameterName,
            parameterType, callSiteArgument, workspaceTexts, defaultValue,
            workspaceParseOptions, semanticCompilation: null);

    internal static CSharpCodeGenerationResult Introduce(
        string filePath,
        string sourceText,
        LspRange selection,
        string parameterName,
        string parameterType,
        string callSiteArgument,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        string? defaultValue,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation semanticCompilation)
        => IntroduceCore(filePath, sourceText, selection, parameterName,
            parameterType, callSiteArgument, workspaceTexts, defaultValue,
            workspaceParseOptions, semanticCompilation);

    private static CSharpCodeGenerationResult IntroduceCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string parameterName,
        string parameterType,
        string callSiteArgument,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        string? defaultValue,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみパラメーターを導入できます。");
        parameterName = parameterName.Trim();
        parameterType = parameterType.Trim();
        callSiteArgument = callSiteArgument.Trim();
        defaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim();
        if (parameterName.Length == 0 || parameterType.Length == 0 || callSiteArgument.Length == 0)
            return Failed("パラメーター名・型・呼び出し側の値をすべて指定してください。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var activeRoot = CSharpSyntaxTree.ParseText(source,
            ParseOptionsFor(filePath, workspaceParseOptions)).GetCompilationUnitRoot();
        var target = FindTargetMethod(activeRoot, selectedSpan);
        if (target is null)
            return Failed("パラメーターを導入するメソッド名全体を選択してください。");
        if (target.Parent is not ClassDeclarationSyntax containingType)
            return Failed("クラス直下のメソッドだけを対象にできます。");
        if (containingType.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))
            || containingType.TypeParameterList is not null
            || target.TypeParameterList is not null)
            return Failed("partial／generic型またはgenericメソッドは対象外です。");
        if (target.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OverrideKeyword)
                || modifier.IsKind(SyntaxKind.AbstractKeyword))
            || target.ExplicitInterfaceSpecifier is not null)
            return Failed("override／abstract／明示的interface実装にはパラメーターを導入できません。");
        if (target.ParameterList.Parameters.Any(parameter =>
                parameter.Default is not null
                || parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ParamsKeyword))
                || parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ThisKeyword))))
            return Failed("既定値付き・params・拡張メソッドのパラメーターを持つメソッドは対象外です。");
        if (target.ParameterList.Parameters.Any(parameter =>
                string.Equals(parameter.Identifier.ValueText, parameterName, StringComparison.Ordinal)))
            return Failed("同名のパラメーターが既にあります。");
        if (target.ParameterList.Parameters.Count > 0
            && target.ParameterList.Parameters.Last().Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.ParamsKeyword)))
            return Failed("paramsパラメーターの後ろへ追加できません。");

        var newParameter = ParseParameter(parameterType, parameterName, defaultValue);
        if (newParameter is null)
            return Failed("パラメーターの型または名前がC#構文として解釈できません。");
        if (defaultValue is null && target.ParameterList.Parameters.Any(parameter => parameter.Default is not null))
            return Failed("既定値付きパラメーターの後ろへ必須パラメーターは追加できません。");
        if (!IsExpression(callSiteArgument))
            return Failed("呼び出し側の値がC#の式として解釈できません。");
        if (defaultValue is not null && !IsExpression(defaultValue))
            return Failed("パラメーターの既定値がC#の式として解釈できません。");

        var files = ParseWorkspaceRoots(filePath, activeRoot, workspaceTexts, workspaceParseOptions);
        var targetNamespace = NamespaceName(containingType);
        var targetTypeName = containingType.Identifier.ValueText;
        var targetName = target.Identifier.ValueText;
        var targetIsStatic = target.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword));
        var activeSemanticModel = semanticCompilation is { } semanticCompilationInstance
            ? CSharpSemanticCompilation.ForFile(semanticCompilationInstance, filePath)
            : null;
        var semanticTarget = activeSemanticModel is not null
            ? FindEquivalent(target, activeSemanticModel) is { } equivalent
                ? activeSemanticModel.GetDeclaredSymbol(equivalent)
                : null
            : null;
        if (semanticCompilation is not null && semanticTarget is not IMethodSymbol)
            return Failed("対象メソッドをC#の意味モデルから解決できないため、パラメーター導入を中止しました。");
        if (semanticTarget is IMethodSymbol semanticMethod &&
            ImplementsInterfaceMember(semanticMethod))
            return Failed("interface契約を変更せずに実装メソッドへパラメーターを導入できません。");
        var callEdits = new Dictionary<string, List<LspTextEdit>>(StringComparer.OrdinalIgnoreCase);
        var callCount = 0;

        foreach (var file in files)
        {
            var semanticModel = semanticCompilation is { } workspaceCompilation
                ? CSharpSemanticCompilation.ForFile(workspaceCompilation, file.Path)
                : null;
            foreach (var invocation in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!string.Equals(InvocationName(invocation), targetName, StringComparison.Ordinal))
                    continue;

                if (semanticModel is not null && semanticTarget is IMethodSymbol targetSymbol)
                {
                    if (FindEquivalent(invocation, semanticModel) is not { } semanticInvocation)
                        return Failed("呼び出し先を意味モデルへ対応付けられないため、パラメーター導入を中止しました。");
                    var symbolInfo = semanticModel.GetSymbolInfo(semanticInvocation);
                    if (!IsSameMethod(symbolInfo, targetSymbol))
                        continue;
                }

                if (semanticModel is null)
                {
                    var relation = ClassifyInvocation(
                        invocation, containingType, targetNamespace, targetTypeName, targetIsStatic);
                    if (relation == InvocationRelation.Unrelated)
                        return Failed("同名メソッドの呼び出し先を構文だけでは一意に解決できません。");
                    if (relation == InvocationRelation.Unknown)
                        return Failed("呼び出し先を解決できないため、パラメーター導入を中止しました。");
                }
                if (invocation.ArgumentList.Arguments.Count != target.ParameterList.Parameters.Count)
                    return Failed("既存の引数数が対象メソッドと一致しない呼び出しがあります。");

                var named = invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null);
                var added = named
                    ? $", {parameterName}: {callSiteArgument}"
                    : $", {callSiteArgument}";
                if (invocation.ArgumentList.Arguments.Count == 0)
                    added = named ? $"{parameterName}: {callSiteArgument}" : callSiteArgument;
                var position = ToLspPosition(file.Source, invocation.ArgumentList.CloseParenToken.SpanStart);
                var range = new LspRange(position, position);
                AddEdit(callEdits, file.Path, new LspTextEdit(range, added));
                callCount++;
            }

            foreach (var identifier in file.Root.DescendantNodes().OfType<IdentifierNameSyntax>()
                         .Where(identifier => string.Equals(
                             identifier.Identifier.ValueText, targetName, StringComparison.Ordinal)))
            {
                if (IsInvocationName(identifier) || IsDeclarationName(identifier))
                    continue;
                if (semanticModel is not null && semanticTarget is IMethodSymbol targetSymbol)
                {
                    if (FindEquivalent(identifier, semanticModel) is { } semanticIdentifier &&
                        IsSameSymbol(semanticModel.GetSymbolInfo(semanticIdentifier), targetSymbol))
                        return Failed("メソッドグループ／nameofなど、書き換えられない参照があります。");
                    continue;
                }
                return Failed("メソッドグループ／nameofなど、書き換えられない参照があります。");
            }
        }

        if (callCount == 0 && defaultValue is null)
            return Failed("ワークスペース内に呼び出しがなく、既定値もないため追加できません。");

        var declarationPosition = ToLspPosition(source, target.ParameterList.CloseParenToken.SpanStart);
        var declarationText = ParameterInsertionText(source, target.ParameterList, newParameter);
        AddEdit(callEdits, Path.GetFullPath(filePath),
            new LspTextEdit(new LspRange(declarationPosition, declarationPosition), declarationText));

        var changes = callEdits.ToDictionary(
            pair => LspUri.FromPath(pair.Key),
            pair => (IReadOnlyList<LspTextEdit>)pair.Value,
            LspUri.Comparer);
        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(changes), $"メソッド「{targetName}」へパラメーター「{parameterName}」を導入");
    }

    private static bool IsSameMethod(SymbolInfo info, IMethodSymbol target)
        => IsSameSymbol(info, target) || info.CandidateSymbols.Any(candidate =>
            candidate is IMethodSymbol method && IsSameSymbol(method, target));

    private static bool IsSameSymbol(SymbolInfo info, ISymbol target)
        => info.Symbol is not null && IsSameSymbol(info.Symbol, target);

    private static bool IsSameSymbol(ISymbol left, ISymbol right)
        => SymbolEqualityComparer.Default.Equals(left, right) ||
           SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

    private static bool ImplementsInterfaceMember(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Length > 0)
            return true;

        return method.ContainingType.AllInterfaces
            .SelectMany(@interface => @interface.GetMembers(method.Name))
            .OfType<IMethodSymbol>()
            .Any(interfaceMethod =>
            {
                var implementation = method.ContainingType
                    .FindImplementationForInterfaceMember(interfaceMethod);
                return implementation is not null && IsSameSymbol(implementation, method);
            });
    }

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static MethodDeclarationSyntax? FindTargetMethod(
        CompilationUnitSyntax root, TextSpan selection)
        => root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(method => method.Identifier.Span == selection);

    private static ParameterSyntax? ParseParameter(
        string type, string name, string? defaultValue)
    {
        var text = defaultValue is null
            ? $"({type} {name})"
            : $"({type} {name} = {defaultValue})";
        var list = SyntaxFactory.ParseParameterList(text);
        return list.Parameters.Count == 1
            && !list.ContainsDiagnostics
            && list.Parameters[0].Identifier.ValueText == name
            ? list.Parameters[0]
            : null;
    }

    private static bool IsExpression(string text)
    {
        var expression = SyntaxFactory.ParseExpression(text);
        return !expression.ContainsDiagnostics && expression.FullSpan.Length > 0;
    }

    private static string ParameterInsertionText(
        SourceText source, ParameterListSyntax list, ParameterSyntax parameter)
    {
        var rendered = parameter.ToString();
        if (list.Parameters.Count == 0) return rendered;
        var last = list.Parameters.Last();
        var between = source.ToString(TextSpan.FromBounds(last.Span.End, list.CloseParenToken.SpanStart));
        return between.Contains(',', StringComparison.Ordinal) ? rendered : ", " + rendered;
    }

    private static string? InvocationName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        };

    private static InvocationRelation ClassifyInvocation(
        InvocationExpressionSyntax invocation,
        ClassDeclarationSyntax targetType,
        string targetNamespace,
        string targetTypeName,
        bool targetIsStatic)
    {
        var caller = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var sameType = caller is not null
            && string.Equals(caller.Identifier.ValueText, targetType.Identifier.ValueText, StringComparison.Ordinal)
            && string.Equals(NamespaceName(caller), targetNamespace, StringComparison.Ordinal);
        switch (invocation.Expression)
        {
            case IdentifierNameSyntax when sameType:
            case GenericNameSyntax when sameType:
                return InvocationRelation.Target;
            case MemberAccessExpressionSyntax member:
                if (member.Expression is ThisExpressionSyntax or BaseExpressionSyntax)
                    return sameType ? InvocationRelation.Target : InvocationRelation.Unknown;
                if (targetIsStatic && IsTypeExpression(member.Expression, targetTypeName, targetNamespace))
                    return InvocationRelation.Target;
                if (IsObjectCreationOf(member.Expression, targetTypeName))
                    return InvocationRelation.Target;
                return InvocationRelation.Unknown;
            case MemberBindingExpressionSyntax:
                return InvocationRelation.Unknown;
            default:
                return InvocationRelation.Unrelated;
        }
    }

    private static bool IsTypeExpression(
        ExpressionSyntax expression, string typeName, string targetNamespace)
    {
        var text = expression.ToString();
        return string.Equals(text, typeName, StringComparison.Ordinal)
            || text.EndsWith("." + typeName, StringComparison.Ordinal)
            || string.Equals(text, targetNamespace + "." + typeName, StringComparison.Ordinal);
    }

    private static bool IsObjectCreationOf(ExpressionSyntax expression, string typeName)
        => expression is ObjectCreationExpressionSyntax creation
            && string.Equals(BaseTypeName(creation.Type), typeName, StringComparison.Ordinal);

    private static bool IsInvocationName(IdentifierNameSyntax identifier)
        => identifier.Parent switch
        {
            InvocationExpressionSyntax invocation when ReferenceEquals(invocation.Expression, identifier) => true,
            MemberAccessExpressionSyntax member when ReferenceEquals(member.Name, identifier)
                && member.Parent is InvocationExpressionSyntax => true,
            _ => false,
        };

    private static bool IsDeclarationName(IdentifierNameSyntax identifier)
        => identifier.Parent is VariableDeclaratorSyntax
            or PropertyDeclarationSyntax
            or EventDeclarationSyntax
            or TypeParameterSyntax;

    private static void AddEdit(
        IDictionary<string, List<LspTextEdit>> edits, string path, LspTextEdit edit)
    {
        var fullPath = Path.GetFullPath(path);
        if (!edits.TryGetValue(fullPath, out var list))
            edits[fullPath] = list = [];
        list.Add(edit);
    }

    private static IReadOnlyList<(string Path, SourceText Source, CompilationUnitSyntax Root)> ParseWorkspaceRoots(
        string activePath, CompilationUnitSyntax activeRoot,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions)
    {
        var activeFullPath = Path.GetFullPath(activePath);
        var result = new List<(string, SourceText, CompilationUnitSyntax)>
        {
            (activeFullPath, activeRoot.SyntaxTree.GetText(), activeRoot),
        };
        if (workspaceTexts is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { activeFullPath };
        foreach (var (path, text) in workspaceTexts)
        {
            if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var fullPath = Path.GetFullPath(path);
            if (!seen.Add(fullPath)) continue;
            var source = SourceText.From(text);
            result.Add((fullPath, source, CSharpSyntaxTree.ParseText(source,
                ParseOptionsFor(fullPath, workspaceParseOptions)).GetCompilationUnitRoot()));
        }
        return result;
    }

    private static CSharpParseOptions ParseOptionsFor(
        string path, IReadOnlyDictionary<string, CSharpParseOptions>? options)
        => options is not null && options.TryGetValue(Path.GetFullPath(path), out var configured)
            ? configured : CSharpParseOptions.Default;

    private static string NamespaceName(TypeDeclarationSyntax type)
        => string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(namespaceNode => namespaceNode.Name.ToString()));

    private static string BaseTypeName(TypeSyntax type)
    {
        var text = type.ToString().TrimEnd('?');
        var dot = text.LastIndexOf('.');
        if (dot >= 0) text = text[(dot + 1)..];
        var generic = text.IndexOf('<');
        return generic >= 0 ? text[..generic] : text;
    }

    private static LspPosition ToLspPosition(SourceText source, int position)
    {
        var line = source.Lines.GetLinePosition(position);
        return new LspPosition(line.Line, line.Character);
    }

    private static bool TryGetSelectionSpan(SourceText source, LspRange range, out TextSpan span)
    {
        span = default;
        if (range.Start.Line < 0 || range.End.Line < 0
            || range.Start.Line >= source.Lines.Count || range.End.Line >= source.Lines.Count)
            return false;
        var start = source.Lines[range.Start.Line].Start
            + Math.Clamp(range.Start.Character, 0, source.Lines[range.Start.Line].Span.Length);
        var end = source.Lines[range.End.Line].Start
            + Math.Clamp(range.End.Character, 0, source.Lines[range.End.Line].Span.Length);
        if (start > end) (start, end) = (end, start);
        if (start == end) return false;
        span = TextSpan.FromBounds(start, end);
        return true;
    }

    private enum InvocationRelation
    {
        Target,
        Unknown,
        Unrelated,
    }

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}

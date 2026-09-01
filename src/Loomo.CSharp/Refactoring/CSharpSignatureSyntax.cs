using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// C# の「シグネチャの変更」を構文木の上で行う部分。<b>意味解決はしない</b>——
/// どのメソッドがどこから呼ばれているかは、すでに動いている言語サーバーの
/// <c>textDocument/references</c> が答えるので、ここは「与えられた位置が宣言なのか呼び出しなのか」を
/// 構文で判定し、引数リストを組み替えるだけを受け持つ。
///
/// <para>この分担にしたのは、MSBuildWorkspace でソリューションをもう一度読み込む
/// （Roslyn 言語サーバーが同じことを既にやっている）のを避けるため。テストしやすさも上がる。</para>
/// </summary>
public static class CSharpSignatureSyntax
{
    /// <summary>キャレット位置を含む（か直上の）メソッド／コンストラクター宣言を読み取る。</summary>
    public static SignatureTarget Read(string filePath, string uri, string text, int line, int character)
    {
        var source = SourceText.From(text);
        if (line < 0 || line >= source.Lines.Count)
            return new SignatureTarget(null, "位置が文書の範囲外です。");
        int position = ClampToLine(source, line, character);

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var node = root.FindToken(position).Parent;

        var declaration = node?.AncestorsAndSelf().FirstOrDefault(IsSignatureOwner);
        if (declaration is null)
            return new SignatureTarget(null,
                "メソッドまたはコンストラクターの宣言の中にキャレットを置いてから実行してください。");

        var parameterList = GetParameterList(declaration);
        if (parameterList is null)
            return new SignatureTarget(null, "パラメーターリストを読み取れませんでした。");

        var identifier = GetIdentifier(declaration);
        var returnType = GetReturnType(declaration);

        var parameters = parameterList.Parameters.Select(p => new SignatureParameter(
            p.Identifier.ValueText,
            p.Type?.ToString() ?? "",
            string.Join(" ", p.Modifiers.Select(m => m.ValueText)),
            p.Default?.Value.ToString())).ToList();

        if (parameters.Any(p => p.Type.Length == 0))
            return new SignatureTarget(null, "型を読み取れないパラメーターがあります。");

        return new SignatureTarget(new MethodSignature(
            filePath,
            uri,
            identifier.ValueText,
            returnType?.ToString() ?? "",
            declaration is ConstructorDeclarationSyntax,
            parameters,
            ToRange(source, parameterList.Span),
            returnType is null ? null : ToRange(source, returnType.Span),
            ToPosition(source, identifier.SpanStart)), null);
    }

    private static bool IsSignatureOwner(SyntaxNode node) =>
        node is MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax;

    private static ParameterListSyntax? GetParameterList(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.ParameterList,
        ConstructorDeclarationSyntax c => c.ParameterList,
        LocalFunctionStatementSyntax l => l.ParameterList,
        _ => null,
    };

    private static SyntaxToken GetIdentifier(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier,
        ConstructorDeclarationSyntax c => c.Identifier,
        LocalFunctionStatementSyntax l => l.Identifier,
        _ => default,
    };

    private static TypeSyntax? GetReturnType(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.ReturnType,
        LocalFunctionStatementSyntax l => l.ReturnType,
        _ => null,
    };

    /// <summary>宣言そのものの書き換え（パラメーターリスト・戻り値型・XMLドキュメントの param 名）。</summary>
    public static IReadOnlyList<LspTextEdit> RewriteDeclaration(
        SourceText source, SyntaxNode declaration, MethodSignature original, SignatureChange change)
    {
        var edits = new List<LspTextEdit>();
        var parameterList = GetParameterList(declaration);
        if (parameterList is null) return edits;

        var text = "(" + string.Join(", ",
            change.Parameters.Select(p => p.Parameter.ToDeclarationText())) + ")";
        edits.Add(new LspTextEdit(ToRange(source, parameterList.Span), text));

        if (GetReturnType(declaration) is { } returnType &&
            change.ReturnType.Length > 0 &&
            !string.Equals(returnType.ToString(), change.ReturnType, StringComparison.Ordinal))
            edits.Add(new LspTextEdit(ToRange(source, returnType.Span), change.ReturnType));

        edits.AddRange(RewriteDocComment(source, declaration, original, change));
        return edits;
    }

    /// <summary>XMLドキュメントの <c>&lt;param name="…"&gt;</c> を新しい名前へ合わせ、
    /// 消えたパラメーターの行を削る。放っておくと CS1572/CS1573 の警告が新たに生えるため。</summary>
    private static IEnumerable<LspTextEdit> RewriteDocComment(
        SourceText source, SyntaxNode declaration, MethodSignature original, SignatureChange change)
    {
        var trivia = declaration.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        if (trivia is null) yield break;

        // 元の名前 → 変更後の名前（消えたものは null）。
        var renames = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < original.Parameters.Count; i++)
        {
            var replacement = change.Parameters.FirstOrDefault(p => p.OriginalIndex == i);
            renames[original.Parameters[i].Name] = replacement?.Parameter.Name;
        }

        foreach (var element in trivia.DescendantNodes().OfType<XmlElementSyntax>())
        {
            if (element.StartTag.Name.LocalName.ValueText != "param") continue;
            var attribute = element.StartTag.Attributes
                .OfType<XmlNameAttributeSyntax>()
                .FirstOrDefault(a => a.Name.LocalName.ValueText == "name");
            if (attribute is null) continue;

            var current = attribute.Identifier.Identifier.ValueText;
            if (!renames.TryGetValue(current, out var replacement)) continue;

            if (replacement is null)
            {
                // 行ごと消す（前の "///" を含む1行を丸ごと落とす）。
                yield return new LspTextEdit(ToRange(source, LineSpanOf(source, element.Span)), "");
                continue;
            }
            if (replacement != current)
                yield return new LspTextEdit(
                    ToRange(source, attribute.Identifier.Identifier.Span), replacement);
        }
    }

    /// <summary><paramref name="span"/> を含む行を、行末（次の行頭）まで広げた範囲。</summary>
    private static TextSpan LineSpanOf(SourceText source, TextSpan span)
    {
        var line = source.Lines.GetLineFromPosition(span.Start);
        return TextSpan.FromBounds(line.Start, Math.Min(line.EndIncludingLineBreak, source.Length));
    }

    /// <summary>参照位置1件が何なのかを判定して編集を作る。呼び出しでも宣言でもないもの
    /// （メソッドグループ・<c>nameof</c>・属性など）は書き換えられないので理由を返す。</summary>
    public static (IReadOnlyList<LspTextEdit> Edits, string? Error) RewriteReference(
        SourceText source, SyntaxNode root, int position,
        MethodSignature original, SignatureChange change)
    {
        var token = root.FindToken(position);
        if (token.ValueText != original.Name && !original.IsConstructor)
            return ([], null);   // 古い位置情報。無視する（誤って別の場所を壊さない）。

        var parent = token.Parent;
        if (parent is null) return ([], null);

        // 宣言（元の宣言・override・インターフェース実装）はパラメーターリストごと書き換える。
        var declaration = parent.AncestorsAndSelf().FirstOrDefault(IsSignatureOwner);
        if (declaration is not null && GetIdentifier(declaration).Span.Contains(position))
            return (RewriteDeclaration(source, declaration, original, change), null);

        var site = FindCallSite(parent, position);
        if (site is not { } callSite)
            return ([], DescribeUnsupported(source, position, parent));

        var (text, error) = BuildArgumentList(callSite.Arguments, callSite.Bracketed, original, change);
        if (error is not null) return ([], $"{Describe(source, position)}: {error}");
        return ([new LspTextEdit(ToRange(source, callSite.Span), text)], null);
    }

    /// <summary>変更後の宣言がC#の構文規則と既存の型内メンバーに衝突しないかを検証する。
    /// LSPの参照一覧が正しくても、変更後にoverloadが同一シグネチャになるとコンパイル不能になるため、
    /// 編集計画を作る前に止める。</summary>
    public static string? ValidateChange(
        SourceText source, MethodSignature original, SignatureChange change)
    {
        if (change.Parameters.Count == 0 && !original.IsConstructor &&
            original.Parameters.Count > 0)
        {
            // 空の引数リスト自体は有効。ここは意図的に何もしない（下の共通検証へ進む）。
        }

        var declaration = FindDeclaration(source, original);
        if (declaration is null) return "対象のメソッド宣言を再確認できません。";

        for (var i = 0; i < change.Parameters.Count; i++)
        {
            var parameter = change.Parameters[i].Parameter;
            if (!SyntaxFacts.IsValidIdentifier(parameter.Name))
                return $"パラメーター '{parameter.Name}' は有効なC#識別子ではありません。";
            if (parameter.Type.Length == 0 || HasTypeSyntaxErrors(parameter.Type))
                return $"パラメーター '{parameter.Name}' の型が正しくありません。";

            var modifiers = ModifierWords(parameter.Modifiers);
            if (modifiers.Any(word => word is not ("ref" or "out" or "in" or "this" or
                                                   "params" or "scoped" or "readonly")))
                return $"パラメーター '{parameter.Name}' の修飾子が正しくありません。";
            if (modifiers.Contains("params", StringComparer.Ordinal) && i != change.Parameters.Count - 1)
                return "paramsパラメーターは最後に置く必要があります。";
            if (modifiers.Contains("this", StringComparer.Ordinal) && i != 0)
                return "thisパラメーターは最初に置く必要があります。";
            if (parameter.DefaultValue is not null &&
                modifiers.Any(word => word is "ref" or "out" or "in" or "this" or "params"))
                return $"パラメーター '{parameter.Name}' の既定値と修飾子の組み合わせは扱えません。";
        }

        if (!original.IsConstructor && change.ReturnType.Length > 0 &&
            HasTypeSyntaxErrors(change.ReturnType))
            return "戻り値の型が正しくありません。";

        if (declaration.Parent is not TypeDeclarationSyntax containingType)
            return null; // local functionには型メンバーのoverload衝突はない。

        var expected = ParameterShape(change.Parameters.Select(p => p.Parameter));
        if (declaration is MethodDeclarationSyntax method)
        {
            var sameName = containingType.Members.OfType<MethodDeclarationSyntax>().Where(candidate =>
                !ReferenceEquals(candidate, method) &&
                candidate.Identifier.ValueText == method.Identifier.ValueText &&
                candidate.TypeParameterList?.Parameters.Count == method.TypeParameterList?.Parameters.Count &&
                string.Equals(candidate.ExplicitInterfaceSpecifier?.ToString(),
                    method.ExplicitInterfaceSpecifier?.ToString(), StringComparison.Ordinal));
            if (sameName.Any(candidate => ParameterShape(candidate.ParameterList.Parameters).Equals(expected,
                    StringComparison.Ordinal)))
                return "変更後のシグネチャが同じ型内のoverloadと衝突します。";
        }
        else if (declaration is ConstructorDeclarationSyntax constructor)
        {
            if (containingType.Members.OfType<ConstructorDeclarationSyntax>().Any(candidate =>
                !ReferenceEquals(candidate, constructor) &&
                ParameterShape(candidate.ParameterList.Parameters).Equals(expected,
                    StringComparison.Ordinal)))
                return "変更後のコンストラクターが同じ型内のoverloadと衝突します。";
        }

        return null;
    }

    /// <summary>構文だけで確実に判定できるdynamic／reflection参照を返す。
    /// 意味モデルなしで文字列全体をメソッド参照とみなすことはせず、明白な危険箇所だけを検出する。</summary>
    public static string? FindDynamicOrReflectionHazard(
        SourceText source, string methodName, bool includeMethodGroups = true)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var dynamicNames = root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .Where(declaration => IsDynamicType(declaration.Type))
            .SelectMany(declaration => declaration.Variables.Select(variable => variable.Identifier.ValueText))
            .Concat(root.DescendantNodes().OfType<ParameterSyntax>()
                .Where(parameter => IsDynamicType(parameter.Type))
                .Select(parameter => parameter.Identifier.ValueText))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == methodName &&
                UsesDynamicReceiver(member.Expression, dynamicNames))
                return $"{Describe(source, invocation.SpanStart)}: dynamic呼び出しを安全に解決できないため中止しました。";

            var calledName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => "",
            };
            if (calledName is "GetMethod" or "GetMethods" or "GetMember" or "InvokeMember" or "CreateDelegate" &&
                invocation.ArgumentList.Arguments.Any(argument =>
                    argument.Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression) &&
                    literal.Token.ValueText == methodName))
                return $"{Describe(source, invocation.SpanStart)}: reflection経由の参照を安全に変更できないため中止しました。";
        }

        if (!includeMethodGroups) return null;

        // language serverによっては、メソッドグループをreferencesに含めないことがある。
        // そのまま宣言と通常呼び出しだけを変更するとdelegate代入やメンバー参照を壊すため、
        // 意味解決なしでは同名の非呼び出し参照を安全側で危険箇所として扱う。
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>()
                     .Where(candidate => candidate.Identifier.ValueText == methodName))
        {
            if (IsInvocationTarget(name)) continue;
            if (name.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                    .Any(IsNameOfInvocation))
                return $"{Describe(source, name.SpanStart)}: nameof で参照されています。手で直してください。";
            return $"{Describe(source, name.SpanStart)}: メソッドグループ／メンバー参照を安全に変更できないため中止しました。";
        }

        return null;

        static bool IsInvocationTarget(SimpleNameSyntax name)
        {
            if (name.Parent is InvocationExpressionSyntax invocation &&
                ReferenceEquals(invocation.Expression, name)) return true;
            if (name.Parent is MemberAccessExpressionSyntax member &&
                ReferenceEquals(member.Name, name) &&
                member.Parent is InvocationExpressionSyntax memberInvocation &&
                ReferenceEquals(memberInvocation.Expression, member)) return true;
            if (name.Parent is MemberBindingExpressionSyntax binding &&
                ReferenceEquals(binding.Name, name) &&
                binding.Parent?.Parent is InvocationExpressionSyntax conditionalInvocation &&
                ReferenceEquals(conditionalInvocation.Expression, binding)) return true;
            return false;
        }

        static bool IsNameOfInvocation(InvocationExpressionSyntax invocation)
            => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" };
    }

    private static SyntaxNode? FindDeclaration(SourceText source, MethodSignature original)
    {
        var position = ClampToLine(source, original.NamePosition.Line, original.NamePosition.Character);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.FindToken(position).Parent?.AncestorsAndSelf()
            .FirstOrDefault(node => IsSignatureOwner(node) && GetIdentifier(node).Span.Contains(position));
    }

    private static string ParameterShape(IEnumerable<ParameterSyntax> parameters)
        => string.Join(";", parameters.Select(parameter =>
            $"{string.Join(" ", parameter.Modifiers.Select(modifier => modifier.ValueText))}:" +
            parameter.Type?.WithoutTrivia().ToString()));

    private static string ParameterShape(IEnumerable<SignatureParameter> parameters)
        => string.Join(";", parameters.Select(parameter =>
            $"{string.Join(" ", ModifierWords(parameter.Modifiers))}:" +
            SyntaxFactory.ParseTypeName(parameter.Type).WithoutTrivia().ToString()));

    private static bool HasTypeSyntaxErrors(string type)
        => CSharpSyntaxTree.ParseText($"class __SignatureType {{ {type} __Method() {{ }} }}")
            .GetDiagnostics().Any(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

    private static string[] ModifierWords(string modifiers)
        => modifiers.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool UsesDynamicReceiver(ExpressionSyntax expression, IReadOnlySet<string> dynamicNames)
        => expression switch
        {
            IdentifierNameSyntax identifier => dynamicNames.Contains(identifier.Identifier.ValueText),
            CastExpressionSyntax cast => IsDynamicType(cast.Type),
            ParenthesizedExpressionSyntax parenthesized => UsesDynamicReceiver(parenthesized.Expression, dynamicNames),
            _ => false,
        };

    private static bool IsDynamicType(TypeSyntax? type)
        => type is IdentifierNameSyntax { Identifier.ValueText: "dynamic" };

    /// <summary>呼び出し1件ぶんの実引数と、書き換えるべき範囲。
    /// <c>new Foo { … }</c> のように括弧そのものが無い呼び出しでは、<paramref name="Span"/> は
    /// 型名の直後の**長さ0の範囲**になる（そこへ括弧ごと挿入する）。</summary>
    private readonly record struct CallSite(
        SeparatedSyntaxList<ArgumentSyntax> Arguments, TextSpan Span, bool Bracketed);

    /// <summary>この位置の識別子が「呼び出しの対象」になっている実引数リストを探す。</summary>
    private static CallSite? FindCallSite(SyntaxNode parent, int position)
    {
        foreach (var node in parent.AncestorsAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation
                    when NameSpanOf(invocation.Expression).Contains(position):
                    return Of(invocation.ArgumentList);
                case ObjectCreationExpressionSyntax creation
                    when creation.Type.Span.Contains(position):
                    return creation.ArgumentList is { } list
                        ? Of(list)
                        : new CallSite([], new TextSpan(creation.Type.Span.End, 0), false);
                case ConstructorInitializerSyntax initializer
                    when initializer.ThisOrBaseKeyword.Span.Contains(position):
                    return Of(initializer.ArgumentList);
                // 引数リストの内側まで来たら、この位置は呼び出し対象ではなく実引数の一部。
                case BaseArgumentListSyntax:
                    return null;
            }
        }
        return null;

        static CallSite Of(BaseArgumentListSyntax list) =>
            new(list.Arguments, list.Span, list is BracketedArgumentListSyntax);
    }

    /// <summary>呼び出し式のうち「メソッド名」に当たる部分の範囲。<c>a.B.C()</c> なら <c>C</c>。</summary>
    private static TextSpan NameSpanOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Span,
        MemberBindingExpressionSyntax binding => binding.Name.Span,
        GenericNameSyntax generic => generic.Identifier.Span,
        _ => expression.Span,
    };

    private static string DescribeUnsupported(SourceText source, int position, SyntaxNode parent)
    {
        var place = Describe(source, position);
        if (parent.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }))
            return $"{place}: nameof で参照されています。手で直してください。";
        if (parent.Ancestors().OfType<AttributeSyntax>().Any())
            return $"{place}: 属性の中で参照されています。手で直してください。";
        return $"{place}: 呼び出しではなくメソッドそのものとして参照されています" +
               "（デリゲートへの代入など）。引数の数や順序を変えると壊れるため中止しました。";
    }

    private static string Describe(SourceText source, int position)
    {
        var line = source.Lines.GetLinePosition(position);
        return $"{line.Line + 1}行目";
    }

    /// <summary>
    /// 実引数リストを組み替える。名前付き引数は名前ごと運び、順序が入れ替わって
    /// 位置指定では表せなくなる場合は以降を名前付きにする（C# は名前付きの後ろに
    /// 位置指定を置けないため）。
    /// </summary>
    internal static (string Text, string? Error) BuildArgumentList(
        SeparatedSyntaxList<ArgumentSyntax> arguments, bool bracketed,
        MethodSignature original, SignatureChange change)
    {
        var (open, close) = bracketed ? ("[", "]") : ("(", ")");

        // 元のパラメーター番号 → 実引数。省略された既定値つき引数はここに入らない。
        var bound = new Dictionary<int, ArgumentSyntax>();
        int positional = 0;
        foreach (var argument in arguments)
        {
            if (argument.NameColon is { } name)
            {
                int index = IndexOfParameter(original, name.Name.Identifier.ValueText);
                if (index < 0) return ("", $"名前付き引数 '{name.Name.Identifier.ValueText}' が現在のシグネチャに見つかりません。");
                bound[index] = argument;
            }
            else
            {
                bound[positional++] = argument;
            }
        }

        var rendered = new List<(string Text, bool Named, bool Omitted)>();
        bool namedFromHere = false;
        foreach (var parameter in change.Parameters)
        {
            if (parameter.IsNew)
            {
                if (parameter.CallSiteArgument is { Length: > 0 } value)
                    rendered.Add((value, false, false));
                else if (parameter.Parameter.DefaultValue is { Length: > 0 })
                    rendered.Add(("", false, true));
                else
                    return ("", $"追加したパラメーター '{parameter.Parameter.Name}' に、呼び出し側へ書く値も既定値もありません。");
                continue;
            }

            if (!bound.TryGetValue(parameter.OriginalIndex, out var argument))
            {
                // 元の呼び出しで省略されていた（既定値つき）。省略のまま運ぶ。
                rendered.Add(("", false, true));
                continue;
            }

            var expression = argument.Expression.ToString();
            var refKind = argument.RefKindKeyword.ValueText;
            if (refKind.Length > 0) expression = $"{refKind} {expression}";
            bool named = argument.NameColon is not null;
            rendered.Add((expression, named, false));
        }

        // 名前付きが1つでも出たら、それ以降は位置では表せない。
        for (int i = 0; i < rendered.Count; i++)
        {
            if (rendered[i].Named) namedFromHere = true;
            else if (namedFromHere && !rendered[i].Omitted) rendered[i] = (rendered[i].Text, true, false);
        }

        // 省略は末尾だけ許される。間に挟まったら既定値を書き出して埋める。
        for (int i = 0; i < rendered.Count; i++)
        {
            if (!rendered[i].Omitted) continue;
            if (rendered.Skip(i + 1).All(r => r.Omitted)) break;

            var parameter = change.Parameters[i].Parameter;
            if (parameter.DefaultValue is not { Length: > 0 } value)
                return ("", $"パラメーター '{parameter.Name}' を省略できません（既定値がありません）。");
            rendered[i] = (value, true, false);   // 名前付きで埋めれば以降の位置がずれない
        }

        var parts = new List<string>();
        for (int i = 0; i < rendered.Count; i++)
        {
            var (text, named, omitted) = rendered[i];
            if (omitted) continue;
            parts.Add(named ? $"{change.Parameters[i].Parameter.Name}: {text}" : text);
        }
        return ($"{open}{string.Join(", ", parts)}{close}", null);
    }

    private static int IndexOfParameter(MethodSignature signature, string name)
    {
        for (int i = 0; i < signature.Parameters.Count; i++)
            if (string.Equals(signature.Parameters[i].Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>変更が「順序も個数も名前も型も同じ」なら呼び出し側は触らなくてよい。</summary>
    public static bool CallSitesUnaffected(MethodSignature original, SignatureChange change)
    {
        if (change.Parameters.Count != original.Parameters.Count) return false;
        for (int i = 0; i < change.Parameters.Count; i++)
        {
            var parameter = change.Parameters[i];
            if (parameter.OriginalIndex != i) return false;
            // 名前が変われば名前付き引数が壊れるので、呼び出し側も見に行く必要がある。
            if (parameter.Parameter.Name != original.Parameters[i].Name) return false;
        }
        return true;
    }

    /// <summary>呼び出し側の引数テキストは変えなくても、method group／delegateの型や
    /// 省略引数の意味が変わるため安全確認が必要なシグネチャ変更かを返す。</summary>
    public static bool SignatureContractChanged(MethodSignature original, SignatureChange change)
    {
        if (!string.Equals(original.ReturnType, change.ReturnType, StringComparison.Ordinal))
            return true;
        if (change.Parameters.Count != original.Parameters.Count) return true;
        for (int i = 0; i < change.Parameters.Count; i++)
        {
            var parameter = change.Parameters[i];
            var old = original.Parameters[i];
            if (parameter.OriginalIndex != i ||
                !string.Equals(parameter.Parameter.Name, old.Name, StringComparison.Ordinal) ||
                !string.Equals(parameter.Parameter.Type, old.Type, StringComparison.Ordinal) ||
                !string.Equals(parameter.Parameter.Modifiers, old.Modifiers, StringComparison.Ordinal) ||
                !string.Equals(parameter.Parameter.DefaultValue, old.DefaultValue, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ── 座標変換 ─────────────────────────────────────────────────────────────

    public static int ClampToLine(SourceText source, int line, int character)
    {
        var textLine = source.Lines[Math.Clamp(line, 0, source.Lines.Count - 1)];
        int length = textLine.End - textLine.Start;
        return textLine.Start + Math.Clamp(character, 0, length);
    }

    internal static LspRange ToRange(SourceText source, TextSpan span)
    {
        var lines = source.Lines.GetLinePositionSpan(span);
        return new LspRange(
            new LspPosition(lines.Start.Line, lines.Start.Character),
            new LspPosition(lines.End.Line, lines.End.Character));
    }

    internal static LspPosition ToPosition(SourceText source, int position)
    {
        var line = source.Lines.GetLinePosition(position);
        return new LspPosition(line.Line, line.Character);
    }
}

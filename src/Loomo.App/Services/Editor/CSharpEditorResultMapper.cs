using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>C#編集処理の結果を、エディタホストで扱う共通形式へ写す。</summary>
internal static class CSharpEditorResultMapper
{
    public static string CommandIdFor(CSharpCodeGenerationKind kind)
        => kind switch
        {
            CSharpCodeGenerationKind.Constructor => CSharpEditorCommandCatalog.GenerateConstructor,
            CSharpCodeGenerationKind.FieldFromConstructorParameter => CSharpEditorCommandCatalog.GenerateField,
            CSharpCodeGenerationKind.PropertiesFromFields => CSharpEditorCommandCatalog.GenerateProperties,
            CSharpCodeGenerationKind.EqualsAndGetHashCode => CSharpEditorCommandCatalog.GenerateEquality,
            CSharpCodeGenerationKind.ToString => CSharpEditorCommandCatalog.GenerateToString,
            CSharpCodeGenerationKind.Deconstruct => CSharpEditorCommandCatalog.GenerateDeconstruct,
            CSharpCodeGenerationKind.MethodFromUsage => CSharpEditorCommandCatalog.GenerateMethodFromUsage,
            CSharpCodeGenerationKind.ImplementInterface => CSharpEditorCommandCatalog.ImplementInterface,
            CSharpCodeGenerationKind.OverrideMembers => CSharpEditorCommandCatalog.GenerateOverride,
            CSharpCodeGenerationKind.DelegatingMembers => CSharpEditorCommandCatalog.GenerateDelegatingMembers,
            CSharpCodeGenerationKind.DisposePattern => CSharpEditorCommandCatalog.GenerateDisposePattern,
            CSharpCodeGenerationKind.AsyncDisposePattern => CSharpEditorCommandCatalog.GenerateAsyncDisposePattern,
            CSharpCodeGenerationKind.NullGuards => CSharpEditorCommandCatalog.GenerateNullGuards,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未登録のC#コード生成種別です。"),
        };

    /// <summary>診断範囲とカーソル位置または選択範囲の重なりを判定する。</summary>
    public static bool IsInRange(LspRange diagnostic, LspRange requested)
    {
        static int Compare(LspPosition left, LspPosition right)
            => left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Character.CompareTo(right.Character);
        var point = Compare(requested.Start, requested.End) == 0;
        if (point)
            return Compare(diagnostic.Start, requested.Start) <= 0 && Compare(requested.Start, diagnostic.End) <= 0;
        return Compare(diagnostic.Start, requested.End) < 0 && Compare(requested.Start, diagnostic.End) < 0;
    }

    /// <summary>グループ化された不要 using 診断を個別 directive の範囲へ写す。</summary>
    public static IReadOnlyList<EditorDiagnosticEntry> ExpandUnnecessaryUsingEntries(
        IReadOnlyList<EditorDiagnosticEntry> entries,
        IReadOnlyList<LspRange> individualRanges)
    {
        var expanded = new List<EditorDiagnosticEntry>();
        foreach (var entry in entries)
        {
            var diagnostics = CSharpDiagnosticMerger.ExpandUnnecessaryUsingGroups(
                [entry.Diagnostic], individualRanges);
            expanded.AddRange(diagnostics.Select(diagnostic => entry with { Diagnostic = diagnostic }));
        }
        return expanded;
    }

    /// <summary>
    /// <para>接続中の言語サーバーがある場合は、毎キーのCompiler Compilationを避けるため
    /// CS8019の位置が手元にないことがある。その場合も不要usingの診断範囲を個別化できるよう、
    /// 構文上のdirective位置を使う。</para>
    /// <para>不要using診断があるときだけ構文解析し、通常入力での余分な解析を避ける。</para>
    /// </summary>
    public static IReadOnlyList<LspRange> UnnecessaryUsingRanges(EditorDiagnosticSnapshot snapshot)
        => snapshot.Entries.Any(entry => entry.Diagnostic.Code is { } code &&
               (code.Equals("IDE0005", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("CS8019", StringComparison.OrdinalIgnoreCase)))
            ? CSharpUsingDirectiveRanges.Find(snapshot.Text)
            : [];

    public static LspDiagnostic AnalysisFailure(string message, string source, string code = "LOOMO")
        => new(new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
            message, DiagnosticSeverity.Warning, source, code);

    public static LspCodeAction? StyleCopQuickFix(StyleCopCodeFixResult result, LspDiagnostic diagnostic)
        => result.Edit is { } edit && result.Error is null
            ? new LspCodeAction(result.Title ?? $"{diagnostic.Code}を修正",
                LspCodeActionKinds.QuickFix, edit, IsPreferred: true)
            : null;

    public static LspCodeAction? CompilerFixAll(CSharpFixAllResult result)
        => result.Edit is { } edit && string.IsNullOrEmpty(result.Error)
            ? new LspCodeAction("C# compilerのFix All", LspCodeActionKinds.SourceFixAll,
                edit, IsPreferred: true)
            : null;

    public static bool AllowsQuickFixKinds(IReadOnlyList<string>? only)
        => only is not { Count: > 0 } || only.Any(kind =>
            LspCodeActionKinds.Matches(kind, LspCodeActionKinds.QuickFix) ||
            LspCodeActionKinds.Matches(LspCodeActionKinds.QuickFix, kind) ||
            LspCodeActionKinds.Matches(kind, LspCodeActionKinds.SourceFixAll) ||
            LspCodeActionKinds.Matches(LspCodeActionKinds.SourceFixAll, kind));

    public static (string Uri, int Line, int Column)? DefinitionLocation(LspLocation? location)
        => location is { } value
            ? (value.Uri, value.Range.Start.Line, value.Range.Start.Character)
            : null;

    /// <summary>生成編集を適用する前に、既存本文を基準に整形したWorkspaceEditへ変換する。</summary>
    public static LspWorkspaceEdit PrepareGeneratedEdit(
        LspWorkspaceEdit edit,
        IReadOnlyDictionary<string, string> openEditorTexts,
        string? activePath,
        string activeText,
        CSharpEditorConfigService editorConfig)
    {
        var originalTexts = openEditorTexts.ToDictionary(
            pair => Path.GetFullPath(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (activePath is { Length: > 0 })
            originalTexts[Path.GetFullPath(activePath)] = activeText;

        // 新規作成URIは空本文を基準に整形する。既存の非表示文書はディスク本文を読む。
        foreach (var uri in edit.Changes.Keys)
        {
            if (LspUri.TryToLocalPath(uri) is not { } rawPath) continue;
            var path = Path.GetFullPath(rawPath);
            if (originalTexts.ContainsKey(path)) continue;
            if (File.Exists(path))
            {
                try { originalTexts[path] = File.ReadAllText(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (edit.FileOperations?.Any(operation =>
                         operation.Kind == LspFileOperationKind.Create && LspUri.SamePath(operation.Uri, uri)) == true)
                originalTexts[path] = "";
        }

        return CSharpGeneratedEditFormatter.FormatWorkspace(edit, originalTexts,
            path => CSharpCleanupOptionsFactory.CreateForFile(path, format: true,
                insertFinalNewlineWhenUnset: null, excludeGeneratedCode: false,
                editorConfigService: editorConfig));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// C# の「シグネチャの変更」（設計書 §32.5）。**LSP にこのリファクタリングは無い**——
/// Roslyn も typescript-language-server も rust-analyzer も code action として公開していないため、
/// Loomo 側で組む。ただし丸ごと自前にはせず、次の分担にしている:
///
/// <list type="bullet">
/// <item>どこから呼ばれているか＝<b>言語サーバー</b>の <c>textDocument/references</c>
///   （すでにソリューションを読み込み済みのプロセスに聞く。MSBuildWorkspace を別に立てない）。</item>
/// <item>宣言と実引数の書き換え＝<b>Roslyn の構文 API</b>（<see cref="CSharpSignatureSyntax"/>）。</item>
/// </list>
///
/// <para>安全側に倒す方針: 書き換えられない参照（デリゲートへの代入・<c>nameof</c>・属性）が
/// 1つでもあれば、**何も適用せず**理由を返す。半分だけ書き換わった状態を作らない。</para>
/// </summary>
public sealed class CSharpSignatureRefactoring
{
    private readonly ILspWorkspace _lsp;
    private readonly IReadOnlyList<string> _workspaceFolders;
    private readonly Func<string, string?> _openText;
    private readonly CSharpCompilation? _semanticCompilation;

    /// <param name="workspaceFolders">ワークスペースの**全**フォルダー（プライマリ＋追加）。
    /// プライマリだけを渡すと、あとから追加したフォルダーにある呼び出し元が黙って書き換え対象から
    /// 外れる——一番危ない失敗の仕方なので、ここは一覧で受ける。</param>
    /// <param name="openText">開いているタブの未保存テキストを返す（無ければ null）。
    /// ディスクだけを見ると、保存前の編集を踏み潰した編集を作ってしまう。</param>
    public CSharpSignatureRefactoring(
        ILspWorkspace lsp, IReadOnlyList<string> workspaceFolders, Func<string, string?> openText)
        : this(lsp, workspaceFolders, openText, semanticCompilation: null)
    {
    }

    /// <summary>
    /// 意味コンパイルを既に専用DLL内で構築した呼び出し元向けの内部境界。
    /// AppへRoslyn型を公開しないため、意味モデルを使う処理はこのアセンブリ内からだけ呼ぶ。
    /// </summary>
    internal CSharpSignatureRefactoring(
        ILspWorkspace lsp, IReadOnlyList<string> workspaceFolders, Func<string, string?> openText,
        CSharpCompilation? semanticCompilation)
    {
        _lsp = lsp;
        _workspaceFolders = workspaceFolders;
        _openText = openText;
        _semanticCompilation = semanticCompilation;
    }

    /// <summary>
    /// AppがRoslyn型を参照せずに意味モデル付きChange Signatureを実行する境界。
    /// Compilationの生成と破棄はC#専用DLL内で完結させる。
    /// </summary>
    public static async Task<SignaturePlan> PlanWithSolutionAsync(
        ILspWorkspace lsp,
        IReadOnlyList<string> workspaceFolders,
        Func<string, string?> openText,
        SolutionModel? solution,
        string activeFilePath,
        string activeText,
        MethodSignature original,
        SignatureChange change,
        IReadOnlyDictionary<string, string>? openTexts = null,
        CSharpEditorConfigService? editorConfig = null,
        CancellationToken cancellationToken = default)
    {
        CSharpCompilation? compilation = null;
        if (solution is not null)
        {
            try
            {
                var context = await Task.Run(() => CSharpWorkspaceOperationContext.Create(
                    solution, activeFilePath, activeText,
                    CSharpWorkspaceSourceScope.Solution,
                    includeSemanticCompilation: true,
                    editorConfigService: editorConfig,
                    openTexts: openTexts), cancellationToken);
                if (context.SourceSnapshotWarning is { } warning)
                    return SignaturePlan.Failed(warning +
                        "参照全体が必要なため、Change Signatureを安全に実行できません。");
                compilation = context.SemanticCompilation;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidOperationException or ArgumentException)
            {
                // MSBuild／Analyzerの一時的な失敗時は、LSP referencesと構文安全確認へ戻す。
            }
        }

        return await new CSharpSignatureRefactoring(
            lsp, workspaceFolders, openText, compilation)
            .PlanAsync(original, change, cancellationToken);
    }

    /// <summary>この機能を出してよいファイルか。</summary>
    public static bool AppliesTo(string? filePath) =>
        filePath is { Length: > 0 } &&
        string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>宣言と全呼び出し元の編集を作る。1件でも書き換えられなければ計画ごと失敗させる。</summary>
    public async Task<SignaturePlan> PlanAsync(
        MethodSignature original, SignatureChange change, CancellationToken ct = default)
    {
        if (change.Parameters.Any(p => p.Parameter.Name.Length == 0 || p.Parameter.Type.Length == 0))
            return SignaturePlan.Failed("名前か型が空のパラメーターがあります。");
        if (change.Parameters.Select(p => p.Parameter.Name).Distinct(StringComparer.Ordinal).Count()
            != change.Parameters.Count)
            return SignaturePlan.Failed("パラメーター名が重複しています。");

        // メニューを開いてからダイアログを確定するまでの間に本文が編集されていることがある。
        // 古い位置のまま書き換えると、別の場所を壊すか黙って何もしないので、ここで突き合わせる。
        if (VerifyStillCurrent(original) is { } stale) return SignaturePlan.Failed(stale);

        if (ReadText(original.FilePath) is not { } currentText)
            return SignaturePlan.Failed("対象のファイルを読み取れませんでした。");
        if (CSharpSignatureSyntax.ValidateChange(
                SourceText.From(currentText), original, change) is { } invalid)
            return SignaturePlan.Failed(invalid);

        if (_semanticCompilation is not null &&
            CSharpSignatureSemanticSafety.FindSignatureConflict(
                _semanticCompilation, original, change) is { } semanticConflict)
            return SignaturePlan.Failed(semanticConflict);

        var locations = await FindReferencesAsync(original, change, ct);
        if (locations is null)
            return SignaturePlan.Failed(
                "呼び出し元を取得できませんでした。言語サーバーの準備が終わってから実行してください。");

        if ((!CSharpSignatureSyntax.CallSitesUnaffected(original, change) ||
             CSharpSignatureSyntax.SignatureContractChanged(original, change)) &&
            FindWorkspaceHazard(original, ct) is { } hazard)
            return SignaturePlan.Failed(hazard);

        // 参照の中に宣言そのものが含まれない構成（includeDeclaration を無視するサーバー）でも
        // 宣言を必ず1件は書き換えるよう、元の宣言位置を明示的に足しておく。
        var byUri = locations
            .GroupBy(l => l.Uri, LspUri.Comparer)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Range.Start).ToList(), LspUri.Comparer);
        if (!byUri.TryGetValue(original.Uri, out var ownPositions))
            byUri[original.Uri] = ownPositions = [];
        if (!ownPositions.Any(p => p.Line == original.NamePosition.Line &&
                                   p.Character == original.NamePosition.Character))
            ownPositions.Add(original.NamePosition);

        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(LspUri.Comparer);
        var expectedTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        int siteCount = 0;
        int skippedOutside = 0;

        foreach (var (uri, positions) in byUri)
        {
            if (ct.IsCancellationRequested) return SignaturePlan.Failed("中断されました。");
            if (LspUri.TryToLocalPath(uri) is not { } path) continue;
            if (!AppliesTo(path)) continue;
            // ワークスペース外（メタデータからの逆コンパイル等）は書き換えてはいけない。
            // ただし**黙って飛ばさない**——件数を数えて結果に出す（気付かないまま
            // 一部だけ変わったコードが残るのが一番危ない）。
            if (!WorkspacePaths.Contains(_workspaceFolders, path))
            {
                skippedOutside += positions.Count;
                continue;
            }
            if (ReadText(path) is not { } text)
            {
                problems.Add($"{path}: 読み取れませんでした。");
                continue;
            }
            expectedTexts[path] = text;

            var source = SourceText.From(text);
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            var edits = new List<LspTextEdit>();

            foreach (var position in positions.OrderBy(p => p.Line).ThenBy(p => p.Character))
            {
                int offset = CSharpSignatureSyntax.ClampToLine(source, position.Line, position.Character);
                var (siteEdits, error) = CSharpSignatureSyntax.RewriteReference(
                    source, root, offset, original, change);
                if (error is not null)
                {
                    problems.Add($"{Path.GetFileName(path)} {error}");
                    continue;
                }
                if (siteEdits.Count == 0) continue;
                edits.AddRange(siteEdits);
                siteCount++;
            }

            if (edits.Count > 0) changes[uri] = Deduplicate(edits);
        }

        if (problems.Count > 0)
            return SignaturePlan.Failed(
                "次の参照を書き換えられないため中止しました。" + string.Join(" / ", problems.Take(5)) +
                (problems.Count > 5 ? $" ほか{problems.Count - 5}件" : ""));
        if (changes.Count == 0)
            return SignaturePlan.Failed("書き換える箇所が見つかりませんでした。");

        return new SignaturePlan(changes, siteCount, null, skippedOutside, expectedTexts);
    }

    /// <summary>読み取ったときの宣言が今も同じ位置に同じ形であるか。ずれていれば理由を返す。</summary>
    internal string? VerifyStillCurrent(MethodSignature original)
    {
        if (ReadText(original.FilePath) is not { } text)
            return "対象のファイルを読み取れませんでした。";

        var target = CSharpSignatureSyntax.Read(
            original.FilePath, original.Uri, text,
            original.NamePosition.Line, original.NamePosition.Character);
        if (target.Signature is not { } current || !SameShape(original, current))
            return "対象のメソッドが変更されました。もう一度実行してください。";
        return null;
    }

    private static bool SameShape(MethodSignature a, MethodSignature b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&
        a.Parameters.Count == b.Parameters.Count &&
        a.Parameters.Zip(b.Parameters).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.Type, pair.Second.Type, StringComparison.Ordinal));

    /// <summary>同じ範囲への編集が2度入らないようにする（宣言が参照一覧にも現れるため）。</summary>
    private static IReadOnlyList<LspTextEdit> Deduplicate(List<LspTextEdit> edits) =>
        edits
            .GroupBy(e => (e.Range.Start.Line, e.Range.Start.Character,
                           e.Range.End.Line, e.Range.End.Character))
            .Select(g => g.First())
            .OrderBy(e => e.Range.Start.Line).ThenBy(e => e.Range.Start.Character)
            .ToList();

    private async Task<IReadOnlyList<LspLocation>?> FindReferencesAsync(
        MethodSignature original, SignatureChange change, CancellationToken ct)
    {
        // 変更が宣言だけで完結する（順序も名前も個数も変わらない＝型と戻り値だけの変更）なら
        // 呼び出し元は見に行かない。言語サーバーが未準備でも戻り値型の変更くらいは通す。
        if (CSharpSignatureSyntax.CallSitesUnaffected(original, change))
            return [];

        if (_semanticCompilation is not null &&
            CSharpSignatureSemanticSafety.FindInvocationReferences(
                _semanticCompilation, original) is { } semanticReferences)
        {
            // RoslynのCompilationに含まれるソースは意味モデルを正本にする。
            // LSPへ同じ問い合わせを重ねると、解析途中の空応答で確定結果を上書きする
            // 可能性があるため、semantic経路が成立した場合はここで返す。
            var declarations = CSharpSignatureSemanticSafety.FindRelatedDeclarationReferences(
                _semanticCompilation, original) ?? [];
            return semanticReferences
                .Concat(declarations)
                .GroupBy(location => (location.Uri, location.Range.Start.Line,
                    location.Range.Start.Character, location.Range.End.Line,
                    location.Range.End.Character))
                .Select(group => group.First())
                .ToArray();
        }

        var text = ReadText(original.FilePath);
        if (text is null) return null;
        using var document = _lsp.OpenDocument(original.FilePath, text);
        if (document is null) return null;

        // サーバーがこの文書を開き終える前に投げると 0 件が返る。準備できるまで少しだけ待つ。
        for (int i = 0; i < 40 && !document.IsReady; i++)
        {
            if (ct.IsCancellationRequested) return null;
            await Task.Delay(100, ct);
        }
        if (!document.IsReady) return null;

        // didOpen 完了は「Roslynのプロジェクト解析完了」を意味しない。大きなsolutionでは
        // 直後のreferencesが空でも、数秒後には呼び出し元が返るため、空応答だけ短時間再試行する。
        // 永久待機にはせず、未対応サーバーや本当に参照が無い場合は従来どおり失敗へ戻す。
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var references = await document.RequestReferencesAsync(
                original.NamePosition.Line, original.NamePosition.Character);
            if (references.Any(reference =>
                    !LspUri.Comparer.Equals(reference.Uri, original.Uri) ||
                    reference.Range.Start.Line != original.NamePosition.Line ||
                    reference.Range.Start.Character != original.NamePosition.Character))
                return references;
            if (ct.IsCancellationRequested) return null;
            await Task.Delay(250, ct);
        }

        return [];
    }

    private string? ReadText(string path)
    {
        if (_openText(Path.GetFullPath(path)) is { } open) return open;
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// <summary>参照一覧に現れないdynamic／reflection呼び出しを見落として部分変更しないよう、
    /// ワークスペースのC#ソースも安全確認する。bin／obj等の生成物は対象外にする。</summary>
    private string? FindWorkspaceHazard(MethodSignature original, CancellationToken ct)
    {
        if (_semanticCompilation is not null &&
            CSharpSignatureSemanticSafety.FindMethodGroupHazard(_semanticCompilation, original) is { } semanticHazard)
            return semanticHazard;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        foreach (var folder in _workspaceFolders)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.cs", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                });
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                if (IsGeneratedOrBuildPath(path) || !seen.Add(Path.GetFullPath(path))) continue;
                if (++scanned > 20_000)
                    return "安全確認できるC#ファイル数の上限を超えたため中止しました。";
                if (ReadText(path) is not { } text) continue;
                if (CSharpSignatureSyntax.FindDynamicOrReflectionHazard(
                        SourceText.From(text), original.Name,
                        includeMethodGroups: _semanticCompilation is null) is { } hazard)
                    return $"{Path.GetFileName(path)}: {hazard}";
            }
        }
        return null;
    }

    private static bool IsGeneratedOrBuildPath(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
}

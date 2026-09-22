using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>
/// C# の構文／意味ベースの編集操作へ渡す共有ワークスペース状態。
/// 現在の未保存本文、選択TFMごとの構文設定、ProjectReferenceを辿ったソース、Compilationを
/// 一つのC#専用境界で組み立てる。開いている複数バッファの未保存本文も受け取れる。
/// AppはUIから本文を受け取り、編集結果を適用するだけにする。
/// </summary>
public sealed record CSharpWorkspaceOperationContext(
    CSharpWorkspaceSourceSnapshot Snapshot,
    CSharpCompilation? SemanticCompilation)
{
    /// <summary>上限によるソース欠落を含まないCompilationか。</summary>
    public bool IsSourceSnapshotComplete => Snapshot.IsComplete;

    /// <summary>上限で欠落したソースがある場合に、UIへ返せる説明を返す。</summary>
    public string? SourceSnapshotWarning => Snapshot.IsComplete
        ? null
        : $"C#ソースが上限で切り詰められています（{Snapshot.SkippedFileCount}ファイル）。";

    /// <summary>
    /// 意味解析の結果をそのまま人へ見せてよいか。上限による切り詰めに加えて、
    /// <b>読めなかったソース</b>（未生成の <c>*.g.cs</c> ／ <c>AssemblyInfo.cs</c>）も見る
    /// ——欠けると <c>InitializeComponent</c> や <c>x:Name</c> の partial half ごと落ちて、
    /// 診断が CS0103／CS0246 だらけになる。
    ///
    /// <para>生成ソースの欠け方は2通りあり、<b>両方見なければ意味が無い</b>。一覧には在るのに
    /// 読めない（＝<see cref="CSharpWorkspaceSourceSnapshot.HasUnreadableSources"/>）と、
    /// design-time build 自体が落ちて<b>一覧にすら載らない</b>
    /// （＝<see cref="CSharpWorkspaceSourceSnapshot.HasIncompleteEvaluation"/>）。後者は
    /// 読み取り失敗が1件も立たないので、前者だけを見ていると
    /// 「生成ソースが在るはずがない」と分かっている経路でだけガードが素通りする。</para>
    /// </summary>
    public bool CanTrustSemanticResults => Snapshot.IsComplete
        && !Snapshot.HasUnreadableSources
        && !Snapshot.HasIncompleteEvaluation;

    /// <summary>信用できないときの理由（UIへそのまま出せる日本語）。信用できるなら null。</summary>
    public string? SemanticTrustWarning => SourceSnapshotWarning ?? (Snapshot.HasUnreadableSources
        ? $"C#ソースを読み込めません（{Snapshot.MissingFileCount}ファイル）。"
            + "ビルドで生成されるファイルが未生成の可能性があります。"
        : Snapshot.HasIncompleteEvaluation
            ? $"C#プロジェクトの評価が完了していません（{Snapshot.IncompleteEvaluationProjectCount}プロジェクト）。"
                + "ビルドで生成されるファイルが評価結果に含まれていません。"
            : null);

    public static CSharpWorkspaceOperationContext Create(
        SolutionModel? solution,
        string activePath,
        string activeText,
        CSharpWorkspaceSourceScope scope = CSharpWorkspaceSourceScope.ProjectGraph,
        bool includeSemanticCompilation = false,
        CSharpCompilationOptions? compilationOptions = null,
        string? assemblyName = null,
        CSharpEditorConfigService? editorConfigService = null,
        IReadOnlyDictionary<string, string>? openTexts = null,
        bool requireTrustedSources = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activePath);
        ArgumentNullException.ThrowIfNull(activeText);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, activeText, scope, openTexts);
        if (!includeSemanticCompilation)
            return new(snapshot, null);
        // 結果を人へ見せられない状態だと分かっているなら、Compilation は組まない。
        // 意味解析の本体はここで、大規模ソリューションでは数秒かかる（CompilationCache の実測で
        // 1.3〜3.1秒）。呼び出し側は結局これを捨てるので、打鍵ごとの再解析でそれを払い続けるのは
        // 丸損なうえ、捨てる Compilation でキャッシュの枠を1つ潰す。
        if (requireTrustedSources && !new CSharpWorkspaceOperationContext(snapshot, null).CanTrustSemanticResults)
            return new(snapshot, null);

        var activeProject = solution?.ProjectForFile(Path.GetFullPath(activePath));
        var referencePaths = activeProject
            ?.SelectedTargetFrameworkModel?.References
            .Select(reference => reference.FullPath)
            .ToArray();
        var analyzerPaths = activeProject
            ?.SelectedTargetFrameworkModel?.Analyzers
            .Select(analyzer => analyzer.FullPath)
            .ToArray();
        var additionalTexts = activeProject
            ?.SelectedTargetFrameworkModel?.AdditionalFiles
            .Select(file => file.FullPath)
            .ToArray();

        var key = new CompilationCacheKey(
            solution, scope, Path.GetFullPath(activePath), assemblyName,
            compilationOptions, editorConfigService);
        var compilation = CompilationCache.GetOrBuild(
            key, snapshot,
            () => CSharpSemanticCompilation.Create(
                snapshot.Texts, snapshot.ParseOptionsByPath, referencePaths,
                assemblyName: assemblyName,
                compilationOptions: compilationOptions,
                analyzerPaths: analyzerPaths,
                additionalTexts: CSharpSemanticCompilation.CreateAdditionalTexts(additionalTexts),
                analyzerConfigOptionsProvider: new CSharpAnalyzerConfigOptionsProvider(
                    editorConfigService ?? new CSharpEditorConfigService(), activePath),
                // ソースで持っているプロジェクトの出力 DLL は参照から外す。この Compilation は
                // ProjectReference 先のソースまで積む（CSharpWorkspaceSourceLoader）ので、
                // 参照先の DLL を足すと同じ型が二重になり CS0436 が出る。
                sourceAssemblyNames: snapshot.SourceAssemblyNames));
        return new(snapshot, compilation);
    }

    /// <summary>抱えている Compilation を捨てる（テスト用。前のテストの結果を持ち越さない）。</summary>
    internal static void ClearCompilationCacheForTest() => CompilationCache.Clear();

    /// <summary>
    /// キャッシュした Compilation を使い回せる条件。<see cref="SolutionModel"/> は record で、
    /// 再評価のたびに新しいインスタンスが作られるので<b>参照同一性がそのまま世代印になる</b>。
    /// 残りは Compilation の形を決める引数で、どれか変われば作り直す。
    ///
    /// <para><see cref="CompilationOptions"/> は<b>値</b>で比べる——
    /// <see cref="CSharpProjectCompilationOptions.Compilation"/> は毎回新しいインスタンスを返すので、
    /// 参照で比べると診断の呼び出しが永久にキャッシュを外す。</para>
    ///
    /// <para>アクティブ文書の<b>パス</b>まで鍵に含めるのは、Source Generator へ渡す
    /// <see cref="CSharpAnalyzerConfigOptionsProvider"/> がそのパスの .editorconfig を見るため。
    /// プロジェクト単位に緩めると、同じプロジェクト内の別ディレクトリのファイルへ
    /// 他所の設定で生成した結果を配ってしまう。打鍵は 1 ファイルの中で続くので、
    /// ここを厳しくしても hot path は失われない（失うのはタブ切替の 1 回だけ）。</para>
    /// </summary>
    private readonly record struct CompilationCacheKey(
        SolutionModel? Solution,
        CSharpWorkspaceSourceScope Scope,
        string ActivePath,
        string? AssemblyName,
        CSharpCompilationOptions? CompilationOptions,
        CSharpEditorConfigService? EditorConfigService)
    {
        public bool Matches(in CompilationCacheKey other)
            => ReferenceEquals(Solution, other.Solution) &&
               Scope == other.Scope &&
               string.Equals(ActivePath, other.ActivePath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal) &&
               SameCompilationOptions(CompilationOptions, other.CompilationOptions) &&
               ReferenceEquals(EditorConfigService, other.EditorConfigService);

        private static bool SameCompilationOptions(
            CSharpCompilationOptions? left, CSharpCompilationOptions? right)
            => ReferenceEquals(left, right) || (left is not null && left.Equals(right));
    }

    /// <summary>
    /// 直前に組み立てた Compilation を 1 つだけ抱え、<b>打鍵で変わった文書だけ差し替える</b>。
    ///
    /// <para>これが無いと、補完・ハイライト・意味色付けの呼び出しごとにソリューション全ファイルを
    /// 読み直してパースし直していた。実測（Loomo 自身・980 ファイル・8.5MB、参照解決と
    /// Source Generator を除いた下限）で 1 回 1289〜3082ms。Roslyn の Compilation は不変かつ
    /// 差分更新が安いので、<see cref="CSharpCompilation.ReplaceSyntaxTree"/> で変わった木だけ
    /// 入れ替えれば同じ 1 回が 4ms で済む。</para>
    ///
    /// <para>作り直す条件は、鍵が変わった／ファイル集合が変わった／構文設定が変わった／
    /// まとめて大量に変わった（＝外部のブランチ切替等）／差分更新が
    /// <see cref="MaxIncrementalUpdates"/> 回続いた、のいずれか。最後の一つは
    /// <b>Source Generator の出力が古くなるのを区切る</b>ためにある——差分更新では生成器を
    /// 回し直さないので、生成結果を無期限に引きずらせない。</para>
    /// </summary>
    private static class CompilationCache
    {
        /// <summary>差分更新を続ける上限。超えたら作り直して Source Generator を回し直す。</summary>
        private const int MaxIncrementalUpdates = 64;

        /// <summary>一度にこれより多く変わっていたら差分ではない（ブランチ切替・一括整形など）。</summary>
        private const int MaxChangedFiles = 8;

        /// <summary>
        /// 抱えておく Compilation の数。<b>1 個では足りない。</b>
        ///
        /// <para>同じ 1 回の打鍵で、意味色付け・補完・コンパイラ診断が<b>別々の鍵で</b>ここへ来る
        /// （順に ProjectGraph／Solution／ProjectGraph + 独自 CompilationOptions）。枠が 1 つだと
        /// 三者が互いを追い出し続け、キャッシュがあるのに毎回作り直すという最悪の形になる。</para>
        ///
        /// <para>増やすのは只ではない——1 枠が Compilation ひとつぶんのヒープを抱え続ける。
        /// 3 経路ぶん＋切替の余裕で 4 とする。</para>
        /// </summary>
        private const int MaxEntries = 4;

        private static readonly object Gate = new();

        /// <summary>直近に使ったものが先頭。数個しか持たないので線形探索で足りる。</summary>
        private static readonly List<Entry> Entries = new();

        public static CSharpCompilation GetOrBuild(
            in CompilationCacheKey key,
            CSharpWorkspaceSourceSnapshot snapshot,
            Func<CSharpCompilation> build)
        {
            // build() は最悪で数秒かかり、その間ロックを握り続ける。呼び出し側は全員
            // 背景スレッドなので待たせて構わないし、むしろ三経路が同じ Compilation を
            // 同時に組み立てる（＝CPU を三重に焼く）のを防げる。
            lock (Gate)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (!Entries[i].Key.Matches(key)) continue;
                    var entry = Entries[i];
                    if (TryUpdate(entry, snapshot) is { } updated)
                    {
                        Entries.RemoveAt(i);
                        Entries.Insert(0, entry);
                        return updated;
                    }
                    Entries.RemoveAt(i);   // 差分で追いつけない＝作り直す
                    break;
                }

                var compilation = build();
                Entries.Insert(0, Entry.For(key, snapshot, compilation));
                if (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
                return compilation;
            }
        }

        /// <summary>キャッシュを捨てる（テスト用。計測が前のソリューションを引きずらないように）。</summary>
        internal static void Clear()
        {
            lock (Gate) Entries.Clear();
        }

        /// <summary>差分で追いつけるなら更新した Compilation を返し、無理なら null（＝作り直し）。</summary>
        private static CSharpCompilation? TryUpdate(Entry entry, CSharpWorkspaceSourceSnapshot snapshot)
        {
            if (entry.Updates >= MaxIncrementalUpdates) return null;
            if (entry.Texts.Count != snapshot.Texts.Count) return null;

            var changed = new List<string>();
            foreach (var (path, text) in snapshot.Texts)
            {
                if (!entry.Texts.TryGetValue(path, out var previous)) return null;   // ファイルが入れ替わった
                if (!SameParseOptions(entry, snapshot, path)) return null;
                if (!string.Equals(previous, text, StringComparison.Ordinal))
                {
                    if (changed.Count == MaxChangedFiles) return null;
                    changed.Add(path);
                }
            }

            if (changed.Count == 0) return entry.Compilation;   // 読み取りだけの呼び出し

            var compilation = entry.Compilation;
            var replaced = new List<(string Path, SyntaxTree Tree)>(changed.Count);
            foreach (var path in changed)
            {
                if (!entry.TreesByPath.TryGetValue(path, out var old)) return null;
                var tree = CSharpSyntaxTree.ParseText(
                    SourceText.From(snapshot.Texts[path]),
                    ParseOptionsFor(snapshot, path),
                    old.FilePath);
                compilation = compilation.ReplaceSyntaxTree(old, tree);
                replaced.Add((path, tree));
            }

            entry.Commit(snapshot, compilation, replaced);
            return compilation;
        }

        /// <summary>構文設定が前回と同じか。<see cref="CSharpProjectCompilationOptions.Parse"/> は
        /// 毎回新しいインスタンスを返すので、参照ではなく<b>値</b>で比べる（Roslyn の
        /// <see cref="CSharpParseOptions"/> は値等価）。</summary>
        private static bool SameParseOptions(
            Entry entry, CSharpWorkspaceSourceSnapshot snapshot, string path)
        {
            var previous = entry.ParseOptions.GetValueOrDefault(path);
            var current = ParseOptionsFor(snapshot, path);
            return ReferenceEquals(previous, current) || (previous is not null && previous.Equals(current));
        }

        private static CSharpParseOptions ParseOptionsFor(
            CSharpWorkspaceSourceSnapshot snapshot, string path)
            => snapshot.ParseOptionsByPath.TryGetValue(path, out var options)
                ? options
                : CSharpParseOptions.Default;

        /// <summary>
        /// 直前の組み立て結果。<see cref="TreesByPath"/> はスナップショットに由来する木だけを持つ
        /// ——Source Generator が足した木はここに無いので、差し替えの対象にならず残る。
        /// </summary>
        private sealed class Entry
        {
            private Entry(
                CompilationCacheKey key,
                Dictionary<string, string> texts,
                Dictionary<string, CSharpParseOptions> parseOptions,
                Dictionary<string, SyntaxTree> treesByPath,
                CSharpCompilation compilation)
            {
                Key = key;
                Texts = texts;
                ParseOptions = parseOptions;
                TreesByPath = treesByPath;
                Compilation = compilation;
            }

            public CompilationCacheKey Key { get; }
            public Dictionary<string, string> Texts { get; private set; }
            public Dictionary<string, CSharpParseOptions> ParseOptions { get; private set; }
            public Dictionary<string, SyntaxTree> TreesByPath { get; }
            public CSharpCompilation Compilation { get; private set; }
            public int Updates { get; private set; }

            public static Entry For(
                in CompilationCacheKey key,
                CSharpWorkspaceSourceSnapshot snapshot,
                CSharpCompilation compilation)
            {
                var treesByPath = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
                foreach (var tree in compilation.SyntaxTrees)
                {
                    if (string.IsNullOrWhiteSpace(tree.FilePath)) continue;
                    string full;
                    try { full = Path.GetFullPath(tree.FilePath); }
                    catch (ArgumentException) { continue; }
                    if (snapshot.Texts.ContainsKey(full)) treesByPath.TryAdd(full, tree);
                }
                return new(key, Copy(snapshot.Texts), CopyOptions(snapshot.ParseOptionsByPath),
                    treesByPath, compilation);
            }

            public void Commit(
                CSharpWorkspaceSourceSnapshot snapshot,
                CSharpCompilation compilation,
                IReadOnlyList<(string Path, SyntaxTree Tree)> replaced)
            {
                Texts = Copy(snapshot.Texts);
                ParseOptions = CopyOptions(snapshot.ParseOptionsByPath);
                foreach (var (path, tree) in replaced) TreesByPath[path] = tree;
                Compilation = compilation;
                Updates++;
            }

            private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
                => new(source, StringComparer.OrdinalIgnoreCase);

            private static Dictionary<string, CSharpParseOptions> CopyOptions(
                IReadOnlyDictionary<string, CSharpParseOptions> source)
                => new(source, StringComparer.OrdinalIgnoreCase);
        }
    }
}

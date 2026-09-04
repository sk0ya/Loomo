namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>右クリックメニューに出す C# 操作 1 項目。見出しとキー表記の正本は
/// <see cref="CSharpEditorCommandCatalog"/>——メニュー側で文字列を持ち直すと必ずズレる。</summary>
/// <param name="CommandId">押されたときに流す Command ID。</param>
/// <param name="Header">メニューの見出し（入力を尋ねる操作は末尾が「…」）。</param>
/// <param name="Gesture">既定キー。無い操作は null（キー表記の欄を空けておく）。</param>
public sealed record CSharpMenuEntry(string CommandId, string Header, string? Gesture);

/// <summary>区切り線で区切られる 1 かたまり。<paramref name="Name"/> はメニューには出さず、
/// 並びの意図をコードとテストで読めるようにするためだけに持つ。</summary>
public sealed record CSharpMenuSection(string Name, IReadOnlyList<CSharpMenuEntry> Entries);

/// <summary>「C#」サブメニューの中身。</summary>
/// <param name="Primary">サブメニューを開いてすぐ見える段（節ごと）。日常的に使うものだけ。</param>
/// <param name="MoreRewrite">「書き換え」入れ子。ここは全部が選択必須なので、選択が無いときは空。</param>
/// <param name="MoreGenerate">「生成」入れ子（たまに使う生成）。</param>
/// <param name="Tidy">「まとめて整える」入れ子のうちコマンド由来のもの。
/// プロジェクト／ソリューション範囲の一括修正は範囲の話なので View 側が足す。</param>
public sealed record CSharpMenuPlan(
    IReadOnlyList<CSharpMenuSection> Primary,
    IReadOnlyList<CSharpMenuEntry> MoreRewrite,
    IReadOnlyList<CSharpMenuEntry> MoreGenerate,
    IReadOnlyList<CSharpMenuEntry> Tidy);

/// <summary>
/// C# の右クリックメニューの<b>並び</b>を決める純関数。WPF に触らないので、
/// 「選択が無いときに押せない項目が出ていないか」「開いてすぐ見える段が短いか」を
/// そのままテストできる。
/// <para><b>方針は「毎日使うものだけを表に出す」</b>。C# の操作は 40 種あるが、
/// 右クリックはその一覧表ではない——開いてすぐ見えるのは
/// using 整理・抽出／導入／インライン化の代表・よく使う生成だけにして、
/// 残りは「書き換え」「生成」「まとめて整える」の入れ子へ落とす。
/// どれも <see cref="CSharpEditorCommandCatalog"/> の同じ Command ID なので、
/// 入れ子へ落ちた操作もコマンドパレットとキーバインドからは今までどおり 1 手で届く。</para>
/// <para>見出しとキー表記はカタログから引く。<see cref="RequiresSelection"/> は実行側
/// （<c>ShellWindow.CSharpCodeGeneration</c> の各 Run*）が <c>control.HasSelection</c> を
/// 必須にしている操作を写したもので、選択が無いときは<b>出さない</b>
/// （設計書 §23.3「押せるのに何も起きない項目は作らない」）。</para>
/// </summary>
public static class CSharpEditorMenu
{
    /// <summary>選択範囲を必須とする操作。実行側のガードと 1 対 1 で対応させる。</summary>
    private static readonly HashSet<string> RequiresSelection = new(StringComparer.Ordinal)
    {
        CSharpEditorCommandCatalog.ExtractMethod,
        CSharpEditorCommandCatalog.ExtractInterface,
        CSharpEditorCommandCatalog.ExtractClass,
        CSharpEditorCommandCatalog.ExtractField,
        CSharpEditorCommandCatalog.ExtractConstant,
        CSharpEditorCommandCatalog.MoveTypeToFile,
        CSharpEditorCommandCatalog.IntroduceVariable,
        CSharpEditorCommandCatalog.IntroduceProperty,
        CSharpEditorCommandCatalog.IntroduceParameter,
        CSharpEditorCommandCatalog.EncapsulateField,
        CSharpEditorCommandCatalog.InlineVariable,
        CSharpEditorCommandCatalog.InlineMethod,
        CSharpEditorCommandCatalog.PullUp,
        CSharpEditorCommandCatalog.PushDown,
        CSharpEditorCommandCatalog.SafeDelete,
        CSharpEditorCommandCatalog.GenerateJsonTypes,
    };

    /// <summary>入力を尋ねる操作（見出しの末尾に「…」を付ける）。</summary>
    private static readonly HashSet<string> Prompts = new(StringComparer.Ordinal)
    {
        CSharpEditorCommandCatalog.Rename,
        CSharpEditorCommandCatalog.ChangeSignature,
        CSharpEditorCommandCatalog.ExtractMethod,
        CSharpEditorCommandCatalog.ExtractInterface,
        CSharpEditorCommandCatalog.ExtractClass,
        CSharpEditorCommandCatalog.ExtractField,
        CSharpEditorCommandCatalog.ExtractConstant,
        CSharpEditorCommandCatalog.MoveTypeToFile,
        CSharpEditorCommandCatalog.IntroduceVariable,
        CSharpEditorCommandCatalog.IntroduceProperty,
        CSharpEditorCommandCatalog.IntroduceParameter,
        CSharpEditorCommandCatalog.EncapsulateField,
        CSharpEditorCommandCatalog.GenerateJsonTypes,
    };

    /// <summary>開いてすぐ見える段（＝毎日使う操作）。ここが長くなると一覧表に戻るので、
    /// 増やすときは何かを入れ子へ落とす。</summary>
    private static readonly (string Name, string[] Ids)[] PrimarySections =
    [
        ("整理", [CSharpEditorCommandCatalog.OrganizeUsings]),
        ("書き換え", [
            CSharpEditorCommandCatalog.ExtractMethod,
            CSharpEditorCommandCatalog.IntroduceVariable,
            CSharpEditorCommandCatalog.InlineVariable,
        ]),
        ("生成", [
            CSharpEditorCommandCatalog.GenerateConstructor,
            CSharpEditorCommandCatalog.GenerateProperties,
            CSharpEditorCommandCatalog.ImplementInterface,
            CSharpEditorCommandCatalog.GenerateOverride,
        ]),
    ];

    /// <summary>「書き換え」入れ子。代表以外の抽出・導入・移動・削除。</summary>
    private static readonly string[] MoreRewriteIds =
    [
        CSharpEditorCommandCatalog.ExtractInterface,
        CSharpEditorCommandCatalog.ExtractClass,
        CSharpEditorCommandCatalog.ExtractField,
        CSharpEditorCommandCatalog.ExtractConstant,
        CSharpEditorCommandCatalog.IntroduceProperty,
        CSharpEditorCommandCatalog.IntroduceParameter,
        CSharpEditorCommandCatalog.EncapsulateField,
        CSharpEditorCommandCatalog.InlineMethod,
        CSharpEditorCommandCatalog.PullUp,
        CSharpEditorCommandCatalog.PushDown,
        CSharpEditorCommandCatalog.SafeDelete,
        CSharpEditorCommandCatalog.MoveTypeToFile,
    ];

    /// <summary>「生成」入れ子。代表以外の生成。</summary>
    private static readonly string[] MoreGenerateIds =
    [
        CSharpEditorCommandCatalog.GenerateField,
        CSharpEditorCommandCatalog.GenerateEquality,
        CSharpEditorCommandCatalog.GenerateToString,
        CSharpEditorCommandCatalog.GenerateDeconstruct,
        CSharpEditorCommandCatalog.GenerateMethodFromUsage,
        CSharpEditorCommandCatalog.GenerateDelegatingMembers,
        CSharpEditorCommandCatalog.GenerateDisposePattern,
        CSharpEditorCommandCatalog.GenerateAsyncDisposePattern,
        CSharpEditorCommandCatalog.GenerateNullGuards,
        CSharpEditorCommandCatalog.GenerateJsonTypes,
    ];

    /// <summary>「まとめて整える」入れ子のコマンド由来ぶん。</summary>
    private static readonly string[] TidyIds = [CSharpEditorCommandCatalog.Cleanup];

    /// <summary>その操作が今の選択状態で実行できるか（メニューに出すかの判定に使う）。</summary>
    public static bool IsApplicable(string commandId, bool hasSelection)
        => hasSelection || !RequiresSelection.Contains(commandId);

    /// <summary>見出し（カタログの名前＋入力を尋ねるなら「…」）。</summary>
    public static string HeaderFor(string commandId)
        => Title(commandId) + (Prompts.Contains(commandId) ? "…" : "");

    /// <summary>既定キー（無ければ null）。</summary>
    public static string? GestureFor(string commandId)
        => CSharpEditorCommandCatalog.All
            .FirstOrDefault(command => command.Id == commandId)?.DefaultBinding;

    public static CSharpMenuPlan Build(bool hasSelection)
        => new(
            Sections(hasSelection, PrimarySections),
            Entries(hasSelection, MoreRewriteIds),
            Entries(hasSelection, MoreGenerateIds),
            Entries(hasSelection, TidyIds));

    private static IReadOnlyList<CSharpMenuEntry> Entries(bool hasSelection, string[] ids)
        => ids
            .Where(id => IsApplicable(id, hasSelection))
            .Select(id => new CSharpMenuEntry(id, HeaderFor(id), GestureFor(id)))
            .ToList();

    private static IReadOnlyList<CSharpMenuSection> Sections(
        bool hasSelection, (string Name, string[] Ids)[] sections)
        => sections
            .Select(section => new CSharpMenuSection(section.Name, Entries(hasSelection, section.Ids)))
            .Where(section => section.Entries.Count > 0)
            .ToList();

    private static string Title(string commandId)
        => CSharpEditorCommandCatalog.All
               .FirstOrDefault(command => command.Id == commandId)?.Title
           ?? throw new ArgumentOutOfRangeException(
               nameof(commandId), commandId, "カタログに無い C# Command ID です。");
}

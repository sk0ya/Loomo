namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>C#編集固有の操作を、UI実装から独立した安定したCommand IDとして定義する。
/// 既定ジェスチャも同じカタログに置き、App側のキーバインド一覧へそのまま投影する。</summary>
public sealed record CSharpEditorCommand(string Id, string Title, string? DefaultBinding = null);

/// <summary>
/// C#の右クリック、コマンドパレット、キーバインドが共有する操作一覧。
/// 実際のWPFイベントやEditorコントロールはApp側のアダプターに置き、ここにはC#機能の語彙だけを置く。
/// </summary>
public static class CSharpEditorCommandCatalog
{
    public const string Rename = "editor.csharp.rename";
    public const string ChangeSignature = "editor.csharp.changeSignature";
    public const string GoToDefinition = "editor.csharp.goToDefinition";
    public const string PeekDefinition = "editor.csharp.peekDefinition";
    public const string GoToImplementation = "editor.csharp.goToImplementation";
    public const string GoToTypeDefinition = "editor.csharp.goToTypeDefinition";
    public const string GoToDeclaration = "editor.csharp.goToDeclaration";
    public const string FindReferences = "editor.csharp.findReferences";
    public const string Format = "editor.csharp.format";
    public const string QuickFix = "editor.csharp.quickFix";
    public const string OrganizeUsings = "editor.csharp.organizeUsings";
    public const string Cleanup = "editor.csharp.cleanup";
    public const string ExtractMethod = "editor.csharp.extractMethod";
    public const string ExtractInterface = "editor.csharp.extractInterface";
    public const string ExtractClass = "editor.csharp.extractClass";
    public const string PullUp = "editor.csharp.pullUp";
    public const string PushDown = "editor.csharp.pushDown";
    public const string IntroduceParameter = "editor.csharp.introduceParameter";
    public const string IntroduceVariable = "editor.csharp.introduceVariable";
    public const string IntroduceProperty = "editor.csharp.introduceProperty";
    public const string ExtractConstant = "editor.csharp.extractConstant";
    public const string InlineVariable = "editor.csharp.inlineVariable";
    public const string InlineMethod = "editor.csharp.inlineMethod";
    public const string SafeDelete = "editor.csharp.safeDelete";
    public const string EncapsulateField = "editor.csharp.encapsulateField";
    public const string ExtractField = "editor.csharp.extractField";
    public const string MoveTypeToFile = "editor.csharp.moveTypeToFile";
    public const string GenerateConstructor = "editor.csharp.generateConstructor";
    public const string GenerateField = "editor.csharp.generateField";
    public const string GenerateProperties = "editor.csharp.generateProperties";
    public const string GenerateEquality = "editor.csharp.generateEquality";
    public const string GenerateToString = "editor.csharp.generateToString";
    public const string GenerateDeconstruct = "editor.csharp.generateDeconstruct";
    public const string GenerateMethodFromUsage = "editor.csharp.generateMethodFromUsage";
    public const string ImplementInterface = "editor.csharp.implementInterface";
    public const string GenerateOverride = "editor.csharp.generateOverride";
    public const string GenerateDelegatingMembers = "editor.csharp.generateDelegatingMembers";
    public const string GenerateDisposePattern = "editor.csharp.generateDisposePattern";
    public const string GenerateAsyncDisposePattern = "editor.csharp.generateAsyncDisposePattern";
    public const string GenerateNullGuards = "editor.csharp.generateNullGuards";
    public const string GenerateJsonTypes = "editor.csharp.generateJsonTypes";
    public const string MoveStatementUp = "editor.csharp.structure.moveUp";
    public const string MoveStatementDown = "editor.csharp.structure.moveDown";
    public const string MoveElementLeft = "editor.csharp.structure.moveLeft";
    public const string MoveElementRight = "editor.csharp.structure.moveRight";
    public const string DeleteSyntaxNode = "editor.csharp.structure.delete";

    public static IReadOnlyList<CSharpEditorCommand> All { get; } =
    [
        new(Rename, "名前を変更", "Shift+F6"),
        new(ChangeSignature, "シグネチャを変更", "Ctrl+F6"),
        new(GoToDefinition, "定義へ移動", "F12"),
        new(PeekDefinition, "定義をPeek表示", "Ctrl+Shift+I"),
        new(GoToImplementation, "実装へ移動", "Ctrl+Alt+B"),
        new(GoToTypeDefinition, "型定義へ移動", "Ctrl+Shift+B"),
        new(GoToDeclaration, "宣言へ移動", "Ctrl+B"),
        new(FindReferences, "参照を検索", "Alt+F7"),
        new(Format, "ドキュメントを整形", "Ctrl+Alt+L"),
        new(QuickFix, "Quick Fixを表示", "Alt+Enter"),
        new(OrganizeUsings, "usingディレクティブを整理", "Ctrl+Alt+O"),
        new(Cleanup, "コードスタイルを一括適用"),
        new(ExtractMethod, "選択範囲からメソッドを抽出", "Ctrl+Alt+M"),
        new(ExtractInterface, "クラスからinterfaceを抽出"),
        new(ExtractClass, "メンバーをクラスへ抽出"),
        new(PullUp, "メンバーを基底クラスへ移動"),
        new(PushDown, "メンバーを派生クラスへ移動"),
        new(IntroduceParameter, "パラメーターを導入"),
        new(IntroduceVariable, "選択式をローカル変数に導入"),
        new(IntroduceProperty, "選択式をプロパティに導入"),
        new(ExtractConstant, "選択リテラルを定数に抽出"),
        new(InlineVariable, "ローカル変数をインライン化"),
        new(InlineMethod, "メソッドをインライン化"),
        new(SafeDelete, "安全に削除"),
        new(EncapsulateField, "フィールドをカプセル化"),
        new(ExtractField, "選択式をフィールドに抽出"),
        new(MoveTypeToFile, "型を別ファイルへ移動"),
        new(GenerateConstructor, "コンストラクターを生成"),
        new(GenerateField, "コンストラクターパラメーターからフィールドを生成"),
        new(GenerateProperties, "プロパティを生成"),
        new(GenerateEquality, "Equals／GetHashCodeを生成"),
        new(GenerateToString, "ToStringを生成"),
        new(GenerateDeconstruct, "Deconstructを生成"),
        new(GenerateMethodFromUsage, "使用箇所からメソッドを生成"),
        new(ImplementInterface, "インターフェースを実装"),
        new(GenerateOverride, "overrideメンバーを生成"),
        new(GenerateDelegatingMembers, "委譲メンバーを生成"),
        new(GenerateDisposePattern, "Disposeパターンを生成"),
        new(GenerateAsyncDisposePattern, "非同期Disposeパターンを生成"),
        new(GenerateNullGuards, "引数のnull guardを生成"),
        new(GenerateJsonTypes, "JSONからC#型を生成"),
        // 構文木を単位にした編集（§35.2）。キーは Rider の Move Statement／Move Element に合わせる。
        new(MoveStatementUp, "文・メンバーを上へ移動", "Ctrl+Shift+Up"),
        new(MoveStatementDown, "文・メンバーを下へ移動", "Ctrl+Shift+Down"),
        new(MoveElementLeft, "引数・要素を左へ移動", "Ctrl+Alt+Shift+Left"),
        new(MoveElementRight, "引数・要素を右へ移動", "Ctrl+Alt+Shift+Right"),
        new(DeleteSyntaxNode, "構文要素を区切りごと削除"),
    ];

    public static bool Contains(string id)
        => All.Any(command => string.Equals(command.Id, id, StringComparison.Ordinal));
}

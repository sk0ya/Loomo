using Editor.Core.Syntax;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// エディタの配色のうち<b>構文トークンの色</b>を、エディタ以外の面（Diff ペインの差分本体）からも引けるようにする。
/// 正本は <see cref="ShellAppearanceCoordinator.BuildEditorTheme"/> が組む <see cref="EditorTheme"/> ひとつで、
/// 外観設定が変わるたびに ShellWindow が <see cref="Apply"/> で流し込む（＝エディタと差分の色が食い違わない）。
/// </summary>
internal static class EditorSyntaxColors
{
    private static EditorTheme _theme = ShellAppearanceCoordinator.ResolveEditorTheme(null);

    /// <summary>配色が変わった（設定でエディタテーマを変えた）。購読側は色を付け直す。</summary>
    internal static event Action? Changed;

    /// <summary>配色を差し替えた回数。<b>表示している間だけ購読する</b>側（静的イベントを掴んだままだと
    /// ビューがプロセスの最後まで生き残るため）が、「離れている間に色が変わったか」を判定するのに使う。</summary>
    internal static int Generation { get; private set; }

    internal static void Apply(EditorTheme theme)
    {
        _theme = theme;
        Generation++;
        Changed?.Invoke();
    }

    /// <summary>
    /// このファイルの字句解析器。拡張子から言語が決まるときだけ返す（決まらなければ色付けしない）。
    /// エディタ以外の面（差分本体・パレットのプレビュー）が<b>同じ解析器</b>を使うための口——
    /// 同じファイルをエディタで開いたときと色が食い違わないのが要点。
    /// </summary>
    internal static SyntaxEngine? CreateEngine(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var languages = SyntaxLanguageRegistry.CreateDefault();
        sk0ya.Loomo.CSharp.Editor.CSharpEditorIntegration.ConfigureSyntax(languages);
        var engine = new SyntaxEngine(languages);
        engine.DetectLanguage(filePath);
        return engine.LanguageName is null ? null : engine;
    }

    /// <summary>
    /// トークン種別の前景色。<b>既定色でよい種別（Text / Identifier / Operator）は null</b> を返し、
    /// 呼び出し側にアプリのテーマ色（<c>Fg</c>）を使わせる——エディタの既定前景は「暗い背景の上の明るい灰」で、
    /// アプリ側だけ明色テーマにしているとき本文がほぼ読めなくなるため。色を付けるのはキーワード・文字列・
    /// コメントのように<b>その色自体に意味がある</b>種別だけに絞る。
    /// </summary>
    internal static Brush? Foreground(TokenKind kind) => kind switch
    {
        TokenKind.Text or TokenKind.Identifier or TokenKind.Operator => null,
        _ => _theme.GetTokenBrush(kind),
    };
}

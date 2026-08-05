namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 外から来た文字列をそのまま <see cref="System.Windows.Controls.MenuItem.Header"/> に載せるための整形。
///
/// <para>WPF のメニューは <c>_</c> をアクセスキー指定と解釈し、**その1文字を表示から消す**。
/// リファクタリング候補の題名は言語サーバーが作るもので、識別子がそのまま入る
/// （<c>Introduce local for 'foo_bar'</c> → 「foobar」と化ける）。ホストが作る固定文言と違って
/// こちらでは中身を選べないので、載せる前に必ず通す。</para>
/// </summary>
internal static class MenuHeaderText
{
    /// <summary>アクセスキー解釈を無効化した見出し文字列。</summary>
    internal static string Escape(string header) => header.Replace("_", "__");
}

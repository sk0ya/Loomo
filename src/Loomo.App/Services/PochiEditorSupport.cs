namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Pochi（vim ライクなモーダル作図ツール, https://github.com/sk0ya/Pochi）の図面ファイル
/// <c>.pochi.json</c> を、EditorSupport ペインで<b>描ける</b>キャンバスとして開く提供者。公開ビルド
/// （GitHub Pages）へそのままナビゲートし、図面データは WebView2 の postMessage ブリッジで往復させる
/// （<c>ShellWindow.PochiBridge.cs</c>）。ローカルへ同梱はしない＝オフラインでは開けない。
///
/// Pochi 側は「<c>hello</c> に ops 一覧で応答したホスト」をデスクトップホストとして扱い、実装されて
/// いない op の UI は自分で隠す設計になっている（app/src/bridge.ts の <c>hasOp</c>）——Loomo は
/// <see cref="Ops"/> の3つだけを実装するので、Pochi 側の Open/Save ダイアログやファイル管理パネルは
/// 出ず、ファイルの読み書きは Loomo のエディタタブ（＝通常の保存フロー）が持つ。
///
/// ナビゲーション URI に対象ファイルのパスを載せているのは、<b>再ナビゲートの引き金</b>にするため。
/// ペインは URI が前回と同じならナビゲートを省くので（<c>EditorSupportWebViewController.RenderPending</c>）、
/// URI がアプリの URL だけだと別の .pochi.json へ切り替えても前のページが残ってしまう。逆にエディタ
/// 本文は URI に載せない——載せると打鍵のたびに再ナビゲート（＝キャンバスの状態が飛ぶ）になる。
///
/// 既知の制限：別ウィンドウへ切り離した複製（<c>DetachedEditorSupportView</c>）はブリッジを配線して
/// いないので、そちらの Pochi は握手に応答が無く<b>web ビルドとして</b>開く（＝ファイルとは無関係の
/// 図面になる）。ハンドシェイク設計のおかげで壊れはしないが、編集はホスト側へ返らない。
/// </summary>
public sealed class PochiEditorSupport : IEditorSupportUriProvider
{
    /// <summary>公開ビルド（main への push で deploy-pages.yml がデプロイする）。</summary>
    public const string AppUrl = "https://sk0ya.github.io/Pochi/";

    /// <summary>Loomo が実装するブリッジ op。<c>hello</c> の応答としてそのまま返す。</summary>
    public static readonly string[] Ops = ["hostDoc", "hostDocChanged", "hostSave"];

    private static readonly string[] Extensions = [".pochi.json"];

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // 図面データはブリッジ（hostDoc）で渡すので、ペインの描画にエディタ本文は要らない。
    public bool UsesEditorText => false;

    public string DescribeTitle(string filePath) => $"Pochi: {Path.GetFileName(filePath)}";

    public string ResolveNavigationUri(string filePath)
        => $"{BaseUrl}?host=loomo&f={Uri.EscapeDataString(Path.GetFullPath(filePath))}";

    /// <summary>
    /// 読み込み元。既定は公開ビルドで、環境変数 <c>POCHI_DEV_URL</c>（Pochi 本体の desktop シェルと同じ
    /// 名前）があればそちらを見る——未 push の Pochi 側変更をローカルの Vite 開発サーバーで確かめるための
    /// 逃げ道で、通常運用では使わない。
    /// </summary>
    private static string BaseUrl
    {
        get
        {
            var dev = Environment.GetEnvironmentVariable("POCHI_DEV_URL");
            return string.IsNullOrWhiteSpace(dev) ? AppUrl : dev.TrimEnd('/') + "/";
        }
    }
}

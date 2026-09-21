using System;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// タイトルバーのデバッグメニューの中身（ブランチ切替＝<see cref="BranchSwitcherView"/> と同じ位置づけ）。
///
/// 構成は上から「操作帯（実行／デバッグ／ビルド／テスト・停止中/実行中の操作・状態）」「実行ターゲット一覧」
/// 「構成を編集…」。帯を一覧の外に固定しているのは対象が違うから——帯の操作は<b>いま選んでいる対象</b>に効き、
/// 一覧の行は<b>その行</b>に効く。行は Rider と同じく「1 行＝そのまま実行できる 1 つの対象」
/// （<see cref="DebugRunTargetItem"/>）なので、行を選べば切替、行の ▶／🐞 を押せばその場で実行・デバッグ。
///
/// DataContext は <see cref="DebugViewModel"/>（IDE ペインと同じインスタンス。ここで対象を切り替えれば
/// ペイン側のヘッダーにも即反映される）。デバッグ起動は VM に任せ、デバッグなしの実行だけは可視ターミナルを
/// 使うホスト側の仕事なので <see cref="RunRequested"/> でウィンドウへ投げる。
/// </summary>
public partial class DebugSwitcherView : UserControl
{
    private readonly DebugSwitcherController _controller;

    public DebugSwitcherView()
    {
        InitializeComponent();
        _controller = new DebugSwitcherController(
            () => DataContext as DebugViewModel,
            () => CloseRequested?.Invoke(this, EventArgs.Empty),
            project => RunRequested?.Invoke(this, project),
            () => OpenIdePaneRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>ポップアップを閉じてほしい。実際に閉じるのは Popup を持つ側（ShellWindow）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>IDE ペインを部屋に出してほしい（ペインの出し入れはウィンドウ側にしかできない）。</summary>
    public event EventHandler? OpenIdePaneRequested;

    /// <summary>デバッグなしで実行してほしい（.csproj の絶対パス）。dotnet run は可視ターミナルで走らせる
    /// ——Solution Explorer の「実行」と同じ経路をウィンドウ側が持っている。</summary>
    public event EventHandler<string>? RunRequested;

    /// <summary>開く直前に、アダプタ（netcoredbg）の有無と実行ターゲット一覧を作り直す。開いている間に
    /// 導入されたり、プロジェクトの launchSettings.json が増えたりするので、起動時の結果のままにしない。</summary>
    public void PrepareForOpen()
        => _controller.PrepareForOpen();

    /// <summary>帯の操作（デバッグ／ビルド／テスト／続行・ステップ／中断・再起動・停止）。コマンド自体は
    /// Command バインディングが実行する（IsEnabled も CanExecute に従う）ので、ここは閉じるだけ。</summary>
    private void OnActionClick(object sender, RoutedEventArgs e) => _controller.Close();

    /// <summary>帯の「▶ 実行」＝いま選んでいる対象をデバッグなしで実行する。</summary>
    private void OnRunCurrentClick(object sender, RoutedEventArgs e) => _controller.RunCurrent();

    private void OnTargetClick(object sender, RoutedEventArgs e) => _controller.SelectCurrentTarget(sender);

    /// <summary>行の 🐞 ＝その対象に切り替えてそのままデバッグを開始する。</summary>
    private void OnTargetDebugClick(object sender, RoutedEventArgs e) => _controller.DebugTarget(sender, e);

    /// <summary>行の ▶ ＝その対象に切り替えてデバッグなしで実行する。</summary>
    private void OnTargetRunClick(object sender, RoutedEventArgs e) => _controller.RunTarget(sender, e);

    private void OnOpenIdePaneClick(object sender, RoutedEventArgs e) => _controller.OpenIdePane();
}

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
    public DebugSwitcherView() => InitializeComponent();

    /// <summary>ポップアップを閉じてほしい。実際に閉じるのは Popup を持つ側（ShellWindow）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>IDE ペインを部屋に出してほしい（ペインの出し入れはウィンドウ側にしかできない）。</summary>
    public event EventHandler? OpenIdePaneRequested;

    /// <summary>デバッグなしで実行してほしい（.csproj の絶対パス）。dotnet run は可視ターミナルで走らせる
    /// ——Solution Explorer の「実行」と同じ経路をウィンドウ側が持っている。</summary>
    public event EventHandler<string>? RunRequested;

    private DebugViewModel? Vm => DataContext as DebugViewModel;

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>開く直前に、アダプタ（netcoredbg）の有無と実行ターゲット一覧を作り直す。開いている間に
    /// 導入されたり、プロジェクトの launchSettings.json が増えたりするので、起動時の結果のままにしない。</summary>
    public void PrepareForOpen()
    {
        if (Vm is not { } vm) return;
        vm.Refresh();
        vm.Profiles.RefreshRunTargets();
    }

    /// <summary>帯の操作（デバッグ／ビルド／テスト／続行・ステップ／中断・再起動・停止）。コマンド自体は
    /// Command バインディングが実行する（IsEnabled も CanExecute に従う）ので、ここは閉じるだけ。</summary>
    private void OnActionClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>帯の「▶ 実行」＝いま選んでいる対象をデバッグなしで実行する。</summary>
    private void OnRunCurrentClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.Profiles.SelectedProjectPath is not { } project) return;
        Close();
        RunRequested?.Invoke(this, project);
    }

    private void OnTargetClick(object sender, RoutedEventArgs e)
    {
        if (SelectTarget(sender) is null) return;
        Close();
    }

    /// <summary>行の 🐞 ＝その対象に切り替えてそのままデバッグを開始する。</summary>
    private void OnTargetDebugClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // 外側の行ボタンへは伝えない（同じ選択が二度走るのを避ける）
        if (SelectTarget(sender) is null || Vm is not { } vm) return;
        Close();
        if (vm.Launch.StartCommand.CanExecute(null))
            vm.Launch.StartCommand.Execute(null);
    }

    /// <summary>行の ▶ ＝その対象に切り替えてデバッグなしで実行する。</summary>
    private void OnTargetRunClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectTarget(sender) is not { RunProjectPath: { } project }) return;
        Close();
        RunRequested?.Invoke(this, project);
    }

    /// <summary>押された行の対象へ切り替える（切り替えた行を返す）。</summary>
    private DebugRunTargetItem? SelectTarget(object sender)
    {
        if (Vm is not { } vm || sender is not FrameworkElement { Tag: DebugRunTargetItem item })
            return null;
        vm.Profiles.SelectRunTarget(item);
        return item;
    }

    private void OnOpenIdePaneClick(object sender, RoutedEventArgs e)
    {
        Close();
        OpenIdePaneRequested?.Invoke(this, EventArgs.Empty);
    }
}

using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Git セッションペイン。コミットグラフ・ブランチ一覧の表示と、コンテキストメニューからの
/// 複雑な git 操作（rebase / merge / cherry-pick / reset 等）を受け付ける。
/// 名前入力・破壊的操作の確認ダイアログはここ（ビュー）が担い、git 実行は ViewModel に委ねる。
/// </summary>
public partial class GitSessionView : UserControl
{
    private GitSessionViewModel? _subscribed;

    public GitSessionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SetupLogColumnResize();
    }

    private GitSessionViewModel? Vm => DataContext as GitSessionViewModel;

    // ===== コミット詳細（変更ファイル一覧）のリンク描画 =====

    /// <summary>DataContext（VM）の差し替えに追従し、CommitDetail の変化を購読し直す。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = Vm;
        if (_subscribed is not null)
            _subscribed.PropertyChanged += OnVmPropertyChanged;
        RenderCommitDetail();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitSessionViewModel.CommitDetail))
            RenderCommitDetail();
    }

    /// <summary>
    /// CommitDetail（<c>git show --stat</c> の生テキスト）を RichTextBox へ整形描画する。
    /// 変更ファイル一覧の統計行はファイルパス部分だけを Hyperlink 化し、その他の行は素のまま流す。
    /// 幅は表示領域へ折り返す（固定 PageWidth を与えると横スクロールバーが縦スクロールの Auto 判定と
    /// 競合し、下端が横バーに隠れて見えなくなるため）。通常のペイン幅では stat 行は折り返さず桁も保たれる。
    /// </summary>
    private void RenderCommitDetail()
    {
        var text = Vm?.CommitDetail ?? "";
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var accent = TryFindResource("Accent") as Brush ?? Brushes.SteelBlue;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            AppendLine(paragraph, lines[i], accent);
            if (i < lines.Length - 1)
                paragraph.Inlines.Add(new LineBreak());
        }

        var doc = new FlowDocument(paragraph)
        {
            FontFamily = CommitDetailBox.FontFamily,
            FontSize = CommitDetailBox.FontSize,
            PagePadding = new Thickness(6, 4, 6, 4),
        };
        CommitDetailBox.Document = doc;
    }

    /// <summary>1 行を Inline 群として追加。統計行ならパス部分を Hyperlink にする。</summary>
    private void AppendLine(Paragraph paragraph, string line, Brush accent)
    {
        if (CommitStatLinks.TryParse(line) is { } stat)
        {
            var before = line[..stat.PathIndex];
            var pathText = line.Substring(stat.PathIndex, stat.PathLength);
            var after = line[(stat.PathIndex + stat.PathLength)..];

            if (before.Length > 0) paragraph.Inlines.Add(new Run(before));
            var link = new Hyperlink(new Run(pathText))
            {
                Foreground = accent,
                Cursor = Cursors.Hand,
                ToolTip = $"{stat.NavigatePath} をエディタで開く",
                Tag = stat.NavigatePath,
            };
            link.Click += OnChangedFileClick;
            paragraph.Inlines.Add(link);
            if (after.Length > 0) paragraph.Inlines.Add(new Run(after));
        }
        else
        {
            paragraph.Inlines.Add(new Run(line));
        }
    }

    private async void OnChangedFileClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Hyperlink { Tag: string path })
            await vm.OpenChangedFileAsync(path);
    }

    /// <summary>右クリックでも対象行を選択状態にする（コンテキストメニューの対象を確定させる）。</summary>
    private void OnListRightClickSelect(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        // OriginalSource が Run 等の FrameworkContentElement のことがある（VisualTreeHelper だと例外）
        while (element is not null and not ListBoxItem and not TreeViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        if (element is ListBoxItem listItem)
            listItem.IsSelected = true;
        else if (element is TreeViewItem treeItem)
            treeItem.IsSelected = true;
    }

    // ===== ブランチ操作 =====

    /// <summary>ツリーで選択中のブランチ。フォルダノード選択中は null（各操作は何もしない）。</summary>
    private GitBranchInfo? SelectedBranch => (BranchList.SelectedItem as BranchTreeNode)?.Branch;

    /// <summary>
    /// ブランチのダブルクリックはチェックアウトではなく、右側のコミットグラフをそのブランチに切り替える
    /// （ブランチの切り替え自体はヘッダーのブランチ切替コントロールから行う）。
    /// </summary>
    private async void OnBranchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.ShowBranchLogAsync(branch);
    }

    /// <summary>
    /// フォルダ行はクリック一回で開閉する（リーフ＝ブランチ行は選択のまま：ダブルクリックでログ表示）。
    /// 展開矢印（ToggleButton, ClickMode=Press）上のクリックは既に開閉済みなので二重に反応しない。
    /// </summary>
    private void OnBranchTreeClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton)
                return;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (element is TreeViewItem { DataContext: BranchTreeNode { IsFolder: true } } item)
            item.IsExpanded = !item.IsExpanded;
    }

    private async void OnShowAllBranchesLog(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.ShowAllBranchesLogAsync();
    }

    /// <summary>
    /// 対象が無い（フォルダ・見出しを右クリックした）ならメニューごと出さない。ブランチ行なら、
    /// そのブランチに意味を成さない項目を無効化する（自分自身へのチェックアウト／マージ／リベース、
    /// 現在ブランチの削除、リモートブランチの削除＝git branch -d では消せない）。
    /// タイトルバーのブランチ切替（BranchSwitcherView.OnTreeContextMenuOpening）と同じ作法。
    /// </summary>
    private void OnBranchContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedBranch is not { } branch)
        {
            e.Handled = true;
            return;
        }

        BranchMenuCheckout.IsEnabled = !branch.IsCurrent;
        BranchMenuMerge.IsEnabled = !branch.IsCurrent;
        BranchMenuMergeStrategy.IsEnabled = !branch.IsCurrent;
        BranchMenuRebase.IsEnabled = !branch.IsCurrent;
        BranchMenuDelete.IsEnabled = !branch.IsCurrent && !branch.IsRemote;
    }

    /// <summary>ダブルクリックと同じ「右のコミットグラフをこのブランチに切り替える」を右クリックからも。</summary>
    private async void OnBranchShowLog(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.ShowBranchLogAsync(branch);
    }

    private async void OnBranchCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.CheckoutBranchAsync(branch);
    }

    private async void OnBranchMerge(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.MergeAsync(branch);
    }

    private async void OnBranchMergeFastForwardOnly(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.MergeAsync(branch, GitMergeStrategy.FastForwardOnly);
    }

    private async void OnBranchMergeNoFastForward(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.MergeAsync(branch, GitMergeStrategy.NoFastForward);
    }

    private async void OnBranchMergeSquash(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedBranch is { } branch)
            await vm.MergeAsync(branch, GitMergeStrategy.Squash);
    }

    private async void OnBranchRebase(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"現在のブランチを {branch.Name} の上へリベースします。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "リベース", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await vm.RebaseAsync(branch);
    }

    private async void OnBranchCreateFrom(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ",
            $"{branch.Name} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.CreateBranchAsync(name, branch.Name);
    }

    private void OnBranchCopyName(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch is { } branch)
        {
            try { Clipboard.SetText(branch.Name); } catch { /* クリップボード占有中は無視 */ }
        }
    }

    private async void OnBranchDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedBranch is not { } branch)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"ブランチ {branch.Name} を削除しますか？",
            "ブランチ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var result = await vm.DeleteBranchAsync(branch, force: false);
        if (result is { Success: false } &&
            result.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            var forceAnswer = MessageBox.Show(Window.GetWindow(this)!,
                $"{branch.Name} はマージされていないコミットを含みます。強制削除（-D）しますか？\nコミットが失われる可能性があります。",
                "ブランチの強制削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (forceAnswer == MessageBoxResult.Yes)
                await vm.DeleteBranchAsync(branch, force: true);
        }
    }

    // ===== タグ操作 =====

    private GitTagInfo? SelectedTag => TagList.SelectedItem as GitTagInfo;

    private async void OnTagDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && SelectedTag is { } tag)
            await vm.CheckoutTagAsync(tag);
    }

    private async void OnTagCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedTag is { } tag)
            await vm.CheckoutTagAsync(tag);
    }

    private async void OnTagPush(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedTag is { } tag)
            await vm.PushTagAsync(tag);
    }

    private void OnTagCopyName(object sender, RoutedEventArgs e)
    {
        if (SelectedTag is { } tag)
        {
            try { Clipboard.SetText(tag.Name); } catch { /* クリップボード占有中は無視 */ }
        }
    }

    private async void OnTagDelete(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedTag is not { } tag)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"タグ {tag.Name} を削除しますか？",
            "タグ削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.DeleteTagAsync(tag);
    }

    private async void OnTagCreate(object sender, RoutedEventArgs e) => await CreateTagAsync(target: null);

    private async void OnCommitCreateTag(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is { } row)
            await CreateTagAsync(row.Hash);
    }

    private async void OnTagsPushAll(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.PushAllTagsAsync();
    }

    /// <summary>タグ名（必須）→メッセージ（任意）の順に入力を取り、作成する。</summary>
    private async Task CreateTagAsync(string? target)
    {
        if (Vm is not { } vm)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "タグを作成", "タグ名を入力してください");
        if (string.IsNullOrWhiteSpace(name))
            return;
        var message = InputDialog.Prompt(Window.GetWindow(this), "タグを作成",
            "注釈メッセージ（空なら軽量タグ）:", allowEmpty: true);
        if (message is null)
            return; // メッセージ入力でキャンセル
        await vm.CreateTagAsync(name, target, string.IsNullOrWhiteSpace(message) ? null : message);
    }

    // ===== サブモジュール操作 =====

    private GitSubmoduleInfo? SelectedSubmodule => SubmoduleList.SelectedItem as GitSubmoduleInfo;

    private async void OnSubmoduleInit(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedSubmodule is { } submodule)
            await vm.InitSubmoduleAsync(submodule);
    }

    private async void OnSubmoduleUpdate(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedSubmodule is { } submodule)
            await vm.UpdateSubmoduleAsync(submodule);
    }

    private async void OnSubmodulesSync(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            await vm.SyncSubmodulesAsync();
    }

    private void OnSubmoduleCopyPath(object sender, RoutedEventArgs e)
    {
        if (SelectedSubmodule is { } submodule)
        {
            try { Clipboard.SetText(submodule.Path); } catch { /* クリップボード占有中は無視 */ }
        }
    }

    // ===== コミット操作 =====

    private GitLogRow? SelectedCommit =>
        LogList.SelectedItem is GitLogRow { IsCommit: true } row ? row : null;

    /// <summary>
    /// コミット一覧を末尾付近までスクロールしたら次ページを追加読み込みする（無限スクロール）。
    /// 下方向のスクロール（またはビューポート縮小）でのみ判定し、追加読み込み後の伸長で連鎖発火しないよう
    /// 純粋な内容伸長（VerticalChange・ViewportHeightChange が 0）は無視する。仮想化は既定の行単位スクロール
    /// なので Extent/Offset/Viewport は行数単位だが、末尾までの残り行数で判定する式は同じく成立する。
    /// </summary>
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (e.VerticalChange <= 0 && e.ViewportHeightChange <= 0)
            return;
        if (e.ExtentHeight <= 0)
            return;
        var remaining = e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight);
        if (remaining <= e.ViewportHeight)
            _ = vm.LoadMoreLogAsync();
    }

    /// <summary>選択コミットの差分を Diff セッションへ（1件=コミットの変更、複数=端点間の比較）。</summary>
    private void OnCommitShowDiff(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var rows = LogList.SelectedItems.OfType<GitLogRow>().Where(r => r.IsCommit).ToList();
        vm.OpenDiffForCommits(rows);
    }

    private async void OnCommitCreateBranch(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新しいブランチ",
            $"コミット {row.ShortHash} から作成するブランチ名を入力してください");
        if (!string.IsNullOrWhiteSpace(name))
            await vm.CreateBranchAsync(name, row.Hash);
    }

    private async void OnCommitCheckout(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.CheckoutCommitAsync(row);
    }

    private async void OnCommitRewriteMessage(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var current = await vm.GetCommitMessageAsync(row);
        var message = InputDialog.Prompt(Window.GetWindow(this), "コミットメッセージを修正",
            $"{row.ShortHash} のコミットメッセージを入力してください。\nこのコミット以降の履歴が書き換わります。",
            current, multiline: true);
        if (message is null || string.Equals(message, current, StringComparison.Ordinal))
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} 以降のコミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "コミットメッセージを修正", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.RewriteCommitMessageAsync(row, message);
    }

    /// <summary>選択中のコミット件数（グラフ継続行は除く）。</summary>
    private int SelectedCommitCount =>
        LogList.SelectedItems.OfType<GitLogRow>().Count(r => r.IsCommit);

    /// <summary>
    /// コミット一覧のコンテキストメニューを開く直前：スカッシュは2件以上、インタラクティブリベースは
    /// 単一選択時だけ見せる。
    /// </summary>
    private void OnCommitContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var visible = SelectedCommitCount >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SquashMenuItem.Visibility = visible;
        SquashSeparator.Visibility = visible;
        InteractiveRebaseMenuItem.Visibility = SelectedCommitCount == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 選択コミットからHEADまでをインタラクティブリベースする。候補取得→確認→ダイアログ→実行の順。
    /// </summary>
    private async void OnCommitInteractiveRebase(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var (entries, error) = await vm.GetRebaseCandidatesAsync(row);
        if (error is not null)
        {
            MessageBox.Show(Window.GetWindow(this)!, error, "インタラクティブリベース",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} から HEAD までの履歴が書き換わります。実行しますか？",
            "インタラクティブリベース", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        var plan = InteractiveRebaseDialog.Show(Window.GetWindow(this), entries);
        if (plan is null)
            return;
        await vm.InteractiveRebaseAsync(row.Hash!, plan.Value.Plan, plan.Value.Messages);
    }

    /// <summary>選択した複数コミットを1つにまとめる（squash）。履歴を書き換えるので確認を取る。</summary>
    private async void OnCommitSquash(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var rows = LogList.SelectedItems.OfType<GitLogRow>().Where(r => r.IsCommit).ToList();
        if (rows.Count < 2)
            return;  // メニューは2件以上のときだけ出るが念のため
        var combinedMessage = await vm.GetCombinedCommitMessageAsync(rows);
        var message = InputDialog.Prompt(Window.GetWindow(this), "スカッシュ後のコミットメッセージ",
            "スカッシュ後に使用するコミットメッセージを編集してください。",
            combinedMessage, multiline: true);
        if (message is null)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"選択した {rows.Count} 件のコミットを1つにまとめます。コミットは作り直されます（履歴が書き換わります）。\n実行しますか？",
            "スカッシュ", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            await vm.SquashAsync(rows, message);
    }

    private async void OnCommitCherryPick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.CherryPickAsync(row);
    }

    private async void OnCommitRevert(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.RevertAsync(row);
    }

    private async void OnCommitResetSoft(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.ResetAsync(row, GitResetMode.Soft);
    }

    private async void OnCommitResetMixed(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.ResetAsync(row, GitResetMode.Mixed);
    }

    private async void OnCommitResetHard(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || SelectedCommit is not { } row)
            return;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"{row.ShortHash} まで hard リセットします。作業ツリー・インデックスの変更はすべて失われます。\n実行しますか？",
            "リセット (hard)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
            await vm.ResetAsync(row, GitResetMode.Hard);
    }

    private async void OnCommitOpenPatch(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && SelectedCommit is { } row)
            await vm.OpenPatchAsync(row);
    }

    private void OnCommitCopyHash(object sender, RoutedEventArgs e)
    {
        if (SelectedCommit is { Hash: { } hash })
        {
            try { Clipboard.SetText(hash); } catch { /* クリップボード占有中は無視 */ }
        }
    }
}

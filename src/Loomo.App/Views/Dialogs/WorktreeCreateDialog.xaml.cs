using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ワークツリー作成ダイアログの結果。</summary>
public sealed record WorktreeCreateResult(GitWorktreeAddRequest Request, GitWorktreeOpenMode? OpenAfter);

/// <summary>
/// ワークツリー作成ダイアログ。作り方（新しいブランチ／既存のブランチ／デタッチ）・ブランチ・起点・置き場所・
/// 作成後の開き方を1枚で聞く。検証と要求の組み立ては <see cref="GitWorktreeCreatePolicy"/>（純ロジック）。
/// </summary>
public partial class WorktreeCreateDialog : Window
{
    /// <summary>「作成後」の選択を同じセッションの次回へ持ち越す（毎回選び直させない）。</summary>
    private static string s_lastAfter = nameof(GitWorktreeOpenMode.Workspace);

    private readonly Func<string?, string> _suggestPath;
    /// <summary>相対パスで打たれた置き場所の基準（メインのワークツリー）。</summary>
    private string? _baseFolder;
    private GitWorktreeBranchContext _branches = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    private string? _lastSuggested;
    private WorktreeCreateResult? _result;

    private WorktreeCreateDialog(Func<string?, string> suggestPath)
    {
        _suggestPath = suggestPath;
        InitializeComponent();
    }

    /// <summary>ダイアログを開く。キャンセルなら null。</summary>
    /// <param name="startPoint">起点の初期値（コミット一覧の「ここからワークツリー」等。null なら現在ブランチ）。</param>
    /// <param name="existingBranch">「既存のブランチ」で開くときのブランチ（ブランチ一覧から来たとき）。</param>
    public static WorktreeCreateResult? Prompt(
        Window? owner, GitSessionViewModel vm, string? startPoint = null, string? existingBranch = null)
    {
        var dialog = new WorktreeCreateDialog(vm.SuggestWorktreePath) { Owner = owner };
        dialog.Initialize(vm, startPoint, existingBranch);
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private void Initialize(GitSessionViewModel vm, string? startPoint, string? existingBranch)
    {
        _baseFolder = vm.MainWorktreePath;
        var infos = vm.BranchInfos;
        var checkedOut = vm.Worktrees.Select(w => w.Branch).OfType<string>().ToList();
        _branches = new GitWorktreeBranchContext(
            infos.Where(b => !b.IsRemote).Select(b => b.Name).ToList(),
            infos.Where(b => b.IsRemote).Select(b => b.Name).ToList(),
            checkedOut);

        ExistingBranchBox.ItemsSource = vm.CheckoutableBranches();
        StartPointBox.ItemsSource = vm.WorktreeStartPointOptions();
        StartPointBox.Text = startPoint ?? infos.FirstOrDefault(b => b.IsCurrent)?.Name ?? "HEAD";
        AfterBox.SelectedItem = AfterBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s_lastAfter) ?? AfterBox.Items[0];

        if (existingBranch is not null)
        {
            ModeExisting.IsChecked = true;
            ExistingBranchBox.Text = existingBranch;
            FollowBranch(existingBranch);
        }
        else
        {
            FollowBranch("");
        }

        Loaded += (_, _) =>
        {
            if (ModeExisting.IsChecked == true) ExistingBranchBox.Focus();
            else NewBranchBox.Focus();
        };
    }

    private GitWorktreeAddMode Mode => ModeExisting.IsChecked == true ? GitWorktreeAddMode.ExistingBranch
        : ModeDetached.IsChecked == true ? GitWorktreeAddMode.Detached
        : GitWorktreeAddMode.NewBranch;

    private string BranchInput => Mode == GitWorktreeAddMode.ExistingBranch ? ExistingBranchBox.Text : NewBranchBox.Text;

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        // InitializeComponent の途中（既定の IsChecked）でも呼ばれるので、まだ無い要素は触らない。
        if (NewBranchBox is null || ExistingBranchBox is null || StartPointBox is null) return;
        var mode = Mode;
        NewBranchBox.Visibility = mode == GitWorktreeAddMode.NewBranch ? Visibility.Visible : Visibility.Collapsed;
        ExistingBranchBox.Visibility = mode == GitWorktreeAddMode.ExistingBranch ? Visibility.Visible : Visibility.Collapsed;
        BranchLabel.Visibility = mode == GitWorktreeAddMode.Detached ? Visibility.Collapsed : Visibility.Visible;
        // 既存ブランチを置くときは起点は要らない（ブランチの先端がそのまま置かれる）。
        var showStart = mode != GitWorktreeAddMode.ExistingBranch;
        StartPointBox.Visibility = showStart ? Visibility.Visible : Visibility.Collapsed;
        StartLabel.Visibility = showStart ? Visibility.Visible : Visibility.Collapsed;
        StartLabel.Text = mode == GitWorktreeAddMode.Detached ? "コミット" : "起点";
        FollowBranch(mode == GitWorktreeAddMode.Detached ? StartPointBox.Text : BranchInput);
    }

    private void OnBranchTextChanged(object sender, TextChangedEventArgs e) => FollowBranch(NewBranchBox.Text);

    private void OnExistingBranchChanged(object sender, SelectionChangedEventArgs e)
    {
        // リモートのブランチは同名のローカルとして置かれるので、フォルダー名もローカル名から作る。
        if (ExistingBranchBox.SelectedItem is string branch)
            FollowBranch(_branches.Remote.Contains(branch) && !_branches.Local.Contains(branch)
                ? GitWorktreeCreatePolicy.LocalNameForRemote(branch) : branch);
    }

    private void OnExistingBranchKeyUp(object sender, KeyEventArgs e) => FollowBranch(ExistingBranchBox.Text);

    /// <summary>置き場所をブランチ名に追従させる（利用者が打ち直していなければ）。</summary>
    private void FollowBranch(string? branch)
    {
        if (PathBox is null) return;
        if (!GitWorktreeCreatePolicy.ShouldFollowBranch(PathBox.Text, _lastSuggested)) return;
        var suggested = _suggestPath(string.IsNullOrWhiteSpace(branch) ? null : branch);
        _lastSuggested = suggested;
        PathBox.Text = suggested;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var current = PathBox.Text.Trim();
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "ワークツリーを置く親フォルダーを選択",
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(current) ?? "")
                ? Path.GetDirectoryName(current) : null,
        };
        if (picker.ShowDialog(this) != true) return;
        // 選ぶのは親フォルダー。その下にブランチ名のフォルダーを掘る（選んだフォルダーそのものに
        // 置くと、中身があるとき必ず失敗するため）。
        var leaf = GitWorktreeArgs.SanitizeFolderName(
            Mode == GitWorktreeAddMode.Detached ? StartPointBox.Text : BranchInput);
        PathBox.Text = Path.Combine(picker.FolderName, leaf.Length > 0 ? leaf : "worktree");
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var mode = Mode;
        var error = GitWorktreeCreatePolicy.Validate(mode, BranchInput, StartPointBox.Text, PathBox.Text, _branches, _baseFolder);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var request = GitWorktreeCreatePolicy.BuildRequest(mode, BranchInput, StartPointBox.Text, PathBox.Text, _branches, _baseFolder);
        var afterTag = (AfterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        s_lastAfter = afterTag;
        _result = new WorktreeCreateResult(request,
            Enum.TryParse<GitWorktreeOpenMode>(afterTag, out var after) ? after : null);
        DialogResult = true;
    }
}

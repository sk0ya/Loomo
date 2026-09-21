using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct GitCommitMenuPolicy(
    bool ShowSquash,
    bool ShowInteractiveRebase,
    bool ShowFileRevision,
    bool ShowHosting,
    bool ShowFilterByAuthor,
    bool CanFilterByAuthor);

/// <summary>コミットのコンテキストメニュー表示状態を計算して反映する。</summary>
internal static class GitSessionMenuPolicyMapper
{
    internal static bool CanOpenChangedFileMenu(
        bool keyboardInvocation, bool pointerOnRow, bool hasSelectedFile)
        => (keyboardInvocation || pointerOnRow) && hasSelectedFile;

    internal static void ApplyCommitMenu(
        GitSessionViewModel? viewModel,
        GitLogRow? selectedCommit,
        int selectedCommitCount,
        MenuItem squash,
        MenuItem interactiveRebase,
        Separator fileRevisionSeparator,
        MenuItem fileRevision,
        MenuItem openOnHosting,
        MenuItem copyHostingUrl,
        MenuItem filterByAuthor)
    {
        var policy = ForCommitMenu(viewModel, selectedCommit, selectedCommitCount);
        squash.Visibility = policy.ShowSquash ? Visibility.Visible : Visibility.Collapsed;
        interactiveRebase.Visibility = policy.ShowInteractiveRebase
            ? Visibility.Visible : Visibility.Collapsed;
        var fileRevisionVisibility = policy.ShowFileRevision ? Visibility.Visible : Visibility.Collapsed;
        fileRevisionSeparator.Visibility = fileRevisionVisibility;
        fileRevision.Visibility = fileRevisionVisibility;
        var hostingVisibility = policy.ShowHosting ? Visibility.Visible : Visibility.Collapsed;
        openOnHosting.Visibility = hostingVisibility;
        copyHostingUrl.Visibility = hostingVisibility;
        filterByAuthor.Visibility = policy.ShowFilterByAuthor
            ? Visibility.Visible : Visibility.Collapsed;
        filterByAuthor.IsEnabled = policy.CanFilterByAuthor;
    }

    internal static GitCommitMenuPolicy ForCommitMenu(
        GitSessionViewModel? viewModel,
        GitLogRow? selectedCommit,
        int selectedCommitCount)
    {
        var hasAuthor = selectedCommit?.Author is { Length: > 0 };
        var isFilteredByAuthor = selectedCommit is { Author.Length: > 0 } row
            && viewModel is { } vm && vm.IsFilteredByAuthor(row);
        return new GitCommitMenuPolicy(
            ShowSquash: selectedCommitCount >= 2,
            ShowInteractiveRebase: selectedCommitCount == 1,
            ShowFileRevision: viewModel?.IsFileHistory == true && selectedCommitCount == 1,
            ShowHosting: viewModel?.IsGitHubRepository == true && selectedCommit is not null,
            ShowFilterByAuthor: hasAuthor,
            CanFilterByAuthor: hasAuthor && !isFilteredByAuthor);
    }
}

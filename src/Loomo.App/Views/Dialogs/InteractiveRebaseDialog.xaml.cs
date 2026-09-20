using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>アクション選択コンボボックスの選択肢1件（表示ラベル付き）。</summary>
public sealed record RebaseActionOption(RebaseAction Action, string Label);

/// <summary>インタラクティブリベースダイアログの1行分の編集状態。</summary>
public sealed partial class RebaseRowVm : ObservableObject
{
    public static readonly IReadOnlyList<RebaseActionOption> AllActionOptions = new[]
    {
        new RebaseActionOption(RebaseAction.Pick, "Pick（採用）"),
        new RebaseActionOption(RebaseAction.Reword, "Reword（メッセージ修正）"),
        new RebaseActionOption(RebaseAction.Edit, "Edit（一時停止）"),
        new RebaseActionOption(RebaseAction.Squash, "Squash（前と結合・メッセージ統合）"),
        new RebaseActionOption(RebaseAction.Fixup, "Fixup（前と結合・メッセージ破棄）"),
        new RebaseActionOption(RebaseAction.Drop, "Drop（除外）"),
    };

    public RebaseRowVm(RebasePlanEntry entry)
    {
        Hash = entry.Hash;
        ShortHash = entry.ShortHash;
        Subject = entry.Subject;
        _action = entry.Action;
    }

    public string Hash { get; }
    public string ShortHash { get; }
    public string Subject { get; }

    /// <summary>ComboBox の ItemsSource（インスタンスプロパティとしてバインド可能にする）。</summary>
    public IReadOnlyList<RebaseActionOption> ActionOptions => AllActionOptions;

    [ObservableProperty] private RebaseAction _action;

    /// <summary>Reword 選択時の新しいメッセージ（未入力なら null）。</summary>
    public string? Message { get; set; }

    public RebasePlanEntry ToEntry() => new(Hash, ShortHash, Subject, Action);
}

/// <summary>
/// インタラクティブリベースの計画（順序・アクション・reword メッセージ）を組み立てるモーダルダイアログ。
/// 実際の git 実行は呼び出し側（<see cref="GitSessionView"/>）が <see cref="GitSessionViewModel"/> 経由で行う。
/// </summary>
public partial class InteractiveRebaseDialog : Window
{
    private readonly ObservableCollection<RebaseRowVm> _rows;

    private InteractiveRebaseDialog(IReadOnlyList<RebasePlanEntry> candidates)
    {
        InitializeComponent();
        _rows = new ObservableCollection<RebaseRowVm>(candidates.Select(c => new RebaseRowVm(c)));
        DataContext = _rows;
    }

    /// <summary>ダイアログを開き、確定した計画と reword メッセージを返す。キャンセル時は null。</summary>
    public static (IReadOnlyList<RebasePlanEntry> Plan, IReadOnlyDictionary<string, string> Messages)? Show(
        Window? owner, IReadOnlyList<RebasePlanEntry> candidates)
    {
        var dialog = new InteractiveRebaseDialog(candidates) { Owner = owner };
        if (dialog.ShowDialog() != true)
            return null;

        var plan = dialog._rows.Select(r => r.ToEntry()).ToList();
        var messages = dialog._rows
            .Where(r => r.Action == RebaseAction.Reword && !string.IsNullOrWhiteSpace(r.Message))
            .ToDictionary(r => r.Hash, r => r.Message!);
        return (plan, messages);
    }

    private RebaseRowVm? SelectedRow => RowsList.SelectedItem as RebaseRowVm;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        EditMessageButton.IsEnabled = SelectedRow is { Action: RebaseAction.Reword };

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        var index = RowsList.SelectedIndex;
        if (index <= 0) return;
        _rows.Move(index, index - 1);
        RowsList.SelectedIndex = index - 1;
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        var index = RowsList.SelectedIndex;
        if (index < 0 || index >= _rows.Count - 1) return;
        _rows.Move(index, index + 1);
        RowsList.SelectedIndex = index + 1;
    }

    private void OnEditMessage(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { Action: RebaseAction.Reword } row)
            return;
        var message = InputDialog.Prompt(this, "コミットメッセージ",
            $"{row.ShortHash} の新しいメッセージを入力してください。", row.Message ?? row.Subject, multiline: true);
        if (message is not null)
            row.Message = message;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var firstNonDrop = _rows.FirstOrDefault(r => r.Action != RebaseAction.Drop);
        if (firstNonDrop is null)
        {
            ShowError("少なくとも1件は Pick / Reword / Edit にしてください。");
            return;
        }
        if (firstNonDrop.Action is RebaseAction.Squash or RebaseAction.Fixup)
        {
            ShowError("先頭のコミットは Pick / Reword / Edit のいずれかにしてください。");
            return;
        }
        var missingMessage = _rows.FirstOrDefault(
            r => r.Action == RebaseAction.Reword && string.IsNullOrWhiteSpace(r.Message));
        if (missingMessage is not null)
        {
            ShowError($"{missingMessage.ShortHash}（Reword）のメッセージを入力してください。");
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}

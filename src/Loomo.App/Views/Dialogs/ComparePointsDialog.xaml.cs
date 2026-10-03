using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>2点比較ダイアログの結果。</summary>
public sealed record ComparePointsResult(GitCompareEndpoint From, GitCompareEndpoint To);

/// <summary>
/// 2点比較ダイアログ。候補（ワークツリー・ブランチ・タグ）から選ぶか、ハッシュ等を直接打つ。
/// 打った文字が候補の見出しと一致しなければ ref として扱う（解決できなければ比較時に理由が出る）。
/// </summary>
public partial class ComparePointsDialog : Window
{
    private ComparePointsResult? _result;

    private ComparePointsDialog()
    {
        InitializeComponent();
    }

    /// <summary>ダイアログを開く。キャンセルなら null。</summary>
    /// <param name="from">左の初期値（null なら空）。</param>
    /// <param name="to">右の初期値（null なら空）。</param>
    public static ComparePointsResult? Prompt(
        Window? owner, IReadOnlyList<GitCompareEndpoint> candidates, GitCompareEndpoint? from, GitCompareEndpoint? to)
    {
        var dialog = new ComparePointsDialog { Owner = owner };
        dialog.FromBox.ItemsSource = candidates;
        dialog.ToBox.ItemsSource = candidates;
        Select(dialog.FromBox, from);
        Select(dialog.ToBox, to);
        dialog.Loaded += (_, _) => (from is null ? dialog.FromBox : dialog.ToBox).Focus();
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    /// <summary>候補の中の同じもの（種別と値が一致）を選ぶ。候補に無ければ文字として入れる。</summary>
    private static void Select(ComboBox box, GitCompareEndpoint? endpoint)
    {
        if (endpoint is null) return;
        var match = box.Items.OfType<GitCompareEndpoint>().FirstOrDefault(c => SameEndpoint(c, endpoint));
        if (match is not null) box.SelectedItem = match;
        else box.Text = endpoint.Value;
    }

    private static bool SameEndpoint(GitCompareEndpoint left, GitCompareEndpoint right)
        => left.Kind == right.Kind && (left.Kind == GitCompareEndpointKind.Worktree
            ? string.Equals(Path.GetFullPath(left.Value), Path.GetFullPath(right.Value), StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.Value, right.Value, StringComparison.Ordinal));

    /// <summary>
    /// 入力欄の中身を端にする。編集可能な ComboBox は候補を選んだ後に文字を打ち直しても SelectedItem が
    /// 残ることがあるので、<b>表示中の文字が選択の見出しと一致するときだけ</b>選択を信じる。
    /// </summary>
    internal static GitCompareEndpoint? Read(ComboBox box)
    {
        var text = box.Text?.Trim() ?? "";
        if (box.SelectedItem is GitCompareEndpoint selected && string.Equals(selected.Label, text, StringComparison.Ordinal))
            return selected;
        if (text.Length == 0) return null;
        var byLabel = box.Items.OfType<GitCompareEndpoint>()
            .FirstOrDefault(c => string.Equals(c.Label, text, StringComparison.Ordinal));
        return byLabel ?? GitCompareEndpoint.Ref(text);
    }

    private void OnSwap(object sender, RoutedEventArgs e)
    {
        var (from, to) = (Read(FromBox), Read(ToBox));
        FromBox.SelectedItem = null;
        ToBox.SelectedItem = null;
        FromBox.Text = "";
        ToBox.Text = "";
        Select(FromBox, to);
        Select(ToBox, from);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var from = Read(FromBox);
        var to = Read(ToBox);
        var error = from is null ? "左（元）を選ぶか入力してください。"
            : to is null ? "右（先）を選ぶか入力してください。"
            : SameEndpoint(from, to) ? "同じものどうしは比較できません。"
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _result = new ComparePointsResult(from!, to!);
        DialogResult = true;
    }
}

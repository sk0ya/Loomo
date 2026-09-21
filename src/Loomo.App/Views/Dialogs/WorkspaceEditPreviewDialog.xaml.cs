namespace sk0ya.Loomo.App.Views;

/// <summary>複数ファイル WorkspaceEdit の適用前プレビュー。</summary>
public partial class WorkspaceEditPreviewDialog : Window
{
    public WorkspaceEditPreviewDialog(
        string title,
        IReadOnlyList<WorkspaceEditPreviewFile> files,
        IReadOnlyList<WorkspaceEditPreviewOperation> operations)
    {
        InitializeComponent();
        Title = $"Loomo - {title}（編集プレビュー）";
        var preview = WorkspaceEditPreviewFormatter.Build(files, operations);
        SummaryText.Text = preview.Summary;
        OperationText.Text = preview.Operations;
        DiffText.Text = preview.Diff;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

using System.Text;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Views;

/// <summary>複数ファイル WorkspaceEdit の適用前プレビュー。</summary>
public sealed record WorkspaceEditPreviewFile(string Path, string OriginalText, string UpdatedText);

public sealed record WorkspaceEditPreviewOperation(string Kind, string Path, string? NewPath = null);

public partial class WorkspaceEditPreviewDialog : Window
{
    public WorkspaceEditPreviewDialog(
        string title,
        IReadOnlyList<WorkspaceEditPreviewFile> files,
        IReadOnlyList<WorkspaceEditPreviewOperation> operations)
    {
        InitializeComponent();
        Title = $"Loomo - {title}（編集プレビュー）";
        SummaryText.Text = $"{files.Count} ファイルを変更" +
            (operations.Count > 0 ? $"、ファイル操作 {operations.Count} 件" : "") +
            "。適用前に内容を確認してください。";
        OperationText.Text = operations.Count == 0
            ? ""
            : string.Join("  ·  ", operations.Select(DescribeOperation));
        DiffText.Text = BuildDiff(files, operations);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private static string BuildDiff(
        IReadOnlyList<WorkspaceEditPreviewFile> files,
        IReadOnlyList<WorkspaceEditPreviewOperation> operations)
    {
        var builder = new StringBuilder();
        const int maxChars = 240_000;
        foreach (var file in files)
        {
            if (string.Equals(file.OriginalText, file.UpdatedText, StringComparison.Ordinal)) continue;
            builder.Append("--- ").Append(file.Path).AppendLine();
            builder.Append("+++ ").Append(file.Path).AppendLine();
            builder.AppendLine(DiffUtil.ToUnifiedText(DiffUtil.Compute(file.OriginalText, file.UpdatedText, context: 4)));
            if (builder.Length >= maxChars)
            {
                builder.AppendLine("… プレビューが長いため省略しました。");
                break;
            }
        }
        foreach (var operation in operations)
            builder.Append("@@ ").Append(DescribeOperation(operation)).AppendLine(" @@");
        return builder.Length == 0 ? "（本文の変更はありません）" : builder.ToString();
    }

    private static string DescribeOperation(WorkspaceEditPreviewOperation operation)
        => operation.Kind switch
        {
            "create" => $"作成: {operation.Path}",
            "rename" => $"名前変更: {operation.Path} → {operation.NewPath}",
            "delete" => $"削除: {operation.Path}",
            _ => $"{operation.Kind}: {operation.Path}",
        };
}

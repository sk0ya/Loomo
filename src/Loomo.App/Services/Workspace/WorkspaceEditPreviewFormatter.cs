using System.Text;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <summary>WorkspaceEdit の差分プレビューに使うファイル内容。</summary>
public sealed record WorkspaceEditPreviewFile(string Path, string OriginalText, string UpdatedText);

/// <summary>WorkspaceEdit の差分プレビューに使うファイル操作。</summary>
public sealed record WorkspaceEditPreviewOperation(string Kind, string Path, string? NewPath = null);

internal sealed record WorkspaceEditPreviewPresentation(string Summary, string Operations, string Diff);

/// <summary>WorkspaceEdit のプレビュー表示用テキストを組み立てる。</summary>
internal static class WorkspaceEditPreviewFormatter
{
    private const int MaxDiffChars = 240_000;

    public static WorkspaceEditPreviewPresentation Build(
        IReadOnlyList<WorkspaceEditPreviewFile> files,
        IReadOnlyList<WorkspaceEditPreviewOperation> operations)
    {
        var summary = $"{files.Count} ファイルを変更" +
            (operations.Count > 0 ? $"、ファイル操作 {operations.Count} 件" : "") +
            "。適用前に内容を確認してください。";
        var operationText = operations.Count == 0
            ? ""
            : string.Join("  ·  ", operations.Select(DescribeOperation));
        return new WorkspaceEditPreviewPresentation(summary, operationText, BuildDiff(files, operations));
    }

    private static string BuildDiff(
        IReadOnlyList<WorkspaceEditPreviewFile> files,
        IReadOnlyList<WorkspaceEditPreviewOperation> operations)
    {
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            if (string.Equals(file.OriginalText, file.UpdatedText, StringComparison.Ordinal))
                continue;
            builder.Append("--- ").Append(file.Path).AppendLine();
            builder.Append("+++ ").Append(file.Path).AppendLine();
            builder.AppendLine(DiffUtil.ToUnifiedText(
                DiffUtil.Compute(file.OriginalText, file.UpdatedText, context: 4)));
            if (builder.Length >= MaxDiffChars)
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

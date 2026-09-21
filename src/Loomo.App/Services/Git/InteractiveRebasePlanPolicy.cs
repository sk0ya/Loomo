using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

internal sealed record RebasePlanEdit(
    string Hash, string ShortHash, string Subject, RebaseAction Action, string? Message);

/// <summary>インタラクティブリベース計画の先頭アクションとメッセージを検証する。</summary>
internal static class InteractiveRebasePlanPolicy
{
    public static (IReadOnlyList<RebasePlanEntry> Plan, IReadOnlyDictionary<string, string?> Messages) Build(
        IEnumerable<RebasePlanEdit> edits)
    {
        var rows = edits.ToArray();
        var plan = rows
            .Select(ToEntry)
            .ToArray();
        var messages = rows
            .Where(row => row.Action == RebaseAction.Reword)
            .ToDictionary(row => row.Hash, row => row.Message);
        return (plan, messages);
    }

    public static RebasePlanEntry ToEntry(RebasePlanEdit edit)
        => new(edit.Hash, edit.ShortHash, edit.Subject, edit.Action);

    public static IReadOnlyDictionary<string, string> NonEmptyMessages(
        IReadOnlyDictionary<string, string?> messages)
        => messages
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value!);

    public static string? Validate(
        IReadOnlyList<RebasePlanEntry> plan,
        IReadOnlyDictionary<string, string?> rewordMessages)
    {
        var firstNonDrop = plan.FirstOrDefault(entry => entry.Action != RebaseAction.Drop);
        if (firstNonDrop is null)
            return "少なくとも1件は Pick / Reword / Edit にしてください。";
        if (firstNonDrop.Action is RebaseAction.Squash or RebaseAction.Fixup)
            return "先頭のコミットは Pick / Reword / Edit のいずれかにしてください。";

        var missingMessage = plan.FirstOrDefault(entry =>
            entry.Action == RebaseAction.Reword
            && (!rewordMessages.TryGetValue(entry.Hash, out var message)
                || string.IsNullOrWhiteSpace(message)));
        return missingMessage is null
            ? null
            : $"{missingMessage.ShortHash}（Reword）のメッセージを入力してください。";
    }
}

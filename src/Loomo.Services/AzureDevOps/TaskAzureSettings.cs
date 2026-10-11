using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Services;

/// <summary>
/// TaskAzure の設定（<c>%LOCALAPPDATA%\TaskAzure\settings.json</c>）のうち、Work Items の照会に要る値。
/// <para>Loomo は「TaskAzure と同じ値」で問い合わせる約束なので、手で写した値ではなく TaskAzure が実際に使っている値を読む。
/// 写し間違い（組織 URL・プロジェクト）や、TaskAzure 側で PAT の環境変数名を変えていることで、
/// TaskAzure は動くのに Loomo だけ失敗する、が起きていた。<b>読むだけ。書かない</b>。</para>
/// </summary>
public sealed record TaskAzureSettings(
    string OrganizationUrl,
    string Project,
    string PatEnvVarName,
    IReadOnlyList<AzureDevOpsPrTarget> PrTargets)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskAzure", "settings.json");

    /// <summary>TaskAzure の設定。無い・読めない・組織 URL が空なら null。</summary>
    public static TaskAzureSettings? TryLoad(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static TaskAzureSettings? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var organizationUrl = Text(root, "OrganizationUrl").Trim().TrimEnd('/');
        if (organizationUrl.Length == 0) return null;
        var envName = Text(root, "PatEnvVarName").Trim();
        var targets = new List<AzureDevOpsPrTarget>();
        if (root.TryGetProperty("PrTargets", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var t in array.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object))
            {
                var project = Text(t, "Project").Trim();
                var repository = Text(t, "Repository").Trim();
                if (project.Length > 0 && repository.Length > 0)
                    targets.Add(new AzureDevOpsPrTarget(project, repository));
            }
        return new TaskAzureSettings(organizationUrl, Text(root, "Project").Trim(),
            envName.Length > 0 ? envName : AzureDevOpsPatStore.EnvironmentVariable, targets);
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services;

/// <summary>ワークスペースの git リモートから分かったこと。<see cref="Organization"/> は最初に見つかった組織、
/// <see cref="Projects"/> はその組織のリモートが指すプロジェクト（PR の照会先に足す）。</summary>
public sealed record AzureDevOpsWorkspaceRemotes(AzureDevOpsOrganization? Organization, IReadOnlyList<string> Projects);

/// <summary>
/// ワークスペースの各フォルダーの git リモートから Azure DevOps の組織を見つける。
/// マルチルートなので、プライマリだけでなく全フォルダーを順に見る（最初に見つかったもの）。
/// </summary>
public sealed class AzureDevOpsOrganizationLocator
{
    private readonly IWorkspaceService _workspace;
    private readonly GitCommandRunner _git;

    public AzureDevOpsOrganizationLocator(IWorkspaceService workspace)
    {
        _workspace = workspace;
        _git = new GitCommandRunner(new GitRootState(workspace));
    }

    public async Task<AzureDevOpsWorkspaceRemotes> FindAsync(CancellationToken cancellationToken)
    {
        AzureDevOpsOrganization? first = null;
        var projects = new List<string>();
        foreach (var folder in _workspace.Folders.ToList())
        {
            var result = await _git.RunInAsync(folder, null, null, cancellationToken, "remote", "-v")
                .ConfigureAwait(false);
            if (!result.Success)
                continue;
            foreach (var raw in result.Output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!line.EndsWith("(fetch)", StringComparison.Ordinal))
                    continue;
                var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !AzureDevOpsOrganization.TryParseRemote(parts[1], out var organization))
                    continue;
                first ??= organization;
                if (!string.Equals(first.BaseUrl, organization.BaseUrl, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (AzureDevOpsOrganization.ProjectFromRemote(parts[1]) is { } project
                    && !projects.Contains(project, StringComparer.OrdinalIgnoreCase))
                    projects.Add(project);
            }
        }
        return new AzureDevOpsWorkspaceRemotes(first, projects);
    }
}

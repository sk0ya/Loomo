using System.Collections.Generic;
using System.Linq;

namespace AgentStudio.Core.Tools;

/// <summary>登録された全ツールを束ねるレジストリ。DIで全 IAgentTool を集約する。</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name);
    }

    public IReadOnlyCollection<IAgentTool> All => _tools.Values;

    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToList();

    public bool TryGet(string name, out IAgentTool tool) => _tools.TryGetValue(name, out tool!);
}

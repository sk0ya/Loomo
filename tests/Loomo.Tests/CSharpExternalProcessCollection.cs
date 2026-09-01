namespace sk0ya.Loomo.Tests;

/// <summary>C# fixture／testhost／Roslyn serverが共有する外部プロセス資源を直列化する。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CSharpExternalProcessCollection
{
    public const string Name = "C# external processes";
}

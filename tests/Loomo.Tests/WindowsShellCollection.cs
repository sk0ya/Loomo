namespace sk0ya.Loomo.Tests;

/// <summary>Windows Shell／ごみ箱はプロセス外の共有資源なので、実体を使うテストを直列化する。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsShellTests
{
    public const string Name = "windows-shell";
}

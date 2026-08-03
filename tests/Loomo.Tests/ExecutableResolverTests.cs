using System;
using System.IO;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// PATH 上の実行ファイル解決。<b>「導入済み」の判定と、実際の起動で同じ解決を使う</b>ことが要点。
///
/// <para>2026-08-03 の実機不具合：npm のグローバル導入は <c>typescript-language-server.cmd</c> なので
/// <see cref="ExecutableResolver.IsOnPath"/>（PATHEXT 総当たり）は true を返すのに、
/// 起動側は素の名前を <c>Process.Start</c> していた。<c>UseShellExecute=false</c> の補完は <c>.exe</c> だけなので
/// プロセスが起動できず、UI は「言語サーバーへの接続待ちです」から永久に進まなかった。</para>
/// </summary>
public sealed class ExecutableResolverTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _originalPath;

    public ExecutableResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loomo-exe-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", _dir + Path.PathSeparator + _originalPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Cmdシムはフルパスで解決される()
    {
        // これを素の名前のまま起動すると Win32Exception（指定されたファイルが見つかりません）になる。
        var expected = Touch("loomo-fake-lsp.cmd");

        // 拡張子の大小は PATHEXT 由来（.CMD）。Windows のパス比較に合わせて大小は問わない。
        Assert.Equal(expected, ExecutableResolver.Resolve("loomo-fake-lsp"), ignoreCase: true);
        Assert.True(ExecutableResolver.IsOnPath("loomo-fake-lsp"));
    }

    [Fact]
    public void Exeがあれば拡張子補完で解決される()
    {
        var expected = Touch("loomo-fake-exe-lsp.exe");

        Assert.Equal(expected, ExecutableResolver.Resolve("loomo-fake-exe-lsp"), ignoreCase: true);
    }

    [Fact]
    public void 拡張子つきで指定されたらそのまま探す()
    {
        var expected = Touch("loomo-fake-ext-lsp.cmd");

        Assert.Equal(expected, ExecutableResolver.Resolve("loomo-fake-ext-lsp.cmd"));
    }

    [Fact]
    public void 絶対パス指定は実在するときだけ返す()
    {
        var existing = Touch("loomo-fake-abs-lsp.exe");

        Assert.Equal(existing, ExecutableResolver.Resolve(existing));
        Assert.Null(ExecutableResolver.Resolve(Path.Combine(_dir, "loomo-not-there.exe")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loomo-definitely-not-installed-xyz")]
    public void 見つからなければnull(string executable)
    {
        Assert.Null(ExecutableResolver.Resolve(executable));
        Assert.False(ExecutableResolver.IsOnPath(executable));
    }
}

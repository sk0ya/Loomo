using System.IO;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="CSharpIdeFixtureTests"/> のクラス共有の足場：フィクスチャ（<c>tests/Fixtures/CSharpIde</c>）の
/// 一時コピー1つと、その MSBuild 評価結果1つ。
///
/// <para>評価は1回数秒かかる。リファクタリング等のテストはどれも同じソリューションを評価していたので、
/// クラスで1回にして使い回す。コピー先のパスは固定なので、評価結果の中のパスもそのまま使える。</para>
///
/// <para>テストはコピーのソースを書き換える（編集を当てる・ファイルを足す）ので、各テストの冒頭と後始末で
/// <see cref="Reset"/> して原本と同じ状態へ戻す。bin／obj は触らない（restore の結果を保つ）。</para>
/// </summary>
public sealed class SharedCSharpFixtureCopy : IAsyncLifetime
{
    private Task<SolutionModel>? _solution;

    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "Loomo-CSharpFixture-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        CopyTree(CSharpIdeFixtureTests.FixtureRoot, Root, includeBuildOutput: true);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* 解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    /// <summary>評価結果（初回だけ評価する）。<b>原本と同じファイル構成</b>の評価なので、テストが
    /// ソースファイルを足したときは使えない（足したファイルが評価に入っていない）——そのときは
    /// <see cref="EvaluateFreshAsync"/>。</summary>
    public Task<SolutionModel> SolutionAsync() => _solution ??= LoadAsync();

    /// <summary>いまのコピーを評価し直す（共有の評価結果は置き換えない）。ファイル構成を変えるテスト用。</summary>
    public Task<SolutionModel> EvaluateFreshAsync() => LoadAsync();

    /// <summary>コピーのソースを原本と同じ状態へ戻し、コピーのルートを返す。原本に無いファイル
    /// （テストが足したもの）は消し、中身が違うものは上書きする。</summary>
    public string Reset()
    {
        var source = CSharpIdeFixtureTests.FixtureRoot;
        foreach (var file in EnumerateSources(Root))
        {
            var original = Path.Combine(source, Path.GetRelativePath(Root, file));
            if (!File.Exists(original))
                File.Delete(file);
        }
        CopyTree(source, Root, includeBuildOutput: false);
        return Root;
    }

    private async Task<SolutionModel> LoadAsync()
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(Root);
        using var service = new SolutionModelService(workspace, new MsBuildProjectEvaluator());
        return await service.ReloadAsync();
    }

    private static void CopyTree(string from, string to, bool includeBuildOutput)
    {
        var files = includeBuildOutput
            ? Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
            : EnumerateSources(from);
        foreach (var file in files)
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!includeBuildOutput && File.Exists(target) && SameContent(file, target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>bin／obj 以外のファイル（テストが書き換え得るもの）。</summary>
    private static IEnumerable<string> EnumerateSources(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                             || part.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    private static bool SameContent(string left, string right)
        => new FileInfo(left).Length == new FileInfo(right).Length
           && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
}

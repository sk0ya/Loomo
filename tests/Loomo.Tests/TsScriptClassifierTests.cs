using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>npm スクリプトの実体コマンド分類（<see cref="TsScriptClassifier"/>）のテスト。
/// 起動手段の振り分け（フロント＝Chrome複合 / Node＝pwa-node）の根拠になるので、代表的な dev スクリプトを固定する。</summary>
public class TsScriptClassifierTests
{
    [Theory]
    [InlineData("vite")]
    [InlineData("vite dev")]
    [InlineData("vite --host")]
    [InlineData("next dev")]
    [InlineData("next dev -p 3001")]
    [InlineData("nuxt dev")]
    [InlineData("ng serve")]
    [InlineData("astro dev")]
    [InlineData("webpack serve")]
    [InlineData("webpack-dev-server")]
    [InlineData("remix vite:dev")]
    [InlineData("vitepress dev docs")]
    public void FrontendDevServer_is_detected(string command)
        => Assert.Equal(TsScriptKind.FrontendDevServer, TsScriptClassifier.Classify(command));

    [Theory]
    [InlineData("node server.js")]
    [InlineData("tsx watch src/index.ts")]
    [InlineData("ts-node src/main.ts")]
    [InlineData("nodemon --exec tsx src/server.ts")]
    [InlineData("nest start --watch")]
    [InlineData("next start")]     // 本番配信＝Node（dev と区別する）
    public void NodeRuntime_is_detected(string command)
        => Assert.Equal(TsScriptKind.NodeRuntime, TsScriptClassifier.Classify(command));

    [Theory]
    [InlineData("vitest")]
    [InlineData("vitest run")]
    [InlineData("jest --coverage")]
    [InlineData("mocha")]
    [InlineData("playwright test")]
    public void Test_is_detected(string command)
        => Assert.Equal(TsScriptKind.Test, TsScriptClassifier.Classify(command));

    [Theory]
    [InlineData("tsc --noEmit")]
    [InlineData("tsc && vite build")]   // ビルドは「フロント開発サーバー」より後段の判定だが build が勝つ
    [InlineData("vite build")]
    [InlineData("next build")]
    [InlineData("eslint .")]
    [InlineData("prettier --write .")]
    public void BuildOrTool_is_detected(string command)
        => Assert.Equal(TsScriptKind.BuildOrTool, TsScriptClassifier.Classify(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("echo hello")]
    [InlineData("some-unknown-cli --flag")]
    public void Unknown_when_nothing_matches(string command)
        => Assert.Equal(TsScriptKind.Unknown, TsScriptClassifier.Classify(command));

    [Fact]
    public void Vite_preview_and_build_are_not_dev_servers()
    {
        Assert.NotEqual(TsScriptKind.FrontendDevServer, TsScriptClassifier.Classify("vite preview"));
        Assert.NotEqual(TsScriptKind.FrontendDevServer, TsScriptClassifier.Classify("vite build"));
    }

    [Theory]
    [InlineData("vite", FrontendFramework.Vite, 5173)]
    [InlineData("next dev", FrontendFramework.Next, 3000)]
    [InlineData("ng serve", FrontendFramework.Angular, 4200)]
    [InlineData("astro dev", FrontendFramework.Astro, 4321)]
    public void DetectFramework_and_DefaultPort(string command, FrontendFramework expected, int port)
    {
        Assert.Equal(expected, TsScriptClassifier.DetectFramework(command));
        Assert.Equal(port, TsScriptClassifier.DefaultPort(expected));
    }

    [Fact]
    public void PinnedPortArgs_vite_uses_strictPort()
    {
        Assert.Equal("--port 5180 --strictPort", TsScriptClassifier.PinnedPortArgs(FrontendFramework.Vite, 5180));
        Assert.Equal("--port 3005", TsScriptClassifier.PinnedPortArgs(FrontendFramework.Next, 3005));
        Assert.Equal("", TsScriptClassifier.PinnedPortArgs(FrontendFramework.None, 1234));
    }
}

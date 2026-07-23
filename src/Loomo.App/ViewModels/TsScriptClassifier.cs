using System;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>npm スクリプトが「何を実行するものか」の分類。<b>スクリプトの実体コマンド文字列</b>（package.json の
/// scripts の値）だけを見て決める——名前（"dev" 等）ではなく、ユーザーが書いた実際のコマンドがグラウンドトゥルース。
/// これで起動手段を正しく振り分ける：フロント開発サーバーはブラウザ（pwa-chrome＋ターミナルでサーバー）、
/// Node ランタイム／テストは pwa-node、ビルド／整形はデバッグ対象外。</summary>
public enum TsScriptKind
{
    /// <summary>フロントの開発サーバー（vite / next dev 等）。アプリコードは<b>ブラウザ</b>で動くので、
    /// Node デバッガを当てても無意味。Chrome デバッグ＋サーバーはターミナルが正解。</summary>
    FrontendDevServer,
    /// <summary>Node で動くランタイム（node / tsx / nest start 等）。pwa-node でブレークが正しく効く。</summary>
    NodeRuntime,
    /// <summary>テストランナー（vitest / jest 等）。Node 実行。専用のテストタブがある。</summary>
    Test,
    /// <summary>ビルド・型チェック・整形（tsc / vite build / eslint 等）。デバッグ対象ではない。</summary>
    BuildOrTool,
    /// <summary>判定不能。</summary>
    Unknown,
}

/// <summary>フロント開発サーバーのフレームワーク（P2 ポート固定注入のためのフラグ方言を選ぶ）。</summary>
public enum FrontendFramework { None, Vite, Next, Angular, Nuxt, Astro, WebpackServe }

/// <summary>npm スクリプトの実体コマンドを分類する（<see cref="TsScriptKind"/>）ヘルパと、
/// フロント開発サーバーのフレームワーク検出＋ポート固定注入（P2）の方言。</summary>
public static class TsScriptClassifier
{
    // フロント開発サーバー：ブラウザで動くアプリを配信する開発サーバー。
    // 注意：vite build / vite preview、next build / next start（本番配信＝Node）は除く。
    private static readonly Regex FrontendDevServer = new(
        @"(?<!\w)(" +
        @"vite(?!\s+(build|preview|optimize))" +           // vite / vite dev / vite serve
        @"|next\s+dev" +
        @"|(nuxt|nuxi)\s+dev" +
        @"|ng\s+serve" +
        @"|astro\s+dev" +
        @"|(remix\s+(vite:)?dev)" +
        @"|(webpack(-dev-server|\s+serve))" +
        @"|(rsbuild|rspack)\s+(dev|serve)" +
        @"|parcel(?!\s+build)" +
        @"|gatsby\s+develop" +
        @"|(docusaurus|vuepress|vitepress)\s+(start|dev)" +
        @"|(storybook\s+dev|start-storybook)" +
        @")(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TestRunner = new(
        @"(?<!\w)(vitest|jest|mocha|ava|jasmine|node\s+--test|(playwright|cypress)\s+.*\b(test|run|open)|karma\s+start)(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BuildOrTool = new(
        @"(?<!\w)(tsc|(vite|next|nuxt|astro|webpack|rollup|esbuild|rsbuild|rspack|parcel|ng)\s+build|next\s+export" +
        @"|eslint|prettier|biome|stylelint|rimraf|shx|cpy|copyfiles|husky|npm-run-all|concurrently)(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NodeRuntime = new(
        @"(?<!\w)(node|tsx|ts-node|nodemon|nest\s+start|next\s+start|nuxt\s+start|pm2|ts-node-dev|node-dev)(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>スクリプトの実体コマンドを分類する。優先順は「フロント開発サーバー → テスト → ビルド/整形 →
    /// Node ランタイム」（具体的なものから）。どれにも当たらなければ <see cref="TsScriptKind.Unknown"/>。</summary>
    public static TsScriptKind Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return TsScriptKind.Unknown;
        if (FrontendDevServer.IsMatch(command)) return TsScriptKind.FrontendDevServer;
        if (TestRunner.IsMatch(command)) return TsScriptKind.Test;
        if (BuildOrTool.IsMatch(command)) return TsScriptKind.BuildOrTool;
        if (NodeRuntime.IsMatch(command)) return TsScriptKind.NodeRuntime;
        return TsScriptKind.Unknown;
    }

    /// <summary>フロント開発サーバーのフレームワークを検出する（P2 ポート固定注入の方言選択用）。
    /// <see cref="TsScriptKind.FrontendDevServer"/> でないコマンドは <see cref="FrontendFramework.None"/>。</summary>
    public static FrontendFramework DetectFramework(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return FrontendFramework.None;
        // vite ベース（remix vite:dev / svelte-kit dev も内部は vite だが判定は素直に）
        if (Regex.IsMatch(command, @"(?<!\w)vite(?!\s+(build|preview))|remix\s+vite:dev", RegexOptions.IgnoreCase))
            return FrontendFramework.Vite;
        if (Regex.IsMatch(command, @"(?<!\w)next\s+dev", RegexOptions.IgnoreCase)) return FrontendFramework.Next;
        if (Regex.IsMatch(command, @"(?<!\w)ng\s+serve", RegexOptions.IgnoreCase)) return FrontendFramework.Angular;
        if (Regex.IsMatch(command, @"(?<!\w)(nuxt|nuxi)\s+dev", RegexOptions.IgnoreCase)) return FrontendFramework.Nuxt;
        if (Regex.IsMatch(command, @"(?<!\w)astro\s+dev", RegexOptions.IgnoreCase)) return FrontendFramework.Astro;
        if (Regex.IsMatch(command, @"webpack(-dev-server|\s+serve)", RegexOptions.IgnoreCase)) return FrontendFramework.WebpackServe;
        return FrontendFramework.None;
    }

    /// <summary>フレームワークの慣習的な既定ポート（空きポート探索の開始点）。</summary>
    public static int DefaultPort(FrontendFramework framework) => framework switch
    {
        FrontendFramework.Vite => 5173,
        FrontendFramework.Next => 3000,
        FrontendFramework.Angular => 4200,
        FrontendFramework.Nuxt => 3000,
        FrontendFramework.Astro => 4321,
        FrontendFramework.WebpackServe => 8080,
        _ => 5173,
    };

    /// <summary>P2：<c>npm run &lt;script&gt;</c> に付ける「ポート固定」引数（<c>-- ...</c> の後半だけを返す）。
    /// ポートを決定論化して、開発サーバーの自動ポートずらし（vite の 5173→5174→…）を封じる。
    /// フラグ方言を持たないフレームワーク（<see cref="FrontendFramework.None"/>）は空＝注入しない（P1 フォールバック）。</summary>
    public static string PinnedPortArgs(FrontendFramework framework, int port) => framework switch
    {
        // vite は --strictPort で「埋まっていたら失敗」にできる＝確実にこのポートか起動失敗の二択。
        FrontendFramework.Vite => $"--port {port} --strictPort",
        // 他はポート指定のみ（strict 相当が無いものは、空きポートを渡すこと自体で実質固定）。
        FrontendFramework.Next => $"--port {port}",
        FrontendFramework.Angular => $"--port {port}",
        FrontendFramework.Nuxt => $"--port {port}",
        FrontendFramework.Astro => $"--port {port}",
        FrontendFramework.WebpackServe => $"--port {port}",
        _ => "",
    };
}
